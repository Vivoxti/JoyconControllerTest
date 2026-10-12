// Joycon2Boost — raises the BLE report rate of Joy-Con 2 connected by *another* app (e.g. Chrome's Web Bluetooth).
//
// Windows keeps one LE link per remote device, shared by every process. Chrome never asks for connection
// parameters, so the link stays at the bthport default of 60 ms (~16.7 Hz). This tool opens the same device
// and requests ThroughputOptimized (Windows 11+), which the native Joycon2Ble.dll gets at 15 ms (~66 Hz).
// The request lasts while the tool runs; it never touches GATT, so it does not disturb the other app.
//
// Usage: Joycon2Boost.exe [AA:BB:CC:DD:EE:FF ...]
//   It also boosts Joy-Con 2 that are already connected, and watches for new ones (hold SYNC) to boost them
//   as soon as the browser connects them.

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Foundation.Metadata.h>
#include <winrt/Windows.Devices.Bluetooth.h>
#include <winrt/Windows.Devices.Bluetooth.Advertisement.h>
#include <winrt/Windows.Devices.Enumeration.h>
#include <winrt/Windows.Storage.Streams.h>

#include <chrono>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <map>
#include <mutex>
#include <string>
#include <string_view>
#include <thread>

using namespace winrt;
using namespace winrt::Windows::Devices::Bluetooth;
using namespace winrt::Windows::Devices::Bluetooth::Advertisement;
using winrt::Windows::Devices::Enumeration::DeviceInformation;
using winrt::Windows::Foundation::Metadata::ApiInformation;

namespace
{
    constexpr uint16_t kNintendoCompanyId = 0x0553;
    constexpr double kSlowIntervalMs = 20.0; // above this we ask again
    constexpr auto kRetryGap = std::chrono::seconds(2);
    constexpr int kMaxRetries = 5;           // in a row, so we never fight the stack forever

    struct Target
    {
        BluetoothLEDevice device{nullptr};
        BluetoothLEPreferredConnectionParametersRequest request{nullptr};
        event_token statusToken{}, paramsToken{};
        int retries = 0;
    };

    std::mutex mutex;
    std::map<uint64_t, Target> targets;

    std::string FormatAddress(uint64_t a)
    {
        char s[18];
        std::snprintf(s, sizeof s, "%02llX:%02llX:%02llX:%02llX:%02llX:%02llX", (a >> 40) & 0xFF, (a >> 32) & 0xFF,
                      (a >> 24) & 0xFF, (a >> 16) & 0xFF, (a >> 8) & 0xFF, a & 0xFF);
        return s;
    }

    bool ParseAddress(const char* text, uint64_t& out)
    {
        unsigned b[6];
        if (sscanf_s(text, "%x:%x:%x:%x:%x:%x", &b[0], &b[1], &b[2], &b[3], &b[4], &b[5]) != 6) return false;
        out = 0;
        for (unsigned v : b) out = out << 8 | (v & 0xFF);
        return true;
    }

    void Log(const char* format, ...)
    {
        auto now = std::chrono::system_clock::to_time_t(std::chrono::system_clock::now());
        tm local{};
        localtime_s(&local, &now);
        std::printf("%02d:%02d:%02d  ", local.tm_hour, local.tm_min, local.tm_sec);
        va_list args;
        va_start(args, format);
        std::vprintf(format, args);
        va_end(args);
        std::printf("\n");
        std::fflush(stdout);
    }

    /// Logs the current parameters; returns the interval in ms (0 if unknown).
    double LogParameters(uint64_t address, BluetoothLEDevice const& device, const char* when)
    {
        try
        {
            auto p = device.GetConnectionParameters();
            double ms = p.ConnectionInterval() * 1.25;
            Log("%s  %s: interval %.2f ms (~%.0f Hz), latency %d, timeout %d ms", FormatAddress(address).c_str(), when,
                ms, ms > 0 ? 1000.0 / ms : 0.0, p.ConnectionLatency(), p.LinkTimeout() * 10);
            return ms;
        }
        catch (hresult_error const&)
        {
            return 0;
        }
    }

    // Called with the mutex held.
    void Boost(uint64_t address, Target& t)
    {
        if (t.request) return;
        LogParameters(address, t.device, "connected");
        try
        {
            t.request = t.device.RequestPreferredConnectionParameters(BluetoothLEPreferredConnectionParameters::ThroughputOptimized());
            Log("%s  requested ThroughputOptimized: status %d", FormatAddress(address).c_str(), static_cast<int>(t.request.Status()));
        }
        catch (hresult_error const& e)
        {
            Log("%s  request failed: 0x%08X", FormatAddress(address).c_str(), static_cast<uint32_t>(e.code().value));
        }
    }

