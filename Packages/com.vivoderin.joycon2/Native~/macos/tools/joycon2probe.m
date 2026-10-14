// joycon2probe — smoke test for Joycon2Ble.bundle without Unity: loads it with dlopen (as Unity does), polls
// events on the main thread and prints them. Built as build/joycon2probe.app (TCC needs a real bundle);
// run build/joycon2probe.app/Contents/MacOS/joycon2probe directly to see the output.
//
//   joycon2probe [seconds] [company-hex]   scan and print advertisements (default: 10 s, Nintendo 0x0553;
//                                          try 004C to see nearby Apple devices)
//   joycon2probe --joycon [seconds]        connect the first Joy-Con 2 in SYNC mode, run the minimal
//                                          handshake (LED, feature mask 0x37, input), print the report rate.
//                                          The real handshake lives in C# (Joycon2Session); this is only a
//                                          transport check.

#import <Foundation/Foundation.h>

#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "joycon2ble.h"

#define SERVICE "ab7de9be-89fe-49ad-828f-118f09df7fd0"
#define INPUT "ab7de9be-89fe-49ad-828f-118f09df7fd2"
#define COMMAND "649d4ac9-8eb7-4e6c-af44-1ea54fe5f005"
#define RESPONSE "c765a961-d9d8-4d36-a20a-5315b111836a"

enum { TagResponse = 1, TagInput = 2 };

static int32_t (*p_version)(void);
static int32_t (*p_init)(void);
static void (*p_shutdown)(void);
static int32_t (*p_start_scan)(uint16_t);
static void (*p_stop_scan)(void);
static int32_t (*p_poll_event)(jc2_event*, uint8_t*, int32_t);
static int32_t (*p_connect)(uint64_t);
static void (*p_disconnect)(int32_t);
static int32_t (*p_subscribe)(int32_t, const char*, const char*, int32_t);
static int32_t (*p_write)(int32_t, const char*, const char*, const uint8_t*, int32_t);

static double Now(void) { return CFAbsoluteTimeGetCurrent(); }

static void Command(int32_t handle, uint8_t cmd, uint8_t sub, const uint8_t* payload, uint8_t length)
{
    uint8_t data[64] = { cmd, 0x91, 0x01, sub, 0x00, length, 0x00, 0x00 };
    memcpy(data + 8, payload, length);
    p_write(handle, SERVICE, COMMAND, data, 8 + length);
}

static void Led(int32_t handle, uint8_t pattern)
{
    uint8_t payload[8] = { pattern };
    Command(handle, 0x09, 0x07, payload, sizeof(payload));
}

static void Hex(const uint8_t* data, int length, int max)
{
    for (int i = 0; i < length && i < max; i++) printf("%02X ", data[i]);
    if (length > max) printf("…");
}

