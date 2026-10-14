// joycon2boost (macOS) — lifts Joy-Con 2 connected by Chrome (Web Bluetooth) from ~33 to ~66 Hz. Agent app (no Dock
// icon), built as build/joycon2boost.app (TCC needs a real bundle). Findings: Documentation~/TEST_LOG.md.
// The game page starts it with joycon2boost://start (Info.plist registers the scheme once the app has been opened by
// hand); a second start reaches the running instance. It quits after IdleExit seconds without Joy-Con links.
//
// How: macOS fixes a link's connection interval when the link is created. Chrome connects without options → 30 ms.
// A link created while another app holds a link with the private low-latency connect options (the same ones as
// Joycon2Ble.bundle) gets 15 ms. Joining an existing link changes nothing. So this tool joins every Joy-Con link with
// those options — the first Joy-Con stays slow, the second one is created fast — and lets go of a slow link while it
// holds another one, so the page can rebuild it fast (Joycon2Manager.RefreshSlowLinks reconnects it). GATT stays
// Chrome's: the tool only subscribes to the input reports to measure the rate.
// Do not call -[CBCentralManager setDesiredConnectionLatency:forPeripheral:]: it cancels the interval override.
//
//   joycon2boost [seconds]   run for at most this long (default: until idle); logs joins, releases and rate changes
//                            to ~/Library/Logs/joycon2boost.log (stdout when started from a terminal)
//   joycon2boost --rates     also log every link's reports/s each second
//   env JC2_MAC_CI_FRAMES=<n> connect option frames (default 6 → 15 ms; 0 = no options, for comparison)

#import <AppKit/AppKit.h>
#import <CoreBluetooth/CoreBluetooth.h>
#import <Foundation/Foundation.h>

#include <stdio.h>
#include <unistd.h>
#include <stdlib.h>
#include <string.h>

#define SERVICE @"ab7de9be-89fe-49ad-828f-118f09df7fd0"
#define INPUT @"ab7de9be-89fe-49ad-828f-118f09df7fd2"

enum { SlowRate = 45, SlowSeconds = 3, IdleExit = 600 };

static int EnvInt(const char* name, int fallback)
{
    const char* value = getenv(name);
    return value && *value ? (int)strtol(value, NULL, 10) : fallback;
}

static void Log(NSString* format, ...)
{
    va_list args;
    va_start(args, format);
    NSString* line = [[NSString alloc] initWithFormat:format arguments:args];
    va_end(args);
    NSDateFormatter* f = [NSDateFormatter new];
    f.dateFormat = @"HH:mm:ss.SSS";
    printf("%s %s\n", [f stringFromDate:NSDate.date].UTF8String, line.UTF8String);
    fflush(stdout);
}

@interface Link : NSObject
@property(nonatomic, strong) CBPeripheral* peripheral;
@property(nonatomic) int reports;    // this second
@property(nonatomic) int rate;       // last full second
@property(nonatomic) int slowSeconds;
@end

@implementation Link
@end

@interface Booster : NSObject <CBCentralManagerDelegate, CBPeripheralDelegate>
@property(nonatomic, strong) CBCentralManager* central;
@property(nonatomic, strong) NSMutableDictionary<NSUUID*, Link*>* links;
/// Let go while slow: not joined again until the link is gone (then it is rebuilt by the page).
@property(nonatomic, strong) NSMutableSet<NSUUID*>* released;
@property(nonatomic) BOOL printRates;
@property(nonatomic) double idleSince;
@end

@implementation Booster

- (NSString*)nameOf:(CBPeripheral*)p
{
    return [NSString stringWithFormat:@"%@ %@", p.name ?: @"(no name)", [p.identifier.UUIDString substringToIndex:8]];
}

- (NSDictionary*)connectOptions
{
    int frames = EnvInt("JC2_MAC_CI_FRAMES", 6);
    return frames <= 0 ? nil : @{
        @"kCBConnectOptionRequiresLowLatency" : @YES,
        @"kCBConnectOptionLatencyCritical" : @YES,
        @"kCBConnectOptionOverrideMinCIFrames" : @(frames),
        @"kCBConnectOptionOverrideMaxCIFrames" : @(frames),
    };
}

- (void)centralManagerDidUpdateState:(CBCentralManager*)central
{
    Log(@"Bluetooth state %ld%s", (long)central.state, central.state == CBManagerStatePoweredOn ? " (powered on)" : "");
}

// Every 0.1 s: join new links quickly (the sooner a low-latency client exists, the sooner new links come out fast).
- (void)poll
{
    if (self.central.state != CBManagerStatePoweredOn) return;
    NSArray<CBPeripheral*>* connected =
        [self.central retrieveConnectedPeripheralsWithServices:@[ [CBUUID UUIDWithString:SERVICE] ]];
    NSMutableSet<NSUUID*>* present = [NSMutableSet new];
    for (CBPeripheral* p in connected) [present addObject:p.identifier];
    for (NSUUID* id in self.released.allObjects)
        if (![present containsObject:id])
        {
            [self.released removeObject:id];
            Log(@"released link %@ is gone: will join the new one", [id.UUIDString substringToIndex:8]);
        }

    for (CBPeripheral* p in connected)
    {
        if (self.links[p.identifier] || [self.released containsObject:p.identifier]) continue;
        Link* link = [Link new];
        link.peripheral = p;
        self.links[p.identifier] = link;
        p.delegate = self;
        Log(@"found %@ → joining its link", [self nameOf:p]);
        [self.central connectPeripheral:p options:self.connectOptions];
    }
}

