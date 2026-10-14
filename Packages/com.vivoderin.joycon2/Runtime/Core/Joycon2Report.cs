using System;

namespace Joycon2
{
    /// <summary>Button bits of the input report (uint32 LE at offset 4). Same layout for L and R.</summary>
    [Flags]
    public enum Joycon2Buttons : uint
    {
        None = 0,
        Y = 1u << 0, X = 1u << 1, B = 1u << 2, A = 1u << 3,
        RightSR = 1u << 4, RightSL = 1u << 5, R = 1u << 6, ZR = 1u << 7,
        Minus = 1u << 8, Plus = 1u << 9, RightStick = 1u << 10, LeftStick = 1u << 11,
        Home = 1u << 12, Capture = 1u << 13, C = 1u << 14,
        Down = 1u << 16, Up = 1u << 17, Right = 1u << 18, Left = 1u << 19,
        LeftSR = 1u << 20, LeftSL = 1u << 21, L = 1u << 22, ZL = 1u << 23,
    }

    /// <summary>
    /// Parsed input report from characteristic ...7fd2 (63 bytes, no report id over BLE).
    /// </summary>
    public struct Joycon2Report
    {
        public const int MinLength = 0x10;  // counter + buttons + both sticks
        public const int FullLength = 0x3C; // through the gyro

        /// <summary>Accelerometer: ±8 g full scale (ndeadly, switch2mac, joycon2cpp agree).</summary>
        public const float AccelGPerLsb = 1f / 4096f;
        /// <summary>Gyro: ±2000 °/s full scale (ndeadly, from the ICM-42670-P datasheet). joycon2cpp uses a
        /// scale 8× smaller — verify by turning the controller 360° and integrating.</summary>
        public const float GyroDpsPerLsb = 2000f / 32767f;

        public uint Counter;
        public Joycon2Buttons Buttons;
        /// <summary>Stick 1 (offset 0x0A) is the left Joy-Con's stick, stick 2 (0x0D) the right one's. 12-bit raw.</summary>
        public ushort Stick1X, Stick1Y, Stick2X, Stick2Y;
        public ushort BatteryMillivolts; // 0 if the report is too short
        /// <summary>IMU fields are filled (feature bit 0x04 on; marker byte 0x29 == 1; not all zero).</summary>
        public bool HasMotion;
        /// <summary>Raw IMU in the controller's own frame (axis orientation: see Joycon2MotionAxes).</summary>
        public short AccelX, AccelY, AccelZ, GyroX, GyroY, GyroZ;
        /// <summary>IMU sample time in µs (offset 0x2A; not reliable on every controller — sanity-check deltas).</summary>
        public uint ImuTimestampMicros;

        public bool IsPressed(Joycon2Buttons button) => (Buttons & button) != 0;

        public static bool TryParse(byte[] data, int length, out Joycon2Report report)
        {
            report = default;
            if (data == null || length < MinLength) return false;

            report.Counter = ReadU32(data, 0x00);
            report.Buttons = (Joycon2Buttons)ReadU32(data, 0x04);
            UnpackStick(data, 0x0A, out report.Stick1X, out report.Stick1Y);
            UnpackStick(data, 0x0D, out report.Stick2X, out report.Stick2Y);
            if (length >= 0x21) report.BatteryMillivolts = (ushort)(data[0x1F] | data[0x20] << 8);
            if (length >= FullLength && data[0x29] == 0x01)
            {
                report.AccelX = ReadI16(data, 0x30);
                report.AccelY = ReadI16(data, 0x32);
                report.AccelZ = ReadI16(data, 0x34);
                report.GyroX = ReadI16(data, 0x36);
                report.GyroY = ReadI16(data, 0x38);
                report.GyroZ = ReadI16(data, 0x3A);
                report.ImuTimestampMicros = ReadU32(data, 0x2A);
                report.HasMotion = report.AccelX != 0 || report.AccelY != 0 || report.AccelZ != 0;
            }
            return true;
        }

        /// <summary>Two 12-bit values packed in 3 bytes: X = low 12 bits, Y = high 12 bits (little endian).</summary>
        public static void UnpackStick(byte[] data, int offset, out ushort x, out ushort y)
        {
            x = (ushort)(data[offset] | (data[offset + 1] & 0x0F) << 8);
            y = (ushort)(data[offset + 1] >> 4 | data[offset + 2] << 4);
        }

        static uint ReadU32(byte[] d, int o) => (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24);
        static short ReadI16(byte[] d, int o) => (short)(d[o] | d[o + 1] << 8);
    }

    /// <summary>
    /// Stick calibration (9 bytes: three packed 12-bit pairs — center, positive range, negative range).
    /// </summary>
    public struct Joycon2StickCalibration
    {
        public const int Length = 9;

        public ushort CenterX, CenterY;
        public ushort MaxX, MaxY; // travel above center
        public ushort MinX, MinY; // travel below center

        /// <summary>Used when nothing could be read: typical Joy-Con 2 values (~±1250 of travel).</summary>
        public static readonly Joycon2StickCalibration Default = new Joycon2StickCalibration
        {
            CenterX = 2048, CenterY = 2048, MaxX = 1250, MaxY = 1250, MinX = 1250, MinY = 1250,
        };

        /// <summary>Fails for an erased slot (FF FF FF) or values that cannot be a real stick.</summary>
        public static bool TryParse(byte[] data, int offset, int length, out Joycon2StickCalibration cal)
        {
            cal = default;
            if (data == null || offset < 0 || offset + Length > length) return false;
            if (data[offset] == 0xFF && data[offset + 1] == 0xFF && data[offset + 2] == 0xFF) return false;

            Joycon2Report.UnpackStick(data, offset, out cal.CenterX, out cal.CenterY);
            Joycon2Report.UnpackStick(data, offset + 3, out cal.MaxX, out cal.MaxY);
            Joycon2Report.UnpackStick(data, offset + 6, out cal.MinX, out cal.MinY);
            return cal.IsPlausible;
        }

        public bool IsPlausible =>
            InRange(CenterX, 1000, 3100) && InRange(CenterY, 1000, 3100) &&
            InRange(MaxX, 400, 2047) && InRange(MaxY, 400, 2047) &&
            InRange(MinX, 400, 2047) && InRange(MinY, 400, 2047);

        /// <summary>Raw 12-bit → [-1, 1] per axis (no dead zone: the Input System applies its own).</summary>
        public void Normalize(ushort rawX, ushort rawY, out float x, out float y)
        {
            x = Axis(rawX, CenterX, MaxX, MinX);
            y = Axis(rawY, CenterY, MaxY, MinY);
        }

        static float Axis(int raw, int center, int max, int min)
        {
            int d = raw - center;
            float v = d >= 0 ? d / (float)Math.Max(max, 1) : d / (float)Math.Max(min, 1);
            return v > 1f ? 1f : v < -1f ? -1f : v;
        }

        static bool InRange(int v, int lo, int hi) => v >= lo && v <= hi;

        public override string ToString() =>
            $"center ({CenterX}, {CenterY}) +({MaxX}, {MaxY}) -({MinX}, {MinY})";
    }
}
