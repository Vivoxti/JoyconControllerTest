// Joycon2Ble for macOS and iOS — CoreBluetooth implementation of Native/windows/include/joycon2ble.h.
// macOS: Native/macos/build.sh → Joycon2Ble.bundle; iOS: Native/ios/build.sh → libJoycon2Ble.a (same source).
//
// Design:
//  - One serial dispatch queue owns the CBCentralManager and every CBPeripheral: all CoreBluetooth calls
//    and delegate callbacks run on it, so the device state below needs no locks.
//  - Delegate callbacks only push into the event queue (lock-protected), drained by jc2_poll_event().
//  - Exported calls copy their arguments and hop onto the queue; only init/shutdown wait for it.
//  - CoreBluetooth hides Bluetooth addresses: an "address" is a process-local id given to each peripheral
//    (keyed by its per-Mac identifier) the first time it is seen.
//  - Nothing ever throws across the C ABI (CBUUID parsing is the only API that can).
//  - Report rate: the Joy-Con sends one input report per connection event and never asks for a shorter
//    connection interval, and CoreBluetooth has no public API for it, so macOS keeps its default (30 ms →
//    ~33 Hz). Private connect options fix that (see "Connection interval").

#import <CoreBluetooth/CoreBluetooth.h>
#import <Foundation/Foundation.h>

#include <objc/message.h>
#include <os/lock.h>
#include <stdarg.h>
#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "joycon2ble.h"

// ───────────────────────────── Event queue ─────────────────────────────

enum { kMaxEvents = 8192, kMaxQueuedWrites = 64 };

typedef struct
{
    jc2_event header;
    uint8_t* payload;
} Event;

static Event g_events[kMaxEvents];
static int g_eventHead, g_eventCount;
static os_unfair_lock g_eventLock = OS_UNFAIR_LOCK_INIT;

static void PushEvent(int32_t type, int32_t handle, int32_t tag, int32_t status, uint64_t address, int32_t rssi,
                      const void* data, size_t length)
{
    uint8_t* copy = NULL;
    if (data && length)
    {
        copy = malloc(length);
        if (!copy) return;
        memcpy(copy, data, length);
    }
    os_unfair_lock_lock(&g_eventLock);
    if (g_eventCount == kMaxEvents)
    {
        // Consumer stalled (e.g. paused in the debugger): drop the oldest.
        free(g_events[g_eventHead].payload);
        g_eventHead = (g_eventHead + 1) % kMaxEvents;
        g_eventCount--;
    }
    Event* e = &g_events[(g_eventHead + g_eventCount) % kMaxEvents];
    e->header = (jc2_event){ type, handle, tag, status, address, rssi, (int32_t)length };
    e->payload = copy;
    g_eventCount++;
    os_unfair_lock_unlock(&g_eventLock);
}

static void ClearEvents(void)
{
    os_unfair_lock_lock(&g_eventLock);
    for (; g_eventCount > 0; g_eventCount--)
    {
        free(g_events[g_eventHead].payload);
        g_eventHead = (g_eventHead + 1) % kMaxEvents;
    }
    g_eventHead = 0;
    os_unfair_lock_unlock(&g_eventLock);
}

static void Log(int level, const char* format, ...) __attribute__((format(printf, 2, 3)));
static void Log(int level, const char* format, ...)
{
    char text[512];
    va_list args;
    va_start(args, format);
    int n = vsnprintf(text, sizeof(text), format, args);
    va_end(args);
    if (n < 0) return;
    size_t length = (size_t)n < sizeof(text) - 1 ? (size_t)n : sizeof(text) - 1;
    PushEvent(JC2_EVENT_LOG, 0, 0, level, 0, 0, text, length);
}

#if TARGET_OS_IOS
#define JC2_PRIVACY_HINT "Settings → Privacy & Security → Bluetooth"
#else
#define JC2_PRIVACY_HINT "System Settings → Privacy & Security → Bluetooth"
#endif

static const char* Str(NSString* s) { return s ? s.UTF8String : "(null)"; }

static CBUUID* ParseUuid(const char* text)
{
    if (!text || !*text) return nil;
    @try
    {
        return [CBUUID UUIDWithString:[NSString stringWithUTF8String:text]];
    }
    @catch (NSException*)
    {
        return nil;
    }
}

