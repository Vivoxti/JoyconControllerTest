#if UNITY_IOS && !UNITY_EDITOR
using System;
using UnityEngine;

namespace Joycon2
{
    /// <summary>
    /// Foreground lifetime on iOS. Unity pauses on every "resign active" — the Bluetooth permission alert, Notification
    /// Center, Control Center — and the 1 Hz keep-alive stops with it. Short pauses keep the links (a link that did drop
    /// arrives as Disconnected on return). After a long one (the app was in the background) the links may be dead
    /// without a disconnect event, so the manager restarts and the Joy-Con need SYNC again.
    /// iOS asks for Bluetooth access itself on the first CoreBluetooth call; a refusal shows up as the scan stopping.
    /// </summary>
    public sealed class Joycon2IOSLifecycle : MonoBehaviour
    {
        static Joycon2IOSLifecycle instance;
        const float RestartAfterPause = 5f;
        bool paused, quitting;
        float pausedAt;

        public static string Status => Joycon2Manager.IsRunning ? Joycon2Manager.Status : "BLE could not start — tap Connect / retry";

        public static void EnsureStarted()
        {
            if (instance != null) return;
            var go = new GameObject("Joycon2 iOS lifecycle");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<Joycon2IOSLifecycle>();
            var controls = go.AddComponent<Joycon2MobileControls>();
            controls.Status = () => Status;
            controls.Retry = Restart;
            // Opens this app's page in Settings, which has the Bluetooth switch.
            controls.SettingsButtons = new (string, Action)[] { ("App settings", () => Application.OpenURL("app-settings:")) };
        }

        void Awake()
        {
            // iOS players default to 30 fps: twice slower than the reports (~33–66 Hz).
            Application.targetFrameRate = 60;
            Joycon2Manager.Start();
        }

        static void Restart()
        {
            Joycon2Manager.Stop();
            Joycon2Manager.Start();
        }

        void OnApplicationPause(bool value)
        {
            if (quitting || value == paused) return;
            paused = value;
            if (value) pausedAt = Time.realtimeSinceStartup;
            else if (!Joycon2Manager.IsRunning || Time.realtimeSinceStartup - pausedAt > RestartAfterPause) Restart();
        }

        void OnApplicationQuit() { quitting = true; Joycon2Manager.Stop(); }
        void OnDestroy() { if (instance == this) instance = null; }
    }
}
#endif
