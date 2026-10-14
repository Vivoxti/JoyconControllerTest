// Joycon2Boost — raises the BLE report rate of Joy-Con 2 connected by *another* app (e.g. Chrome's Web Bluetooth).
//
// Windows keeps one LE link per remote device, shared by every process. Chrome never asks for connection
// parameters, so the link stays at the bthport default of 60 ms (~16.7 Hz). This tool opens the same device
// and requests ThroughputOptimized (Windows 11+), which the native Joycon2Ble.dll gets at 15 ms (~66 Hz).
// The request lasts while the tool runs; it never touches GATT, so it does not disturb the other app.
//
// Usage: Joycon2Boost.exe [AA:BB:CC:DD:EE:FF ...]   run by hand: also registers joycon2boost:// for this user,
//                                                   so the game page can start it later (Chrome asks once)
//        Joycon2Boost.exe joycon2boost://...       how the browser starts it: exits by itself once no Joy-Con 2
//                                                   has been connected for a minute (e.g. the tab was closed)
//        Joycon2Boost.exe --unregister              removes joycon2boost://
//   It boosts Joy-Con 2 that are already connected, and watches for new ones (hold SYNC) to boost them
//   as soon as the browser connects them. Only one instance runs at a time.

#include <windows.h>

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Foundation.Metadata.h>
#include <winrt/Windows.Devices.Bluetooth.h>
#include <winrt/Windows.Devices.Bluetooth.Advertisement.h>
#include <winrt/Windows.Devices.Bluetooth.GenericAttributeProfile.h>
#include <winrt/Windows.Devices.Enumeration.h>
#include <winrt/Windows.Storage.Streams.h>

#include <chrono>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <map>
#include <mutex>
#include <string>
#include <set>
#include <string_view>
#include <thread>

using namespace winrt;
using namespace winrt::Windows::Devices::Bluetooth;
using namespace winrt::Windows::Devices::Bluetooth::Advertisement;
using namespace winrt::Windows::Devices::Bluetooth::GenericAttributeProfile;
using winrt::Windows::Devices::Enumeration::DeviceInformation;
using winrt::Windows::Foundation::Metadata::ApiInformation;

namespace
{
    constexpr uint16_t kNintendoCompanyId = 0x0553;
    constexpr guid kNintendoService{0xab7de9be, 0x89fe, 0x49ad, {0x82, 0x8f, 0x11, 0x8f, 0x09, 0xdf, 0x7f, 0xd0}};
    constexpr wchar_t kProtocolKey[] = L"Software\\Classes\\joycon2boost";
    constexpr char kProtocolPrefix[] = "joycon2boost:";
    constexpr auto kIdleExit = std::chrono::seconds(60); // browser-started instance only
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

    bool RegisterProtocol()
    {
        wchar_t exe[MAX_PATH];
        if (!GetModuleFileNameW(nullptr, exe, MAX_PATH)) return false;
        std::wstring command = L"\"" + std::wstring(exe) + L"\" \"%1\"";
        const wchar_t description[] = L"URL:Joycon2Boost (Joy-Con 2 report rate)";

        HKEY key;
        if (RegCreateKeyExW(HKEY_CURRENT_USER, kProtocolKey, 0, nullptr, 0, KEY_WRITE, nullptr, &key, nullptr) != ERROR_SUCCESS)
            return false;
        bool ok = RegSetValueExW(key, nullptr, 0, REG_SZ, reinterpret_cast<const BYTE*>(description), sizeof description) == ERROR_SUCCESS &&
                  RegSetValueExW(key, L"URL Protocol", 0, REG_SZ, reinterpret_cast<const BYTE*>(L""), sizeof(wchar_t)) == ERROR_SUCCESS;
        HKEY open = nullptr;
        ok = ok && RegCreateKeyExW(key, L"shell\\open\\command", 0, nullptr, 0, KEY_WRITE, nullptr, &open, nullptr) == ERROR_SUCCESS;
        if (ok)
        {
            ok = RegSetValueExW(open, nullptr, 0, REG_SZ, reinterpret_cast<const BYTE*>(command.c_str()),
                                static_cast<DWORD>((command.size() + 1) * sizeof(wchar_t))) == ERROR_SUCCESS;
            RegCloseKey(open);
        }
        RegCloseKey(key);
        return ok;
    }

