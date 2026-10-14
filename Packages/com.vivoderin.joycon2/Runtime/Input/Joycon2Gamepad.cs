using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Utilities;

namespace Joycon2
{
    /// <summary>
    /// <see cref="GamepadState"/> plus the IMU. The embedded gamepad state keeps every Gamepad control at
    /// its usual offset; the motion controls follow it.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 68)]
    public struct Joycon2GamepadState : IInputStateTypeInfo
    {
        public static FourCC Format => new FourCC('J', 'C', '2', 'G');
        public FourCC format => Format;

        [FieldOffset(0)] public GamepadState gamepad;

        /// <summary>°/s in the sideways frame (see <see cref="Joycon2MotionAxes"/>), gyro bias removed.</summary>
        [InputControl(name = "angularVelocity", layout = "Vector3", displayName = "Angular Velocity", noisy = true)]
        [FieldOffset(28)] public Vector3 angularVelocity;

        /// <summary>g in the sideways frame; lying flat face up reads (0, +1, 0).</summary>
        [InputControl(name = "acceleration", layout = "Vector3", displayName = "Acceleration", noisy = true)]
        [FieldOffset(40)] public Vector3 acceleration;

        /// <summary>Pose relative to the last recenter (Select): controller → recentered frame.</summary>
        [InputControl(name = "attitude", layout = "Quaternion", displayName = "Attitude", noisy = true)]
        [FieldOffset(52)] public Quaternion attitude;
    }

    /// <summary>
    /// One Joy-Con 2 held sideways, exposed as a regular <see cref="Gamepad"/> so every
    /// <c>&lt;Gamepad&gt;/...</c> binding works unchanged, plus the IMU as
    /// <c>&lt;Joycon2Gamepad&gt;/angularVelocity</c>, <c>/acceleration</c> and <c>/attitude</c>. State is fed by <see cref="Joycon2Manager"/>.
    /// </summary>
#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoad]
#endif
    [InputControlLayout(stateType = typeof(Joycon2GamepadState), displayName = "Joy-Con 2", canRunInBackground = true)]
    public class Joycon2Gamepad : Gamepad
    {
        public const string InterfaceName = "Joycon2";

        /// <summary>The connection behind this device (calibration, raw report, rate). Set by the manager.</summary>
        public Joycon2Controller Controller { get; internal set; }

        /// <summary>°/s, sideways frame: +x pitches the rail edge down, +y turns right, +z rolls left (Unity signs).</summary>
        public Vector3Control angularVelocity { get; private set; }
        /// <summary>g, sideways frame: x → right, y → up out of the face, z → towards the rail.</summary>
        public Vector3Control acceleration { get; private set; }
        /// <summary>Pose since the last recenter (press Select). Identity = held as when recentered;
        /// <c>attitude * Vector3.up</c> is where the button face points.</summary>
        public QuaternionControl attitude { get; private set; }

        protected override void FinishSetup()
        {
            base.FinishSetup();
            angularVelocity = GetChildControl<Vector3Control>("angularVelocity");
            acceleration = GetChildControl<Vector3Control>("acceleration");
            attitude = GetChildControl<QuaternionControl>("attitude");
        }

        public Joycon2Side Side => Controller?.Side ??
            (description.product != null && description.product.EndsWith("(R)") ? Joycon2Side.Right : Joycon2Side.Left);

        /// <summary>Player LEDs on the Joy-Con: 0 → P1 … 3 → P4, −1 → off.</summary>
        public int PlayerIndex
        {
            get => Controller?.PlayerIndex ?? -1;
            set => Joycon2Manager.SetPlayerIndex(this, value);
        }

        /// <summary>Battery voltage in volts (about 3.3–4.2 V; 0 until the first report).</summary>
        public float BatteryVoltage => Controller?.BatteryVoltage ?? 0f;

        /// <summary>Input reports per second over the last second (~66 natively; browsers may be lower).</summary>
        public float ReportRate => Controller?.ReportRate ?? 0f;

        /// <summary>Makes the current pose the zero of <see cref="attitude"/> (as pressing Select does).</summary>
        public void Recenter() => Controller?.Recenter();

        /// <summary>
        /// Off while the pose steers something, so the zero of <see cref="attitude"/> does not slowly drift to the grip
        /// the player happens to hold (see <see cref="Joycon2Orientation.AutoCenter"/>).
        /// </summary>
        public bool AutoCenter
        {
            get => Controller?.Orientation.AutoCenter ?? false;
            set { if (Controller != null) Controller.Orientation.AutoCenter = value; }
        }

        /// <summary>The Joy-Con 2 gamepads currently connected.</summary>
        public static IEnumerable<Joycon2Gamepad> All
        {
            get
            {
                foreach (var c in Joycon2Manager.Controllers)
                    if (c.Device != null) yield return c.Device;
            }
        }

        static Joycon2Gamepad() => RegisterLayout();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void RegisterLayout() =>
            InputSystem.RegisterLayout<Joycon2Gamepad>(matches: new InputDeviceMatcher().WithInterface(InterfaceName));

        internal static Joycon2GamepadState ToState(in Joycon2PadState pad, in Joycon2Vector3 gyro, in Joycon2Vector3 accel,
                                                    in Joycon2Quaternion attitude) =>
            new Joycon2GamepadState
            {
                gamepad = ToGamepadState(pad),
                angularVelocity = new Vector3(gyro.X, gyro.Y, gyro.Z),
                acceleration = new Vector3(accel.X, accel.Y, accel.Z),
                attitude = new Quaternion(attitude.X, attitude.Y, attitude.Z, attitude.W),
            };

        internal static GamepadState ToGamepadState(in Joycon2PadState pad)
        {
            var state = new GamepadState { leftStick = new Vector2(pad.StickX, pad.StickY) };
            var b = pad.Buttons;
            if ((b & Joycon2PadButtons.South) != 0) state = state.WithButton(GamepadButton.South);
            if ((b & Joycon2PadButtons.East) != 0) state = state.WithButton(GamepadButton.East);
            if ((b & Joycon2PadButtons.North) != 0) state = state.WithButton(GamepadButton.North);
            if ((b & Joycon2PadButtons.West) != 0) state = state.WithButton(GamepadButton.West);
            if ((b & Joycon2PadButtons.LeftShoulder) != 0) state = state.WithButton(GamepadButton.LeftShoulder);
            if ((b & Joycon2PadButtons.RightShoulder) != 0) state = state.WithButton(GamepadButton.RightShoulder);
            if ((b & Joycon2PadButtons.Start) != 0) state = state.WithButton(GamepadButton.Start);
            if ((b & Joycon2PadButtons.Select) != 0) state = state.WithButton(GamepadButton.Select);
            if ((b & Joycon2PadButtons.LeftStickPress) != 0) state = state.WithButton(GamepadButton.LeftStick);
            state.leftTrigger = (b & Joycon2PadButtons.LeftTrigger) != 0 ? 1f : 0f;
            state.rightTrigger = (b & Joycon2PadButtons.RightTrigger) != 0 ? 1f : 0f;
            return state;
        }
    }
}
