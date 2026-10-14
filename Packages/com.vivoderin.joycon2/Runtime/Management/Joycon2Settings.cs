using UnityEngine;

namespace Joycon2
{
    /// <summary>When the F9 diagnostics overlay is available.</summary>
    public enum Joycon2OverlayMode
    {
        Off,
        /// <summary>Editor and development builds: visible at start, F9 toggles, F10 records raw reports.</summary>
        DevelopmentBuilds,
        /// <summary>Also in release builds, hidden until F9.</summary>
        Always,
    }

    /// <summary>
    /// Project-wide Joy-Con 2 settings: Edit → Project Settings → Joy-Con 2. Stored as
    /// <c>Resources/Joycon2Settings.asset</c>; without the asset the defaults apply.
    /// At runtime <see cref="Joycon2Manager.Settings"/> is a copy, so changing it never touches the asset.
    /// </summary>
    public sealed class Joycon2Settings : ScriptableObject
    {
        public const string ResourceName = "Joycon2Settings";

        [Header("Connection")]
        [Tooltip("Start before the first scene loads. Off: call Joycon2Lifecycle.Start() yourself (it also handles Android " +
                 "permissions and app pauses).")]
        [SerializeField] bool autoStart = true;
        [Tooltip("Scanning stops once this many Joy-Con 2 are connected (scanning slows live links down). Player LEDs go up to 4.")]
        [SerializeField, Range(1, 8)] int maxControllers = 2;
        [Tooltip("Only connect to Joy-Con with SYNC held: a Joy-Con paired to a console ignores other hosts anyway.")]
        [SerializeField] bool pairingModeOnly = true;
        [Tooltip("Seconds from connect to a ready gamepad before giving up.")]
        [SerializeField, Min(1)] float connectTimeout = 20f;
        [Tooltip("Seconds before retrying a Joy-Con whose connection failed: they stop answering after rapid retries.")]
        [SerializeField, Min(0)] float retryCooldown = 3f;
        [Tooltip("A ready Joy-Con streams 30–66 reports per second; this much silence means the link is gone even if the OS " +
                 "did not say so (Bluetooth reset, Bluefy). 0 = off.")]
        [SerializeField, Min(0)] float reportSilenceTimeout = 3f;

        [Header("Motion")]
        [Tooltip("Select (Capture on L, Home on R) makes the current pose the zero of the attitude control.")]
        [SerializeField] bool recenterOnSelect = true;

        [Header("Built-in UI")]
        [Tooltip("F9 diagnostics overlay (buttons, sticks, IMU, report rate) and F10 raw report recording.")]
        [SerializeField] Joycon2OverlayMode diagnosticsOverlay = Joycon2OverlayMode.DevelopmentBuilds;
        [Tooltip("Android / iOS: an IMGUI strip with the status, Connect / retry and Settings buttons. Off: build your own " +
                 "from Joycon2Lifecycle.Status / Retry() / SettingsShortcuts.")]
        [SerializeField] bool mobileConnectionPanel = true;
        [Tooltip("WebGL: an HTML panel with Connect L / R (the chooser opens straight from the tap), status and diagnostics. " +
                 "Off: call Joycon2Manager.RequestController() from your own click handler.")]
        [SerializeField] bool webConnectionPanel = true;
        [Tooltip("Android / iOS: Application.targetFrameRate set on start (players default to 30, reports come at ~66 Hz). 0 = leave it.")]
        [SerializeField, Min(0)] int mobileTargetFrameRate = 60;

        [Header("Web (browser report-rate helpers)")]
        [Tooltip("Web on macOS with joycon2boost: rebuild a link that stays much slower than another one (needs SYNC on it).")]
        [SerializeField] bool refreshSlowLinks = true;
        [Tooltip("WebGL builds: copy the helper downloads (Joycon2Boost.exe, Joycon2Boost-mac.zip, Joycon2Boost-android.apk) from " +
                 "the package next to index.html.")]
        [SerializeField] bool copyRateHelpersToWebBuild = true;

        [Header("Builds")]
        [Tooltip("Shown by iOS / macOS when asking for Bluetooth. Without it the OS kills the app on the first Bluetooth call, " +
                 "so builds always get one (Info.plist on iOS, Player Settings on macOS if empty).")]
        [SerializeField] string bluetoothUsageDescription = "Joy-Con 2 controllers connect over Bluetooth.";

        public bool AutoStart { get => autoStart; set => autoStart = value; }
        public int MaxControllers { get => maxControllers; set => maxControllers = Mathf.Max(1, value); }
        public bool PairingModeOnly { get => pairingModeOnly; set => pairingModeOnly = value; }
        public float ConnectTimeout { get => connectTimeout; set => connectTimeout = value; }
        public float RetryCooldown { get => retryCooldown; set => retryCooldown = value; }
        public float ReportSilenceTimeout { get => reportSilenceTimeout; set => reportSilenceTimeout = value; }
        public bool RecenterOnSelect { get => recenterOnSelect; set => recenterOnSelect = value; }
        public Joycon2OverlayMode DiagnosticsOverlay { get => diagnosticsOverlay; set => diagnosticsOverlay = value; }
        public bool MobileConnectionPanel { get => mobileConnectionPanel; set => mobileConnectionPanel = value; }
        public bool WebConnectionPanel { get => webConnectionPanel; set => webConnectionPanel = value; }
        public int MobileTargetFrameRate { get => mobileTargetFrameRate; set => mobileTargetFrameRate = value; }
        /// <summary>Safe to toggle at runtime, e.g. off during a round so a link is never rebuilt mid-play.</summary>
        public bool RefreshSlowLinks { get => refreshSlowLinks; set => refreshSlowLinks = value; }
        public bool CopyRateHelpersToWebBuild { get => copyRateHelpersToWebBuild; set => copyRateHelpersToWebBuild = value; }
        public string BluetoothUsageDescription { get => bluetoothUsageDescription; set => bluetoothUsageDescription = value; }

        /// <summary>Diagnostics overlay is created in this player (and visible at start when <paramref name="visible"/>).</summary>
        public bool OverlayEnabled(out bool visible)
        {
            bool development = Application.isEditor || Debug.isDebugBuild;
            visible = development;
            return diagnosticsOverlay == Joycon2OverlayMode.Always ||
                   diagnosticsOverlay == Joycon2OverlayMode.DevelopmentBuilds && development;
        }

        /// <summary>The project's settings asset, or a fresh instance with the defaults.</summary>
        public static Joycon2Settings Load()
        {
            var asset = Resources.Load<Joycon2Settings>(ResourceName);
            return asset != null ? asset : CreateInstance<Joycon2Settings>();
        }

        /// <summary>A runtime copy: edits do not leak into the asset (they would persist in the Editor).</summary>
        internal static Joycon2Settings LoadCopy()
        {
            var asset = Resources.Load<Joycon2Settings>(ResourceName);
            var copy = asset != null ? Instantiate(asset) : CreateInstance<Joycon2Settings>();
            copy.hideFlags = HideFlags.HideAndDontSave;
            return copy;
        }
    }
}
