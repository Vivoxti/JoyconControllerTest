// Joycon2Ble — see include/joycon2ble.h for the contract.
//
// Design:
//  - One "control" worker thread owns the advertisement watcher.
//  - One worker thread per device runs connect / subscribe / write / close strictly in order,
//    blocking on WinRT async operations (allowed: the workers are MTA threads we own).
//  - WinRT callbacks (advertisements, notifications, connection status) only push into the event queue.
//  - Nothing ever throws across the C ABI.

#include "joycon2ble.h"

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Foundation.Metadata.h>
#include <winrt/Windows.Devices.Bluetooth.h>
#include <winrt/Windows.Devices.Bluetooth.Advertisement.h>
#include <winrt/Windows.Devices.Bluetooth.GenericAttributeProfile.h>
#include <winrt/Windows.Storage.Streams.h>

#include <algorithm>
#include <atomic>
#include <cstdio>
#include <chrono>
#include <condition_variable>
#include <cstdarg>
#include <cstring>
#include <deque>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

using namespace winrt;
using namespace winrt::Windows::Devices::Bluetooth;
using namespace winrt::Windows::Devices::Bluetooth::Advertisement;
using namespace winrt::Windows::Devices::Bluetooth::GenericAttributeProfile;
using namespace winrt::Windows::Storage::Streams;

namespace
{
    // ───────────────────────────── Event queue ─────────────────────────────

    struct Event
    {
        jc2_event header{};
        std::vector<uint8_t> payload;
    };

    class EventQueue
    {
    public:
        void Push(Event&& e)
        {
            std::lock_guard lock(mutex_);
            if (queue_.size() >= kMaxEvents)
                queue_.pop_front(); // consumer stalled (e.g. paused in the debugger): drop the oldest
            queue_.push_back(std::move(e));
        }

        bool Pop(Event& out)
        {
            std::lock_guard lock(mutex_);
            if (queue_.empty()) return false;
            out = std::move(queue_.front());
            queue_.pop_front();
            return true;
        }

        void Clear()
        {
            std::lock_guard lock(mutex_);
            queue_.clear();
        }

    private:
        static constexpr size_t kMaxEvents = 8192;
        std::mutex mutex_;
        std::deque<Event> queue_;
    };

    EventQueue g_events;

    void PushEvent(int32_t type, int32_t handle, int32_t tag, int32_t status, uint64_t address = 0,
                   int32_t rssi = 0, const uint8_t* data = nullptr, size_t length = 0)
    {
        Event e;
        e.header.type = type;
        e.header.handle = handle;
        e.header.tag = tag;
        e.header.status = status;
        e.header.address = address;
        e.header.rssi = rssi;
        e.header.length = static_cast<int32_t>(length);
        if (data && length) e.payload.assign(data, data + length);
        g_events.Push(std::move(e));
    }

    void Log(int level, const char* format, ...)
    {
        char text[512];
        va_list args;
        va_start(args, format);
        int n = vsnprintf(text, sizeof(text), format, args);
        va_end(args);
        if (n < 0) return;
        size_t length = std::min(static_cast<size_t>(n), sizeof(text) - 1);
        PushEvent(JC2_EVENT_LOG, 0, 0, level, 0, 0, reinterpret_cast<const uint8_t*>(text), length);
    }

    std::string Narrow(const hstring& s) { return to_string(s); }

    // ───────────────────────────── Worker thread ─────────────────────────────

    // A thread with a FIFO of jobs. State is shared so a worker stuck in a long WinRT call can be
    // detached on shutdown without dangling.
    class Worker
    {
    public:
        explicit Worker(const char* name) : state_(std::make_shared<State>())
        {
            auto state = state_;
            std::string label = name;
            thread_ = std::thread([state, label] { Run(*state, label); });
        }

        ~Worker() { StopAndJoin(std::chrono::milliseconds(2000)); }

