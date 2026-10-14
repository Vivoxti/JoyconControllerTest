using System;
using System.Collections.Generic;
using UnityEngine;

namespace Joycon2
{
    /// <summary>A button a connection UI can offer next to "Connect / retry", e.g. Bluetooth or app settings.</summary>
    public readonly struct Joycon2SettingsShortcut
    {
        public readonly string Label;
        public readonly Action Open;
        public Joycon2SettingsShortcut(string label, Action open) { Label = label; Open = open; }
    }

    /// <summary>
    /// Platform-aware start of Joy-Con 2 support: Android permissions and app pause, iOS pause, the built-in connection
    /// UI (<see cref="Joycon2Settings"/>), then <see cref="Joycon2Manager.Start"/>. Runs automatically before the first scene
    /// unless <see cref="Joycon2Settings.AutoStart"/> is off — then call <see cref="Start"/> yourself.
    /// <see cref="Status"/>, <see cref="Retry"/> and <see cref="SettingsShortcuts"/> are what a custom connection UI needs.
    /// </summary>
    public static class Joycon2Lifecycle
    {
        static GameObject host;

        public static bool IsStarted => host != null;

        /// <summary>One line for the player: missing permission, Bluetooth off, scanning, "hold SYNC"…</summary>
        public static string Status
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                if (host != null) return Joycon2AndroidLifecycle.Status;
#elif UNITY_IOS && !UNITY_EDITOR
                if (host != null && !Joycon2Manager.IsRunning) return "Bluetooth could not start — tap Connect / retry";
#endif
                return Joycon2Manager.Status;
            }
        }

        /// <summary>Platform settings pages worth offering when Bluetooth is off or access was refused (none on desktop).</summary>
        public static IReadOnlyList<Joycon2SettingsShortcut> SettingsShortcuts { get; } =
#if UNITY_ANDROID && !UNITY_EDITOR
            new[]
            {
                new Joycon2SettingsShortcut("Bluetooth settings", () => Joycon2AndroidLifecycle.OpenSettings(true)),
                new Joycon2SettingsShortcut("App settings", () => Joycon2AndroidLifecycle.OpenSettings(false)),
            };
#elif UNITY_IOS && !UNITY_EDITOR
            // Opens this app's page in Settings, which has its Bluetooth switch.
            new[] { new Joycon2SettingsShortcut("App settings", () => Application.OpenURL("app-settings:")) };
#else
            Array.Empty<Joycon2SettingsShortcut>();
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => host = null; // Enter Play Mode without domain reload

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void AutoStart()
        {
            if (Joycon2Manager.Settings.AutoStart) Start();
        }

        /// <summary>Starts Joy-Con 2 support with the platform's permission and pause handling and the built-in UI.</summary>
        public static void Start()
        {
            if (host != null) return;
            var settings = Joycon2Manager.Settings;
            host = CreateHost("Joycon2 Lifecycle");
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            if (settings.MobileTargetFrameRate > 0) Application.targetFrameRate = settings.MobileTargetFrameRate;
            if (settings.MobileConnectionPanel) host.AddComponent<Joycon2MobileControls>();
#endif
#if UNITY_ANDROID && !UNITY_EDITOR
            host.AddComponent<Joycon2AndroidLifecycle>(); // starts the manager once permissions are granted
#elif UNITY_IOS && !UNITY_EDITOR
            host.AddComponent<Joycon2IOSLifecycle>();
            Joycon2Manager.Start();
#else
            Joycon2Manager.Start();
#endif
#if UNITY_WEBGL && !UNITY_EDITOR
            // Created even if the transport failed: the panel explains HTTPS / unsupported browsers.
            if (settings.WebConnectionPanel) host.AddComponent<Joycon2WebControls>();
#endif
        }

        /// <summary>A scene-independent object: survives scene loads in play mode, hidden and unsaved in edit mode.</summary>
        internal static GameObject CreateHost(string name)
        {
            var go = new GameObject(name);
            if (Application.isPlaying) UnityEngine.Object.DontDestroyOnLoad(go);
            else go.hideFlags = HideFlags.HideAndDontSave;
            return go;
        }

        /// <summary>Stops everything <see cref="Start"/> set up, links included.</summary>
        public static void Stop()
        {
            Joycon2Manager.Stop();
            var h = host;
            host = null;
            if (h == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(h);
            else UnityEngine.Object.DestroyImmediate(h);
        }

        /// <summary>"Connect / retry": asks for missing permissions (Android) and restarts Bluetooth.</summary>
        public static void Retry()
        {
            if (host == null)
            {
                Start();
                return;
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            Joycon2AndroidLifecycle.ConnectOrRetry();
#else
            Joycon2Manager.Stop();
            Joycon2Manager.Start();
#endif
        }
    }
}
