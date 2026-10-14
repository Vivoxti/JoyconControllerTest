using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Joycon2
{
    /// <summary>
    /// Diagnostics for every connection (state, Hz, buttons, sticks, IMU). F9 — show / hide.
    /// F10 — start/stop recording raw input reports to persistentDataPath/Joycon2Dumps (fixtures for tests).
    /// Created by <see cref="Joycon2Manager"/> according to <see cref="Joycon2Settings.DiagnosticsOverlay"/>.
    /// </summary>
    public sealed class Joycon2DebugOverlay : MonoBehaviour
    {
        bool visible;
        public bool Visible { get => visible; set => visible = value; }
        StreamWriter recording;
        string recordingPath;
        double recordingStart;
        GUIStyle style;
        readonly StringBuilder text = new StringBuilder(1024);
        public bool IsRecording => recording != null;
        public void ToggleVisible() => visible = !visible;
        public void ToggleRecording() { if (recording == null) StartRecording(); else StopRecording(); }

        void OnEnable() => Joycon2Manager.RawReport += OnRawReport;

        void OnDisable()
        {
            Joycon2Manager.RawReport -= OnRawReport;
            StopRecording();
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return;
            if (keyboard.f9Key.wasPressedThisFrame) visible = !visible;
            if (keyboard.f10Key.wasPressedThisFrame)
            {
                ToggleRecording();
            }
        }

        void OnGUI()
        {
            if (!visible) return;
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.UpperLeft,
                    fontSize = 13,
                    richText = false,
                    wordWrap = false,
                };
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
                style.fontSize = 18;
#endif
#if !UNITY_WEBGL || UNITY_EDITOR
                // No OS fonts on the web. Only an installed name: a missing one (Consolas on macOS) warns on every draw.
                var installed = new HashSet<string>(Font.GetOSInstalledFontNames());
                foreach (string name in new[] { "Consolas", "Menlo", "Courier New" })
                    if (installed.Contains(name))
                    {
                        style.font = Font.CreateDynamicFontFromOSFont(name, 13);
                        break;
                    }
#endif
            }

            text.Clear();
            text.Append("Joy-Con 2  [F9 hide, F10 ").Append(recording == null ? "record" : "stop recording").Append("]\n");
            text.Append("transport: ").Append(Joycon2Manager.Transport?.Name ?? "-")
                .Append("   ").Append(Joycon2Manager.Status).Append('\n');
            if (recording != null) text.Append("recording → ").Append(recordingPath).Append('\n');
            if (Joycon2Manager.Controllers.Count == 0)
                text.Append(Joycon2Manager.ConnectsOnRequest
                    ? "no controllers — hold SYNC on a Joy-Con 2, then click / press a key and pick it in the browser list\n"
                    : "no controllers — hold SYNC on a Joy-Con 2 until its LEDs run\n");

            foreach (var c in Joycon2Manager.Controllers)
            {
                var s = c.Session;
                text.Append('\n').Append(c.Name).Append("  ").Append(c.AddressText)
                    .Append("  ").Append(s.State).Append(s.State == Joycon2Session.SessionState.Ready ? "" : $" ({s.Status})");
                if (c.Device != null) text.Append("  device #").Append(c.Device.deviceId).Append("  P").Append(c.PlayerIndex + 1);
                if (c.Refreshing) text.Append("  RELINKING for a faster link: press SYNC on it");
                text.Append('\n');
                text.Append($"  MTU {c.Mtu}  {c.ReportRate,5:0.0} Hz  reports {s.ReportCount}  {c.LastReportLength} bytes  calib {s.CalibratedFrom}");
                if (c.WriteFailures > 0) text.Append("  write failures ").Append(c.WriteFailures);
                text.Append('\n');
                if (!s.HasReport) continue;

                var r = s.LastReport;
                ushort rawX = c.Side == Joycon2Side.Left ? r.Stick1X : r.Stick2X;
                ushort rawY = c.Side == Joycon2Side.Left ? r.Stick1Y : r.Stick2Y;
                var pad = Joycon2Mapping.Sideways(c.Side, r, s.Calibration);
                text.Append($"  buttons 0x{(uint)r.Buttons:X8} {r.Buttons}\n");
                text.Append($"  stick raw ({rawX,4}, {rawY,4})  pad ({pad.StickX,5:0.00}, {pad.StickY,5:0.00})  {pad.Buttons}\n");
                text.Append($"  battery {r.BatteryMillivolts} mV\n");
                if (r.HasMotion)
                {
                    var g = c.AngularVelocity;
                    var a = c.Acceleration;
                    text.Append($"  gyro °/s  pitch {g.X,7:0.0}  turn {g.Y,7:0.0}  roll {g.Z,7:0.0}" +
                                $"   bias ({c.GyroBias.X:0.0}, {c.GyroBias.Y:0.0}, {c.GyroBias.Z:0.0}){(c.GyroBias.IsStill ? " still" : "")}\n");
                    text.Append($"  accel g   right {a.X,5:0.00}  up {a.Y,5:0.00}  fwd {a.Z,5:0.00}" +
                                $"   raw imu a({r.AccelX}, {r.AccelY}, {r.AccelZ}) g({r.GyroX}, {r.GyroY}, {r.GyroZ})\n");
                    var rel = c.Orientation.Relative;
                    var e = rel.EulerDegrees;
                    var face = rel.Rotate(new Joycon2Vector3(0f, 1f, 0f));
                    text.Append($"  attitude  pitch {e.X,6:0}°  turn {e.Y,6:0}°  roll {e.Z,6:0}°" +
                                $"   face → right {face.X,5:0.00}  up {face.Y,5:0.00}  fwd {face.Z,5:0.00}   [Select = zero]\n");
                }
                else text.Append("  no motion data\n");
            }

            var content = new GUIContent(text.ToString());
            var size = style.CalcSize(content);
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            // Below the touch panel (Joycon2MobileControls), inside the safe area.
            var oldMatrix = GUI.matrix;
            var safe = Screen.safeArea;
            float scale = Mathf.Min(Joycon2MobileControls.UiScale, (safe.width - 20f) / (size.x + 10f));
            GUI.matrix = Matrix4x4.TRS(new Vector3(safe.xMin, Screen.height - safe.yMax, 0), Quaternion.identity, Vector3.one * scale);
            float top = Joycon2Manager.Settings.MobileConnectionPanel ? Joycon2MobileControls.PanelBottom * Joycon2MobileControls.UiScale / scale : 10f;
            GUI.Box(new Rect(10, top, size.x + 10, size.y + 6), content, style);
            GUI.matrix = oldMatrix;
#else
            GUI.Box(new Rect(10, 10, size.x + 10, size.y + 6), content, style);
#endif
        }

        void OnRawReport(Joycon2Controller c, byte[] data, int length)
        {
            if (recording == null) return;
            var line = new StringBuilder(16 + length * 2);
            line.Append((Time.realtimeSinceStartupAsDouble - recordingStart).ToString("0.0000")).Append('\t')
                .Append(c.Side == Joycon2Side.Left ? 'L' : 'R').Append('\t');
            for (int i = 0; i < length; i++) line.Append(data[i].ToString("x2"));
            recording.WriteLine(line.ToString());
        }

        void StartRecording()
        {
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "Joycon2Dumps");
                Directory.CreateDirectory(dir);
                recordingPath = Path.Combine(dir, $"joycon2-{DateTime.Now:yyyyMMdd-HHmmss}.tsv");
                recording = new StreamWriter(recordingPath);
                recording.WriteLine("# time\tside\treport-hex");
                recordingStart = Time.realtimeSinceStartupAsDouble;
                Debug.Log($"[Joycon2] recording raw reports to {recordingPath}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Joycon2] cannot record: {e.Message}");
                recording = null;
            }
        }

        void StopRecording()
        {
            if (recording == null) return;
            recording.Dispose();
            recording = null;
            Debug.Log($"[Joycon2] recording saved: {recordingPath}");
#if UNITY_WEBGL && !UNITY_EDITOR
            try { Joycon2WebControls.PrepareDownload(Path.GetFileName(recordingPath), File.ReadAllText(recordingPath)); }
            catch (Exception e) { Debug.LogError($"[Joycon2] cannot export recording: {e.Message}"); }
#endif
        }
    }
}