static const char* StateName(CBManagerState state)
{
    switch (state)
    {
        case CBManagerStateUnknown: return "unknown";
        case CBManagerStateResetting: return "resetting";
        case CBManagerStateUnsupported: return "unsupported";
        case CBManagerStateUnauthorized: return "unauthorized";
        case CBManagerStatePoweredOff: return "powered off";
        case CBManagerStatePoweredOn: return "powered on";
    }
    return "?";
}

// ───────────────────────────── Connection interval (private SPI) ─────────────────────────────
//
// Private keys of -[CBCentralManager connectPeripheral:options:], read by bluetoothd (macOS 15) and used the same
// way by JamCon (MIT, github.com/jturnshek/JamCon), which measured a 15 ms link / ~66 Hz on a Joy-Con 2 with
// CI frames = 6. The frame values are opaque (not 1.25 ms units). They only apply to a new connection.
// Unknown keys are ignored by bluetoothd. Env for experiments: JC2_MAC_CI_FRAMES=<n> (0 = system default),
// JC2_MAC_DESIRED_LATENCY=0|1|2 also calls -[CBCentralManager setDesiredConnectionLatency:forPeripheral:].

static int EnvInt(const char* name, int fallback)
{
    const char* value = getenv(name);
    return value && *value ? (int)strtol(value, NULL, 10) : fallback;
}

static NSDictionary* ConnectOptions(void)
{
    int frames = EnvInt("JC2_MAC_CI_FRAMES", 6);
    if (frames <= 0) return nil;
    return @{
        @"kCBConnectOptionRequiresLowLatency" : @YES,
        @"kCBConnectOptionLatencyCritical" : @YES,
        @"kCBConnectOptionOverrideMinCIFrames" : @(frames),
        @"kCBConnectOptionOverrideMaxCIFrames" : @(frames),
    };
}

static void RequestDesiredLatency(CBCentralManager* central, CBPeripheral* peripheral, int32_t handle)
{
    NSInteger latency = EnvInt("JC2_MAC_DESIRED_LATENCY", -1);
    SEL selector = NSSelectorFromString(@"setDesiredConnectionLatency:forPeripheral:");
    if (latency < 0 || ![central respondsToSelector:selector]) return;
    ((void (*)(id, SEL, NSInteger, id))objc_msgSend)(central, selector, latency, peripheral);
    Log(0, "[%d] requested connection latency %ld", handle, (long)latency);
}

// ───────────────────────────── Device ─────────────────────────────

@interface JC2Device : NSObject
@property(nonatomic) int32_t handle;
@property(nonatomic) uint64_t address;
@property(nonatomic, strong) CBPeripheral* peripheral;
@property(nonatomic) BOOL connected;          // didConnect seen
@property(nonatomic) BOOL ready;              // services and characteristics discovered, CONNECTED sent
@property(nonatomic) int pendingServices;     // characteristic discoveries still running
@property(nonatomic) CFAbsoluteTime connectStart;
@property(nonatomic, strong) NSMutableArray<dispatch_block_t>* pendingOps;               // run once ready
@property(nonatomic, strong) NSMutableDictionary<CBUUID*, NSNumber*>* tags;              // characteristic → tag
@property(nonatomic, strong) NSMutableArray<NSArray*>* queuedWrites;                     // [characteristic, data]
@end

@implementation JC2Device
@end

// ───────────────────────────── State (queue-owned) ─────────────────────────────

@class JC2Central;

static dispatch_queue_t g_queue;
static CBCentralManager* g_central;
static JC2Central* g_delegate;
static BOOL g_wantScan, g_scanning;
static uint16_t g_scanCompany;
static NSMutableDictionary<NSUUID*, NSNumber*>* g_ids;               // survives shutdown: ids stay stable
static NSMutableDictionary<NSNumber*, CBPeripheral*>* g_peripherals; // id → last seen peripheral
static NSMutableDictionary<NSNumber*, NSNumber*>* g_lastReported;    // id → time of last DEVICE_FOUND
static NSMutableDictionary<NSNumber*, JC2Device*>* g_devices;        // handle → device
static uint64_t g_nextId;
static atomic_int g_nextHandle;
static atomic_bool g_initialized;

