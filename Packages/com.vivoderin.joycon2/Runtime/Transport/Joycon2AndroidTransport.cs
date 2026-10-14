#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using UnityEngine;

namespace Joycon2
{
    /// <summary>JNI wrapper for the generic Java BLE pipe. All Nintendo logic stays in the C# session.</summary>
    public sealed class Joycon2AndroidTransport : IJoycon2Transport
    {
        /// <summary>Plugins/Android/joycon2ble.aar (sources: Native~/android).</summary>
        const string JavaClass = "com.vivoderin.joycon2.BleTransport";
        AndroidJavaObject pipe;
        public string Name => "Android BLE";
        public bool RequiresUserGesture => false;
        public string LocalAddressPrefix => null;

        public bool Initialize()
        {
            try
            {
                using var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
                int minimumMtu = Joycon2Protocol.FactoryStick2Offset + Joycon2StickCalibration.Length +
                    Joycon2Protocol.MemoryReadDataOffset + 3;
                pipe = new AndroidJavaObject(JavaClass, activity, minimumMtu);
                if (pipe.Call<bool>("initialize")) return true;
                Shutdown();
            }
            catch (Exception e)
            {
                Debug.LogError($"[Joycon2] Android BLE initialization: {e.Message}");
                Shutdown();
            }
            return false;
        }

        public void Shutdown()
        {
            if (pipe == null) return;
            try { pipe.Call("shutdown"); }
            catch (Exception e) { Debug.LogWarning($"[Joycon2] Android BLE shutdown: {e.Message}"); }
            finally { pipe.Dispose(); pipe = null; }
        }

        public void StartScan(ushort companyId) => pipe?.Call("startScan", (int)companyId);
        public void StopScan() => pipe?.Call("stopScan");
        public void RequestDevice(string requestJson, int tag) { }
        public int Connect(ulong address) => pipe == null ? -1 : pipe.Call<int>("connect", (long)address);
        public void Disconnect(int handle) => pipe?.Call("disconnect", handle);
        public void Subscribe(int handle, string serviceUuid, string characteristicUuid, int tag) =>
            pipe?.Call("subscribe", handle, serviceUuid, characteristicUuid, tag);

        public void Write(int handle, string serviceUuid, string characteristicUuid, byte[] data, int length)
        {
            if (pipe == null || data == null || length <= 0 || length > data.Length) return;
            var bytes = new sbyte[length]; // Java byte is signed; preserve every bit across JNI.
            Buffer.BlockCopy(data, 0, bytes, 0, length);
            pipe.Call("write", handle, serviceUuid, characteristicUuid, bytes);
        }

        public bool TryDequeueEvent(out Joycon2TransportEvent e, byte[] payload)
        {
            e = default;
            if (pipe == null) return false;
            var frame = pipe.Call<sbyte[]>("pollEvent");
            if (frame == null) return false;
            if (Joycon2AndroidEventCodec.TryDecode(frame, payload, out e)) return true;
            Debug.LogError("[Joycon2] Malformed Android BLE event; stopping the transport.");
            Shutdown();
            e = new Joycon2TransportEvent { Type = Joycon2TransportEventType.ScanStopped, Status = -1003 };
            return true;
        }
    }
}
#endif
