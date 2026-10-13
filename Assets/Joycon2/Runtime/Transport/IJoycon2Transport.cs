namespace Joycon2
{
    public enum Joycon2TransportEventType
    {
        None = 0,
        Log = 1,           // Status = level (0 info, 1 warning, 2 error); payload = UTF-8 text
        DeviceFound = 2,   // Address, Rssi; payload = manufacturer data after the company id
        Connected = 3,     // Handle, Address, Status = ATT MTU
        ConnectFailed = 4, // Handle, Address, Status = error code
        Disconnected = 5,  // Handle, Address — link lost
        Subscribed = 6,    // Handle, Tag, Status (0 = ok)
        Notification = 7,  // Handle, Tag; payload = value
        WriteFailed = 8,   // Handle, Status
        ScanStopped = 9,   // Status = error (0 = on request)
        DevicePicked = 10, // RequestDevice result. Status 0: Address, Tag = request tag, payload = device name (UTF-8);
                           // otherwise Status 1 = cancelled, 2 = error, payload = message
    }

    public struct Joycon2TransportEvent
    {
        public Joycon2TransportEventType Type;
        public int Handle;
        public int Tag;
        public int Status;
        public ulong Address;
        public int Rssi;
        /// <summary>Full payload length; the buffer may hold fewer bytes if it was too small.</summary>
        public int Length;
    }

    /// <summary>
    /// Thin per-platform BLE pipe: scan, connect, subscribe, write. Knows nothing about Nintendo.
    /// All results arrive as events, drained by polling from the main thread.
    /// </summary>
    public interface IJoycon2Transport
    {
        string Name { get; }
        /// <summary>No background scanning: devices are picked by the user (browser chooser) via
        /// <see cref="RequestDevice"/>, which must follow a click or key press.</summary>
        bool RequiresUserGesture { get; }
        /// <summary>Null if addresses are Bluetooth MACs. Otherwise they are local ids (the browser and CoreBluetooth
        /// hide MACs), printed as "prefix#id".</summary>
        string LocalAddressPrefix { get; }
        /// <summary>False if the platform/plugin is unavailable.</summary>
        bool Initialize();
        void Shutdown();

        void StartScan(ushort companyId);
        void StopScan();
        /// <summary>
        /// Opens the device chooser (only when <see cref="RequiresUserGesture"/>). <paramref name="requestJson"/> is a
        /// Web Bluetooth <c>requestDevice</c> options object; byte arrays are plain JSON arrays. Answers with
        /// <see cref="Joycon2TransportEventType.DevicePicked"/> carrying <paramref name="tag"/>.
        /// </summary>
        void RequestDevice(string requestJson, int tag);

        bool TryDequeueEvent(out Joycon2TransportEvent e, byte[] payload);

        /// <summary>Returns a handle (> 0) or a negative error.</summary>
        int Connect(ulong address);
        void Disconnect(int handle);
        void Subscribe(int handle, string serviceUuid, string characteristicUuid, int tag);
        void Write(int handle, string serviceUuid, string characteristicUuid, byte[] data, int length);
    }

    /// <summary>Platforms without a transport (yet): nothing is ever found.</summary>
    public sealed class Joycon2NullTransport : IJoycon2Transport
    {
        public string Name => "none";
        public bool RequiresUserGesture => false;
        public string LocalAddressPrefix => null;
        public bool Initialize() => false;
        public void Shutdown() { }
        public void StartScan(ushort companyId) { }
        public void StopScan() { }
        public void RequestDevice(string requestJson, int tag) { }
        public bool TryDequeueEvent(out Joycon2TransportEvent e, byte[] payload) { e = default; return false; }
        public int Connect(ulong address) => -1;
        public void Disconnect(int handle) { }
        public void Subscribe(int handle, string serviceUuid, string characteristicUuid, int tag) { }
        public void Write(int handle, string serviceUuid, string characteristicUuid, byte[] data, int length) { }
    }
}