static JC2Device* DeviceFor(CBPeripheral* peripheral)
{
    for (JC2Device* d in g_devices.allValues)
        if (d.peripheral == peripheral) return d;
    return nil;
}

static uint64_t IdFor(CBPeripheral* peripheral, BOOL* isNew)
{
    NSNumber* known = g_ids[peripheral.identifier];
    *isNew = known == nil;
    if (!known)
    {
        known = @(++g_nextId);
        g_ids[peripheral.identifier] = known;
    }
    g_peripherals[known] = peripheral;
    return known.unsignedLongLongValue;
}

static void StartScanNow(void)
{
    [g_central scanForPeripheralsWithServices:nil options:@{ CBCentralManagerScanOptionAllowDuplicatesKey : @YES }];
    g_scanning = YES;
    Log(0, "scan started (company 0x%04X)", g_scanCompany);
}

static CBCharacteristic* FindCharacteristic(CBPeripheral* peripheral, CBUUID* service, CBUUID* characteristic)
{
    for (CBService* s in peripheral.services)
    {
        if (service && ![s.UUID isEqual:service]) continue;
        for (CBCharacteristic* c in s.characteristics)
            if ([c.UUID isEqual:characteristic]) return c;
    }
    return nil;
}

static void RunWhenReady(JC2Device* d, dispatch_block_t op)
{
    if (d.ready) op();
    else [d.pendingOps addObject:op];
}

static void Forget(JC2Device* d)
{
    d.peripheral.delegate = nil;
    [g_devices removeObjectForKey:@(d.handle)];
}

static void FailConnect(JC2Device* d, int32_t status, NSString* reason)
{
    Log(2, "[%d] connect #%llu failed: %s", d.handle, d.address, Str(reason));
    PushEvent(JC2_EVENT_CONNECT_FAILED, d.handle, 0, status ? status : -1, d.address, 0, NULL, 0);
    Forget(d);
    [g_central cancelPeripheralConnection:d.peripheral];
}

static void FlushWrites(JC2Device* d)
{
    while (d.queuedWrites.count && d.peripheral.canSendWriteWithoutResponse)
    {
        NSArray* w = d.queuedWrites.firstObject;
        [d.queuedWrites removeObjectAtIndex:0];
        [d.peripheral writeValue:w[1] forCharacteristic:w[0] type:CBCharacteristicWriteWithoutResponse];
    }
}

// ───────────────────────────── Delegate ─────────────────────────────

@interface JC2Central : NSObject <CBCentralManagerDelegate, CBPeripheralDelegate>
@property(nonatomic) BOOL alive; // false after shutdown: late callbacks already queued are ignored
@end

@implementation JC2Central

- (void)centralManagerDidUpdateState:(CBCentralManager*)central
{
    if (!self.alive) return;
    CBManagerState state = central.state;
    Log(state == CBManagerStatePoweredOn ? 0 : 1, "Bluetooth %s", StateName(state));
    if (state == CBManagerStateUnauthorized)
        Log(2, "Bluetooth access denied for %s: " JC2_PRIVACY_HINT,
            Str(NSProcessInfo.processInfo.processName));

    if (state == CBManagerStatePoweredOn)
    {
        if (g_wantScan && !g_scanning) StartScanNow();
        return;
    }
    if (state == CBManagerStateUnknown || state == CBManagerStateResetting) return;

    // Off / unauthorized / unsupported: CoreBluetooth drops every link without didDisconnect.
    if (g_scanning)
    {
        g_scanning = NO;
        PushEvent(JC2_EVENT_SCAN_STOPPED, 0, 0, (int32_t)state, 0, 0, NULL, 0);
    }
    for (JC2Device* d in g_devices.allValues)
    {
        if (d.connected) PushEvent(JC2_EVENT_DISCONNECTED, d.handle, 0, 0, d.address, 0, NULL, 0);
        else PushEvent(JC2_EVENT_CONNECT_FAILED, d.handle, 0, (int32_t)state, d.address, 0, NULL, 0);
        Forget(d);
    }
}

