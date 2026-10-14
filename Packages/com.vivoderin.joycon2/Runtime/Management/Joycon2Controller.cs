namespace Joycon2
{
    /// <summary>
    /// One BLE connection to a Joy-Con 2, from "connecting" until it is dropped. Owned by <see cref="Joycon2Manager"/>;
    /// games normally use the <see cref="Joycon2Gamepad"/> behind <see cref="Device"/> instead.
    /// </summary>
    public sealed class Joycon2Controller
    {
        internal Joycon2Controller(int handle, ulong address, string addressText, Joycon2Side? side, double now)
        {
            Handle = handle;
            Address = address;
            AddressText = addressText;
            Side = side ?? Joycon2Side.Left;
            ConnectStartTime = now;
            rateWindowStart = now;
        }

        public int Handle { get; internal set; }
        /// <summary>Bluetooth address (Windows, Android) or a per-process id (macOS, iOS, web) — see <see cref="AddressText"/>.</summary>
        public ulong Address { get; }
        public string AddressText { get; }
        /// <summary>L or R. Over Web Bluetooth it may only be known once the handshake read the controller's info block.</summary>
        public Joycon2Side Side { get; internal set; }
        public string Name => Side == Joycon2Side.Left ? "Joy-Con 2 (L)" : "Joy-Con 2 (R)";
        public Joycon2Session Session { get; internal set; }
        /// <summary>The Input System device; created once the handshake is done, removed when the link drops.</summary>
        public Joycon2Gamepad Device { get; internal set; }
        public bool IsReady => Device != null;
        /// <summary>Player LEDs: 0 → P1 … 3 → P4, −1 = off. Set through <see cref="Joycon2Manager.SetPlayerIndex"/>.</summary>
        public int PlayerIndex { get; internal set; } = -1;

        /// <summary>ATT MTU (0 where the platform does not report it).</summary>
        public int Mtu { get; internal set; }
        /// <summary>Input reports per second, measured over the last second.</summary>
        public float ReportRate { get; internal set; }
        public int WriteFailures { get; internal set; }
        public int LastReportLength { get; internal set; }
        public double ConnectStartTime { get; internal set; }
        /// <summary>Battery voltage in volts from the last report (0 before the first one).</summary>
        public float BatteryVoltage => Session != null && Session.HasReport ? Session.LastReport.BatteryMillivolts / 1000f : 0f;

        /// <summary>Gyro zero, learned while the controller rests.</summary>
        public Joycon2GyroBias GyroBias { get; } = new Joycon2GyroBias();
        /// <summary>Latest angular velocity in the sideways frame (°/s, bias removed); zero until motion data arrives.</summary>
        public Joycon2Vector3 AngularVelocity { get; private set; }
        /// <summary>Latest acceleration in the sideways frame (g).</summary>
        public Joycon2Vector3 Acceleration { get; private set; }
        /// <summary>Pose from the gyro (+ gravity). Select recenters it when <see cref="Joycon2Settings.RecenterOnSelect"/> is on.</summary>
        public Joycon2Orientation Orientation { get; } = new Joycon2Orientation();

        /// <summary>Makes the current pose the zero of <see cref="Joycon2Gamepad.attitude"/>.</summary>
        public void Recenter() => Orientation.Recenter();

        /// <summary>Being reconnected for a faster link (web on macOS); the gamepad and its player stay.</summary>
        public bool Refreshing => refreshUntil > 0;

        internal double refreshUntil;     // > 0 while refreshing: give up after this
        internal double refreshConnectAt; // > 0: the link is closed, connect again at this time
        internal double slowSince = -1;
        internal int refreshCount;

        internal long rateWindowCount;
        internal double rateWindowStart;

        double lastReportTime;
        uint lastImuTimestamp;
        bool selectHeld;

        /// <summary>
        /// Folds the session's latest report into the motion state (gyro bias, pose) and returns the gamepad-shaped
        /// result. Call once per new report.
        /// </summary>
        internal Joycon2PadState ProcessReport(bool recenterOnSelect)
        {
            var report = Session.LastReport;
            float dt = MotionDeltaTime(report, Session.LastReportTime);
            if (report.HasMotion)
            {
                float accel = new Joycon2Vector3(report.AccelX, report.AccelY, report.AccelZ).Magnitude * Joycon2Report.AccelGPerLsb;
                GyroBias.Update(report.GyroX * Joycon2Report.GyroDpsPerLsb, report.GyroY * Joycon2Report.GyroDpsPerLsb,
                                report.GyroZ * Joycon2Report.GyroDpsPerLsb, accel, dt);
            }
            Joycon2MotionMapping.Sideways(Side, report, GyroBias, out var gyro, out var acceleration);
            AngularVelocity = gyro;
            Acceleration = acceleration;
            if (report.HasMotion) Orientation.Update(gyro, acceleration, dt);

            var pad = Joycon2Mapping.Sideways(Side, report, Session.Calibration);
            bool select = (pad.Buttons & Joycon2PadButtons.Select) != 0;
            if (select && !selectHeld && recenterOnSelect) Orientation.Recenter();
            selectHeld = select;
            return pad;
        }

        /// <summary>Seconds since the previous report: the IMU's own µs clock when it looks sane, else host time.</summary>
        float MotionDeltaTime(in Joycon2Report report, double now)
        {
            double hostDt = lastReportTime > 0 ? now - lastReportTime : 0;
            uint imuDelta = report.ImuTimestampMicros - lastImuTimestamp; // wraps correctly
            bool imuValid = lastImuTimestamp != 0 && imuDelta > 0 && imuDelta < 100_000;
            lastReportTime = now;
            lastImuTimestamp = report.ImuTimestampMicros;
            return imuValid ? imuDelta * 1e-6f : (float)hostDt;
        }

        internal void CountReport(int length)
        {
            LastReportLength = length;
            rateWindowCount++;
        }

        internal void UpdateRate(double now)
        {
            if (now - rateWindowStart < 1.0) return;
            ReportRate = (float)(rateWindowCount / (now - rateWindowStart));
            rateWindowCount = 0;
            rateWindowStart = now;
        }

        internal void ResetRate(double now)
        {
            ReportRate = 0;
            rateWindowCount = 0;
            rateWindowStart = now;
        }

        public override string ToString() => $"{Name} {AddressText}";
    }
}