int main(int argc, char** argv)
{
    @autoreleasepool
    {
        BOOL joycon = argc > 1 && strcmp(argv[1], "--joycon") == 0;
        int argBase = joycon ? 2 : 1;
        double seconds = argc > argBase ? atof(argv[argBase]) : (joycon ? 30 : 10);
        uint16_t company = !joycon && argc > argBase + 1 ? (uint16_t)strtoul(argv[argBase + 1], NULL, 16) : 0x0553;

        // build/joycon2probe.app/Contents/MacOS/joycon2probe → build/Joycon2Ble.bundle
        NSString* dir = [NSBundle.mainBundle.bundlePath stringByDeletingLastPathComponent];
        NSString* path = [dir stringByAppendingPathComponent:@"Joycon2Ble.bundle/Contents/MacOS/Joycon2Ble"];
        void* lib = dlopen(path.UTF8String, RTLD_NOW | RTLD_LOCAL);
        if (!lib)
        {
            fprintf(stderr, "dlopen: %s\n", dlerror());
            return 1;
        }
#define LOAD(name) p_##name = dlsym(lib, "jc2_" #name); if (!p_##name) { fprintf(stderr, "missing jc2_" #name "\n"); return 1; }
        LOAD(version) LOAD(init) LOAD(shutdown) LOAD(start_scan) LOAD(stop_scan) LOAD(poll_event)
        LOAD(connect) LOAD(disconnect) LOAD(subscribe) LOAD(write)

        printf("version %d\n", p_version());
        double t = Now();
        int rc = p_init();
        printf("init %d (%.1f ms)\n", rc, (Now() - t) * 1000);
        if (rc != 0) return 1;
        p_start_scan(company);

        uint8_t payload[512];
        jc2_event e;
        int found = 0, reports = 0, totalReports = 0, responses = 0, lastLength = 0;
        int32_t handle = 0;
        BOOL ready = NO;
        double end = Now() + seconds, rateStart = Now(), lastKeepAlive = 0, readyAt = 0;
        uint8_t lastReport[64];

        while (Now() < end)
        {
            while (p_poll_event(&e, payload, sizeof(payload)))
            {
                int length = e.length < (int)sizeof(payload) ? e.length : (int)sizeof(payload);
                switch (e.type)
                {
                    case JC2_EVENT_LOG:
                        printf("  log[%d] %.*s\n", e.status, length, payload);
                        break;
                    case JC2_EVENT_DEVICE_FOUND:
                    {
                        found++;
                        BOOL isJoycon = length >= 7 && payload[3] == 0x7E && payload[4] == 0x05 &&
                                        (payload[5] == 0x66 || payload[5] == 0x67) && payload[6] == 0x20;
                        BOOL sync = length >= 16;
                        for (int i = 10; i < 16 && i < length; i++) sync &= payload[i] == 0;
                        if (!joycon || isJoycon)
                        {
                            printf("found #%llu rssi %d%s%s: ", e.address, e.rssi,
                                   isJoycon ? (payload[5] == 0x67 ? " Joy-Con 2 (L)" : " Joy-Con 2 (R)") : "",
                                   isJoycon ? (sync ? " SYNC" : " (not in SYNC)") : "");
                            Hex(payload, length, 24);
                            printf("\n");
                        }
                        if (joycon && isJoycon && sync && handle == 0)
                        {
                            p_stop_scan();
                            handle = p_connect(e.address);
                            t = Now();
                            printf("connect #%llu → handle %d\n", e.address, handle);
                        }
                        break;
                    }
                    case JC2_EVENT_CONNECTED:
                        printf("CONNECTED handle %d, MTU %d (%.0f ms)\n", e.handle, e.status, (Now() - t) * 1000);
                        p_subscribe(handle, SERVICE, RESPONSE, TagResponse);
                        break;
                    case JC2_EVENT_CONNECT_FAILED:
                        printf("CONNECT_FAILED handle %d status %d\n", e.handle, e.status);
                        handle = 0;
                        p_start_scan(company);
                        break;
                    case JC2_EVENT_DISCONNECTED:
                        printf("DISCONNECTED handle %d status %d after %.1f s ready\n", e.handle, e.status,
                               ready ? Now() - readyAt : 0);
                        handle = 0;
                        ready = NO;
                        break;
                    case JC2_EVENT_SUBSCRIBED:
                        printf("SUBSCRIBED tag %d status %d\n", e.tag, e.status);
                        if (e.tag == TagResponse)
                        {
                            uint8_t mask[4] = { 0x37 };
                            Led(handle, 0x01);
                            Command(handle, 0x0C, 0x02, mask, sizeof(mask));
                            Command(handle, 0x0C, 0x04, mask, sizeof(mask));
                            p_subscribe(handle, SERVICE, INPUT, TagInput);
                        }
                        else if (e.tag == TagInput && e.status == 0)
                        {
                            ready = YES;
                            readyAt = rateStart = lastKeepAlive = Now();
                            reports = 0;
                        }
                        break;
                    case JC2_EVENT_NOTIFICATION:
                        if (e.tag == TagResponse)
                        {
                            responses++;
                            printf("response: ");
                            Hex(payload, length, 16);
                            printf("\n");
                        }
                        else
                        {
                            reports++;
                            totalReports++;
                            lastLength = length;
                            memcpy(lastReport, payload, length < 64 ? length : 64);
                        }
                        break;
                    case JC2_EVENT_WRITE_FAILED:
                        printf("WRITE_FAILED handle %d status %d\n", e.handle, e.status);
                        break;
                    case JC2_EVENT_SCAN_STOPPED:
                        printf("SCAN_STOPPED status %d\n", e.status);
                        break;
                }
            }

            if (ready && Now() - lastKeepAlive >= 1.0)
            {
                Led(handle, 0x01); // keep-alive: macOS drops the link ~15 s after the host's last write
                lastKeepAlive = Now();
            }
            if (ready && Now() - rateStart >= 1.0)
            {
                printf("%.1f Hz, %d bytes, buttons %02X%02X%02X: ", reports / (Now() - rateStart), lastLength,
                       lastReport[6], lastReport[5], lastReport[4]);
                Hex(lastReport, lastLength, 20);
                printf("\n");
                reports = 0;
                rateStart = Now();
            }
            [NSThread sleepForTimeInterval:0.005];
        }

        if (handle > 0) p_disconnect(handle);
        t = Now();
        p_shutdown();
        printf("shutdown %.1f ms; %d advertisements, %d responses, %d reports\n", (Now() - t) * 1000, found, responses,
               totalReports);

        // Restart check: the transport must come back after a full shutdown.
        rc = p_init();
        p_shutdown();
        printf("re-init %d\n", rc);
        return 0;
    }
}