- (void)centralManager:(CBCentralManager*)central
    didDiscoverPeripheral:(CBPeripheral*)peripheral
        advertisementData:(NSDictionary<NSString*, id>*)advertisementData
                     RSSI:(NSNumber*)RSSI
{
    if (!self.alive || !g_scanning) return;
    NSData* data = advertisementData[CBAdvertisementDataManufacturerDataKey];
    if (data.length < 2) return;
    const uint8_t* bytes = data.bytes;
    if ((uint16_t)(bytes[0] | bytes[1] << 8) != g_scanCompany) return;

    BOOL isNew;
    uint64_t address = IdFor(peripheral, &isNew);
    if (isNew)
        Log(0, "device #%llu = %s \"%s\"", address, Str(peripheral.identifier.UUIDString), Str(peripheral.name));

    // Same throttle as on Windows: duplicates are on so a controller entering SYNC is seen at once.
    CFAbsoluteTime now = CFAbsoluteTimeGetCurrent();
    NSNumber* last = g_lastReported[@(address)];
    if (last && now - last.doubleValue < 0.25) return;
    g_lastReported[@(address)] = @(now);

    PushEvent(JC2_EVENT_DEVICE_FOUND, 0, 0, 0, address, RSSI.intValue, bytes + 2, data.length - 2);
}

- (void)centralManager:(CBCentralManager*)central didConnectPeripheral:(CBPeripheral*)peripheral
{
    if (!self.alive) return;
    JC2Device* d = DeviceFor(peripheral);
    if (!d)
    {
        [central cancelPeripheralConnection:peripheral];
        return;
    }
    d.connected = YES;
    Log(0, "[%d] link up after %.0f ms, discovering services", d.handle,
        (CFAbsoluteTimeGetCurrent() - d.connectStart) * 1000);
    RequestDesiredLatency(central, peripheral, d.handle);
    [peripheral discoverServices:nil];
}

- (void)centralManager:(CBCentralManager*)central
    didFailToConnectPeripheral:(CBPeripheral*)peripheral
                         error:(NSError*)error
{
    if (!self.alive) return;
    JC2Device* d = DeviceFor(peripheral);
    if (d) FailConnect(d, (int32_t)error.code, error.localizedDescription);
}

- (void)centralManager:(CBCentralManager*)central
    didDisconnectPeripheral:(CBPeripheral*)peripheral
                      error:(NSError*)error
{
    if (!self.alive) return;
    JC2Device* d = DeviceFor(peripheral);
    // Not connected yet: a stale callback from an earlier, cancelled link to the same peripheral.
    if (!d || !d.connected) return;
    Log(1, "[%d] disconnected: %s", d.handle, error ? Str(error.localizedDescription) : "no error");
    if (d.ready) PushEvent(JC2_EVENT_DISCONNECTED, d.handle, 0, (int32_t)error.code, d.address, 0, NULL, 0);
    else PushEvent(JC2_EVENT_CONNECT_FAILED, d.handle, 0, error ? (int32_t)error.code : -1, d.address, 0, NULL, 0);
    Forget(d);
}

// Private delegate method (CoreBluetooth calls it if implemented): the link's actual parameters, in BLE units —
// interval × 1.25 ms, supervision timeout × 10 ms. Arguments are the NSNumbers bluetoothd sent.
- (void)centralManager:(CBCentralManager*)central
    didUpdateConnectionParameters:(CBPeripheral*)peripheral
                         interval:(id)interval
                          latency:(id)latency
               supervisionTimeout:(id)timeout
{
    if (!self.alive) return;
    JC2Device* d = DeviceFor(peripheral);
    Class number = NSNumber.class;
    if (!d || ![interval isKindOfClass:number] || ![latency isKindOfClass:number] || ![timeout isKindOfClass:number]) return;
    double ms = [interval doubleValue] * 1.25;
    Log(0, "[%d] connection interval %.2f ms (~%.0f Hz), latency %d, timeout %d ms", d.handle, ms, 1000 / ms,
        [latency intValue], [timeout intValue] * 10);
}

- (void)peripheral:(CBPeripheral*)peripheral didDiscoverServices:(NSError*)error
{
    if (!self.alive) return;
    JC2Device* d = DeviceFor(peripheral);
    if (!d || d.ready) return;
    if (error || peripheral.services.count == 0)
    {
        FailConnect(d, (int32_t)error.code, error ? error.localizedDescription : @"no GATT services");
        return;
    }
    d.pendingServices = (int)peripheral.services.count;
    for (CBService* s in peripheral.services) [peripheral discoverCharacteristics:nil forService:s];
}

