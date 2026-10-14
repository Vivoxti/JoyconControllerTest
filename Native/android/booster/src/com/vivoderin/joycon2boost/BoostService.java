package com.vivoderin.joycon2boost;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.bluetooth.BluetoothDevice;
import android.bluetooth.BluetoothGatt;
import android.bluetooth.BluetoothGattCallback;
import android.bluetooth.BluetoothManager;
import android.bluetooth.BluetoothProfile;
import android.content.Intent;
import android.os.Handler;
import android.os.IBinder;
import android.util.Log;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Arrays;
import java.io.File;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;

/** Attaches to already connected Joy-Con links and uses public HIGH priority. No scanning, GATT writes or subscriptions. */
public final class BoostService extends Service {
    public static volatile String status = "Stopped";
    private static final String TAG = "Joycon2Boost";
    private static final String STOP = "com.vivoderin.joycon2boost.STOP";
    private final Handler handler = new Handler();
    private final Map<String, Link> links = new HashMap<>();
    private final Map<String, Long> retryAfter = new HashMap<>();
    private final Map<String, Long> unsupportedUntil = new HashMap<>();
    private BluetoothManager manager;
    private boolean running;
    private long started;
    private String error = "";
    private String seen = "No device list yet";

    private final Runnable tick = new Runnable() {
        @Override public void run() {
            if (!running) return;
            // An experimental helper must not silently hold controllers indefinitely.
            if (android.os.SystemClock.elapsedRealtime() - started > 30 * 60 * 1000L) { stopSelf(); return; }
            try {
                List<BluetoothDevice> devices = manager.getConnectedDevices(BluetoothProfile.GATT);
                List<String> configured = Arrays.asList(getSharedPreferences("boost", MODE_PRIVATE).getString("controllers", "").split(","));
                String service = getSharedPreferences("boost", MODE_PRIVATE).getString("service", "");
                StringBuilder found = new StringBuilder("Android lists ").append(devices.size()).append(" BLE devices: ");
                long now = android.os.SystemClock.elapsedRealtime();
                for (BluetoothDevice device : devices) {
                    String name = device.getName();
                    String address = device.getAddress();
                    found.append(name == null ? "unnamed" : name).append(" [").append(address).append("] ");
                    boolean named = "Joy-Con 2 (L)".equals(name) || "Joy-Con 2 (R)".equals(name);
                    if (!named && !configured.contains(address) && service.isEmpty()) continue;
                    if (name == null || name.isEmpty()) name = "Joy-Con 2 " + address;
                    Long cooldown = retryAfter.get(address);
                    Long unsupported = unsupportedUntil.get(address);
                    if (links.containsKey(address) || (cooldown != null && now < cooldown) || (unsupported != null && now < unsupported)) continue;
                    Link link = new Link(device, name, now, service.isEmpty() ? null : java.util.UUID.fromString(service));
                    links.put(address, link);
                    link.gatt = device.connectGatt(BoostService.this, false, link.callback, BluetoothDevice.TRANSPORT_LE);
                    if (link.gatt == null) drop(link, "could not attach");
                }
                seen = found.toString();
                for (Link link : new ArrayList<>(links.values())) {
                    if (!link.verified && now - link.created > 20000) drop(link, "attach/discovery timed out");
                    else if (link.connected && now - link.lastRequest >= 10000) requestHigh(link);
                }
                updateStatus();
            } catch (RuntimeException failure) { error = failure.toString(); Log.w(TAG, error); updateStatus(); }
            handler.postDelayed(this, 2000);
        }
    };

