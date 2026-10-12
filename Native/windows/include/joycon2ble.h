// Joycon2Ble — thin BLE GATT transport for Windows (C++/WinRT), consumed from Unity via P/Invoke.
//
// The DLL knows nothing about Nintendo: it scans for advertisements of a given company id, connects
// to a BLE address, subscribes to characteristics and writes bytes. All protocol logic lives in C#.
//
// Threading: every call is non-blocking. WinRT work runs on internal threads (one per device);
// results are pushed into a single event queue that the caller drains with jc2_poll_event().
#pragma once

#include <stdint.h>

#ifdef JC2_EXPORTS
#define JC2_API extern "C" __declspec(dllexport)
#else
#define JC2_API extern "C" __declspec(dllimport)
#endif

#define JC2_VERSION 1

enum jc2_event_type
{
    JC2_EVENT_NONE = 0,
    JC2_EVENT_LOG = 1,            // status = level (0 info, 1 warning, 2 error); payload = UTF-8 text
    JC2_EVENT_DEVICE_FOUND = 2,   // address, rssi, tag = address type (0 public, 1 random);
                                  // payload = manufacturer data after the 2-byte company id
    JC2_EVENT_CONNECTED = 3,      // handle, address, status = max PDU size (ATT MTU)
    JC2_EVENT_CONNECT_FAILED = 4, // handle, address, status = HRESULT / GattCommunicationStatus
    JC2_EVENT_DISCONNECTED = 5,   // handle, address — link lost (not sent for jc2_disconnect)
    JC2_EVENT_SUBSCRIBED = 6,     // handle, tag, status (0 = ok)
    JC2_EVENT_NOTIFICATION = 7,   // handle, tag, payload = characteristic value
    JC2_EVENT_WRITE_FAILED = 8,   // handle, status
    JC2_EVENT_SCAN_STOPPED = 9,   // status = BluetoothError (0 = stopped on request)
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