- (void)peripheral:(CBPeripheral*)peripheral
    didDiscoverCharacteristicsForService:(CBService*)service
                                   error:(NSError*)error
{
    if (!self.alive) return;
    JC2Device* d = DeviceFor(peripheral);
    if (!d || d.ready) return;
    if (error)
        Log(1, "[%d] characteristics of %s: %s", d.handle, Str(service.UUID.UUIDString), Str(error.localizedDescription));
    for (CBCharacteristic* c in service.characteristics)
        Log(0, "[%d] gatt %s / %s props 0x%02lX", d.handle, Str(service.UUID.UUIDString), Str(c.UUID.UUIDString),
            (unsigned long)c.properties);
    if (--d.pendingServices > 0) return;

    // ATT MTU = max write payload + 3-byte ATT header (Windows reports the same quantity).
    int32_t mtu = (int32_t)[peripheral maximumWriteValueLengthForType:CBCharacteristicWriteWithoutResponse] + 3;
    Log(0, "[%d] ready after %.0f ms, MTU %d", d.handle, (CFAbsoluteTimeGetCurrent() - d.connectStart) * 1000, mtu);
    d.ready = YES;
    PushEvent(JC2_EVENT_CONNECTED, d.handle, 0, mtu, d.address, 0, NULL, 0);

    NSArray<dispatch_block_t>* ops = [d.pendingOps copy];
    [d.pendingOps removeAllObjects];
    for (dispatch_block_t op in ops) op();
}

- (void)peripheral:(CBPeripheral*)peripheral
    didUpdateNotificationStateForCharacteristic:(CBCharacteristic*)characteristic
                                          error:(NSError*)error
{
    if (!self.alive) return;
    JC2Device* d = DeviceFor(peripheral);
    NSNumber* tag = d.tags[characteristic.UUID];
    if (!tag) return;
    if (error)
        Log(2, "[%d] enabling notifications on %s failed: %s", d.handle, Str(characteristic.UUID.UUIDString),
            Str(error.localizedDescription));
    PushEvent(JC2_EVENT_SUBSCRIBED, d.handle, tag.intValue, error ? (int32_t)(error.code ? error.code : -1) : 0,
              d.address, 0, NULL, 0);
}

- (void)peripheral:(CBPeripheral*)peripheral
    didUpdateValueForCharacteristic:(CBCharacteristic*)characteristic
                              error:(NSError*)error
{
    if (!self.alive || error) return;
    JC2Device* d = DeviceFor(peripheral);
    NSNumber* tag = d.tags[characteristic.UUID];
    if (!tag) return;
    NSData* value = characteristic.value;
    PushEvent(JC2_EVENT_NOTIFICATION, d.handle, tag.intValue, 0, 0, 0, value.bytes, value.length);
}

- (void)peripheral:(CBPeripheral*)peripheral
    didWriteValueForCharacteristic:(CBCharacteristic*)characteristic
                             error:(NSError*)error
{
    // Only writes with response (characteristics without WriteWithoutResponse) end up here.
    if (!self.alive || !error) return;
    JC2Device* d = DeviceFor(peripheral);
    if (d) PushEvent(JC2_EVENT_WRITE_FAILED, d.handle, 0, (int32_t)(error.code ? error.code : -1), d.address, 0, NULL, 0);
}

- (void)peripheralIsReadyToSendWriteWithoutResponse:(CBPeripheral*)peripheral
{
    if (!self.alive) return;
    JC2Device* d = DeviceFor(peripheral);
    if (d) FlushWrites(d);
}

@end

// ───────────────────────────── C ABI ─────────────────────────────

JC2_API int32_t jc2_version(void) { return JC2_VERSION; }

