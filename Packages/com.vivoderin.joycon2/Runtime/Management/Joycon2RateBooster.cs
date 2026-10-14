#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace Joycon2
{
    /// <summary>
    /// Browser report-rate helper. The page cannot see it; it sees the report rate go up.
    /// <list type="bullet">
    /// <item>Windows: Chrome leaves Web Bluetooth links at Windows' 60 ms default (~17 Hz). <c>Joycon2Boost.exe</c>
    /// (Native~/windows/tools) asks Windows for 15 ms (~67 Hz) on those same links.</item>
    /// <item>macOS: links get 30 ms (~33 Hz). <c>joycon2boost.app</c> (Native~/macos/tools) makes links created while it
    /// runs 15 ms (~66 Hz); one created before it stays slow until <see cref="Joycon2Settings.RefreshSlowLinks"/>
    /// rebuilds it.</item>
    /// <item>Android: Chrome gets ~20 Hz; <c>Joycon2Boost-android.apk</c> (Native~/android/booster) asks for HIGH priority on
    /// Chrome's links. The built-in web panel has its Boost button.</item>
    /// </list>
    /// Run once by hand, the helper registers <c>joycon2boost://</c>, and the page can start it with <see cref="Launch"/>.
    /// The WebGL build copies the downloads next to index.html (<see cref="Joycon2Settings.CopyRateHelpersToWebBuild"/>).
    /// </summary>
    public static class Joycon2RateBooster
    {
        public enum HelperPlatform { None, Windows, Mac }

        public const string LaunchUrl = "joycon2boost://start";

        public const string WindowsHelperFile = "Joycon2Boost.exe";
        public const string MacHelperFile = "Joycon2Boost-mac.zip";
        public const string AndroidHelperFile = "Joycon2Boost-android.apk";

        /// <summary>Relative to the page: the WebGL build puts the helpers next to index.html.</summary>
        public static string DownloadUrl => Platform == HelperPlatform.Mac ? MacHelperFile : WindowsHelperFile;

        /// <summary>Below this a link counts as not boosted (Windows ~17 → ~67 Hz, macOS ~33 → ~66 Hz).</summary>
        public static float SlowRate => Platform == HelperPlatform.Mac ? 45f : 30f;

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern int jc2web_os();
        [DllImport("__Internal")] static extern void jc2web_open_link(string url, int download);

        static int os = -1;
        static int BrowserOs => os < 0 ? os = jc2web_os() : os;
        static void OpenLink(string url, bool download) => jc2web_open_link(url, download ? 1 : 0);
#else
        static int BrowserOs => 0;
        static void OpenLink(string url, bool download) { }
#endif

        /// <summary>Which helper applies: Joy-Con 2 over Web Bluetooth in a browser on Windows or macOS.</summary>
        public static HelperPlatform Platform =>
            !Joycon2Manager.ConnectsOnRequest ? HelperPlatform.None :
            BrowserOs == 1 ? HelperPlatform.Windows : BrowserOs == 2 ? HelperPlatform.Mac : HelperPlatform.None;

        public static bool Available => Platform != HelperPlatform.None;

        /// <summary>Some ready Joy-Con 2 is above <see cref="SlowRate"/>: on macOS that only happens with the helper.</summary>
        public static bool AnyFast
        {
            get
            {
                foreach (var c in Joycon2Manager.Controllers)
                    if (c.Device != null && !c.Refreshing && c.ReportRate >= SlowRate) return true;
                return false;
            }
        }

        /// <summary>
        /// Report rate of the slowest ready Joy-Con 2 if the helper looks absent; 0 otherwise. On macOS one slow Joy-Con
        /// next to a fast one means the helper runs (the slow one is being rebuilt), so that does not count.
        /// </summary>
        public static float SlowestRate
        {
            get
            {
                if (!Available || (Platform == HelperPlatform.Mac && AnyFast)) return 0f;
                float slowest = float.MaxValue;
                foreach (var c in Joycon2Manager.Controllers)
                    if (c.Device != null && !c.Refreshing && c.ReportRate > 0f && c.ReportRate < slowest) slowest = c.ReportRate;
                return slowest < SlowRate ? slowest : 0f;
            }
        }

        /// <summary>Starts the helper through joycon2boost:// (nothing happens if it was never run by hand).
        /// Call from the frame of a click or key press.</summary>
        public static void Launch() => OpenLink(LaunchUrl, false);

        /// <summary>Downloads the helper (run it once by hand; it registers joycon2boost:// and starts boosting).
        /// Call from the frame of a click or key press.</summary>
        public static void DownloadHelper() => OpenLink(DownloadUrl, true);
    }
}