    /// A connected device is a Joy-Con 2 if it says so, or (no name known) has the Nintendo service in the cache.
    bool IsJoycon(BluetoothLEDevice const& device)
    {
        if (std::wstring_view(device.Name()).starts_with(L"Joy-Con 2")) return true;
        try
        {
            auto result = device.GetGattServicesForUuidAsync(kNintendoService, BluetoothCacheMode::Cached).get();
            bool found = result.Status() == GattCommunicationStatus::Success && result.Services().Size() > 0;
            for (auto const& service : result.Services()) service.Close();
            return found;
        }
        catch (hresult_error const&)
        {
            return false;
        }
    }

    std::set<std::wstring> notJoycons; // device ids already checked

    void TrackConnected()
    {
        try
        {
            auto connected = DeviceInformation::FindAllAsync(
                BluetoothLEDevice::GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus::Connected)).get();
            for (auto const& info : connected)
            {
                std::wstring id{info.Id()};
                if (notJoycons.count(id)) continue;
                auto device = BluetoothLEDevice::FromIdAsync(info.Id()).get();
                if (!device) continue;
                {
                    std::lock_guard lock(mutex);
                    if (targets.count(device.BluetoothAddress())) continue;
                }
                if (IsJoycon(device)) std::thread(Track, device.BluetoothAddress()).detach();
                else notJoycons.insert(id);
            }
        }
        catch (hresult_error const& e)
        {
            std::printf("listing connected devices failed: 0x%08X\n", static_cast<uint32_t>(e.code().value));
        }
    }

    bool AnyBoosted()
    {
        std::lock_guard lock(mutex);
        for (auto const& [address, t] : targets)
            if (t.request) return true;
        return false;
    }
}

int main(int argc, char** argv)
{
    bool fromBrowser = false;
    for (int i = 1; i < argc; i++)
    {
        if (_strnicmp(argv[i], kProtocolPrefix, sizeof kProtocolPrefix - 1) == 0) fromBrowser = true;
        if (std::strcmp(argv[i], "--unregister") == 0)
        {
            RegDeleteTreeW(HKEY_CURRENT_USER, kProtocolKey);
            std::printf("joycon2boost:// removed.\n");
            return 0;
        }
    }

    // One instance: the page may start us again on every press.
    CreateMutexW(nullptr, FALSE, L"Local\\Joycon2Boost");
    if (GetLastError() == ERROR_ALREADY_EXISTS)
    {
        std::printf("Joycon2Boost is already running.\n");
        Sleep(1500);
        return 0;
    }

    init_apartment(apartment_type::multi_threaded);
    if (!ApiInformation::IsMethodPresent(L"Windows.Devices.Bluetooth.BluetoothLEDevice", L"RequestPreferredConnectionParameters"))
    {
        std::printf("RequestPreferredConnectionParameters needs Windows 11.\n");
        Sleep(5000);
        return 1;
    }

    if (fromBrowser)
    {
        std::printf("Joycon2Boost (started from the browser): closes itself when no Joy-Con 2 is connected for %lld s.\n",
                    static_cast<long long>(kIdleExit.count()));
        ShowWindow(GetConsoleWindow(), SW_SHOWMINNOACTIVE); // stay out of the game's way
    }
    else
    {
        if (RegisterProtocol())
            std::printf("Registered joycon2boost:// for this user: the game page can start me from now on "
                        "(Joycon2Boost.exe --unregister to undo).\n");
        std::printf("Joycon2Boost: keep this window open while playing; Ctrl+C to stop.\n");
    }

    for (int i = 1; i < argc; i++)
    {
        uint64_t address;
        if (ParseAddress(argv[i], address)) std::thread(Track, address).detach();
    }
    TrackConnected();

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

    auto lastActive = std::chrono::steady_clock::now();
    for (;;)
    {
        std::this_thread::sleep_for(std::chrono::seconds(5));
        TrackConnected();
        auto now = std::chrono::steady_clock::now();
        if (AnyBoosted()) lastActive = now;
        else if (fromBrowser && now - lastActive > kIdleExit)
        {
            Log("no Joy-Con 2 connected for %lld s, exiting", static_cast<long long>(kIdleExit.count()));
            watcher.Stop();
            std::_Exit(0); // skip static destructors: WinRT callbacks may still be in flight
        }
    }
}
