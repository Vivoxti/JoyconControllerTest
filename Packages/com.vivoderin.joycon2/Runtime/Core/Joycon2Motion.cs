using System;

namespace Joycon2
{
    /// <summary>Engine-free 3-vector (the core does not reference UnityEngine).</summary>
    public struct Joycon2Vector3
    {
        public float X, Y, Z;
        public Joycon2Vector3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public float Magnitude => (float)Math.Sqrt(X * X + Y * Y + Z * Z);
        public override string ToString() => $"({X:0.00}, {Y:0.00}, {Z:0.00})";
    }

    /// <summary>
    /// Maps the IMU's own axes onto the sideways grip frame, per side.
    ///
    /// Sideways frame (Unity convention, left-handed): X → right, Y → up out of the button face,
    /// Z → forward (towards the SL/SR rail). Acceleration is the reaction to gravity: lying flat face up
    /// reads (0, +1, 0) g. Angular velocity follows Unity's rotation sign: +X pitches the rail edge down,
    /// +Y turns right, +Z rolls left (counter-clockwise as the player sees it).
    ///
    /// IMU frame (no source documents it; derived on hardware, 2026-10-13): right-handed, with the Joy-Con
    /// held upright and its face towards you +Y points to the top (shoulder-button end), +X to the right
    /// edge, +Z out of the button face. The first guess (+X to the top) put every tilt 90° off.
    /// Checked by tilting one Joy-Con with the sun test; the other side follows by symmetry (same IMU
    /// mounting assumed) — confirm it too. See PROTOCOL.md for the check procedure.
    /// </summary>
    public struct Joycon2MotionAxes
    {
        /// <summary>Source axis per output axis: ±1 = ±IMU X, ±2 = ±IMU Y, ±3 = ±IMU Z.</summary>
        public sbyte X, Y, Z;

        public Joycon2MotionAxes(sbyte x, sbyte y, sbyte z) { X = x; Y = y; Z = z; }

        // Left Joy-Con is rotated 90° counter-clockwise: IMU +Y (top) → left, IMU +X (right edge, the rail) → forward.
        public static Joycon2MotionAxes SidewaysLeft = new Joycon2MotionAxes(-2, 3, 1);
        // Right Joy-Con is rotated 90° clockwise: IMU +Y (top) → right, IMU +X (right edge) → towards the player.
        public static Joycon2MotionAxes SidewaysRight = new Joycon2MotionAxes(2, 3, -1);

        public static Joycon2MotionAxes For(Joycon2Side side) => side == Joycon2Side.Left ? SidewaysLeft : SidewaysRight;

        /// <summary>Maps a polar vector (acceleration).</summary>
        public Joycon2Vector3 MapVector(float x, float y, float z) =>
            new Joycon2Vector3(Pick(X, x, y, z), Pick(Y, x, y, z), Pick(Z, x, y, z));

        /// <summary>Maps an angular velocity measured with the right-hand rule into Unity's left-handed sign.</summary>
        public Joycon2Vector3 MapAngular(float x, float y, float z) =>
            new Joycon2Vector3(-Pick(X, x, y, z), -Pick(Y, x, y, z), -Pick(Z, x, y, z));

        static float Pick(sbyte axis, float x, float y, float z)
        {
            float v = Math.Abs(axis) == 1 ? x : Math.Abs(axis) == 2 ? y : z;
            return axis < 0 ? -v : v;
        }
    }

    /// <summary>
    /// Gyro bias estimate: while the controller rests (small rotation, ~1 g), slowly track the gyro
    /// reading as its zero. Nothing in flash is documented for this (PROTOCOL.md).
    /// </summary>
    public sealed class Joycon2GyroBias
    {
        public float StillRateThreshold = 6f;   // °/s per axis
        public float StillAccelTolerance = 0.06f; // |a| within 1 ± this, in g
        public float SettleTime = 0.5f;         // seconds of stillness before learning
        public float TimeConstant = 2f;         // seconds

        public float X { get; private set; }
        public float Y { get; private set; }
        public float Z { get; private set; }
        public bool IsStill => stillFor >= SettleTime;

        float stillFor;

        /// <param name="gx">Raw gyro in °/s (IMU frame).</param>
        /// <param name="accelMagnitude">|accel| in g.</param>
        public void Update(float gx, float gy, float gz, float accelMagnitude, float dt)
        {
            if (dt <= 0f || dt > 0.5f) return; // first sample or a stall: no meaningful step
            bool still = Math.Abs(gx - X) < StillRateThreshold && Math.Abs(gy - Y) < StillRateThreshold &&
                         Math.Abs(gz - Z) < StillRateThreshold && Math.Abs(accelMagnitude - 1f) < StillAccelTolerance;
            stillFor = still ? stillFor + dt : 0f;
            if (!IsStill) return;

            float k = Math.Min(1f, dt / TimeConstant);
            X += (gx - X) * k;
            Y += (gy - Y) * k;
            Z += (gz - Z) * k;
        }

        public void Reset()
        {
            X = Y = Z = 0f;
            stillFor = 0f;
        }
    }

    public static class Joycon2MotionMapping
    {
        /// <summary>Report IMU → sideways frame: angular velocity in °/s (bias removed), acceleration in g.</summary>
        public static void Sideways(Joycon2Side side, in Joycon2Report r, Joycon2GyroBias bias,
                                    out Joycon2Vector3 angularVelocity, out Joycon2Vector3 acceleration)
        {
            if (!r.HasMotion)
            {
                angularVelocity = default;
                acceleration = default;
                return;
            }
            var axes = Joycon2MotionAxes.For(side);
            float gx = r.GyroX * Joycon2Report.GyroDpsPerLsb - (bias?.X ?? 0f);
            float gy = r.GyroY * Joycon2Report.GyroDpsPerLsb - (bias?.Y ?? 0f);
            float gz = r.GyroZ * Joycon2Report.GyroDpsPerLsb - (bias?.Z ?? 0f);
            angularVelocity = axes.MapAngular(gx, gy, gz);
            acceleration = axes.MapVector(r.AccelX * Joycon2Report.AccelGPerLsb, r.AccelY * Joycon2Report.AccelGPerLsb,
                                          r.AccelZ * Joycon2Report.AccelGPerLsb);
        }
    }
}