JC2_API int32_t jc2_init(void)
{
    // Without this key macOS and iOS kill the process on the first CoreBluetooth call (TCC). The Unity Editor has
    // it; a macOS player gets it from Player Settings → macOS → Bluetooth Usage Description, an iOS player from
    // KagefumiIOSBuild's post-process step.
    if (![NSBundle.mainBundle objectForInfoDictionaryKey:@"NSBluetoothAlwaysUsageDescription"])
        return JC2_ERROR_NO_USAGE_DESCRIPTION;

    static dispatch_once_t once;
    dispatch_once(&once, ^{
        g_queue = dispatch_queue_create("com.joycon2.ble", DISPATCH_QUEUE_SERIAL);
        g_ids = [NSMutableDictionary new];
    });
    dispatch_sync(g_queue, ^{
        if (g_central) return;
        g_peripherals = [NSMutableDictionary new];
        g_lastReported = [NSMutableDictionary new];
        g_devices = [NSMutableDictionary new];
        g_wantScan = g_scanning = NO;
        g_delegate = [JC2Central new];
        g_delegate.alive = YES;
        if (CBManager.authorization == CBManagerAuthorizationDenied)
            Log(2, "Bluetooth access denied for %s: " JC2_PRIVACY_HINT,
                Str(NSProcessInfo.processInfo.processName));
        g_central = [[CBCentralManager alloc] initWithDelegate:g_delegate
                                                         queue:g_queue
                                                       options:@{ CBCentralManagerOptionShowPowerAlertKey : @NO }];
        atomic_store(&g_initialized, true);
    });
    return 0;
}

JC2_API void jc2_shutdown(void)
{
    if (!atomic_exchange(&g_initialized, false)) return;
    dispatch_sync(g_queue, ^{
        g_delegate.alive = NO;
        if (g_scanning && g_central.state == CBManagerStatePoweredOn) [g_central stopScan];
        for (JC2Device* d in g_devices.allValues)
        {
            d.peripheral.delegate = nil;
            [g_central cancelPeripheralConnection:d.peripheral];
        }
        g_central.delegate = nil;
        g_central = nil;
        g_delegate = nil;
        g_devices = nil;
        g_peripherals = nil;
        g_lastReported = nil;
        g_wantScan = g_scanning = NO;
    });
    ClearEvents();
}

JC2_API int32_t jc2_start_scan(uint16_t company_id)
{
    if (!atomic_load(&g_initialized)) return -1;
    dispatch_async(g_queue, ^{
        if (!g_central) return;
        g_scanCompany = company_id;
        g_wantScan = YES;
        CBManagerState state = g_central.state;
        if (state == CBManagerStatePoweredOn) StartScanNow();
        // Still starting up: centralManagerDidUpdateState starts the scan.
        else if (state != CBManagerStateUnknown && state != CBManagerStateResetting)
            PushEvent(JC2_EVENT_SCAN_STOPPED, 0, 0, (int32_t)state, 0, 0, NULL, 0);
    });
    return 0;
}

JC2_API void jc2_stop_scan(void)
{
    if (!atomic_load(&g_initialized)) return;
    dispatch_async(g_queue, ^{
        if (!g_central) return;
        g_wantScan = NO;
        if (g_scanning && g_central.state == CBManagerStatePoweredOn) [g_central stopScan];
        if (g_scanning) Log(0, "scan stopped");
        g_scanning = NO;
    });
}

JC2_API int32_t jc2_poll_event(jc2_event* ev, uint8_t* payload, int32_t capacity)
{
    if (!ev) return 0;
    os_unfair_lock_lock(&g_eventLock);
    if (g_eventCount == 0)
    {
        os_unfair_lock_unlock(&g_eventLock);
        return 0;
    }
    Event e = g_events[g_eventHead];
    g_eventHead = (g_eventHead + 1) % kMaxEvents;
    g_eventCount--;
    os_unfair_lock_unlock(&g_eventLock);

    *ev = e.header;
    if (payload && capacity > 0 && e.payload)
        memcpy(payload, e.payload, (size_t)(e.header.length < capacity ? e.header.length : capacity));
    free(e.payload);
    return 1;
}

