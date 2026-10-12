using System;

namespace Joycon2
{
    /// <summary>
    /// Engine-free quaternion with Unity's algebra and conventions (same product, same AngleAxis form),
    /// so values convert 1:1 to <c>UnityEngine.Quaternion</c>.
    /// </summary>
    public struct Joycon2Quaternion
    {
        public float X, Y, Z, W;

        public Joycon2Quaternion(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }

        public static readonly Joycon2Quaternion Identity = new Joycon2Quaternion(0f, 0f, 0f, 1f);

        public static Joycon2Quaternion operator *(Joycon2Quaternion a, Joycon2Quaternion b) => new Joycon2Quaternion(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y + a.Y * b.W + a.Z * b.X - a.X * b.Z,
            a.W * b.Z + a.Z * b.W + a.X * b.Y - a.Y * b.X,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        /// <summary>Inverse of a unit quaternion.</summary>
        public Joycon2Quaternion Conjugate => new Joycon2Quaternion(-X, -Y, -Z, W);

        public Joycon2Quaternion Normalized
        {
            get
            {
                float m = (float)Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
                return m < 1e-9f ? Identity : new Joycon2Quaternion(X / m, Y / m, Z / m, W / m);
            }
        }

        public Joycon2Vector3 Rotate(Joycon2Vector3 v)
        {
            // v' = v + 2w(u × v) + 2u × (u × v)
            var u = new Joycon2Vector3(X, Y, Z);
            var t = Scale(Cross(u, v), 2f);
            var c = Cross(u, t);
            return new Joycon2Vector3(v.X + W * t.X + c.X, v.Y + W * t.Y + c.Y, v.Z + W * t.Z + c.Z);
        }

        /// <summary>Rotation by |r| radians about r (r in radians, any length).</summary>
        public static Joycon2Quaternion FromRotationVector(Joycon2Vector3 r)
        {
            float angle = r.Magnitude;
            if (angle < 1e-9f) return Identity;
            float s = (float)Math.Sin(angle * 0.5f) / angle;
            return new Joycon2Quaternion(r.X * s, r.Y * s, r.Z * s, (float)Math.Cos(angle * 0.5f));
        }

        /// <summary>Rotation vector (axis × angle in radians) of the shortest rotation taking unit vector a onto b.</summary>
        public static Joycon2Vector3 RotationBetween(Joycon2Vector3 a, Joycon2Vector3 b)
        {
            var axis = Cross(a, b);
            float sin = axis.Magnitude;
            float cos = a.X * b.X + a.Y * b.Y + a.Z * b.Z;
            float angle = (float)Math.Atan2(sin, cos);
            if (sin < 1e-6f) return default; // parallel (or opposite: no defined axis, leave it to the next sample)
            return Scale(axis, angle / sin);
        }

        /// <summary>Euler angles in degrees, Unity order (Z, X, Y) — for display only.</summary>
        public Joycon2Vector3 EulerDegrees
        {
            get
            {
                const float rad = 180f / (float)Math.PI;
                float sinX = 2f * (W * X - Y * Z);
                float x = Math.Abs(sinX) >= 1f ? Math.Sign(sinX) * 90f : (float)Math.Asin(sinX) * rad;
                float y = (float)Math.Atan2(2f * (W * Y + X * Z), 1f - 2f * (X * X + Y * Y)) * rad;
                float z = (float)Math.Atan2(2f * (W * Z + X * Y), 1f - 2f * (X * X + Z * Z)) * rad;
                return new Joycon2Vector3(x, y, z);
            }
        }

        internal static Joycon2Vector3 Cross(Joycon2Vector3 a, Joycon2Vector3 b) =>
            new Joycon2Vector3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        internal static Joycon2Vector3 Scale(Joycon2Vector3 v, float k) => new Joycon2Vector3(v.X * k, v.Y * k, v.Z * k);

        public override string ToString() => $"({X:0.000}, {Y:0.000}, {Z:0.000}, {W:0.000})";
    }

    /// <summary>
    /// Orientation of one Joy-Con in the sideways frame: gyro integration, with pitch/roll slowly pulled
    /// towards gravity whenever the controller is not being shaken (yaw has no reference and may drift).
    /// <see cref="Recenter"/> makes the current pose the zero of <see cref="Relative"/>.
    /// </summary>
    public sealed class Joycon2Orientation
    {
        /// <summary>Seconds for a tilt error to be mostly corrected from the accelerometer (0 = gyro only).</summary>
        public float GravityCorrectionTime = 0.8f;
        /// <summary>Accelerometer readings further than this from 1 g (movement, shakes) are not trusted.</summary>
        public float GravityTolerance = 0.15f;

