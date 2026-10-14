using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Joycon2
{
    /// <summary>One BLE connection to a Joy-Con 2 (from "connecting" until it is dropped).</summary>
    public sealed class Joycon2Controller
    {
        public int Handle { get; internal set; }
        public ulong Address { get; internal set; }
        public Joycon2Side Side { get; internal set; }
        public Joycon2Session Session { get; internal set; }
        /// <summary>Created once the handshake is done; removed when the link drops.</summary>
        public Joycon2Gamepad Device { get; internal set; }
        public int Mtu { get; internal set; }
        public int PlayerIndex { get; internal set; } = -1;
        /// <summary>Input reports per second, measured over the last second.</summary>
        public float ReportRate { get; internal set; }
        public int WriteFailures { get; internal set; }
        public int LastReportLength { get; internal set; }
        public double ConnectStartTime { get; internal set; }
        /// <summary>Gyro zero, learned while the controller rests.</summary>
        public Joycon2GyroBias GyroBias { get; } = new Joycon2GyroBias();
        /// <summary>Latest IMU in the sideways frame (°/s, g); zero until motion data arrives.</summary>
        public Joycon2Vector3 AngularVelocity { get; internal set; }
        public Joycon2Vector3 Acceleration { get; internal set; }

        /// <summary>Pose from the gyro (+ gravity); Select recenters it (see <see cref="Joycon2Manager.RecenterOnSelect"/>).</summary>
        public Joycon2Orientation Orientation { get; } = new Joycon2Orientation();

        internal double lastReportTime;
        internal uint lastImuTimestamp;
        internal bool selectHeld;

        internal long rateWindowCount;
        internal double rateWindowStart;

        /// <summary>Being reconnected for a faster link (<see cref="Joycon2Manager.RefreshSlowLinks"/>); the gamepad stays.</summary>
        public bool Refreshing => refreshUntil > 0;
        internal double refreshUntil;     // > 0 while refreshing: give up after this
        internal double refreshConnectAt; // > 0: the link is closed, connect again at this time
        internal double slowSince = -1;
        internal int refreshCount;

        public string Name => Side == Joycon2Side.Left ? "Joy-Con 2 (L)" : "Joy-Con 2 (R)";
        public string AddressText => Joycon2Manager.FormatAddress(Address);
    }

    /// <summary>
    /// Owns the platform transport, connects Joy-Con 2 controllers as they appear (SYNC held), runs their
    /// sessions and feeds them into the Input System as <see cref="Joycon2Gamepad"/> devices.
    /// Starts automatically before the first scene loads; everything runs on the main thread from
    /// <see cref="InputSystem.onBeforeUpdate"/>.
    /// </summary>
    public static class Joycon2Manager
    {
        /// <summary>Set to false (e.g. from a SubsystemRegistration hook) to start manually with <see cref="Start"/>.</summary>
        public static bool AutoStart = true;
        /// <summary>Scanning stops once this many controllers are connected (scanning degrades live links).</summary>
        public static int MaxControllers = 2;
        /// <summary>Only connect to controllers advertising in pairing mode — a Joy-Con only accepts us then.</summary>
        public static bool PairingModeOnly = true;
        /// <summary>Select (Capture on L, Home on R) makes the current pose the zero of <c>attitude</c>.</summary>
        public static bool RecenterOnSelect = true;
        public static double ConnectTimeout = 20.0;
        /// <summary>Back-off per address after a failure: Joy-Cons stop responding after rapid retries.</summary>
        public static double RetryCooldown = 3.0;
        public static Joycon2SessionOptions SessionOptions = new Joycon2SessionOptions();

        /// <summary>
        /// Web only. macOS fixes a link's interval when the link is created, and makes it fast (15 ms) only if another app
        /// already holds a low-latency link then (Native/macos joycon2boost). So with the helper running the first Joy-Con
        /// stays at ~33 Hz while the second gets ~66: reconnect a link that is much slower than another one. The
        /// gamepad and its player stay; only the link is rebuilt. The Joy-Con only accepts the new link in SYNC: show
        /// <see cref="Joycon2Controller.Refreshing"/> as "press SYNC on it" (picking it in the chooser again also works).
        /// Turn it off while a refresh would get in the way (mid-round); one already running goes on.
        /// </summary>
        public static bool RefreshSlowLinks = true;
        public static float SlowLinkRate = 45f;
        public static float FastLinkRate = 55f;
        public static double SlowLinkSeconds = 4.0;
        public static int MaxLinkRefreshes = 3;
        /// <summary>Pause between closing and reopening: Chrome would reattach to the old link while it is still up.</summary>
        public static double RefreshDelay = 1.5;
        public static double RefreshAttemptTimeout = 8.0;
        public static double RefreshTimeout = 120.0;

        public static IJoycon2Transport Transport { get; private set; }
        public static bool IsRunning { get; private set; }
        /// <summary>
        /// The transport cannot scan in the background (WebGL): each controller is added by
        /// <see cref="RequestController"/>, which has to be called right after a click or key press.
        /// </summary>
        public static bool ConnectsOnRequest => Transport != null && Transport.RequiresUserGesture;
        /// <summary>A <see cref="RequestController"/> call would open the chooser now.</summary>
        public static bool CanRequestController => IsRunning && ConnectsOnRequest && !pickPending && !WebPickerBusy &&
                                                   (controllers.Count < MaxControllers || AnyRefreshing);
        static bool WebPickerBusy
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return Joycon2WebControls.IsPicking;
#else
                return false;
#endif
            }
        }
        static bool AnyRefreshing
        {
            get
            {
                foreach (var c in controllers)
                    if (c.Refreshing) return true;
                return false;
            }
        }
        public static bool IsScanning { get; private set; }
        public static string Status { get; private set; } = "stopped";
        public static IReadOnlyList<Joycon2Controller> Controllers => controllers;

        /// <summary>Raised on the main thread for every parsed input report (debug tools, recorders).</summary>
        public static event Action<Joycon2Controller, byte[], int> RawReport;

        static readonly List<Joycon2Controller> controllers = new List<Joycon2Controller>();
        static readonly Dictionary<ulong, double> cooldownUntil = new Dictionary<ulong, double>();
        static readonly HashSet<ulong> ignoredNotPairing = new HashSet<ulong>();
        static readonly byte[] payload = new byte[512];
        static double scanRetryAt;
        static bool pickPending;
        static GameObject host;

        const int PickAny = 0, PickLeft = 1, PickRight = 2;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            // Survive "Enter Play Mode" without domain reload.
            Stop();
            AutoStart = true;
            RawReport = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void AutoInitialize()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (AutoStart) Joycon2AndroidLifecycle.EnsureStarted();