    @Override public void onCreate() {
        super.onCreate();
        manager = getSystemService(BluetoothManager.class);
        NotificationManager notifications = getSystemService(NotificationManager.class);
        notifications.createNotificationChannel(new NotificationChannel("boost", "Joy-Con BLE boost", NotificationManager.IMPORTANCE_LOW));
        PendingIntent open = PendingIntent.getActivity(this, 0, new Intent(this, BoostActivity.class), PendingIntent.FLAG_IMMUTABLE);
        PendingIntent stop = PendingIntent.getService(this, 1, new Intent(this, BoostService.class).setAction(STOP), PendingIntent.FLAG_IMMUTABLE);
        Notification notification = new Notification.Builder(this, "boost")
            .setSmallIcon(android.R.drawable.ic_dialog_info).setContentTitle("Joy-Con 2 Boost is running")
            .setContentText("Connect in Chrome; check Hz in game Details").setContentIntent(open).setOngoing(true)
            .addAction(new Notification.Action.Builder(null, "Stop", stop).build()).build();
        startForeground(1, notification);
    }
    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent != null && STOP.equals(intent.getAction())) { stopSelf(); return START_NOT_STICKY; }
        if (!running) { running = true; started = android.os.SystemClock.elapsedRealtime(); handler.post(tick); }
        return START_NOT_STICKY;
    }
    @Override public IBinder onBind(Intent intent) { return null; }
    @Override public void onDestroy() {
        running = false; handler.removeCallbacksAndMessages(null);
        for (Link link : new ArrayList<>(links.values())) close(link);
        links.clear(); status = "Stopped"; saveStatus(); super.onDestroy();
    }
    private boolean current(Link link) { return running && links.get(link.device.getAddress()) == link; }
    private void requestHigh(Link link) {
        if (!current(link) || !link.connected || !link.verified || link.gatt == null) return;
        try {
            link.lastRequest = android.os.SystemClock.elapsedRealtime();
            link.accepted = link.gatt.requestConnectionPriority(BluetoothGatt.CONNECTION_PRIORITY_HIGH);
            Log.i(TAG, link.name + " HIGH request accepted=" + link.accepted);
        } catch (RuntimeException failure) { error = failure.toString(); Log.w(TAG, error); }
    }
    private void close(Link link) {
        // close unregisters this app's client. Chrome retains its own client on the same physical link.
        if (link.gatt != null) { try { link.gatt.close(); } catch (RuntimeException ignored) {} }
    }
    private void drop(Link link, String reason) {
        if (links.get(link.device.getAddress()) != link) return;
        links.remove(link.device.getAddress()); retryAfter.put(link.device.getAddress(), android.os.SystemClock.elapsedRealtime() + 10000);
        close(link); error = link.name + ": " + reason; Log.w(TAG, error);
    }
    private void updateStatus() {
        StringBuilder text = new StringBuilder("Running. Waiting for Joy-Con connections from Chrome.\n");
        text.append(seen).append('\n');
        for (Link link : links.values()) text.append(link.name).append(link.verified ? " · attached · HIGH request " + (link.accepted ? "accepted" : "not accepted") : " · checking device").append('\n');
        if (!error.isEmpty()) text.append(error).append('\n');
        text.append("Request accepted is not a frequency measurement. Compare Hz in the game."); status = text.toString(); saveStatus();
    }
    private void saveStatus() {
        File directory = getExternalFilesDir(null);
        if (directory == null) return;
        try (FileOutputStream stream = new FileOutputStream(new File(directory, "status.txt"))) {
            stream.write(status.getBytes(StandardCharsets.UTF_8));
        } catch (java.io.IOException failure) { Log.w(TAG, "Status export: " + failure); }
    }
    private final class Link {
        final BluetoothDevice device; final String name; final long created;
        final java.util.UUID service;
        BluetoothGatt gatt; boolean connected, verified, accepted; long lastRequest;
        Link(BluetoothDevice d, String n, long time, java.util.UUID target) { device = d; name = n; created = time; service = target; }
        final BluetoothGattCallback callback = new BluetoothGattCallback() {
            @Override public void onConnectionStateChange(BluetoothGatt g, int result, int state) {
                handler.post(() -> {
                    if (!current(Link.this) || gatt != g) return;
                    if (result != BluetoothGatt.GATT_SUCCESS || state == BluetoothProfile.STATE_DISCONNECTED) {
                        drop(Link.this, "link ended, status " + result); updateStatus(); return;
                    }
                    if (state == BluetoothProfile.STATE_CONNECTED) {
                        connected = true; error = "";
                        if (service == null) verified = true;
                        else if (!g.discoverServices()) { drop(Link.this, "discovery busy, will retry"); updateStatus(); return; }
                        for (Link link : links.values()) requestHigh(link);
                        updateStatus();
                    }
                });
            }
            @Override public void onServicesDiscovered(BluetoothGatt g, int result) {
                handler.post(() -> {
                    if (!current(Link.this) || gatt != g) return;
                    if (result != BluetoothGatt.GATT_SUCCESS) { drop(Link.this, "discovery failed " + result); updateStatus(); return; }
                    if (g.getService(service) == null) {
                        unsupportedUntil.put(device.getAddress(), android.os.SystemClock.elapsedRealtime() + 300000);
                        links.remove(device.getAddress()); close(Link.this); updateStatus(); return;
                    }
                    verified = true; Log.i(TAG, name + " target service verified");
                    for (Link link : links.values()) requestHigh(link);
                    updateStatus();
                });
            }
        };
    }
}