        /// <summary>The zero is set automatically once, this long after motion data starts (tilt has settled by then).</summary>
        public float InitialCenterDelay = 1.5f;
        /// <summary>Seconds for the zero to drift to the pose the controller is usually held in (0 = never).</summary>
        public float AutoCenterTime = 20f;
        /// <summary>Only poses within this many degrees of the zero count as the usual grip; bigger, deliberate
        /// tilts do not move it.</summary>
        public float AutoCenterMaxAngle = 30f;
        /// <summary>Clear while the pose is steering something, so the zero holds still under the player.</summary>
        public bool AutoCenter = true;

        static readonly Joycon2Vector3 Up = new Joycon2Vector3(0f, 1f, 0f);
        static readonly Joycon2Vector3 Right = new Joycon2Vector3(1f, 0f, 0f);
        static readonly Joycon2Vector3 Forward = new Joycon2Vector3(0f, 0f, 1f);

        float runningTime;
        bool initialCenterDone;

        /// <summary>Controller → world (world up = gravity up; heading relative to the first sample, may drift).</summary>
        public Joycon2Quaternion Current { get; private set; } = Joycon2Quaternion.Identity;
        /// <summary>The zero pose, heading removed (see <see cref="Level"/>).</summary>
        public Joycon2Quaternion Reference { get; private set; } = Joycon2Quaternion.Identity;
        /// <summary>
        /// Controller → zero pose, ignoring heading: identity right after <see cref="Recenter"/>, and turning
        /// around (or gyro heading drift) does not change it — only tilting does.
        /// </summary>
        public Joycon2Quaternion Relative => (Reference.Conjugate * Level(Current)).Normalized;

        /// <param name="angularVelocity">°/s, sideways frame, Unity signs.</param>
        /// <param name="acceleration">g, sideways frame (reaction to gravity: points up at rest).</param>
        public void Update(Joycon2Vector3 angularVelocity, Joycon2Vector3 acceleration, float dt)
        {
            if (dt <= 0f || dt > 0.25f) return; // first sample or a stall
            const float deg2rad = (float)Math.PI / 180f;
            var q = Current * Joycon2Quaternion.FromRotationVector(Joycon2Quaternion.Scale(angularVelocity, dt * deg2rad));

            float g = acceleration.Magnitude;
            if (GravityCorrectionTime > 0f && g > 1e-3f && Math.Abs(g - 1f) < GravityTolerance)
            {
                // Rotate the estimate so that the up it predicts (in controller space) moves towards the measured one.
                var measured = Joycon2Quaternion.Scale(acceleration, 1f / g);
                var predicted = q.Conjugate.Rotate(Up);
                var error = Joycon2Quaternion.RotationBetween(measured, predicted);
                float k = Math.Min(1f, dt / GravityCorrectionTime);
                q = q * Joycon2Quaternion.FromRotationVector(Joycon2Quaternion.Scale(error, k));
            }
            Current = q.Normalized;

            runningTime += dt;
            if (!initialCenterDone && runningTime >= InitialCenterDelay)
            {
                initialCenterDone = true;
                Recenter();
            }
            else if (initialCenterDone && AutoCenter && AutoCenterTime > 0f)
            {
                var level = Level(Current);
                if (AngleDegrees(Reference.Conjugate * level) < AutoCenterMaxAngle)
                    Reference = Nlerp(Reference, level, Math.Min(1f, dt / AutoCenterTime));
            }
        }

        /// <summary>Makes the current pose the zero.</summary>
        public void Recenter()
        {
            Reference = Level(Current);
            initialCenterDone = true;
        }

        public void Reset()
        {
            Current = Reference = Joycon2Quaternion.Identity;
            runningTime = 0f;
            initialCenterDone = false;
        }

        /// <summary>The same pose turned about world up so that its heading is zero (right axis along +X).</summary>
        public static Joycon2Quaternion Level(Joycon2Quaternion q)
        {
            var r = q.Rotate(Right);
            float heading;
            if (r.X * r.X + r.Z * r.Z > 0.09f) heading = (float)Math.Atan2(-r.Z, r.X);
            else
            {
                // Rolled almost upright: the right axis has no heading, use the forward one.
                var f = q.Rotate(Forward);
                heading = (float)Math.Atan2(f.X, f.Z);
            }
            return (Joycon2Quaternion.FromRotationVector(new Joycon2Vector3(0f, -heading, 0f)) * q).Normalized;
        }

        public static float AngleDegrees(Joycon2Quaternion q) =>
            2f * (float)Math.Acos(Math.Min(1f, Math.Abs(q.Normalized.W))) * 180f / (float)Math.PI;

        static Joycon2Quaternion Nlerp(Joycon2Quaternion a, Joycon2Quaternion b, float t)
        {
            float dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
            float s = dot < 0f ? -1f : 1f;
            return new Joycon2Quaternion(a.X + (s * b.X - a.X) * t, a.Y + (s * b.Y - a.Y) * t,
                                         a.Z + (s * b.Z - a.Z) * t, a.W + (s * b.W - a.W) * t).Normalized;
        }
    }
}
