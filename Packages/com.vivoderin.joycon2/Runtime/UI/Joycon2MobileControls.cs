using UnityEngine;

namespace Joycon2
{
    /// <summary>
    /// Built-in touch strip for phones (Android, iOS): status line, Connect / retry, platform settings buttons, Details
    /// (the diagnostics overlay) and Record. Works even when Bluetooth was refused and the manager is not running.
    /// Added by <see cref="Joycon2Lifecycle"/> when <see cref="Joycon2Settings.MobileConnectionPanel"/> is on; a game with its
    /// own UI turns it off and uses <see cref="Joycon2Lifecycle.Status"/>, <see cref="Joycon2Lifecycle.Retry"/> and
    /// <see cref="Joycon2Lifecycle.SettingsShortcuts"/>.
    /// </summary>
    public sealed class Joycon2MobileControls : MonoBehaviour
    {
        GUIStyle buttonStyle, labelStyle;
        Joycon2DebugOverlay overlay;

        /// <summary>Same scale as the panel: the overlay draws below it.</summary>
        public static float UiScale => Mathf.Clamp(Screen.width / 1100f, 0.65f, 2f);
        public const float PanelBottom = 105f;

        void OnGUI()
        {
            var oldMatrix = GUI.matrix;
            var oldDepth = GUI.depth;
            GUI.depth = -100;
            // Keep clear of the notch / Dynamic Island in landscape.
            var safe = Screen.safeArea;
            float scale = UiScale;
            GUI.matrix = Matrix4x4.TRS(new Vector3(safe.xMin, Screen.height - safe.yMax, 0), Quaternion.identity, Vector3.one * scale);
            buttonStyle ??= new GUIStyle(GUI.skin.button) { fontSize = 18 };
            labelStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 18 };
            GUI.Box(new Rect(8, 8, 800, 85), GUIContent.none);
            GUI.Label(new Rect(18, 12, 780, 28), "Joy-Con 2: " + Joycon2Lifecycle.Status, labelStyle);

            float x = 18;
            bool Button(string label, float width)
            {
                bool pressed = GUI.Button(new Rect(x, 44, width, 38), label, buttonStyle);
                x += width + 10;
                return pressed;
            }

            if (Button("Connect / retry", 150)) Joycon2Lifecycle.Retry();
            foreach (var shortcut in Joycon2Lifecycle.SettingsShortcuts)
                if (Button(shortcut.Label, shortcut.Label.Length > 12 ? 180 : 140)) shortcut.Open();
            if (overlay == null) overlay = FindFirstObjectByType<Joycon2DebugOverlay>();
            if (overlay != null)
            {
                if (Button("Details", 130)) overlay.ToggleVisible();
                if (Button(overlay.IsRecording ? "Stop recording" : "Record", 140)) overlay.ToggleRecording();
            }
            GUI.matrix = oldMatrix;
            GUI.depth = oldDepth;
        }
    }
}