// Every second: rates, and let go of a slow link while holding another one.
- (void)tick
{
    double now = CFAbsoluteTimeGetCurrent();
    if (self.links.count > 0 || self.released.count > 0) self.idleSince = now;
    else if (now - self.idleSince > IdleExit)
    {
        Log(@"no Joy-Con 2 for %d s, quitting", IdleExit);
        [NSApp terminate:nil];
    }

    for (Link* link in self.links.allValues)
    {
        int previous = link.rate;
        link.rate = link.reports;
        link.reports = 0;
        if (self.printRates || abs(link.rate - previous) > 15)
            Log(@"%@: %d reports/s", [self nameOf:link.peripheral], link.rate);
        link.slowSeconds = link.rate > 0 && link.rate < SlowRate ? link.slowSeconds + 1 : 0;
    }
    for (Link* link in self.links.allValues)
    {
        if (link.slowSeconds < SlowSeconds || self.links.count < 2) continue;
        CBPeripheral* p = link.peripheral;
        Log(@"%@ stays at %d reports/s: letting go so the page can rebuild it fast", [self nameOf:p], link.rate);
        [self.links removeObjectForKey:p.identifier];
        [self.released addObject:p.identifier];
        [self.central cancelPeripheralConnection:p];
    }
}

- (void)centralManager:(CBCentralManager*)central didConnectPeripheral:(CBPeripheral*)p
{
    Log(@"joined %@", [self nameOf:p]);
    [p discoverServices:@[ [CBUUID UUIDWithString:SERVICE] ]];
}

- (void)centralManager:(CBCentralManager*)central didFailToConnectPeripheral:(CBPeripheral*)p error:(NSError*)error
{
    Log(@"join failed %@: %@", [self nameOf:p], error.localizedDescription);
    [self.links removeObjectForKey:p.identifier];
}

- (void)centralManager:(CBCentralManager*)central didDisconnectPeripheral:(CBPeripheral*)p error:(NSError*)error
{
    if (!self.links[p.identifier]) return; // released on purpose
    Log(@"link gone %@: %@", [self nameOf:p], error.localizedDescription ?: @"no error");
    [self.links removeObjectForKey:p.identifier];
}

- (void)peripheral:(CBPeripheral*)p didDiscoverServices:(NSError*)error
{
    for (CBService* s in p.services)
        if ([s.UUID isEqual:[CBUUID UUIDWithString:SERVICE]])
            [p discoverCharacteristics:@[ [CBUUID UUIDWithString:INPUT] ] forService:s];
}

- (void)peripheral:(CBPeripheral*)p didDiscoverCharacteristicsForService:(CBService*)s error:(NSError*)error
{
    for (CBCharacteristic* c in s.characteristics)
        if ([c.UUID isEqual:[CBUUID UUIDWithString:INPUT]]) [p setNotifyValue:YES forCharacteristic:c];
}

- (void)peripheral:(CBPeripheral*)p didUpdateValueForCharacteristic:(CBCharacteristic*)c error:(NSError*)error
{
    self.links[p.identifier].reports++;
}

// joycon2boost://… from the browser: LaunchServices started us (or we already run); nothing else to do.
- (void)handleURL:(NSAppleEventDescriptor*)event withReplyEvent:(NSAppleEventDescriptor*)reply
{
    (void)reply;
    Log(@"started from %@", [event paramDescriptorForKeyword:keyDirectObject].stringValue);
    self.idleSince = CFAbsoluteTimeGetCurrent();
}

@end

int main(int argc, char** argv)
{
    @autoreleasepool
    {
        if (!isatty(STDOUT_FILENO))
        {
            NSString* log = [NSHomeDirectory() stringByAppendingPathComponent:@"Library/Logs/joycon2boost.log"];
            freopen(log.fileSystemRepresentation, "a", stdout);
        }
        [NSApplication sharedApplication];
        [NSApp setActivationPolicy:NSApplicationActivationPolicyAccessory];

        Booster* booster = [Booster new];
        booster.idleSince = CFAbsoluteTimeGetCurrent();
        booster.links = [NSMutableDictionary new];
        booster.released = [NSMutableSet new];
        double seconds = 0;
        for (int i = 1; i < argc; i++)
        {
            if (strcmp(argv[i], "--rates") == 0) booster.printRates = YES;
            else seconds = atof(argv[i]);
        }

        [NSAppleEventManager.sharedAppleEventManager setEventHandler:booster
                                                         andSelector:@selector(handleURL:withReplyEvent:)
                                                       forEventClass:kInternetEventClass
                                                          andEventID:kAEGetURL];
        booster.central = [[CBCentralManager alloc] initWithDelegate:booster queue:dispatch_get_main_queue()];
        [NSTimer scheduledTimerWithTimeInterval:0.1 repeats:YES block:^(NSTimer* t) {
            (void)t;
            [booster poll];
        }];
        [NSTimer scheduledTimerWithTimeInterval:1 repeats:YES block:^(NSTimer* t) {
            (void)t;
            [booster tick];
        }];
        Log(@"joycon2boost (pid %d): joining Joy-Con 2 links of other apps (Chrome) for a fast interval", getpid());
        if (seconds > 0)
            [NSTimer scheduledTimerWithTimeInterval:seconds repeats:NO block:^(NSTimer* t) {
                (void)t;
                [NSApp terminate:nil];
            }];
        // Quitting drops our side of the links; bluetoothd keeps them up for Chrome.
        [NSApp run];
    }
    return 0;
}