        void Post(std::function<void()> job)
        {
            {
                std::lock_guard lock(state_->mutex);
                if (state_->stopping) return;
                state_->jobs.push_back(std::move(job));
            }
            state_->cv.notify_all();
        }

        // Lets queued jobs finish, then exits. Detaches if that takes longer than the timeout.
        void StopAndJoin(std::chrono::milliseconds timeout)
        {
            if (!thread_.joinable()) return;
            std::unique_lock lock(state_->mutex);
            state_->stopping = true;
            state_->cv.notify_all();
            if (std::this_thread::get_id() == thread_.get_id())
            {
                // Last reference dropped by one of our own jobs: the loop exits after it returns.
                lock.unlock();
                thread_.detach();
                return;
            }
            bool finished = state_->cv.wait_for(lock, timeout, [&] { return state_->finished; });
            lock.unlock();
            if (finished) thread_.join();
            else thread_.detach();
        }

    private:
        struct State
        {
            std::mutex mutex;
            std::condition_variable cv;
            std::deque<std::function<void()>> jobs;
            bool stopping = false;
            bool finished = false;
        };

        static void Run(State& state, const std::string& label)
        {
            init_apartment(apartment_type::multi_threaded);
            for (;;)
            {
                std::function<void()> job;
                {
                    std::unique_lock lock(state.mutex);
                    state.cv.wait(lock, [&] { return state.stopping || !state.jobs.empty(); });
                    if (state.jobs.empty()) break; // stopping and drained
                    job = std::move(state.jobs.front());
                    state.jobs.pop_front();
                }
                try
                {
                    job();
                }
                catch (const hresult_error& e)
                {
                    Log(2, "[%s] unhandled WinRT error 0x%08X: %s", label.c_str(),
                        static_cast<uint32_t>(e.code().value), Narrow(e.message()).c_str());
                }
                catch (const std::exception& e)
                {
                    Log(2, "[%s] unhandled exception: %s", label.c_str(), e.what());
                }
                catch (...)
                {
                    Log(2, "[%s] unhandled unknown exception", label.c_str());
                }
            }
            uninit_apartment();
            {
                std::lock_guard lock(state.mutex);
                state.finished = true;
            }
            state.cv.notify_all();
        }

        std::shared_ptr<State> state_;
        std::thread thread_;
    };

    // ───────────────────────────── Helpers ─────────────────────────────

    bool ParseGuid(const char* text, guid& out)
    {
        if (!text || !*text) return false;
        if (*text == '{') text++;
        unsigned int d1 = 0, d2 = 0, d3 = 0, b[8] = {};
        int n = sscanf_s(text, "%8x-%4x-%4x-%2x%2x-%2x%2x%2x%2x%2x%2x", &d1, &d2, &d3, &b[0], &b[1], &b[2], &b[3],
                         &b[4], &b[5], &b[6], &b[7]);
        if (n != 11) return false;
        out.Data1 = d1;
        out.Data2 = static_cast<uint16_t>(d2);
        out.Data3 = static_cast<uint16_t>(d3);
        for (int i = 0; i < 8; i++) out.Data4[i] = static_cast<uint8_t>(b[i]);
        return true;
    }

    std::string GuidToString(const guid& g)
    {
        char text[40];
        snprintf(text, sizeof(text), "%08x-%04x-%04x-%02x%02x-%02x%02x%02x%02x%02x%02x", g.Data1, g.Data2,
                 g.Data3, g.Data4[0], g.Data4[1], g.Data4[2], g.Data4[3], g.Data4[4], g.Data4[5], g.Data4[6],
                 g.Data4[7]);
        return text;
    }

    struct GuidHash
    {
        size_t operator()(const guid& g) const noexcept
        {
            const uint64_t* p = reinterpret_cast<const uint64_t*>(&g);
            return std::hash<uint64_t>{}(p[0] ^ (p[1] * 0x9E3779B97F4A7C15ull));
        }
    };

    // ───────────────────────────── Device ─────────────────────────────