#else
            if (AutoStart) Start();
#endif
#if UNITY_WEBGL && !UNITY_EDITOR
            // Also show browser/HTTPS failures when the transport did not start.
            Joycon2WebControls.EnsureStarted();
#endif
        }

#if UNITY_EDITOR
        // Never leave BLE links open in edit mode or across a script reload.
        [UnityEditor.InitializeOnLoadMethod]
        static void HookEditor()
        {
            UnityEditor.EditorApplication.playModeStateChanged += change =>
            {
                if (change == UnityEditor.PlayModeStateChange.ExitingPlayMode) Stop();
            };
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Stop;
        }
#endif

        public static void Start()
        {
            if (IsRunning) return;
            Transport = CreateTransport();
            if (!Transport.Initialize())
            {
                Status = $"no BLE transport ({Transport.Name})";
                Debug.Log($"[Joycon2] {Status} on {Application.platform}; Joy-Con 2 support is off.");
                Transport = null;
                return;
            }

            IsRunning = true;
            Status = "running";
            scanRetryAt = 0;
            pickPending = false;
            InputSystem.onBeforeUpdate += Pump;

            host = new GameObject("Joycon2 Manager");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<Joycon2Host>();
            host.AddComponent<Joycon2DebugOverlay>();
            Debug.Log(ConnectsOnRequest
                ? $"[Joycon2] started ({Transport.Name}). Hold SYNC on a Joy-Con 2, then pick it in the browser chooser (RequestController)."
                : $"[Joycon2] started ({Transport.Name}). Hold SYNC on a Joy-Con 2 to connect it.");
        }

        public static void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            InputSystem.onBeforeUpdate -= Pump;

            foreach (var c in controllers)
                if (c.Device != null && c.Device.added) InputSystem.RemoveDevice(c.Device);
            controllers.Clear();
            cooldownUntil.Clear();
            ignoredNotPairing.Clear();

            Transport.Shutdown();
            Transport = null;
            IsScanning = false;
            Status = "stopped";

            var h = host;
            host = null;
            if (h != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(h);
                else UnityEngine.Object.DestroyImmediate(h);
            }
        }

        /// <summary>Lights the player LEDs (0 → P1 …) on the Joy-Con behind a device. Ignored for other devices.</summary>
        public static void SetPlayerIndex(InputDevice device, int playerIndex)
        {
            foreach (var c in controllers)
            {
                if (c.Device != device) continue;
                c.PlayerIndex = playerIndex;
                c.Session.PlayerLed = Joycon2Protocol.PlayerLedPattern(playerIndex);
            }
        }

        public static void Disconnect(Joycon2Controller controller) => Drop(controller, "disconnected by request", 0);

        /// <summary>
        /// Opens the browser's Bluetooth chooser listing Joy-Con 2 (only <paramref name="side"/>, if given). Only when
        /// <see cref="ConnectsOnRequest"/>; call it from the frame that saw a click or key press (gamepad buttons
        /// do not count as a user gesture in browsers). The picked controller then connects as usual.
        /// </summary>
        public static bool RequestController(Joycon2Side? side = null)
        {
            if (!CanRequestController) return false;
            pickPending = true;
            Status = "Bluetooth chooser open";
            int tag = side == null ? PickAny : side == Joycon2Side.Left ? PickLeft : PickRight;
            Transport.RequestDevice(BuildDeviceRequest(side), tag);
            return true;
        }

        /// <summary>
        /// Web Bluetooth requestDevice options: manufacturer data with the PID (as the reference web demo), or the service
        /// and the exact name. Chrome on macOS also lists devices another app already connected (joycon2boost --connect):
        /// it finds them by the services in the filters, and they have no advertisement data to match.
        /// </summary>
        internal static string BuildDeviceRequest(Joycon2Side? side)
        {
            var filters = new StringBuilder();
            void Filter(ushort pid, string name)
            {
                if (filters.Length > 0) filters.Append(',');
                // Manufacturer data after the company id: [3..4] VID, [5..6] PID — match the PID only.
                filters.Append("{\"manufacturerData\":[{\"companyIdentifier\":").Append(Joycon2Protocol.CompanyId)
                       .Append(",\"dataPrefix\":[0,0,0,0,0,").Append(pid & 0xFF).Append(',').Append(pid >> 8)
                       .Append("],\"mask\":[0,0,0,0,0,255,255]}]},{\"services\":[\"").Append(Joycon2Protocol.ServiceUuid)
                       .Append("\"],\"name\":\"").Append(name).Append("\"}");
            }
            if (side != Joycon2Side.Right) Filter(Joycon2Protocol.ProductIdLeft, "Joy-Con 2 (L)");
            if (side != Joycon2Side.Left) Filter(Joycon2Protocol.ProductIdRight, "Joy-Con 2 (R)");
            return $"{{\"filters\":[{filters}],\"optionalServices\":[\"{Joycon2Protocol.ServiceUuid}\"]}}";
        }

        static IJoycon2Transport CreateTransport()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            return new Joycon2NativeTransport();
