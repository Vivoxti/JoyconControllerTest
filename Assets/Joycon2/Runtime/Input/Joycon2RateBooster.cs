#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace Joycon2
{
    /// <summary>
    /// Windows + browser: Chrome leaves Web Bluetooth links at Windows' 60 ms default (~17 Hz). The companion
    /// <c>Joycon2Boost.exe</c> (Native/windows/tools) asks Windows for 15 ms (~67 Hz) on those same links. Once it has
    /// been run by hand it registers <c>joycon2boost://</c>, and the page can start it with <see cref="Launch"/>.
    /// The page cannot see the helper; it sees the report rate (<see cref="SlowestRate"/>) go up.
    /// </summary>
    public static class Joycon2RateBooster
    {
        public const string LaunchUrl = "joycon2boost://start";
        /// <summary>Relative to the page: the WebGL build puts the helper next to index.html.</summary>
        public const string DownloadUrl = "Joycon2Boost.exe";
        /// <summary>Below this a link counts as not boosted (Windows default ~16.7 Hz, boosted ~67 Hz).</summary>
        public const float SlowRate = 30f;

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern int jc2web_is_windows();
        [DllImport("__Internal")] static extern void jc2web_open_link(string url, int download);

        static int isWindows = -1;
        static bool IsWindowsBrowser => (isWindows < 0 ? isWindows = jc2web_is_windows() : isWindows) == 1;
        static void OpenLink(string url, bool download) => jc2web_open_link(url, download ? 1 : 0);
#else
        static bool IsWindowsBrowser => false;
        static void OpenLink(string url, bool download) { }
#endif

        /// <summary>The helper applies: Joy-Con 2 over Web Bluetooth in a browser on Windows.</summary>
        public static bool Available => Joycon2Manager.ConnectsOnRequest && IsWindowsBrowser;

        /// <summary>Report rate of the slowest ready Joy-Con 2 if it is below <see cref="SlowRate"/>; 0 if none is slow.</summary>
        public static float SlowestRate
        {
            get
            {
                if (!Available) return 0f;
                float slowest = float.MaxValue;
                foreach (var c in Joycon2Manager.Controllers)
                    if (c.Device != null && c.ReportRate > 0f && c.ReportRate < slowest) slowest = c.ReportRate;
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