    // Address types seen in advertisements, so connect can use the exact type.
    std::mutex g_addressMutex;
    std::unordered_map<uint64_t, BluetoothAddressType> g_addressTypes;

    class Device : public std::enable_shared_from_this<Device>
    {
    public:
        Device(int32_t handle, uint64_t address) : handle_(handle), address_(address), worker_("device") {}

        ~Device() { worker_.StopAndJoin(std::chrono::milliseconds(1500)); }

        int32_t Handle() const { return handle_; }

        void Post(std::function<void(Device&)> job)
        {
            auto self = shared_from_this();
            worker_.Post([self, job = std::move(job)] {
                if (!self->closed_) job(*self);
            });
        }

        // Runs Close() on the worker after already-queued jobs, then lets the thread exit.
        void Shutdown(std::chrono::milliseconds timeout)
        {
            auto self = shared_from_this();
            worker_.Post([self] { self->Close(); });
            worker_.StopAndJoin(timeout);
        }

        void Connect()
        {
            BluetoothAddressType type = BluetoothAddressType::Unspecified;
            {
                std::lock_guard lock(g_addressMutex);
                auto it = g_addressTypes.find(address_);
                if (it != g_addressTypes.end()) type = it->second;
            }

            try
            {
                device_ = type == BluetoothAddressType::Unspecified
                              ? BluetoothLEDevice::FromBluetoothAddressAsync(address_).get()
                              : BluetoothLEDevice::FromBluetoothAddressAsync(address_, type).get();
                if (!device_)
                {
                    Fail(static_cast<int32_t>(0x80070490) /* E_NOT_FOUND */, "device not found");
                    return;
                }

                // Keep the link up while we hold the session — the Joy-Con is never OS-paired.
                session_ = GattSession::FromDeviceIdAsync(device_.BluetoothDeviceId()).get();
                if (session_) session_.MaintainConnection(true);

                std::weak_ptr<Device> weak = weak_from_this();
                statusToken_ = device_.ConnectionStatusChanged([weak](const BluetoothLEDevice& sender, auto&&) {
                    if (auto self = weak.lock()) self->OnConnectionStatus(sender.ConnectionStatus());
                });

                RequestFastConnection();

                // Uncached discovery also establishes the link; the controller may need a moment.
                GattDeviceServicesResult services{nullptr};
                for (int attempt = 0; attempt < 4 && !closed_; attempt++)
                {
                    services = device_.GetGattServicesAsync(BluetoothCacheMode::Uncached).get();
                    if (services.Status() == GattCommunicationStatus::Success) break;
                    Log(1, "[%d] service discovery attempt %d failed (status %d)", handle_, attempt + 1,
                        static_cast<int>(services.Status()));
                    std::this_thread::sleep_for(std::chrono::milliseconds(300));
                }
                if (closed_) return;
                if (!services || services.Status() != GattCommunicationStatus::Success)
                {
                    Fail(services ? static_cast<int32_t>(services.Status()) : -1, "GATT service discovery failed");
                    return;
                }

                for (const auto& service : services.Services())
                {
                    services_.push_back(service);
                    auto chars = service.GetCharacteristicsAsync(BluetoothCacheMode::Uncached).get();
                    if (chars.Status() != GattCommunicationStatus::Success)
                    {
                        Log(1, "[%d] characteristics of %s: status %d", handle_,
                            GuidToString(service.Uuid()).c_str(), static_cast<int>(chars.Status()));
                        continue;
                    }
                    for (const auto& c : chars.Characteristics())
                    {
                        characteristics_.emplace(std::make_pair(service.Uuid(), c.Uuid()), c);
                        Log(0, "[%d] gatt %s / %s props 0x%02X", handle_, GuidToString(service.Uuid()).c_str(),
                            GuidToString(c.Uuid()).c_str(), static_cast<uint32_t>(c.CharacteristicProperties()));
                    }
                }

                int32_t mtu = session_ ? session_.MaxPduSize() : 0;
                LogConnectionParameters();
                connected_ = true;
                PushEvent(JC2_EVENT_CONNECTED, handle_, 0, mtu, address_);
            }
            catch (const hresult_error& e)
            {
                Fail(e.code().value, Narrow(e.message()).c_str());
            }
        }

