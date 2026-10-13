package com.vivoderin.joycon2;

import android.Manifest;
import android.annotation.SuppressLint;
import android.bluetooth.*;
import android.bluetooth.le.*;
import android.content.*;
import android.content.pm.PackageManager;
import android.os.*;
import android.util.Log;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.atomic.AtomicInteger;

/** Generic BLE byte pipe. UUIDs, manufacturer id and all protocol bytes come from C#.
 * State belongs to one HandlerThread. Callbacks only post work onto that thread.
 * pollEvent() is the only synchronous cross-thread operation (a short queue lock).
 */
@SuppressLint("MissingPermission")
public final class BleTransport {
    private static final String TAG = "Joycon2Ble";
    private static final UUID CCCD = UUID.fromString("00002902-0000-1000-8000-00805f9b34fb");
    private static final int MAX_EVENTS = 2048, MAX_OPERATIONS = 64;
    private static final int ERROR_PERMISSION = -1001, ERROR_DISABLED = -1002,
        ERROR_OPERATION = -1003, ERROR_TIMEOUT = -1004, ERROR_MTU = -1005;
    private final ArrayDeque<byte[]> events = new ArrayDeque<>();
    private final AtomicInteger nextHandle = new AtomicInteger(1);
    private final HashMap<Integer, Link> links = new HashMap<>();
    private final HashMap<Long, BluetoothDevice> devices = new HashMap<>();
    private final HashMap<Long, Long> lastAdvertisement = new HashMap<>();
    private final Context context;
    private final int minimumMtu;
    private final BluetoothAdapter adapter;
    private final HandlerThread thread;
    private final Handler worker;
    private volatile boolean closed;
    private boolean scanning;
    private long lastScanStart = -10000;
    private BluetoothLeScanner scanner;
    private ScanCallback scanCallback;
    private BroadcastReceiver stateReceiver;

    public BleTransport(Context suppliedContext, int requiredMtu) {
        context = suppliedContext.getApplicationContext();
        minimumMtu = requiredMtu;
        BluetoothManager manager = (BluetoothManager) context.getSystemService(Context.BLUETOOTH_SERVICE);
        adapter = manager == null ? null : manager.getAdapter();
        thread = new HandlerThread("Joycon2 BLE");
        thread.start();
        worker = new Handler(thread.getLooper());
    }

    public boolean initialize() {
        if (closed || adapter == null || !context.getPackageManager().hasSystemFeature(PackageManager.FEATURE_BLUETOOTH_LE))
            return false;
        worker.post(() -> {
            stateReceiver = new BroadcastReceiver() {
                @Override public void onReceive(Context c, Intent intent) {
                    int state = intent.getIntExtra(BluetoothAdapter.EXTRA_STATE, BluetoothAdapter.ERROR);
                    worker.post(() -> {
                        if (closed) return;
                        if (state == BluetoothAdapter.STATE_OFF || state == BluetoothAdapter.STATE_TURNING_OFF) {
                            stopScanInternal(ERROR_DISABLED);
                            for (Link link : new ArrayList<>(links.values())) finish(link, ERROR_DISABLED, false);
                        } else if (state == BluetoothAdapter.STATE_ON) log(0, "Bluetooth on; hold SYNC to connect");
                    });
                }
            };
            IntentFilter filter = new IntentFilter(BluetoothAdapter.ACTION_STATE_CHANGED);
            if (Build.VERSION.SDK_INT >= 33) context.registerReceiver(stateReceiver, filter, Context.RECEIVER_EXPORTED);
            else context.registerReceiver(stateReceiver, filter);
            log(0, "Android BLE transport ready (API " + Build.VERSION.SDK_INT + ")");
        });
        return true;
    }

    private boolean allowed() {
        if (Build.VERSION.SDK_INT >= 31)
            return context.checkSelfPermission(Manifest.permission.BLUETOOTH_SCAN) == PackageManager.PERMISSION_GRANTED
                && context.checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED;
        return context.checkSelfPermission(Manifest.permission.ACCESS_FINE_LOCATION) == PackageManager.PERMISSION_GRANTED;
    }

