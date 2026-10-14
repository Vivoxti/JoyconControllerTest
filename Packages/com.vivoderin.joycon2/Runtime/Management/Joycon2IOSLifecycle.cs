#if UNITY_IOS && !UNITY_EDITOR
using UnityEngine;

namespace Joycon2
{
    /// <summary>
    /// Foreground lifetime on iOS (added by <see cref="Joycon2Lifecycle"/>). Unity pauses on every "resign active" — the
    /// Bluetooth permission alert, Notification Center, Control Center — and the 1 Hz keep-alive stops with it. Short pauses
    /// keep the links (a link that did drop arrives as Disconnected on return). After a long one (the app was in the
    /// background) the links may be dead without a disconnect event, so the manager restarts and the Joy-Con need SYNC again.
    /// iOS asks for Bluetooth access itself on the first CoreBluetooth call; a refusal shows up as the scan stopping.
    /// </summary>
    sealed class Joycon2IOSLifecycle : MonoBehaviour
    {
        const float RestartAfterPause = 5f;
        bool paused, quitting;
        float pausedAt;

        void OnApplicationPause(bool value)
        {
            if (quitting || value == paused) return;
            paused = value;
            if (value) pausedAt = Time.realtimeSinceStartup;
            else if (!Joycon2Manager.IsRunning || Time.realtimeSinceStartup - pausedAt > RestartAfterPause) Joycon2Lifecycle.Retry();
        }

        void OnApplicationQuit()
        {
            quitting = true;
            Joycon2Manager.Stop();
        }
    }
}
#endif