    void Track(uint64_t address)
    {
        {
            std::lock_guard lock(mutex);
            if (targets.count(address)) return;
            targets[address]; // reserve: advertisements keep coming while we open it
        }
        BluetoothLEDevice device{nullptr};
        try
        {
            device = BluetoothLEDevice::FromBluetoothAddressAsync(address).get();
        }
        catch (hresult_error const&)
        {
        }
        std::lock_guard lock(mutex);
        if (!device)
        {
            targets.erase(address); // try again on the next advertisement
            return;
        }
        auto& t = targets[address];
        t.device = device;
        Log("%s  found (%ls), waiting for the browser to connect it", FormatAddress(address).c_str(), device.Name().c_str());

        t.statusToken = device.ConnectionStatusChanged([address](BluetoothLEDevice const& d, auto&&) {
            std::lock_guard lock(mutex);
            auto it = targets.find(address);
            if (it == targets.end()) return;
            if (d.ConnectionStatus() == BluetoothConnectionStatus::Connected)
            {
                it->second.retries = 0;
                Boost(address, it->second);
            }
            else if (it->second.request)
            {
                Log("%s  disconnected", FormatAddress(address).c_str());
                it->second.request.Close();
                it->second.request = nullptr;
            }
        });
        t.paramsToken = device.ConnectionParametersChanged([address](BluetoothLEDevice const& d, auto&&) {
            double ms = LogParameters(address, d, "parameters changed");
            std::lock_guard lock(mutex);
            auto it = targets.find(address);
            if (it == targets.end() || !it->second.request) return;
            auto& t = it->second;
            if (ms <= kSlowIntervalMs)
            {
                t.retries = 0;
                return;
            }
            // Slowed down although our request is alive (e.g. a released request of another app landed later):
            // ask again shortly, a few times at most.
            if (t.retries >= kMaxRetries) return;
            t.retries++;
            std::thread([address] {
                std::this_thread::sleep_for(kRetryGap);
                std::lock_guard lock(mutex);
                auto it = targets.find(address);
                if (it == targets.end() || !it->second.request) return;
                auto& t = it->second;
                try
                {
                    if (t.device.GetConnectionParameters().ConnectionInterval() * 1.25 <= kSlowIntervalMs) return;
                }
                catch (hresult_error const&)
                {
                    return;
                }
                Log("%s  slowed down again, re-requesting (%d/%d)", FormatAddress(address).c_str(), t.retries, kMaxRetries);
                t.request.Close();
                t.request = nullptr;
                Boost(address, t);
            }).detach();
        });
        if (device.ConnectionStatus() == BluetoothConnectionStatus::Connected) Boost(address, t);
    }
}

int main(int argc, char** argv)
{
    init_apartment(apartment_type::multi_threaded);
    if (!ApiInformation::IsMethodPresent(L"Windows.Devices.Bluetooth.BluetoothLEDevice", L"RequestPreferredConnectionParameters"))
    {
        std::printf("RequestPreferredConnectionParameters needs Windows 11.\n");
        return 1;
    }

    std::printf("Joycon2Boost: keep this window open while playing; Ctrl+C to stop.\n");
    for (int i = 1; i < argc; i++)
    {
        uint64_t address;
        if (ParseAddress(argv[i], address)) std::thread(Track, address).detach();
        else std::printf("not an address: %s\n", argv[i]);
    }

    // Already connected (only listed when Windows knows the device, e.g. it was paired once).
    try
    {
        auto connected = DeviceInformation::FindAllAsync(
            BluetoothLEDevice::GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus::Connected)).get();
        for (auto const& info : connected)
        {
            auto device = BluetoothLEDevice::FromIdAsync(info.Id()).get();
            if (device && std::wstring_view(device.Name()).starts_with(L"Joy-Con 2"))
                std::thread(Track, device.BluetoothAddress()).detach();
        }
    }
    catch (hresult_error const& e)
    {
        std::printf("listing connected devices failed: 0x%08X\n", static_cast<uint32_t>(e.code().value));
    }

    BluetoothLEAdvertisementWatcher watcher;
    watcher.ScanningMode(BluetoothLEScanningMode::Passive);
    BluetoothLEManufacturerData filter;
    filter.CompanyId(kNintendoCompanyId);
    watcher.AdvertisementFilter().Advertisement().ManufacturerData().Append(filter);
    watcher.Received([](auto&&, BluetoothLEAdvertisementReceivedEventArgs const& args) {
        uint64_t address = args.BluetoothAddress();
        {
            std::lock_guard lock(mutex);
            if (targets.count(address)) return;
        }
        std::thread(Track, address).detach();
    });
    watcher.Start();
    std::printf("Watching for Joy-Con 2 (hold SYNC), then connect them in the browser.\n");

    for (;;) std::this_thread::sleep_for(std::chrono::hours(1));
}
