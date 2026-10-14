using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Joycon2
{
    /// <summary>
    /// Owns the platform BLE transport, connects Joy-Con 2 as they appear (SYNC held; on the web — as the player picks
    /// them, see <see cref="RequestController"/>), runs their sessions and feeds them into the Input System as
    /// <see cref="Joycon2Gamepad"/> devices. Started by <see cref="Joycon2Lifecycle"/>; everything runs on the main thread
    /// from <see cref="InputSystem.onBeforeUpdate"/>.
    /// </summary>
    public static partial class Joycon2Manager
    {
        static Joycon2Settings settings;

        /// <summary>
        /// Runtime copy of the project's <see cref="Joycon2Settings"/>. Change it from code before the manager starts
        /// (e.g. <c>[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]</c>) or at runtime;
        /// the asset itself is never modified.
        /// </summary>
        public static Joycon2Settings Settings
        {
            get => settings != null ? settings : settings = Joycon2Settings.LoadCopy();
            set => settings = value;
        }

        /// <summary>Handshake timings, feature mask, keep-alive. Protocol-level; the defaults work on every platform.</summary>
        public static Joycon2SessionOptions SessionOptions { get; set; } = new Joycon2SessionOptions();

        public static IJoycon2Transport Transport { get; private set; }
        /// <summary>Tests: replaces the platform transport on the next <see cref="Start"/>.</summary>
        internal static Func<IJoycon2Transport> TransportFactory;
        public static bool IsRunning { get; private set; }
        public static bool IsScanning { get; private set; }
        /// <summary>Short status line for UIs and logs.</summary>
        public static string Status { get; private set; } = "stopped";
        /// <summary>Every connection, including ones still in the handshake (their <see cref="Joycon2Controller.Device"/> is null).</summary>
        public static IReadOnlyList<Joycon2Controller> Controllers => controllers;

        /// <summary>A Joy-Con 2 finished its handshake; its <see cref="Joycon2Controller.Device"/> is now in the Input System.</summary>
        public static event Action<Joycon2Controller> ControllerConnected;
        /// <summary>A ready Joy-Con 2 dropped; its gamepad has been removed. The string is the reason.</summary>
        public static event Action<Joycon2Controller, string> ControllerDisconnected;
        /// <summary>Raised on the main thread for every parsed input report (recorders, debug tools). The buffer is reused.</summary>
        public static event Action<Joycon2Controller, byte[], int> RawReport;

        static readonly List<Joycon2Controller> controllers = new List<Joycon2Controller>();
        static readonly Dictionary<ulong, double> cooldownUntil = new Dictionary<ulong, double>();
        static readonly HashSet<ulong> ignoredNotPairing = new HashSet<ulong>();
        static readonly byte[] payload = new byte[512];
        static double scanRetryAt;
        static double lastPumpTime;
        static double silenceGraceUntil;
        static GameObject host;

        /// <summary>Android: the periodic stop of a bounded scan (Android throttles long or frequent scans) — not an error.</summary>
        const int AndroidScanCycle = -1004;
        const int AndroidLocationOff = -1006;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            // Enter Play Mode without domain reload.
            Stop();
            settings = null;
            TransportFactory = null;
            SessionOptions = new Joycon2SessionOptions();
            ControllerConnected = null;
            ControllerDisconnected = null;
            RawReport = null;
        }

#if UNITY_EDITOR
        // Never leave BLE links open in edit mode or across a script reload.
        [UnityEditor.InitializeOnLoadMethod]
        static void HookEditor()
        {
            UnityEditor.EditorApplication.playModeStateChanged += change =>
            {
                if (change == UnityEditor.PlayModeStateChange.ExitingPlayMode) Joycon2Lifecycle.Stop();
            };
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Joycon2Lifecycle.Stop;
        }
#endif

        /// <summary>
        /// Starts the transport and the connection loop. Low level: prefer <see cref="Joycon2Lifecycle.Start"/>, which also
        /// handles Android permissions and app pauses. Does nothing if Bluetooth is unavailable (see <see cref="Status"/>).
        /// </summary>
        public static void Start()
        {
            if (IsRunning) return;
            Transport = TransportFactory?.Invoke() ?? CreateTransport();
            if (!Transport.Initialize())
            {
                Status = $"no Bluetooth LE ({Transport.Name})";
                Debug.Log($"[Joycon2] {Status} on {Application.platform}; Joy-Con 2 support is off.");
                Transport = null;
                return;
            }

            IsRunning = true;
            Status = "running";
            scanRetryAt = 0;
            lastPumpTime = silenceGraceUntil = 0;
            pickPending = false;
            InputSystem.onBeforeUpdate += Pump;

            host = Joycon2Lifecycle.CreateHost("Joycon2 Manager");
            host.AddComponent<Joycon2Host>();
            if (Settings.OverlayEnabled(out bool overlayVisible))
                host.AddComponent<Joycon2DebugOverlay>().Visible = overlayVisible && !HasOwnDiagnostics;
            Debug.Log(ConnectsOnRequest
                ? $"[Joycon2] started ({Transport.Name}). Hold SYNC on a Joy-Con 2, then pick it in the browser chooser."
                : $"[Joycon2] started ({Transport.Name}). Hold SYNC on a Joy-Con 2 to connect it.");
        }

        /// <summary>The mobile web panel has its own diagnostics: the overlay starts hidden there.</summary>
        static bool HasOwnDiagnostics =>
#if UNITY_WEBGL && !UNITY_EDITOR
            Application.isMobilePlatform && Settings.WebConnectionPanel;
#else
            false;
#endif

        /// <summary>Disconnects every Joy-Con 2, removes their gamepads and shuts the transport down.</summary>
        public static void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            InputSystem.onBeforeUpdate -= Pump;

            var dropped = controllers.ToArray();
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
            // Last: a handler may start the manager again.
            foreach (var c in dropped) RemoveDevice(c, "stopped");
        }

        /// <summary>The connection behind an Input System device, or null for anything that is not a Joy-Con 2.</summary>
        public static Joycon2Controller FindController(InputDevice device)
        {
            foreach (var c in controllers)
                if (c.Device != null && c.Device == device) return c;
            return null;
        }

        /// <summary>
        /// Lights the player LEDs (0 → P1 … 3 → P4, −1 → off) on the Joy-Con behind a device. Safe to call for any device:
        /// does nothing for other gamepads, keyboards…
        /// </summary>
        public static void SetPlayerIndex(InputDevice device, int playerIndex)
        {
            var c = FindController(device);
            if (c == null) return;
            c.PlayerIndex = playerIndex;
            c.Session.PlayerLed = Joycon2Protocol.PlayerLedPattern(playerIndex);
        }

        public static void Disconnect(Joycon2Controller controller) => Drop(controller, "disconnected by request", 0);

        static IJoycon2Transport CreateTransport()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || (UNITY_IOS && !UNITY_EDITOR)
            return new Joycon2NativeTransport();