#elif UNITY_WEBGL
            return new Joycon2WebBluetoothTransport();
#elif UNITY_ANDROID
            return new Joycon2AndroidTransport();
#else
            return new Joycon2NullTransport();
#endif
        }

        // ───────────────────────────── Pump ─────────────────────────────

        static void Pump()
        {
            if (!IsRunning) return;
            double now = Time.realtimeSinceStartupAsDouble;

            while (IsRunning && Transport.TryDequeueEvent(out var e, payload))
                HandleEvent(e, Math.Min(e.Length, payload.Length), now);

            for (int i = controllers.Count - 1; i >= 0; i--)
                UpdateController(controllers[i], now);

            if (ConnectsOnRequest) CheckSlowLinks(now);
            UpdateScanning(now);
        }

        static void HandleEvent(in Joycon2TransportEvent e, int length, double now)
        {
            Joycon2Controller c = e.Handle > 0 ? Find(e.Handle) : null;
            switch (e.Type)
            {
                case Joycon2TransportEventType.Log:
                    string text = "[Joycon2/native] " + Encoding.UTF8.GetString(payload, 0, length);
                    if (e.Status >= 2) Debug.LogError(text);
                    else if (e.Status == 1) Debug.LogWarning(text);
                    else Debug.Log(text);
                    break;

                case Joycon2TransportEventType.DeviceFound:
                    OnDeviceFound(e, length, now);
                    break;

                case Joycon2TransportEventType.DevicePicked:
                    OnDevicePicked(e, length, now);
                    break;

                case Joycon2TransportEventType.Connected:
                    if (c == null) break;
                    c.Mtu = e.Status;
                    Debug.Log($"[Joycon2] {c.Name} {c.AddressText} connected (MTU {(c.Mtu > 0 ? c.Mtu.ToString() : "n/a")}), handshake…");
                    c.Session.OnConnected(now);
                    break;

                case Joycon2TransportEventType.ConnectFailed:
                    if (c != null && c.Refreshing) RetryRefresh(c, now, $"connect failed (0x{e.Status:X8})");
                    else if (c != null) Drop(c, $"connect failed (0x{e.Status:X8})", RetryCooldown);
                    break;

                case Joycon2TransportEventType.Disconnected:
                    if (c != null && c.Refreshing) RetryRefresh(c, now, "link lost");
                    else if (c != null) Drop(c, "link lost", RetryCooldown);
                    break;

                case Joycon2TransportEventType.Subscribed:
                    c?.Session.OnSubscribed(e.Tag, e.Status, now);
                    break;

                case Joycon2TransportEventType.Notification:
                    if (c != null && c.Session.OnNotification(e.Tag, payload, length, now))
                        OnReport(c, length);
                    break;

                case Joycon2TransportEventType.WriteFailed:
                    if (c != null && c.WriteFailures++ == 0)
                        Debug.LogWarning($"[Joycon2] {c.Name}: write failed (status {e.Status})");
                    break;

                case Joycon2TransportEventType.ScanStopped:
                    if (e.Status != 0 && IsScanning)
                    {
                        IsScanning = false;
                        scanRetryAt = now + 5;
                        Status = $"scan stopped (error {e.Status}) — is Bluetooth on?";
                        Debug.LogWarning($"[Joycon2] {Status}");
                    }
                    break;
            }
        }

        static void OnDeviceFound(in Joycon2TransportEvent e, int length, double now)
        {
            if (!Joycon2Advertisement.TryParse(payload, length, out var adv)) return;
            if (PairingModeOnly && !adv.PairingMode)
            {
                if (ignoredNotPairing.Add(e.Address))
                    Debug.Log($"[Joycon2] {(adv.Side == Joycon2Side.Left ? "Joy-Con 2 (L)" : "Joy-Con 2 (R)")} " +
                              $"{FormatAddress(e.Address)} is looking for its console — hold SYNC to connect it here.");
                return;
            }
            if (controllers.Count >= MaxControllers) return;
            foreach (var existing in controllers)
                if (existing.Address == e.Address) return;
            if (cooldownUntil.TryGetValue(e.Address, out double until) && now < until) return;

            var c = BeginConnect(e.Address, adv.Side, now);
            if (c != null) Debug.Log($"[Joycon2] found {c.Name} {c.AddressText} (RSSI {e.Rssi} dBm), connecting…");
        }

        static void OnDevicePicked(in Joycon2TransportEvent e, int length, double now)
        {
            pickPending = false;
            string text = Encoding.UTF8.GetString(payload, 0, length);
            if (e.Status != 0)
            {
                if (e.Status == 1) Debug.Log($"[Joycon2] no Joy-Con picked ({text})");
                else Debug.LogWarning($"[Joycon2] Bluetooth chooser failed: {text}");
                return;
            }

            // The chooser lists L and R together and Chrome may not know the name ("Unknown or unsupported
            // device"); then the session reads L/R from the controller itself.
            Joycon2Side? side = null;
            if (e.Tag == PickLeft || text.EndsWith("(L)")) side = Joycon2Side.Left;
            else if (e.Tag == PickRight || text.EndsWith("(R)")) side = Joycon2Side.Right;

            foreach (var existing in controllers)
                if (existing.Address == e.Address)
                {
                    if (existing.Refreshing && existing.Session.State != Joycon2Session.SessionState.Ready)
                    {
                        Debug.Log($"[Joycon2] picked {existing.Name} {existing.AddressText} again, reconnecting it now");
                        Transport.Disconnect(existing.Handle);
                        existing.refreshConnectAt = now;
                    }
                    else Debug.Log($"[Joycon2] {existing.Name} {existing.AddressText} is already connected");
                    return;
                }
            if (controllers.Count >= MaxControllers) return;

            var c = BeginConnect(e.Address, side, now);
            if (c != null) Debug.Log($"[Joycon2] picked {(side == null ? "a Joy-Con 2 (side from the controller)" : c.Name)} " +
                                     $"{c.AddressText} (name \"{text}\"), connecting…");
        }

        static Joycon2Controller BeginConnect(ulong address, Joycon2Side? side, double now)
        {
            int handle = Transport.Connect(address);
            if (handle <= 0) return null;

            var c = new Joycon2Controller
            {
                Handle = handle,
                Address = address,
                Side = side ?? Joycon2Side.Left,
                ConnectStartTime = now,
                rateWindowStart = now,
            };
            c.Session = NewSession(c, side);
            controllers.Add(c);
            return c;
        }

        static Joycon2Session NewSession(Joycon2Controller c, Joycon2Side? side) =>
            new Joycon2Session(new TransportLink(c.Handle), side, SessionOptions)
            {
                Log = message => Debug.Log($"[Joycon2] {c.AddressText} {message}"),
            };

        static void UpdateController(Joycon2Controller c, double now)
        {
            if (c.refreshConnectAt > 0)
            {
                ReconnectWhenDue(c, now);
                return;
            }

            var session = c.Session;
            session.Update(now);
            c.Side = session.Side; // may be learned during the handshake

            if (session.State == Joycon2Session.SessionState.Failed)
            {
                if (c.Refreshing) RetryRefresh(c, now, session.Status);
                else Drop(c, session.Status, RetryCooldown);
                return;
            }
            if (session.State != Joycon2Session.SessionState.Ready)
            {
                if (c.Refreshing && now - c.ConnectStartTime > RefreshAttemptTimeout) RetryRefresh(c, now, $"timed out at step {session.Status}");
                else if (now - c.ConnectStartTime > ConnectTimeout) Drop(c, $"timed out at step {session.Status}", RetryCooldown);
                return;
            }

            if (c.Refreshing)
            {
                c.refreshUntil = 0;
                Debug.Log($"[Joycon2] {c.Name} {c.AddressText} reconnected after {now - c.ConnectStartTime:0.0} s");
            }
            if (c.Device == null) AddDevice(c);

            if (now - c.rateWindowStart >= 1.0)
            {
                c.ReportRate = (float)(c.rateWindowCount / (now - c.rateWindowStart));
                c.rateWindowCount = 0;
                c.rateWindowStart = now;
            }
        }

        // ───────────────────────────── Slow link refresh (web) ─────────────────────────────

        static void CheckSlowLinks(double now)
        {
            bool anyFast = false;
            foreach (var c in controllers)
                if (!c.Refreshing && c.Session.State == Joycon2Session.SessionState.Ready && c.ReportRate >= FastLinkRate)
                    anyFast = true;

            foreach (var c in controllers)
            {
                bool slow = anyFast && !c.Refreshing && c.Session.State == Joycon2Session.SessionState.Ready &&
                            c.ReportRate > 0 && c.ReportRate < SlowLinkRate;
                if (!slow)
                {
                    c.slowSince = -1;
                    continue;
                }
                if (c.slowSince < 0) c.slowSince = now;
                else if (now - c.slowSince >= SlowLinkSeconds && c.refreshCount < MaxLinkRefreshes && RefreshSlowLinks)
                    StartRefresh(c, now);
            }
        }

        static void StartRefresh(Joycon2Controller c, double now)
        {
            c.refreshCount++;
            Debug.Log($"[Joycon2] {c.Name} {c.AddressText}: {c.ReportRate:0} Hz while another Joy-Con is fast — " +
                      $"reconnecting it for a faster link ({c.refreshCount}/{MaxLinkRefreshes})");
            Transport.Disconnect(c.Handle);
            c.refreshUntil = now + RefreshTimeout;
            c.refreshConnectAt = now + RefreshDelay;
            c.slowSince = -1;
            c.ReportRate = 0;
        }

        static void ReconnectWhenDue(Joycon2Controller c, double now)
        {
            if (now > c.refreshUntil)
            {
                c.refreshUntil = 0;
                Drop(c, "not back for a faster link — hold SYNC and pick it again", RetryCooldown);
                return;
            }
            if (now < c.refreshConnectAt) return;

            int handle = Transport.Connect(c.Address);
            if (handle <= 0)
            {
                c.refreshConnectAt = now + 1;
                return;
            }
            c.refreshConnectAt = 0;
            c.Handle = handle;
            c.ConnectStartTime = now;
            c.rateWindowCount = 0;
            c.rateWindowStart = now;
            c.Session = NewSession(c, c.Side);
            c.Session.PlayerLed = Joycon2Protocol.PlayerLedPattern(c.PlayerIndex);
        }

        static void RetryRefresh(Joycon2Controller c, double now, string reason)
        {
            Debug.Log($"[Joycon2] {c.Name} {c.AddressText} reconnect: {reason}, retrying (waiting for SYNC on it)");
            Transport.Disconnect(c.Handle);
            c.refreshConnectAt = now + 1;
        }

        static void AddDevice(Joycon2Controller c)
        {
            if (c.PlayerIndex < 0) c.PlayerIndex = LowestFreePlayerIndex();
            c.Session.PlayerLed = Joycon2Protocol.PlayerLedPattern(c.PlayerIndex);

            var device = InputSystem.AddDevice(new UnityEngine.InputSystem.Layouts.InputDeviceDescription
            {
                interfaceName = Joycon2Gamepad.InterfaceName,
                manufacturer = "Nintendo",
                product = c.Name,
                serial = c.AddressText,
            }) as Joycon2Gamepad;

            if (device == null)
            {
                Drop(c, "Input System did not create a Joycon2Gamepad", RetryCooldown);
                return;
            }
            device.Controller = c;
            c.Device = device;
            Debug.Log($"[Joycon2] {c.Name} {c.AddressText} ready as gamepad #{device.deviceId} " +
                      $"(LED P{c.PlayerIndex + 1}, calibration: {c.Session.CalibratedFrom})");
        }

        static void OnReport(Joycon2Controller c, int length)
        {
            c.LastReportLength = length;
            c.rateWindowCount++;
            RawReport?.Invoke(c, payload, length);

            var report = c.Session.LastReport;
            double now = c.Session.LastReportTime;
            float dt = MotionDeltaTime(c, report, now);
            if (report.HasMotion)
            {
                float accel = new Joycon2Vector3(report.AccelX, report.AccelY, report.AccelZ).Magnitude * Joycon2Report.AccelGPerLsb;
                c.GyroBias.Update(report.GyroX * Joycon2Report.GyroDpsPerLsb, report.GyroY * Joycon2Report.GyroDpsPerLsb,
                                  report.GyroZ * Joycon2Report.GyroDpsPerLsb, accel, dt);
            }
            Joycon2MotionMapping.Sideways(c.Side, report, c.GyroBias, out var gyro, out var acceleration);
            c.AngularVelocity = gyro;
            c.Acceleration = acceleration;
            if (report.HasMotion) c.Orientation.Update(gyro, acceleration, dt);

            var pad = Joycon2Mapping.Sideways(c.Side, report, c.Session.Calibration);
            bool select = (pad.Buttons & Joycon2PadButtons.Select) != 0;
            if (select && !c.selectHeld && RecenterOnSelect) c.Orientation.Recenter();
            c.selectHeld = select;

            if (c.Device == null || !c.Device.added) return;
            InputSystem.QueueStateEvent(c.Device, Joycon2Gamepad.ToState(pad, gyro, acceleration, c.Orientation.Relative));
        }

        /// <summary>Seconds since the previous report: the IMU's own µs clock when it looks sane, else host time.</summary>
        static float MotionDeltaTime(Joycon2Controller c, in Joycon2Report report, double now)
        {
            double hostDt = c.lastReportTime > 0 ? now - c.lastReportTime : 0;
            uint imuDelta = report.ImuTimestampMicros - c.lastImuTimestamp; // wraps correctly
            bool imuValid = c.lastImuTimestamp != 0 && imuDelta > 0 && imuDelta < 100_000;
            c.lastReportTime = now;
            c.lastImuTimestamp = report.ImuTimestampMicros;
            return imuValid ? imuDelta * 1e-6f : (float)hostDt;
        }

        static void Drop(Joycon2Controller c, string reason, double cooldown)
        {
            if (!controllers.Remove(c)) return;
            Debug.Log($"[Joycon2] {c.Name} {c.AddressText} dropped: {reason}");
            if (c.Device != null && c.Device.added) InputSystem.RemoveDevice(c.Device);
            c.Device = null;
            Transport?.Disconnect(c.Handle);
            if (cooldown > 0) cooldownUntil[c.Address] = Time.realtimeSinceStartupAsDouble + cooldown;
        }

        static void UpdateScanning(double now)
        {
            bool want = controllers.Count < MaxControllers;
            if (ConnectsOnRequest)
            {
                if (!pickPending) Status = WebPickerBusy ? "Bluetooth chooser open" :
                    want ? "hold SYNC, then tap Connect L / R (or press J)" : "all controllers connected";
                return;
            }
            if (want && !IsScanning && now >= scanRetryAt)
            {
                Transport.StartScan(Joycon2Protocol.CompanyId);
                IsScanning = true;
                Status = "scanning";
            }
            else if (!want && IsScanning)
            {
                Transport.StopScan();
                IsScanning = false;
                Status = "all controllers connected";
            }
        }

        static int LowestFreePlayerIndex()
        {
            for (int index = 0; ; index++)
            {
                bool taken = false;
                foreach (var c in controllers)
                    if (c.PlayerIndex == index) taken = true;
                if (!taken) return index;
            }
        }

        static Joycon2Controller Find(int handle)
        {
            foreach (var c in controllers)
                if (c.Handle == handle) return c;
            return null;
        }

        internal static string FormatAddress(ulong a) => Transport?.LocalAddressPrefix is string prefix ? $"{prefix}#{a}" :
            $"{(a >> 40) & 0xFF:X2}:{(a >> 32) & 0xFF:X2}:{(a >> 24) & 0xFF:X2}:{(a >> 16) & 0xFF:X2}:{(a >> 8) & 0xFF:X2}:{a & 0xFF:X2}";

        sealed class TransportLink : IJoycon2Link
        {
            readonly int handle;
            public TransportLink(int handle) => this.handle = handle;

            public void Subscribe(string characteristicUuid, int tag) =>
                Transport?.Subscribe(handle, Joycon2Protocol.ServiceUuid, characteristicUuid, tag);

            public void Write(string characteristicUuid, byte[] data) =>
                Transport?.Write(handle, Joycon2Protocol.ServiceUuid, characteristicUuid, data, data.Length);
        }

        /// <summary>Lifetime anchor: stops the manager when play mode ends / the app quits.</summary>
        sealed class Joycon2Host : MonoBehaviour
        {
            void OnApplicationQuit() => Stop();
            // Destroy is deferred. An old host must not stop a manager restarted in the same frame.
            void OnDestroy() { if (host == gameObject) Stop(); }
        }
    }
}
