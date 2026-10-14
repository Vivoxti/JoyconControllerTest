using System;

namespace Joycon2
{
    public enum Joycon2Side { Left, Right }

    /// <summary>
    /// Joy-Con 2 BLE constants and command builders. See Documentation~/PROTOCOL.md in the package.
    /// Pure C# (no UnityEngine) so it can be reused by every transport and tested without the engine.
    /// </summary>
    public static class Joycon2Protocol
    {
        public const ushort CompanyId = 0x0553;
        public const ushort VendorId = 0x057E;
        public const ushort ProductIdRight = 0x2066;
        public const ushort ProductIdLeft = 0x2067;

        public const string ServiceUuid = "ab7de9be-89fe-49ad-828f-118f09df7fd0";
        public const string InputReportUuid = "ab7de9be-89fe-49ad-828f-118f09df7fd2";
        public const string CommandUuid = "649d4ac9-8eb7-4e6c-af44-1ea54fe5f005";
        public const string CommandResponseUuid = "c765a961-d9d8-4d36-a20a-5315b111836a";

        // Commands: [cmd] 0x91 0x01 [sub] 0x00 [payload length] 0x00 0x00 [payload...]
        public const byte CmdMemory = 0x02, SubMemoryRead = 0x04;
        public const byte CmdLed = 0x09, SubLedSetPlayer = 0x07;
        public const byte CmdFeature = 0x0C, SubFeatureSetMask = 0x02, SubFeatureEnable = 0x04;

        public const int CommandHeaderLength = 8;
        /// <summary>Memory read responses: 8-byte command header, 8-byte echo (len, addr), then data.</summary>
        public const int MemoryReadDataOffset = 16;
        public const int MaxMemoryReadLength = 0x4F;

        /// <summary>Feature flags: buttons | sticks | IMU | mouse | 0x20. What the console enables for Joy-Cons.
        /// Never 0xFF: it produces phantom ZL/ZR presses.</summary>
        public const byte DefaultFeatureMask = 0x37;

        // Memory map (SPI flash).
        public const uint InfoAddress = 0x00013000;                     // factory info: serial, VID/PID, colours
        public const int InfoProductIdOffset = 0x14;                    // u16 LE, same PID as in the advertisement
        public const int InfoReadLength = 0x20;
        public const uint FactoryStickCalibrationAddress = 0x000130A8; // stick 1; stick 2 at +0x40
        public const int FactoryStick2Offset = 0x40;
        public const uint UserStickCalibrationAddress = 0x001FC040;    // 2-byte header, stick 1 at +2; stick 2 at +0x22
        public const int UserStick1Offset = 0x02, UserStick2Offset = 0x22;

        public static readonly byte[] PlayerLedPatterns = { 0x01, 0x03, 0x07, 0x0F };

        public static byte PlayerLedPattern(int playerIndex) =>
            playerIndex < 0 ? (byte)0 : PlayerLedPatterns[Math.Min(playerIndex, PlayerLedPatterns.Length - 1)];

        public static byte[] BuildCommand(byte command, byte subcommand, params byte[] payload)
        {
            payload = payload ?? Array.Empty<byte>();
            var data = new byte[CommandHeaderLength + payload.Length];
            data[0] = command;
            data[1] = 0x91; // request
            data[2] = 0x01; // transport: BLE
            data[3] = subcommand;
            data[5] = (byte)payload.Length;
            Buffer.BlockCopy(payload, 0, data, CommandHeaderLength, payload.Length);
            return data;
        }

        public static byte[] SetPlayerLed(byte pattern) =>
            BuildCommand(CmdLed, SubLedSetPlayer, pattern, 0, 0, 0, 0, 0, 0, 0);

        public static byte[] SetFeatureMask(byte flags) =>
            BuildCommand(CmdFeature, SubFeatureSetMask, flags, 0, 0, 0);

        public static byte[] EnableFeatures(byte flags) =>
            BuildCommand(CmdFeature, SubFeatureEnable, flags, 0, 0, 0);

        public static byte[] ReadMemory(uint address, int length)
        {
            if (length <= 0 || length > MaxMemoryReadLength) throw new ArgumentOutOfRangeException(nameof(length));
            return BuildCommand(CmdMemory, SubMemoryRead,
                (byte)length, 0x7E, 0, 0,
                (byte)address, (byte)(address >> 8), (byte)(address >> 16), (byte)(address >> 24));
        }

        /// <summary>True if a command-response notification answers the given command.</summary>
        public static bool IsResponseTo(byte[] data, int length, byte command, byte subcommand) =>
            data != null && length >= CommandHeaderLength && data[0] == command && data[3] == subcommand;
    }

    /// <summary>What a Joy-Con 2 puts in its BLE advertisement (manufacturer data for company 0x0553).</summary>
    public struct Joycon2Advertisement
    {
        public ushort VendorId;
        public ushort ProductId;
        public Joycon2Side Side;
        /// <summary>SYNC is held: no host address in the advertisement. Only then does the controller accept us.</summary>
        public bool PairingMode;

        /// <param name="data">Manufacturer data after the 2-byte company id.</param>
        public static bool TryParse(byte[] data, int length, out Joycon2Advertisement adv)
        {
            adv = default;
            if (data == null || length < 7) return false;
            adv.VendorId = (ushort)(data[3] | data[4] << 8);
            adv.ProductId = (ushort)(data[5] | data[6] << 8);
            if (adv.VendorId != Joycon2Protocol.VendorId) return false;
            if (adv.ProductId == Joycon2Protocol.ProductIdLeft) adv.Side = Joycon2Side.Left;
            else if (adv.ProductId == Joycon2Protocol.ProductIdRight) adv.Side = Joycon2Side.Right;
            else return false;

            adv.PairingMode = true;
            if (length >= 16)
                for (int i = 10; i < 16; i++)
                    if (data[i] != 0) adv.PairingMode = false;
            return true;
        }
    }
}