    public void startScan(int companyId) {
        post(() -> {
            if (scanning) return;
            if (!allowed()) { scanError(ERROR_PERMISSION, "Bluetooth permission missing"); return; }
            if (!adapter.isEnabled()) { scanError(ERROR_DISABLED, "Bluetooth off; enable it in settings"); return; }
            if (SystemClock.elapsedRealtime() - lastScanStart < 6500) {
                scanError(ERROR_TIMEOUT, "Scan cooldown; retrying shortly"); return;
            }
            scanner = adapter.getBluetoothLeScanner();
            if (scanner == null) { scanError(ERROR_DISABLED, "BLE scanner unavailable"); return; }
            // Capture this scan's id and callback; old scan results must not enter a later scan.
            scanCallback = new ScanCallback() {
                @Override public void onScanResult(int callbackType, ScanResult result) {
                    worker.post(() -> {
                        if (closed || !scanning || scanCallback != this || result.getScanRecord() == null) return;
                        byte[] data = result.getScanRecord().getManufacturerSpecificData(companyId);
                        if (data == null) return;
                        try {
                            long address = parseAddress(result.getDevice().getAddress());
                            long now = SystemClock.elapsedRealtime();
                            Long previous = lastAdvertisement.get(address);
                            if (previous != null && now - previous < 250) return;
                            lastAdvertisement.put(address, now);
                            devices.put(address, result.getDevice());
                            emit(2, 0, 0, 0, address, result.getRssi(), data);
                        } catch (RuntimeException e) { log(1, "Advertisement: " + e); }
                    });
                }
                @Override public void onScanFailed(int errorCode) {
                    worker.post(() -> {
                        if (closed || scanCallback != this) return;
                        scanning = false;
                        scanCallback = null;
                        scanError(errorCode, "BLE scan failed " + errorCode);
                    });
                }
            };
            try {
                ScanFilter filter = new ScanFilter.Builder().setManufacturerData(companyId, new byte[0]).build();
                ScanSettings settings = new ScanSettings.Builder().setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY).build();
                scanning = true;
                lastScanStart = SystemClock.elapsedRealtime();
                scanner.startScan(Collections.singletonList(filter), settings, scanCallback);
                log(0, String.format(Locale.ROOT, "Scan started (company 0x%04X)", companyId));
                // Android throttles frequent startScan calls. Bound scans and leave the manager's retry gap.
                ScanCallback started = scanCallback;
                worker.postDelayed(() -> {
                    if (!closed && scanning && scanCallback == started) stopScanInternal(ERROR_TIMEOUT);
                }, 30000);
            } catch (RuntimeException e) {
                scanning = false;
                scanCallback = null;
                scanError(ERROR_OPERATION, "Start scan: " + e);
            }
        });
    }

    public void stopScan() { post(() -> stopScanInternal(0)); }

    private void stopScanInternal(int status) {
        if (!scanning) return;
        ScanCallback old = scanCallback;
        scanning = false;
        scanCallback = null;
        try { if (scanner != null && old != null) scanner.stopScan(old); }
        catch (RuntimeException e) { log(1, "Stop scan: " + e); }
        emit(9, 0, 0, status, 0, 0, null);
    }

    public int connect(long address) {
        if (closed) return -1;
        int handle = nextHandle.getAndIncrement();
        post(() -> {
            Link link = new Link(handle, address);
            links.put(handle, link);
            try {
                if (!allowed()) { finish(link, ERROR_PERMISSION, false); return; }
                if (!adapter.isEnabled()) { finish(link, ERROR_DISABLED, false); return; }
                BluetoothDevice device = devices.get(address);
                if (device == null) { finish(link, ERROR_OPERATION, false); return; }
                link.gatt = device.connectGatt(context, false, link.callback, BluetoothDevice.TRANSPORT_LE);
                if (link.gatt == null) { finish(link, ERROR_OPERATION, false); return; }
                worker.postDelayed(() -> { if (current(link) && !link.ready) finish(link, ERROR_TIMEOUT, false); }, 18000);
                log(0, "[" + handle + "] connecting (no bonding)");
            } catch (RuntimeException e) { log(2, "Connect: " + e); finish(link, ERROR_OPERATION, false); }
        });
        return handle;
    }

    public void disconnect(int handle) {
        post(() -> { Link link = links.get(handle); if (link != null) finish(link, 0, true); });
    }

    public void subscribe(int handle, String service, String characteristic, int tag) {
        post(() -> {
            Link link = links.get(handle);
            if (link == null) return;
            enqueue(link, new Operation(true, service, characteristic, tag, null));
        });
    }

    public void write(int handle, String service, String characteristic, byte[] bytes) {
        // Caller/JNI buffer can be reused after returning.
        byte[] copy = bytes.clone();
        post(() -> {
            Link link = links.get(handle);
            if (link != null) enqueue(link, new Operation(false, service, characteristic, 0, copy));
        });
    }

    public byte[] pollEvent() { synchronized (events) { return events.pollFirst(); } }

    public void shutdown() {
        if (closed) return;
        closed = true;
        synchronized (events) { events.clear(); }
        // Runs after earlier work, which checks closed/current before doing anything.
        worker.post(() -> {
            stopScanInternal(0);
            for (Link link : new ArrayList<>(links.values())) finish(link, 0, true);
            if (stateReceiver != null) {
                try { context.unregisterReceiver(stateReceiver); } catch (RuntimeException ignored) { }
                stateReceiver = null;
            }
            devices.clear();
            lastAdvertisement.clear();
            thread.quitSafely();
        });
    }

    private void post(Runnable action) { if (!closed) worker.post(() -> { if (!closed) action.run(); }); }
    private boolean current(Link link) { return !closed && links.get(link.handle) == link; }
    private void scanError(int status, String text) { log(1, text); emit(9, 0, 0, status, 0, 0, null); }
    private void log(int level, String text) {
        if (level == 2) Log.e(TAG, text); else if (level == 1) Log.w(TAG, text); else Log.i(TAG, text);
        emit(1, 0, 0, level, 0, 0, text.getBytes(StandardCharsets.UTF_8));
    }
    private void emit(int type, int handle, int tag, int status, long address, int rssi, byte[] data) {
        if (closed) return;
        int length = data == null ? 0 : data.length;
        ByteBuffer buffer = ByteBuffer.allocate(32 + length).order(ByteOrder.LITTLE_ENDIAN);
        buffer.putInt(type).putInt(handle).putInt(tag).putInt(status).putLong(address).putInt(rssi).putInt(length);
        if (data != null) buffer.put(data);
        synchronized (events) {
            if (events.size() >= MAX_EVENTS) {
                // Preserve lifecycle/command events; a stalled consumer may discard an input notification.
                Iterator<byte[]> it = events.iterator();
                boolean removed = false;
                while (it.hasNext()) { if (it.next()[0] == 7) { it.remove(); removed = true; break; } }
                if (!removed) events.removeFirst();
            }
            events.addLast(buffer.array());
        }
    }

    private static long parseAddress(String address) { return Long.parseLong(address.replace(":", ""), 16); }

    private void requestPriority(Link link) {
        if (!current(link) || link.gatt == null) return;
        try { log(0, "[" + link.handle + "] high priority accepted=" +
            link.gatt.requestConnectionPriority(BluetoothGatt.CONNECTION_PRIORITY_HIGH)); }
        catch (RuntimeException e) { log(1, "Priority: " + e); }
    }

    private void discover(Link link) {
        if (!current(link) || link.discovering || link.ready) return;
        link.discovering = true;
        try { if (!link.gatt.discoverServices()) finish(link, ERROR_OPERATION, false); }
        catch (RuntimeException e) { log(2, "Discover: " + e); finish(link, ERROR_OPERATION, false); }
    }

    private void finish(Link link, int status, boolean requested) {
        if (links.get(link.handle) != link) return;
        links.remove(link.handle); // Invalidate callbacks before touching the GATT object.
        link.operations.clear();
        if (link.active != null && link.active.timeout != null) worker.removeCallbacks(link.active.timeout);
        link.active = null;
        if (link.gatt != null) {
            try { link.gatt.disconnect(); } catch (RuntimeException ignored) { }
            try { link.gatt.close(); } catch (RuntimeException ignored) { }
        }
        log(status == 0 ? 0 : 1, "[" + link.handle + "] closed (status " + status + ")");
        if (!requested) emit(link.ready ? 5 : 4, link.handle, 0, status, link.address, 0, null);
    }

    private void enqueue(Link link, Operation op) {
        if (!link.ready || link.operations.size() >= MAX_OPERATIONS) { failOp(link, op, ERROR_OPERATION); return; }
        link.operations.addLast(op);
        advance(link);
    }

    private BluetoothGattCharacteristic find(Link link, Operation op) {
        UUID uuid = UUID.fromString(op.characteristic);
        if (op.service != null && !op.service.isEmpty()) {
            BluetoothGattService service = link.gatt.getService(UUID.fromString(op.service));
            return service == null ? null : service.getCharacteristic(uuid);
        }
        for (BluetoothGattService service : link.gatt.getServices()) {
            BluetoothGattCharacteristic characteristic = service.getCharacteristic(uuid);
            if (characteristic != null) return characteristic;
        }
        return null;
    }

    @SuppressWarnings("deprecation") // API 25–32 uses the legacy write methods, guarded by SDK_INT.
    private void advance(Link link) {
        if (!current(link) || link.active != null || link.operations.isEmpty()) return;
        Operation op = link.operations.removeFirst();
        link.active = op;
        try {
            op.target = find(link, op);
            if (op.target == null) { complete(link, ERROR_OPERATION); return; }
            boolean started;
            if (op.subscription) {
                BluetoothGattDescriptor descriptor = op.target.getDescriptor(CCCD);
                if (descriptor == null || !link.gatt.setCharacteristicNotification(op.target, true)) {
                    complete(link, ERROR_OPERATION); return;
                }
                // Map before CCCD: notifications can race the descriptor callback.
                link.tags.put(op.target.getUuid(), op.tag);
                if (Build.VERSION.SDK_INT >= 33)
                    started = link.gatt.writeDescriptor(descriptor, BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE) == BluetoothStatusCodes.SUCCESS;
                else {
                    descriptor.setValue(BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE);
                    started = link.gatt.writeDescriptor(descriptor);
                }
            } else {
                if (op.data.length > link.mtu - 3) { complete(link, ERROR_MTU); return; }
                if (Build.VERSION.SDK_INT >= 33)
                    started = link.gatt.writeCharacteristic(op.target, op.data, BluetoothGattCharacteristic.WRITE_TYPE_NO_RESPONSE) == BluetoothStatusCodes.SUCCESS;
                else {
                    op.target.setWriteType(BluetoothGattCharacteristic.WRITE_TYPE_NO_RESPONSE);
                    op.target.setValue(op.data);
                    started = link.gatt.writeCharacteristic(op.target);
                }
            }
            if (!started) { complete(link, ERROR_OPERATION); return; }
            op.timeout = () -> {
                if (!current(link) || link.active != op) return;
                failOp(link, op, ERROR_TIMEOUT);
                // A late Android callback has no operation id. Closing is safer than advancing to
                // another write on the same characteristic and attributing the old callback to it.
                finish(link, ERROR_TIMEOUT, false);
            };
            worker.postDelayed(op.timeout, 3000);
        } catch (RuntimeException e) { log(2, "GATT operation: " + e); complete(link, ERROR_OPERATION); }
    }

    private void failOp(Link link, Operation op, int status) {
        log(1, "[" + link.handle + "] " + (op.subscription ? "subscribe" : "write") + " failed " + status);
        emit(op.subscription ? 6 : 8, link.handle, op.tag, status, link.address, 0, null);
    }

    private void complete(Link link, int status) {
        Operation op = link.active;
        if (op == null) return;
        if (op.timeout != null) worker.removeCallbacks(op.timeout);
        link.active = null;
        if (status != BluetoothGatt.GATT_SUCCESS) {
            if (op.subscription && op.target != null) {
                link.tags.remove(op.target.getUuid());
                try { link.gatt.setCharacteristicNotification(op.target, false); } catch (RuntimeException ignored) { }
            }
            failOp(link, op, status);
        } else if (op.subscription) emit(6, link.handle, op.tag, 0, link.address, 0, null);
        advance(link);
    }

    private static final class Operation {
        final boolean subscription;
        final String service, characteristic;
        final int tag;
        final byte[] data;
        BluetoothGattCharacteristic target;
        Runnable timeout;
        Operation(boolean sub, String s, String c, int t, byte[] d) {
            subscription = sub; service = s; characteristic = c; tag = t; data = d;
        }
    }

    private final class Link {
        final int handle;
        final long address;
        BluetoothGatt gatt;
        boolean ready, discovering, mtuPending;
        int mtu = 23;
        final ArrayDeque<Operation> operations = new ArrayDeque<>();
        final HashMap<UUID, Integer> tags = new HashMap<>();
        final HashSet<UUID> loggedNotifications = new HashSet<>();
        Operation active;
        Link(int h, long a) { handle = h; address = a; }
        boolean accepts(BluetoothGatt g) { return current(this) && gatt == g; }

        void notification(BluetoothGatt g, BluetoothGattCharacteristic characteristic, byte[] value) {
            if (value == null) return;
            byte[] copy = value.clone();
            UUID uuid = characteristic.getUuid();
            worker.post(() -> {
                if (!accepts(g)) return;
                Integer tag = tags.get(uuid);
                if (tag == null) return;
                if (loggedNotifications.add(uuid)) log(0, "[" + handle + "] first notify " + uuid + ": " + copy.length + " bytes");
                emit(7, handle, tag, 0, address, 0, copy);
            });
        }

        final BluetoothGattCallback callback = new BluetoothGattCallback() {
            @Override public void onConnectionStateChange(BluetoothGatt g, int status, int state) {
                worker.post(() -> {
                    if (!accepts(g)) return;
                    if (status != BluetoothGatt.GATT_SUCCESS || state == BluetoothProfile.STATE_DISCONNECTED) {
                        finish(Link.this, status, false); return;
                    }
                    if (state != BluetoothProfile.STATE_CONNECTED) return;
                    requestPriority(Link.this);
                    try {
                        mtuPending = g.requestMtu(517);
                        if (!mtuPending) { finish(Link.this, ERROR_MTU, false); return; }
                        worker.postDelayed(() -> {
                            if (accepts(g) && mtuPending) finish(Link.this, ERROR_MTU, false);
                        }, 4000);
                    } catch (RuntimeException e) { log(2, "MTU: " + e); finish(Link.this, ERROR_MTU, false); }
                });
            }
            @Override public void onMtuChanged(BluetoothGatt g, int negotiated, int status) {
                worker.post(() -> {
                    if (!accepts(g)) return;
                    mtuPending = false;
                    log(0, "[" + handle + "] MTU " + negotiated + " (status " + status + ")");
                    if (status != BluetoothGatt.GATT_SUCCESS || negotiated < minimumMtu) {
                        finish(Link.this, ERROR_MTU, false); return;
                    }
                    mtu = negotiated;
                    discover(Link.this);
                });
            }
            @Override public void onServicesDiscovered(BluetoothGatt g, int status) {
                worker.post(() -> {
                    if (!accepts(g) || ready) return;
                    if (status != BluetoothGatt.GATT_SUCCESS) { finish(Link.this, status, false); return; }
                    ready = true;
                    log(0, "[" + handle + "] GATT ready, MTU " + mtu + ", services " + g.getServices().size());
                    emit(3, handle, 0, mtu, address, 0, null);
                    // A second connection can alter the first one's scheduling. Reassert both.
                    worker.postDelayed(() -> { for (Link link : links.values()) if (link.ready) requestPriority(link); }, 1000);
                });
            }
            @Override public void onDescriptorWrite(BluetoothGatt g, BluetoothGattDescriptor descriptor, int status) {
                worker.post(() -> {
                    if (!accepts(g) || active == null || !active.subscription || active.target == null) return;
                    if (CCCD.equals(descriptor.getUuid()) && active.target.getUuid().equals(descriptor.getCharacteristic().getUuid()))
                        complete(Link.this, status);
                });
            }
            @Override public void onCharacteristicWrite(BluetoothGatt g, BluetoothGattCharacteristic characteristic, int status) {
                worker.post(() -> {
                    if (!accepts(g) || active == null || active.subscription || active.target == null) return;
                    if (active.target.getUuid().equals(characteristic.getUuid())) complete(Link.this, status);
                });
            }
            @Override public void onCharacteristicChanged(BluetoothGatt g, BluetoothGattCharacteristic characteristic, byte[] value) {
                notification(g, characteristic, value);
            }
            @Override @Deprecated public void onCharacteristicChanged(BluetoothGatt g, BluetoothGattCharacteristic characteristic) {
                notification(g, characteristic, characteristic.getValue());
            }
        };
    }
}
