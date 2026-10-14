using System;

namespace Joycon2
{
    /// <summary>32-byte little-endian transport header followed by untouched GATT bytes (Java byte[]).</summary>
    public static class Joycon2AndroidEventCodec
    {
        public const int HeaderLength = 32;

        public static bool TryDecode(sbyte[] frame, byte[] payload, out Joycon2TransportEvent e)
        {
            e = default;
            if (frame == null || frame.Length < HeaderLength) return false;
            int type = ReadInt(frame, 0);
            int length = ReadInt(frame, 28);
            if (type < 1 || type > 9 || length < 0 || length != frame.Length - HeaderLength) return false;
            e = new Joycon2TransportEvent
            {
                Type = (Joycon2TransportEventType)type,
                Handle = ReadInt(frame, 4), Tag = ReadInt(frame, 8), Status = ReadInt(frame, 12),
                Address = (uint)ReadInt(frame, 16) | ((ulong)(uint)ReadInt(frame, 20) << 32),
                Rssi = ReadInt(frame, 24), Length = length,
            };
            if (payload != null) Buffer.BlockCopy(frame, HeaderLength, payload, 0, Math.Min(length, payload.Length));
            return true;
        }

        static int ReadInt(sbyte[] d, int offset) => (byte)d[offset] | (byte)d[offset + 1] << 8 |
            (byte)d[offset + 2] << 16 | (byte)d[offset + 3] << 24;
    }
}
