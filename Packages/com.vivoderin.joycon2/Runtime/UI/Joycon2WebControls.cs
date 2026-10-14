#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace Joycon2
{
    /// <summary>
    /// The built-in WebGL connection panel (Plugins/WebGL/Joycon2WebControls.jslib): Connect L / R open the Bluetooth chooser
    /// straight from the DOM tap (prepared filters, no frame in between), status, diagnostics, Record / Download, and on
    /// Android the Boost helper. Added by <see cref="Joycon2Lifecycle"/> when <see cref="Joycon2Settings.WebConnectionPanel"/>.
    /// On phones it also closes the links when the page goes to the background (a hidden mobile tab loses them silently).
    /// </summary>
    sealed class Joycon2WebControls : MonoBehaviour
    {
        [DllImport("__Internal")] static extern void jc2web_ui_init(string leftOptions, string rightOptions, string androidHelperFile);
        [DllImport("__Internal")] static extern void jc2web_ui_update(int left, int right, string status, string details, int recording);
        [DllImport("__Internal")] static extern int jc2web_ui_actions();
        [DllImport("__Internal")] static extern void jc2web_ui_dump(string filename, string contents);
        [DllImport("__Internal")] static extern void jc2web_ui_shutdown();

        const int ActionRecord = 1;

        readonly StringBuilder details = new StringBuilder();
        Joycon2DebugOverlay overlay;
        float nextUpdate;
        float sampleStarted;
        int frames;

        void Awake()
        {
            jc2web_ui_init(Joycon2Manager.BuildDeviceRequest(Joycon2Side.Left),
                           Joycon2Manager.BuildDeviceRequest(Joycon2Side.Right),
                           Joycon2Manager.Settings.CopyRateHelpersToWebBuild ? Joycon2RateBooster.AndroidHelperFile : "");
        }

        void Update()
        {
            frames++;
            int actions = jc2web_ui_actions();
            if (Time.unscaledTime < nextUpdate && actions == 0) return;
            if (overlay == null) overlay = FindFirstObjectByType<Joycon2DebugOverlay>();
            if ((actions & ActionRecord) != 0 && overlay != null) overlay.ToggleRecording();
            nextUpdate = Time.unscaledTime + 0.25f;
            details.Clear();
            bool leftPresent = false, rightPresent = false;
            foreach (var c in Joycon2Manager.Controllers)
            {
                if (!c.Refreshing)
                {
                    if (c.Side == Joycon2Side.Left) leftPresent = true;
                    else rightPresent = true;
                }
                details.Append(c.Name).Append(" · ").Append(c.Session.State)
                    .Append(" · ").Append(c.ReportRate.ToString("0.0")).Append(" Hz")
                    .Append(" · ").Append(c.LastReportLength).Append(" bytes")
                    .Append(" · gyro ").Append(c.Session.HasReport && c.Session.LastReport.HasMotion ? "OK" : "—")
                    .Append('\n');
            }
            if (details.Length == 0) details.Append("Hold SYNC until the lights blink, then connect each Joy-Con.\n");
            float elapsed = Time.unscaledTime - sampleStarted;
            if (elapsed > 0f) details.Append("Game: ").Append((frames / elapsed).ToString("0")).Append(" FPS\n");
            sampleStarted = Time.unscaledTime;
            frames = 0;
            bool canPick = Joycon2Manager.CanRequestController;
            jc2web_ui_update(canPick && !leftPresent ? 1 : 0, canPick && !rightPresent ? 1 : 0,
                Joycon2Manager.Status, details.ToString(), overlay != null && overlay.IsRecording ? 1 : 0);
        }

        /// <summary>Offers a finished recording as a download (the page cannot write files).</summary>
        internal static void PrepareDownload(string filename, string contents) => jc2web_ui_dump(filename, contents);

        void OnDestroy() => jc2web_ui_shutdown();
    }
}
#endif