        void Subscribe(guid service, bool anyService, guid characteristic, int32_t tag)
        {
            auto c = Find(service, anyService, characteristic);
            if (!c)
            {
                Log(2, "[%d] subscribe: characteristic %s not found", handle_, GuidToString(characteristic).c_str());
                PushEvent(JC2_EVENT_SUBSCRIBED, handle_, tag, -1, address_);
                return;
            }

            try
            {
                int32_t handle = handle_;
                auto alive = alive_;
                auto token = c.ValueChanged([handle, tag, alive](const GattCharacteristic&,
                                                                 const GattValueChangedEventArgs& args) {
                    if (!*alive) return;
                    IBuffer value = args.CharacteristicValue();
                    PushEvent(JC2_EVENT_NOTIFICATION, handle, tag, 0, 0, 0, value.data(), value.Length());
                });
                subscriptions_.push_back({c, token});

                auto status = c.WriteClientCharacteristicConfigurationDescriptorAsync(
                                   GattClientCharacteristicConfigurationDescriptorValue::Notify)
                                  .get();
                if (status != GattCommunicationStatus::Success)
                    Log(2, "[%d] enabling notifications on %s failed (status %d)", handle_,
                        GuidToString(characteristic).c_str(), static_cast<int>(status));
                PushEvent(JC2_EVENT_SUBSCRIBED, handle_, tag, static_cast<int32_t>(status), address_);
            }
            catch (const hresult_error& e)
            {
                Log(2, "[%d] subscribe %s: 0x%08X %s", handle_, GuidToString(characteristic).c_str(),
                    static_cast<uint32_t>(e.code().value), Narrow(e.message()).c_str());
                PushEvent(JC2_EVENT_SUBSCRIBED, handle_, tag, e.code().value, address_);
            }
        }

        void Write(guid service, bool anyService, guid characteristic, const std::vector<uint8_t>& data)
        {
            auto c = Find(service, anyService, characteristic);
            if (!c)
            {
                Log(2, "[%d] write: characteristic %s not found", handle_, GuidToString(characteristic).c_str());
                PushEvent(JC2_EVENT_WRITE_FAILED, handle_, 0, -1, address_);
                return;
            }

            try
            {
                Buffer buffer(static_cast<uint32_t>(data.size()));
                if (!data.empty()) memcpy(buffer.data(), data.data(), data.size());
                buffer.Length(static_cast<uint32_t>(data.size()));
                auto result = c.WriteValueWithResultAsync(buffer, GattWriteOption::WriteWithoutResponse).get();
                if (result.Status() != GattCommunicationStatus::Success)
                    PushEvent(JC2_EVENT_WRITE_FAILED, handle_, 0, static_cast<int32_t>(result.Status()), address_);
            }
            catch (const hresult_error& e)
            {
                PushEvent(JC2_EVENT_WRITE_FAILED, handle_, 0, e.code().value, address_);
            }
        }

    private:
        using CharacteristicKey = std::pair<guid, guid>;

        struct KeyLess
        {
            bool operator()(const CharacteristicKey& a, const CharacteristicKey& b) const
            {
                int c = memcmp(&a.first, &b.first, sizeof(guid));
                if (c != 0) return c < 0;
                return memcmp(&a.second, &b.second, sizeof(guid)) < 0;
            }
        };

        GattCharacteristic Find(const guid& service, bool anyService, const guid& characteristic)
        {
            if (!anyService)
            {
                auto it = characteristics_.find({service, characteristic});
                if (it != characteristics_.end()) return it->second;
            }
            for (const auto& [key, c] : characteristics_)
                if (key.second == characteristic) return c;
            return nullptr;
        }