JC2_API int32_t jc2_connect(uint64_t address)
{
    if (!atomic_load(&g_initialized)) return -1;
    int32_t handle = atomic_fetch_add(&g_nextHandle, 1) + 1;
    dispatch_async(g_queue, ^{
        if (!g_central) return;
        CBPeripheral* peripheral = g_peripherals[@(address)];
        if (!peripheral)
        {
            Log(2, "[%d] connect: device #%llu was never seen by the scan", handle, address);
            PushEvent(JC2_EVENT_CONNECT_FAILED, handle, 0, -1, address, 0, NULL, 0);
            return;
        }
        JC2Device* d = [JC2Device new];
        d.handle = handle;
        d.address = address;
        d.peripheral = peripheral;
        d.connectStart = CFAbsoluteTimeGetCurrent();
        d.pendingOps = [NSMutableArray new];
        d.tags = [NSMutableDictionary new];
        d.queuedWrites = [NSMutableArray new];
        g_devices[@(handle)] = d;
        peripheral.delegate = g_delegate;
        NSDictionary* options = ConnectOptions();
        Log(0, "[%d] connecting #%llu%s", handle, address,
            options ? [NSString stringWithFormat:@" (low latency, CI frames %@)", options[@"kCBConnectOptionOverrideMinCIFrames"]].UTF8String : "");
        [g_central connectPeripheral:peripheral options:options];
    });
    return handle;
}

JC2_API void jc2_disconnect(int32_t handle)
{
    if (!atomic_load(&g_initialized)) return;
    dispatch_async(g_queue, ^{
        JC2Device* d = g_devices[@(handle)];
        if (!d) return;
        Forget(d);
        [g_central cancelPeripheralConnection:d.peripheral];
    });
}

JC2_API int32_t jc2_subscribe(int32_t handle, const char* service_uuid, const char* characteristic_uuid, int32_t tag)
{
    if (!atomic_load(&g_initialized)) return -1;
    CBUUID* service = ParseUuid(service_uuid);
    CBUUID* characteristic = ParseUuid(characteristic_uuid);
    if (!characteristic) return -2;
    dispatch_async(g_queue, ^{
        JC2Device* d = g_devices[@(handle)];
        if (!d) return;
        RunWhenReady(d, ^{
            CBCharacteristic* c = FindCharacteristic(d.peripheral, service, characteristic);
            if (!c)
            {
                Log(2, "[%d] subscribe: characteristic %s not found", d.handle, Str(characteristic.UUIDString));
                PushEvent(JC2_EVENT_SUBSCRIBED, d.handle, tag, -1, d.address, 0, NULL, 0);
                return;
            }
            d.tags[c.UUID] = @(tag);
            if (c.isNotifying) PushEvent(JC2_EVENT_SUBSCRIBED, d.handle, tag, 0, d.address, 0, NULL, 0);
            else [d.peripheral setNotifyValue:YES forCharacteristic:c];
        });
    });
    return 0;
}

JC2_API int32_t jc2_write(int32_t handle, const char* service_uuid, const char* characteristic_uuid,
                          const uint8_t* data, int32_t length)
{
    if (!atomic_load(&g_initialized)) return -1;
    CBUUID* service = ParseUuid(service_uuid);
    CBUUID* characteristic = ParseUuid(characteristic_uuid);
    if (!characteristic || length < 0 || (length > 0 && !data)) return -2;
    NSData* value = [NSData dataWithBytes:data length:(NSUInteger)length];
    dispatch_async(g_queue, ^{
        JC2Device* d = g_devices[@(handle)];
        if (!d) return;
        RunWhenReady(d, ^{
            CBCharacteristic* c = FindCharacteristic(d.peripheral, service, characteristic);
            if (!c)
            {
                Log(2, "[%d] write: characteristic %s not found", d.handle, Str(characteristic.UUIDString));
                PushEvent(JC2_EVENT_WRITE_FAILED, d.handle, 0, -1, d.address, 0, NULL, 0);
                return;
            }
            if (!(c.properties & CBCharacteristicPropertyWriteWithoutResponse))
            {
                [d.peripheral writeValue:value forCharacteristic:c type:CBCharacteristicWriteWithResponse];
                return;
            }
            // CoreBluetooth silently drops write-without-response while its buffer is full: queue and
            // flush from peripheralIsReadyToSendWriteWithoutResponse, keeping the call order.
            if (d.queuedWrites.count >= kMaxQueuedWrites)
            {
                [d.queuedWrites removeObjectAtIndex:0];
                PushEvent(JC2_EVENT_WRITE_FAILED, d.handle, 0, -2, d.address, 0, NULL, 0);
            }
            [d.queuedWrites addObject:@[ c, value ]];
            FlushWrites(d);
        });
    });
    return 0;
}
