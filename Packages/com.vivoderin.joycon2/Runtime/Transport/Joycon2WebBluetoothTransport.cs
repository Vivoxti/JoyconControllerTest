#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;

namespace Joycon2
{
    /// <summary>
    /// Web Bluetooth via Plugins/WebGL/Joycon2WebBluetooth.jslib (Chrome/Edge; HTTPS or localhost).
    /// There is no background scan on the web: each controller is picked in the browser's chooser, which only
    /// opens after a click or key press (<see cref="RequiresUserGesture"/>). Addresses are page-local ids.
    /// </summary>
    public sealed class Joycon2WebBluetoothTransport : IJoycon2Transport
    {
        const string Lib = "__Internal";
        const int ExpectedVersion = 1;

        [StructLayout(LayoutKind.Sequential)]
        struct NativeEvent
        {
            public int type, handle, tag, status;
            public ulong address;
            public int rssi, length;
        }

        [DllImport(Lib)] static extern int jc2web_version();
        [DllImport(Lib)] static extern int jc2web_init();
        [DllImport(Lib)] static extern void jc2web_shutdown();
        [DllImport(Lib)] static extern void jc2web_request_device(string requestJson, int tag);
        [DllImport(Lib)] static extern int jc2web_poll_event(out NativeEvent ev, [Out] byte[] payload, int capacity);
        [DllImport(Lib)] static extern int jc2web_connect(int id);
        [DllImport(Lib)] static extern void jc2web_disconnect(int handle);
        [DllImport(Lib)] static extern void jc2web_subscribe(int handle, string service, string characteristic, int tag);
        [DllImport(Lib)] static extern void jc2web_write(int handle, string service, string characteristic, byte[] data, int length);
        [DllImport(Lib)] static extern int jc2web_is_picking();

        /// <summary>A chooser is open, or waits for the next click to open: another request would be refused.</summary>
        public static bool IsPicking => jc2web_is_picking() != 0;

        bool initialized;

        public string Name => "Web Bluetooth";
        public bool RequiresUserGesture => true;
        public string LocalAddressPrefix => "web";

        public bool Initialize()
        {
            if (initialized) return true;
            int version = jc2web_version();
            if (version != ExpectedVersion)
            {
                UnityEngine.Debug.LogError($"[Joycon2] Joycon2WebBluetooth.jslib version {version}, expected {ExpectedVersion}.");
                return false;
            }
            initialized = jc2web_init() == 0; // fails without navigator.bluetooth (Safari, Firefox, http://)
            return initialized;
        }

        public void Shutdown()
        {
            if (!initialized) return;
            initialized = false;
            jc2web_shutdown();
        }

        public void StartScan(ushort companyId) { }
        public void StopScan() { }

        public void RequestDevice(string requestJson, int tag)
        {
            if (initialized) jc2web_request_device(requestJson, tag);
        }

        public bool TryDequeueEvent(out Joycon2TransportEvent e, byte[] payload)
        {
            e = default;
            if (!initialized || jc2web_poll_event(out var n, payload, payload?.Length ?? 0) == 0) return false;
            e.Type = (Joycon2TransportEventType)n.type;
            e.Handle = n.handle;
            e.Tag = n.tag;
            e.Status = n.status;
            e.Address = n.address;
            e.Rssi = n.rssi;
            e.Length = n.length;
            return true;
        }

        public int Connect(ulong address) => initialized ? jc2web_connect((int)address) : -1;

        public void Disconnect(int handle)
        {
            if (initialized) jc2web_disconnect(handle);
        }

        public void Subscribe(int handle, string serviceUuid, string characteristicUuid, int tag)
        {
            if (initialized) jc2web_subscribe(handle, serviceUuid, characteristicUuid, tag);
        }

        public void Write(int handle, string serviceUuid, string characteristicUuid, byte[] data, int length)
        {
            if (initialized && data != null && length > 0 && length <= data.Length)
                jc2web_write(handle, serviceUuid, characteristicUuid, data, length);
        }
    }
}
#endif