        void RequestFastConnection()
        {
            // Windows 11+: ask for the shortest connection interval (higher report rate, lower latency).
            try
            {
                using winrt::Windows::Foundation::Metadata::ApiInformation;
                if (!ApiInformation::IsMethodPresent(L"Windows.Devices.Bluetooth.BluetoothLEDevice",
                                                     L"RequestPreferredConnectionParameters"))
                    return;
                connectionRequest_ = device_.RequestPreferredConnectionParameters(
                    BluetoothLEPreferredConnectionParameters::ThroughputOptimized());
                Log(0, "[%d] requested throughput-optimized connection parameters (status %d)", handle_,
                    static_cast<int>(connectionRequest_.Status()));
            }
            catch (const hresult_error& e)
            {
                Log(1, "[%d] connection parameter request failed: 0x%08X", handle_, static_cast<uint32_t>(e.code().value));
            }
        }

        void LogConnectionParameters()
        {
            try
            {
                using winrt::Windows::Foundation::Metadata::ApiInformation;
                if (!ApiInformation::IsMethodPresent(L"Windows.Devices.Bluetooth.BluetoothLEDevice", L"GetConnectionParameters"))
                    return;
                auto p = device_.GetConnectionParameters();
                Log(0, "[%d] connection interval %.2f ms, latency %d, timeout %d ms, MTU %d", handle_,
                    p.ConnectionInterval() * 1.25, p.ConnectionLatency(), p.LinkTimeout() * 10,
                    session_ ? session_.MaxPduSize() : 0);
            }
            catch (const hresult_error&)
            {
            }
        }

        void OnConnectionStatus(BluetoothConnectionStatus status)
        {
            if (status == BluetoothConnectionStatus::Disconnected && connected_.exchange(false) && !closed_)
                PushEvent(JC2_EVENT_DISCONNECTED, handle_, 0, 0, address_);
        }

        void Fail(int32_t code, const char* message)
        {
            Log(2, "[%d] connect %012llX failed: 0x%08X %s", handle_, static_cast<unsigned long long>(address_),
                static_cast<uint32_t>(code), message);
            PushEvent(JC2_EVENT_CONNECT_FAILED, handle_, 0, code, address_);
            Close();
        }

        void Close()
        {
            if (closed_.exchange(true)) return;
            *alive_ = false;
            connected_ = false;
            try
            {
                for (auto& s : subscriptions_) s.characteristic.ValueChanged(s.token);
                subscriptions_.clear();
                characteristics_.clear();
                for (auto& s : services_) s.Close();
                services_.clear();
                if (connectionRequest_) connectionRequest_.Close();
                connectionRequest_ = nullptr;
                if (session_)
                {
                    session_.MaintainConnection(false);
                    session_.Close();
                }
                session_ = nullptr;
                if (device_)
                {
                    device_.ConnectionStatusChanged(statusToken_);
                    device_.Close();
                }
                device_ = nullptr;
            }
            catch (const hresult_error& e)
            {
                Log(1, "[%d] close: 0x%08X", handle_, static_cast<uint32_t>(e.code().value));
            }
        }

        struct Subscription
        {
            GattCharacteristic characteristic{nullptr};
            event_token token;
        };

        const int32_t handle_;
        const uint64_t address_;
        Worker worker_;
        std::atomic<bool> closed_{false};
        std::atomic<bool> connected_{false};
        std::shared_ptr<std::atomic<bool>> alive_ = std::make_shared<std::atomic<bool>>(true);

        // Touched only on the worker thread.
        BluetoothLEDevice device_{nullptr};
        GattSession session_{nullptr};
        BluetoothLEPreferredConnectionParametersRequest connectionRequest_{nullptr};
        event_token statusToken_{};
        std::vector<GattDeviceService> services_;
        std::map<CharacteristicKey, GattCharacteristic, KeyLess> characteristics_;
        std::vector<Subscription> subscriptions_;
    };