#elif UNITY_WEBGL && !UNITY_EDITOR
            return new Joycon2WebBluetoothTransport();
#elif UNITY_ANDROID && !UNITY_EDITOR
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
            // The app was suspended (iOS background) or stalled: the controllers were silent because we were. Give them
            // a fresh silence window before calling the links lost.
            if (lastPumpTime > 0 && now - lastPumpTime > 0.5) silenceGraceUntil = now + Settings.ReportSilenceTimeout;
            lastPumpTime = now;

            while (IsRunning && Transport.TryDequeueEvent(out var e, payload))
                HandleEvent(e, Math.Min(e.Length, payload.Length), now);
            if (!IsRunning) return; // a malformed event can shut the transport down

            // Event handlers (ControllerConnected / Disconnected) may disconnect others or stop the manager.
            for (int i = controllers.Count - 1; i >= 0 && IsRunning; i--)
                if (i < controllers.Count) UpdateController(controllers[i], now);
            if (!IsRunning) return;

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
                    Debug.Log($"[Joycon2] {c} connected (MTU {(c.Mtu > 0 ? c.Mtu.ToString() : "n/a")}), handshake…");
                    c.Session.OnConnected(now);
                    break;

                case Joycon2TransportEventType.ConnectFailed:
                    if (c != null && c.Refreshing) RetryRefresh(c, now, $"connect failed (0x{e.Status:X8})");
                    else if (c != null) Drop(c, $"connect failed (0x{e.Status:X8})", Settings.RetryCooldown);
                    break;

                case Joycon2TransportEventType.Disconnected:
                    if (c != null && c.Refreshing) RetryRefresh(c, now, "link lost");
                    else if (c != null) Drop(c, "link lost", Settings.RetryCooldown);
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
                    if (e.Status == 0 || !IsScanning) break;
                    IsScanning = false;
                    if (e.Status == AndroidScanCycle)
                    {
                        scanRetryAt = now + 1; // the transport enforces its own gap between scans
                        break;
                    }
                    scanRetryAt = now + 5;
                    Status = ScanStoppedStatus(e.Status);
                    Debug.LogWarning($"[Joycon2] {Status}");
                    break;
            }
        }

        /// <summary>CoreBluetooth (macOS, iOS) reports its CBManagerState; WinRT and Android — an error code.</summary>
        static string ScanStoppedStatus(int status)
        {
            if (Transport != null && Transport.Name.StartsWith("CoreBluetooth"))
                switch (status)
                {
                    case 2: return "Bluetooth LE is not supported on this device";
                    case 3: return "Bluetooth access denied — allow it in Settings → Privacy & Security → Bluetooth";
                    case 4: return "Bluetooth is off — turn it on";
                }
            if (status == AndroidLocationOff) return "Location is off — Android 11 and older need it on to find Bluetooth devices";
            return $"scan stopped (error {status}) — is Bluetooth on?";
        }

        static void OnDeviceFound(in Joycon2TransportEvent e, int length, double now)
        {
            if (!Joycon2Advertisement.TryParse(payload, length, out var adv)) return;
            if (Settings.PairingModeOnly && !adv.PairingMode)
            {
                if (ignoredNotPairing.Add(e.Address))
                    Debug.Log($"[Joycon2] {(adv.Side == Joycon2Side.Left ? "Joy-Con 2 (L)" : "Joy-Con 2 (R)")} " +
                              $"{FormatAddress(e.Address)} is looking for its console — hold SYNC to connect it here.");
                return;
            }
            if (controllers.Count >= Settings.MaxControllers) return;
            foreach (var existing in controllers)
                if (existing.Address == e.Address) return;
            if (cooldownUntil.TryGetValue(e.Address, out double until) && now < until) return;

            var c = BeginConnect(e.Address, adv.Side, now);
            if (c != null) Debug.Log($"[Joycon2] found {c} (RSSI {e.Rssi} dBm), connecting…");
        }

        static Joycon2Controller BeginConnect(ulong address, Joycon2Side? side, double now)
        {
            int handle = Transport.Connect(address);
            if (handle <= 0) return null;

            var c = new Joycon2Controller(handle, address, FormatAddress(address), side, now);
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
                else Drop(c, session.Status, Settings.RetryCooldown);
                return;
            }
            if (session.State != Joycon2Session.SessionState.Ready)
            {
                if (c.Refreshing && now - c.ConnectStartTime > RefreshAttemptTimeout) RetryRefresh(c, now, $"timed out at step {session.Status}");
                else if (now - c.ConnectStartTime > Settings.ConnectTimeout) Drop(c, $"timed out at step {session.Status}", Settings.RetryCooldown);
                return;
            }

            if (c.Refreshing)
            {
                c.refreshUntil = 0;
                Debug.Log($"[Joycon2] {c} reconnected after {now - c.ConnectStartTime:0.0} s");
            }
            // A ready Joy-Con streams nonstop. Silence means a link the OS lost without telling us (Bluetooth reset on
            // macOS / iOS, Bluefy). Queued reports are drained above first, so an app pause alone does not trip it.
            double silence = Settings.ReportSilenceTimeout;
            if (silence > 0 && now >= silenceGraceUntil && session.HasReport && now - session.LastReportTime > silence)
            {
                Drop(c, $"no reports for {now - session.LastReportTime:0.0} s, link lost", Settings.RetryCooldown);
                return;
            }
            if (c.Device == null) AddDevice(c);
            c.UpdateRate(now);
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
                Drop(c, "Input System did not create a Joycon2Gamepad", Settings.RetryCooldown);
                return;
            }
            device.Controller = c;
            c.Device = device;
            Debug.Log($"[Joycon2] {c} ready as gamepad #{device.deviceId} " +
                      $"(LED P{c.PlayerIndex + 1}, calibration: {c.Session.CalibratedFrom})");
            ControllerConnected?.Invoke(c);
        }

        static void OnReport(Joycon2Controller c, int length)
        {
            c.CountReport(length);
            RawReport?.Invoke(c, payload, length);

            var pad = c.ProcessReport(Settings.RecenterOnSelect);
            if (c.Device == null || !c.Device.added) return;
            InputSystem.QueueStateEvent(c.Device,
                Joycon2Gamepad.ToState(pad, c.AngularVelocity, c.Acceleration, c.Orientation.Relative));
        }

        static void Drop(Joycon2Controller c, string reason, double cooldown)
        {
            if (!controllers.Remove(c)) return;
            Debug.Log($"[Joycon2] {c} dropped: {reason}");
            Transport?.Disconnect(c.Handle);
            if (cooldown > 0) cooldownUntil[c.Address] = Time.realtimeSinceStartupAsDouble + cooldown;
            RemoveDevice(c, reason);
        }

        static void RemoveDevice(Joycon2Controller c, string reason)
        {
            var device = c.Device;
            if (device == null) return;
            c.Device = null;
            if (device.added) InputSystem.RemoveDevice(device);
            ControllerDisconnected?.Invoke(c, reason);
        }

        static void UpdateScanning(double now)
        {
            bool want = controllers.Count < Settings.MaxControllers;
            if (ConnectsOnRequest)
            {
                if (!pickPending) Status = WebPickerBusy ? "Bluetooth chooser open" :
                    want ? "hold SYNC, then pick the Joy-Con 2 (Connect L / R)" : "all controllers connected";
                return;
            }
            if (want && !IsScanning && now >= scanRetryAt)
            {
                Transport.StartScan(Joycon2Protocol.CompanyId);
                IsScanning = true;
                Status = "scanning — hold SYNC on a Joy-Con 2";
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
