using System;

namespace Joycon2
{
    /// <summary>Gamepad-shaped buttons, independent of the Input System so the mapping is testable anywhere.</summary>
    [Flags]
    public enum Joycon2PadButtons : uint
    {
        None = 0,
        South = 1 << 0, East = 1 << 1, North = 1 << 2, West = 1 << 3,
        LeftShoulder = 1 << 4, RightShoulder = 1 << 5,
        LeftTrigger = 1 << 6, RightTrigger = 1 << 7,
        Start = 1 << 8, Select = 1 << 9,
        LeftStickPress = 1 << 10,
    }

    public struct Joycon2PadState
    {
        public float StickX, StickY;
        public Joycon2PadButtons Buttons;
    }

    /// <summary>
    /// One Joy-Con held sideways (SL/SR rail on top) as a small gamepad.
    /// Left Joy-Con is rotated 90° counter-clockwise, right Joy-Con 90° clockwise.
    /// </summary>
    public static class Joycon2Mapping
    {
        public static Joycon2PadState Sideways(Joycon2Side side, in Joycon2Report report, in Joycon2StickCalibration cal)
        {
            var state = new Joycon2PadState();
            var b = report.Buttons;

            if (side == Joycon2Side.Left)
            {
                cal.Normalize(report.Stick1X, report.Stick1Y, out float x, out float y);
                state.StickX = -y;
                state.StickY = x;
                Map(ref state, b, Joycon2Buttons.Left, Joycon2PadButtons.South);
                Map(ref state, b, Joycon2Buttons.Down, Joycon2PadButtons.East);
                Map(ref state, b, Joycon2Buttons.Right, Joycon2PadButtons.North);
                Map(ref state, b, Joycon2Buttons.Up, Joycon2PadButtons.West);
                Map(ref state, b, Joycon2Buttons.LeftSL, Joycon2PadButtons.LeftShoulder);
                Map(ref state, b, Joycon2Buttons.LeftSR, Joycon2PadButtons.RightShoulder);
                Map(ref state, b, Joycon2Buttons.L, Joycon2PadButtons.LeftTrigger);
                Map(ref state, b, Joycon2Buttons.ZL, Joycon2PadButtons.RightTrigger);
                Map(ref state, b, Joycon2Buttons.Minus, Joycon2PadButtons.Start);
                Map(ref state, b, Joycon2Buttons.Capture, Joycon2PadButtons.Select);
                Map(ref state, b, Joycon2Buttons.LeftStick, Joycon2PadButtons.LeftStickPress);
            }
            else
            {
                // Right Joy-Con: stick data arrives in the stick-2 field (calibration comes from slot 1).
                cal.Normalize(report.Stick2X, report.Stick2Y, out float x, out float y);
                state.StickX = y;
                state.StickY = -x;
                Map(ref state, b, Joycon2Buttons.A, Joycon2PadButtons.South);
                Map(ref state, b, Joycon2Buttons.X, Joycon2PadButtons.East);
                Map(ref state, b, Joycon2Buttons.Y, Joycon2PadButtons.North);
                Map(ref state, b, Joycon2Buttons.B, Joycon2PadButtons.West);
                Map(ref state, b, Joycon2Buttons.RightSL, Joycon2PadButtons.LeftShoulder);
                Map(ref state, b, Joycon2Buttons.RightSR, Joycon2PadButtons.RightShoulder);
                Map(ref state, b, Joycon2Buttons.R, Joycon2PadButtons.LeftTrigger);
                Map(ref state, b, Joycon2Buttons.ZR, Joycon2PadButtons.RightTrigger);
                Map(ref state, b, Joycon2Buttons.Plus, Joycon2PadButtons.Start);
                Map(ref state, b, Joycon2Buttons.Home, Joycon2PadButtons.Select);
                Map(ref state, b, Joycon2Buttons.RightStick, Joycon2PadButtons.LeftStickPress);
            }
            return state;
        }

        static void Map(ref Joycon2PadState state, Joycon2Buttons raw, Joycon2Buttons from, Joycon2PadButtons to)
        {
            if ((raw & from) != 0) state.Buttons |= to;
        }
    }
}
