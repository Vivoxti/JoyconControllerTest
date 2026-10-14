// Joycon2Ble — thin BLE GATT transport, consumed from Unity via P/Invoke. One C ABI, two implementations:
// Windows (C++/WinRT, src/joycon2ble.cpp) and macOS / iOS (CoreBluetooth, Native~/macos/src/joycon2ble.m).
//
// The library knows nothing about Nintendo: it scans for advertisements of a given company id, connects
// to a BLE address, subscribes to characteristics and writes bytes. All protocol logic lives in C#.
//
// Threading: every call is non-blocking. Bluetooth work runs on internal threads/queues;
// results are pushed into a single event queue that the caller drains with jc2_poll_event().
//
// Addresses: Bluetooth MACs on Windows. CoreBluetooth hides them, so on macOS an "address" is a
// process-local id (1, 2, …) given to each peripheral the first time it is seen.
#pragma once

#include <stdint.h>

#ifdef __cplusplus
#define JC2_EXTERN_C extern "C"
#else
#define JC2_EXTERN_C
#endif

#if defined(_WIN32)
#ifdef JC2_EXPORTS
#define JC2_API JC2_EXTERN_C __declspec(dllexport)
#else
#define JC2_API JC2_EXTERN_C __declspec(dllimport)
#endif
#else
#define JC2_API JC2_EXTERN_C __attribute__((visibility("default")))
#endif

#define JC2_VERSION 1

#define JC2_ERROR_NO_USAGE_DESCRIPTION (-2)

enum jc2_event_type
{
    JC2_EVENT_NONE = 0,
    JC2_EVENT_LOG = 1,            // status = level (0 info, 1 warning, 2 error); payload = UTF-8 text
    JC2_EVENT_DEVICE_FOUND = 2,   // address, rssi, tag = address type (0 public, 1 random; macOS: always 0);
                                  // payload = manufacturer data after the 2-byte company id
    JC2_EVENT_CONNECTED = 3,      // handle, address, status = max PDU size (ATT MTU)
    JC2_EVENT_CONNECT_FAILED = 4, // handle, address, status = HRESULT / GattCommunicationStatus (macOS: NSError code)
    JC2_EVENT_DISCONNECTED = 5,   // handle, address — link lost (not sent for jc2_disconnect)
    JC2_EVENT_SUBSCRIBED = 6,     // handle, tag, status (0 = ok)
    JC2_EVENT_NOTIFICATION = 7,   // handle, tag, payload = characteristic value
    JC2_EVENT_WRITE_FAILED = 8,   // handle, status
    JC2_EVENT_SCAN_STOPPED = 9,   // status = BluetoothError (macOS: CBManagerState); 0 = on request
};

// 32 bytes, natural alignment — mirrored by a [StructLayout(Sequential)] struct in C#.
typedef struct jc2_event
{
    int32_t type;
    int32_t handle;
    int32_t tag;
    int32_t status;
    uint64_t address;
    int32_t rssi;
    int32_t length; // full payload length; only min(length, capacity) bytes are copied
} jc2_event;

JC2_API int32_t jc2_version(void);

// Idempotent. Must be called before anything else; jc2_shutdown() + jc2_init() is a full restart.
// Returns 0, or (macOS) JC2_ERROR_NO_USAGE_DESCRIPTION: the app's Info.plist has no
// NSBluetoothAlwaysUsageDescription, and touching CoreBluetooth would make macOS kill the process.
JC2_API int32_t jc2_init(void);
// Stops scanning, drops every connection and clears the event queue.
JC2_API void jc2_shutdown(void);

// Starts a passive advertisement watcher that reports devices carrying manufacturer data with this id.
JC2_API int32_t jc2_start_scan(uint16_t company_id);
JC2_API void jc2_stop_scan(void);

// Returns 1 and fills *ev (+ payload) if an event was dequeued, 0 if the queue is empty.
JC2_API int32_t jc2_poll_event(jc2_event* ev, uint8_t* payload, int32_t capacity);

// Returns a handle > 0. Completion is reported with JC2_EVENT_CONNECTED / JC2_EVENT_CONNECT_FAILED.
JC2_API int32_t jc2_connect(uint64_t address);
JC2_API void jc2_disconnect(int32_t handle);

// UUIDs are strings like "ab7de9be-89fe-49ad-828f-118f09df7fd2". service_uuid may be null/empty:
// the characteristic is then looked up in every service of the device.
// Operations on one handle run strictly in call order.
JC2_API int32_t jc2_subscribe(int32_t handle, const char* service_uuid, const char* characteristic_uuid, int32_t tag);
JC2_API int32_t jc2_write(int32_t handle, const char* service_uuid, const char* characteristic_uuid,
                          const uint8_t* data, int32_t length);