    // ───────────────────────────── Global state ─────────────────────────────

    // Heap objects that are never destroyed: if the host exits without jc2_shutdown(), their destructors
    // would otherwise join threads / release COM objects under the loader lock.
    std::mutex g_mutex; // guards everything below
    bool g_initialized = false;
    Worker* g_control = nullptr;
    struct WatcherHolder
    {
        BluetoothLEAdvertisementWatcher watcher{nullptr};
    };
    BluetoothLEAdvertisementWatcher& g_watcher = (new WatcherHolder())->watcher; // control worker only
    std::map<int32_t, std::shared_ptr<Device>>& g_devices = *new std::map<int32_t, std::shared_ptr<Device>>();
    int32_t g_nextHandle = 1;

    // Advertisement throttle: a Joy-Con advertises many times per second.
    std::mutex g_seenMutex;
    std::unordered_map<uint64_t, std::chrono::steady_clock::time_point> g_lastSeen;

    std::shared_ptr<Device> FindDevice(int32_t handle)
    {
        std::lock_guard lock(g_mutex);
        auto it = g_devices.find(handle);
        return it == g_devices.end() ? nullptr : it->second;
    }

    void OnAdvertisement(uint16_t companyId, const BluetoothLEAdvertisementReceivedEventArgs& args)
    {
        auto sections = args.Advertisement().GetManufacturerDataByCompanyId(companyId);
        if (sections.Size() == 0) return;

        uint64_t address = args.BluetoothAddress();
        auto now = std::chrono::steady_clock::now();
        {
            std::lock_guard lock(g_seenMutex);
            auto& last = g_lastSeen[address];
            if (now - last < std::chrono::milliseconds(250)) return;
            last = now;
        }

        int32_t addressType = 0;
        try
        {
            auto type = args.BluetoothAddressType();
            addressType = type == BluetoothAddressType::Random ? 1 : 0;
            std::lock_guard lock(g_addressMutex);
            g_addressTypes[address] = type;
        }
        catch (const hresult_error&)
        {
            // BluetoothAddressType needs Windows 10 2004+; connect falls back to the untyped overload.
        }

        IBuffer data = sections.GetAt(0).Data();
        PushEvent(JC2_EVENT_DEVICE_FOUND, 0, addressType, 0, address, args.RawSignalStrengthInDBm(), data.data(),
                  data.Length());
    }

    void StopWatcherOnControlThread()
    {
        if (!g_watcher) return;
        try
        {
            g_watcher.Stop();
        }
        catch (const hresult_error&)
        {
        }
        g_watcher = nullptr;
    }
}

// ───────────────────────────── C ABI ─────────────────────────────

JC2_API int32_t jc2_version(void) { return JC2_VERSION; }

JC2_API int32_t jc2_init(void)
{
    std::lock_guard lock(g_mutex);
    if (g_initialized) return 0;
    try
    {
        g_control = new Worker("control");
        g_initialized = true;
        return 0;
    }
    catch (...)
    {
        return -1;
    }
}

JC2_API void jc2_shutdown(void)
{
    std::unique_ptr<Worker> control;
    std::map<int32_t, std::shared_ptr<Device>> devices;
    {
        std::lock_guard lock(g_mutex);
        if (!g_initialized) return;
        g_initialized = false;
        control.reset(g_control);
        g_control = nullptr;
        devices.swap(g_devices);
    }

    try
    {
        control->Post([] { StopWatcherOnControlThread(); });
        control->StopAndJoin(std::chrono::milliseconds(1000));
        for (auto& [handle, device] : devices) device->Shutdown(std::chrono::milliseconds(1500));
    }
    catch (...)
    {
    }
    devices.clear();
    {
        std::lock_guard lock(g_seenMutex);
        g_lastSeen.clear();
    }
    g_events.Clear();
}

