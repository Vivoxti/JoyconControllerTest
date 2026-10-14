#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace Joycon2
{
    /// <summary>Browser UI only. Prepared chooser options allow requestDevice directly inside a DOM tap.</summary>
    public sealed class Joycon2WebControls : MonoBehaviour
    {
        [DllImport("__Internal")] static extern void jc2web_ui_init(string leftOptions, string rightOptions);
        [DllImport("__Internal")] static extern void jc2web_ui_update(int left, int right, string status, string details, int recording);
        [DllImport("__Internal")] static extern int jc2web_ui_actions();
        [DllImport("__Internal")] static extern int jc2web_is_picking();
        [DllImport("__Internal")] static extern void jc2web_ui_dump(string filename, string contents);
        [DllImport("__Internal")] static extern void jc2web_ui_shutdown();

        public static bool IsPicking => jc2web_is_picking() != 0;
        static Joycon2WebControls instance;
        readonly StringBuilder details = new StringBuilder();
        Joycon2DebugOverlay overlay;
        float nextUpdate;
        float sampleStarted;
        int frames;

        public static void EnsureStarted()
        {
            if (instance != null) return;
            var go = new GameObject("Joycon2 Web controls");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<Joycon2WebControls>();
            jc2web_ui_init(Joycon2Manager.BuildDeviceRequest(Joycon2Side.Left),
                           Joycon2Manager.BuildDeviceRequest(Joycon2Side.Right));
        }

        void Update()
        {
            frames++;
            int actions = jc2web_ui_actions();
            if (Time.unscaledTime < nextUpdate && actions == 0) return;
            if (overlay == null) overlay = FindFirstObjectByType<Joycon2DebugOverlay>();
            if ((actions & 1) != 0 && overlay != null) overlay.ToggleRecording();
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
            details.Append("Press a Joy-Con button to join the game.");
            bool canPick = Joycon2Manager.CanRequestController;
            jc2web_ui_update(canPick && !leftPresent ? 1 : 0, canPick && !rightPresent ? 1 : 0,
                Joycon2Manager.Status, details.ToString(), overlay != null && overlay.IsRecording ? 1 : 0);
        }

        public static void PrepareDownload(string filename, string contents) => jc2web_ui_dump(filename, contents);

        void OnDestroy()
        {
            jc2web_ui_shutdown();
            if (instance == this) instance = null;
        }
    }
}
#endif
