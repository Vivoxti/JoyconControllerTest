#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
using System;
using System.Runtime.InteropServices;

namespace Joycon2
{
    /// <summary>
    /// Native BLE plugin with one C ABI (Native/windows/include/joycon2ble.h) on two platforms:
    /// Windows — WinRT, Plugins/Windows/x86_64/Joycon2Ble.dll (sources: Native/windows);
    /// macOS — CoreBluetooth, Plugins/macOS/Joycon2Ble.bundle (sources: Native/macos).
    /// Same plugin for Editor and player, Mono and IL2CPP. Note: once loaded, the Editor keeps it until it is closed.
    /// </summary>
    public sealed class Joycon2NativeTransport : IJoycon2Transport
    {
        const string Lib = "Joycon2Ble";
        const int ExpectedVersion = 1;
        const int ErrorNoUsageDescription = -2;
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
        const string Plugin = "Joycon2Ble.bundle", Sources = "Native/macos/build.sh";
#else
        const string Plugin = "Joycon2Ble.dll", Sources = "Native/windows/build.ps1";
#endif

        [StructLayout(LayoutKind.Sequential)]
        struct NativeEvent
        {
            public int type, handle, tag, status;
            public ulong address;
            public int rssi, length;
        }

        [DllImport(Lib)] static extern int jc2_version();
        [DllImport(Lib)] static extern int jc2_init();
        [DllImport(Lib)] static extern void jc2_shutdown();
        [DllImport(Lib)] static extern int jc2_start_scan(ushort companyId);
        [DllImport(Lib)] static extern void jc2_stop_scan();
        [DllImport(Lib)] static extern int jc2_poll_event(out NativeEvent ev, [Out] byte[] payload, int capacity);
        [DllImport(Lib)] static extern int jc2_connect(ulong address);
        [DllImport(Lib)] static extern void jc2_disconnect(int handle);
        [DllImport(Lib, CharSet = CharSet.Ansi)]
        static extern int jc2_subscribe(int handle, string service, string characteristic, int tag);
        [DllImport(Lib, CharSet = CharSet.Ansi)]
        static extern int jc2_write(int handle, string service, string characteristic, byte[] data, int length);

        bool initialized;

#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
        public string Name => "CoreBluetooth";
        /// <summary>CoreBluetooth hides Bluetooth addresses: the plugin numbers peripherals per process.</summary>
        public string LocalAddressPrefix => "mac";
#else
        public string Name => "WinRT";
        public string LocalAddressPrefix => null;
#endif
        public bool RequiresUserGesture => false;

        public bool Initialize()
        {
            if (initialized) return true;
            try
            {
                int version = jc2_version();
                if (version != ExpectedVersion)
                {
                    UnityEngine.Debug.LogError($"[Joycon2] {Plugin} version {version}, expected {ExpectedVersion}. Rebuild it: {Sources}.");
                    return false;
                }
                int result = jc2_init();
                if (result == ErrorNoUsageDescription)
                    UnityEngine.Debug.LogError("[Joycon2] Info.plist has no NSBluetoothAlwaysUsageDescription, macOS would kill the app " +
                                               "on Bluetooth access: fill Player Settings → macOS → Bluetooth Usage Description.");
                initialized = result == 0;
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                UnityEngine.Debug.LogError($"[Joycon2] {Plugin} not available: {e.Message}");
            }
            return initialized;
        }

        public void Shutdown()
        {
            if (!initialized) return;
            initialized = false;
            jc2_shutdown();
        }

        public void StartScan(ushort companyId)
        {
            if (initialized) jc2_start_scan(companyId);
        }

        public void StopScan()
        {
            if (initialized) jc2_stop_scan();
        }

        public void RequestDevice(string requestJson, int tag) { }

        public bool TryDequeueEvent(out Joycon2TransportEvent e, byte[] payload)
        {
            e = default;
            if (!initialized || jc2_poll_event(out var n, payload, payload?.Length ?? 0) == 0) return false;
            e.Type = (Joycon2TransportEventType)n.type;
            e.Handle = n.handle;
            e.Tag = n.tag;
            e.Status = n.status;
            e.Address = n.address;
            e.Rssi = n.rssi;
            e.Length = n.length;
            return true;
        }

        public int Connect(ulong address) => initialized ? jc2_connect(address) : -1;

        public void Disconnect(int handle)
        {
            if (initialized) jc2_disconnect(handle);
        }

        public void Subscribe(int handle, string serviceUuid, string characteristicUuid, int tag)
        {
            if (initialized) jc2_subscribe(handle, serviceUuid, characteristicUuid, tag);
        }

        public void Write(int handle, string serviceUuid, string characteristicUuid, byte[] data, int length)
        {
            if (initialized) jc2_write(handle, serviceUuid, characteristicUuid, data, length);
        }
    }
}
#endif