JC2_API int32_t jc2_start_scan(uint16_t company_id)
{
    std::lock_guard lock(g_mutex);
    if (!g_initialized) return -1;
    g_control->Post([company_id] {
        StopWatcherOnControlThread();
        {
            std::lock_guard seen(g_seenMutex);
            g_lastSeen.clear();
        }
        g_watcher = BluetoothLEAdvertisementWatcher();
        // Passive: manufacturer data is in the advertisement itself, and scan requests compete with live links.
        g_watcher.ScanningMode(BluetoothLEScanningMode::Passive);
        g_watcher.Received([company_id](const BluetoothLEAdvertisementWatcher&,
                                        const BluetoothLEAdvertisementReceivedEventArgs& args) {
            try
            {
                OnAdvertisement(company_id, args);
            }
            catch (...)
            {
            }
        });
        g_watcher.Stopped([](const BluetoothLEAdvertisementWatcher&,
                             const BluetoothLEAdvertisementWatcherStoppedEventArgs& args) {
            PushEvent(JC2_EVENT_SCAN_STOPPED, 0, 0, static_cast<int32_t>(args.Error()));
        });
        g_watcher.Start();
        Log(0, "scan started (company 0x%04X)", company_id);
    });
    return 0;
}

JC2_API void jc2_stop_scan(void)
{
    std::lock_guard lock(g_mutex);
    if (!g_initialized) return;
    g_control->Post([] { StopWatcherOnControlThread(); });
}

JC2_API int32_t jc2_poll_event(jc2_event* ev, uint8_t* payload, int32_t capacity)
{
    if (!ev) return 0;
    Event e;
    if (!g_events.Pop(e)) return 0;
    *ev = e.header;
    if (payload && capacity > 0 && !e.payload.empty())
        memcpy(payload, e.payload.data(), std::min(e.payload.size(), static_cast<size_t>(capacity)));
    return 1;
}

JC2_API int32_t jc2_connect(uint64_t address)
{
    std::shared_ptr<Device> device;
    {
        std::lock_guard lock(g_mutex);
        if (!g_initialized) return -1;
        int32_t handle = g_nextHandle++;
        device = std::make_shared<Device>(handle, address);
        g_devices[handle] = device;
    }
    device->Post([](Device& d) { d.Connect(); });
    return device->Handle();
}

JC2_API void jc2_disconnect(int32_t handle)
{
    std::shared_ptr<Device> device;
    {
        std::lock_guard lock(g_mutex);
        auto it = g_devices.find(handle);
        if (it == g_devices.end()) return;
        device = it->second;
        g_devices.erase(it);
    }
    // Close on a helper thread so the caller (Unity main thread) never waits on the radio.
    std::thread([device] { device->Shutdown(std::chrono::milliseconds(3000)); }).detach();
}

JC2_API int32_t jc2_subscribe(int32_t handle, const char* service_uuid, const char* characteristic_uuid, int32_t tag)
{
    guid service{}, characteristic{};
    bool anyService = !ParseGuid(service_uuid, service);
    if (!ParseGuid(characteristic_uuid, characteristic)) return -2;
    auto device = FindDevice(handle);
    if (!device) return -1;
    device->Post([=](Device& d) { d.Subscribe(service, anyService, characteristic, tag); });
    return 0;
}

JC2_API int32_t jc2_write(int32_t handle, const char* service_uuid, const char* characteristic_uuid,
                          const uint8_t* data, int32_t length)
{
    guid service{}, characteristic{};
    bool anyService = !ParseGuid(service_uuid, service);
    if (!ParseGuid(characteristic_uuid, characteristic)) return -2;
    if (length < 0 || (length > 0 && !data)) return -3;
    auto device = FindDevice(handle);
    if (!device) return -1;
    std::vector<uint8_t> bytes(data, data + length);
    device->Post([=, bytes = std::move(bytes)](Device& d) { d.Write(service, anyService, characteristic, bytes); });
    return 0;
}
