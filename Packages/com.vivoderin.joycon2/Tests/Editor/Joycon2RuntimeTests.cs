using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace Joycon2.Tests
{
    // The Unity-facing layer: settings, controller report processing, the Input System device, browser filters, builds.
    public class Joycon2RuntimeTests
    {
        sealed class NullLink : IJoycon2Link
        {
            public void Subscribe(string characteristicUuid, int tag) { }
            public void Write(string characteristicUuid, byte[] data) { }
        }

        static Joycon2Controller ControllerWithReport(Joycon2Side side, byte[] report, double time)
        {
            var c = new Joycon2Controller(1, 42, "test#42", side, 0) { Session = new Joycon2Session(new NullLink(), side) };
            Assert.IsTrue(c.Session.OnNotification(Joycon2Session.TagInput, report, report.Length, time));
            return c;
        }

        // ───────────────────────────── Settings ─────────────────────────────

        [Test]
        public void Settings_DefaultsSuitTwoPlayerGames()
        {
            var s = ScriptableObject.CreateInstance<Joycon2Settings>();
            Assert.IsTrue(s.AutoStart);
            Assert.AreEqual(2, s.MaxControllers);
            Assert.IsTrue(s.PairingModeOnly);
            Assert.AreEqual(Joycon2OverlayMode.DevelopmentBuilds, s.DiagnosticsOverlay);
            Assert.IsFalse(string.IsNullOrWhiteSpace(s.BluetoothUsageDescription));
            Object.DestroyImmediate(s);
        }

        [Test]
        public void Settings_OverlayModes()
        {
            var s = ScriptableObject.CreateInstance<Joycon2Settings>();
            s.DiagnosticsOverlay = Joycon2OverlayMode.Off;
            Assert.IsFalse(s.OverlayEnabled(out _));
            s.DiagnosticsOverlay = Joycon2OverlayMode.DevelopmentBuilds;
            Assert.IsTrue(s.OverlayEnabled(out bool visible)); // the Editor counts as development
            Assert.IsTrue(visible);
            s.DiagnosticsOverlay = Joycon2OverlayMode.Always;
            Assert.IsTrue(s.OverlayEnabled(out _));
            Object.DestroyImmediate(s);
        }

        [Test]
        public void Settings_RuntimeCopyNeverTouchesTheAsset()
        {
            var copy = Joycon2Settings.LoadCopy();
            var asset = Resources.Load<Joycon2Settings>(Joycon2Settings.ResourceName);
            Assert.AreNotSame(asset, copy);
            int before = asset != null ? asset.MaxControllers : 2;
            copy.MaxControllers = before + 3;
            if (asset != null) Assert.AreEqual(before, asset.MaxControllers);
            Object.DestroyImmediate(copy);
        }

        [Test]
        public void Settings_MaxControllersAtLeastOne()
        {
            var s = ScriptableObject.CreateInstance<Joycon2Settings>();
            s.MaxControllers = 0;
            Assert.AreEqual(1, s.MaxControllers);
            Object.DestroyImmediate(s);
        }

        // ───────────────────────────── Controller ─────────────────────────────

        [Test]
        public void Controller_ProcessReport_MapsSidewaysAndRecentersOnSelect()
        {
            var report = Joycon2CoreTests.MakeReport((uint)(Joycon2Buttons.A | Joycon2Buttons.Home), 0, 0, 2048, 2048);
            var c = ControllerWithReport(Joycon2Side.Right, report, 1.0);
            var pad = c.ProcessReport(recenterOnSelect: true);
            Assert.AreEqual(Joycon2PadButtons.South | Joycon2PadButtons.Select, pad.Buttons);
            Assert.AreEqual(3.6f, c.BatteryVoltage, 1e-3f);
            Assert.AreEqual(1f, c.Acceleration.Y, 1e-3f, "accel z = 1 g in the IMU frame is 'up' sideways");
        }

        [Test]
        public void Controller_SelectRecentersOnlyOnPress()
        {
            var still = Joycon2CoreTests.MakeReport(0, 2048, 2048, 0, 0);
            var c = ControllerWithReport(Joycon2Side.Left, still, 0.0);
            c.ProcessReport(true);
            // Tilt the pose by hand, then press Select (Capture on L): the zero follows.
            for (int i = 1; i <= 10; i++)
            {
                var tilted = Joycon2CoreTests.MakeReport(0, 2048, 2048, 0, 0);
                tilted[0x36] = 0x00; tilted[0x37] = 0x10; // gyro x
                Assert.IsTrue(c.Session.OnNotification(Joycon2Session.TagInput, tilted, tilted.Length, i * 0.015));
                c.ProcessReport(true);
            }
            Assert.Greater(Joycon2Orientation.AngleDegrees(c.Orientation.Relative), 1f);
            var select = Joycon2CoreTests.MakeReport((uint)Joycon2Buttons.Capture, 2048, 2048, 0, 0);
            Assert.IsTrue(c.Session.OnNotification(Joycon2Session.TagInput, select, select.Length, 0.2));
            c.ProcessReport(true);
            Assert.Less(Joycon2Orientation.AngleDegrees(c.Orientation.Relative), 0.5f);
        }

        [Test]
        public void Controller_ReportRateOverOneSecond()
        {
            var c = new Joycon2Controller(1, 1, "x", Joycon2Side.Left, 10.0);
            for (int i = 0; i < 66; i++) c.CountReport(63);
            c.UpdateRate(10.5);
            Assert.AreEqual(0f, c.ReportRate);
            c.UpdateRate(11.0);
            Assert.AreEqual(66f, c.ReportRate, 0.01f);
            Assert.AreEqual(63, c.LastReportLength);
        }

        // ───────────────────────────── Input System device ─────────────────────────────

        [Test]
        public void Gamepad_IsAGamepadWithMotionControls()
        {
            var pad = InputSystem.AddDevice<Joycon2Gamepad>();
            try
            {
                Assert.IsInstanceOf<Gamepad>(pad);
                Assert.IsTrue(InputControlPath.Matches("<Gamepad>/buttonSouth", pad.buttonSouth));
                Assert.IsNotNull(pad.angularVelocity);
                Assert.IsNotNull(pad.acceleration);
                Assert.IsNotNull(pad.attitude);
                Assert.IsTrue(pad.canRunInBackground);
                Assert.AreEqual(-1, pad.PlayerIndex, "no controller behind a device added by hand");
                pad.Recenter(); // no controller: harmless
            }
            finally { InputSystem.RemoveDevice(pad); }
        }

        [Test]
        public void Gamepad_StateCarriesButtonsSticksAndMotion()
        {
            var pad = InputSystem.AddDevice<Joycon2Gamepad>();
            try
            {
                var state = new Joycon2PadState { StickX = 0.5f, StickY = -1f,
                    Buttons = Joycon2PadButtons.South | Joycon2PadButtons.RightTrigger | Joycon2PadButtons.Start };
                InputSystem.QueueStateEvent(pad, Joycon2Gamepad.ToState(state, new Joycon2Vector3(10, 20, 30),
                    new Joycon2Vector3(0, 1, 0), Joycon2Quaternion.Identity));
                InputSystem.Update();
                Assert.IsTrue(pad.buttonSouth.isPressed);
                Assert.IsFalse(pad.buttonEast.isPressed);
                Assert.IsTrue(pad.startButton.isPressed);
                Assert.AreEqual(1f, pad.rightTrigger.ReadValue());
                Assert.AreEqual(0.5f, pad.leftStick.ReadUnprocessedValue().x, 1e-5f);
                Assert.AreEqual(new Vector3(10, 20, 30), pad.angularVelocity.ReadValue());
                Assert.AreEqual(Vector3.up, pad.acceleration.ReadValue());
                Assert.AreEqual(Quaternion.identity, pad.attitude.ReadValue());
            }
            finally { InputSystem.RemoveDevice(pad); }
        }

        [Test]
        public void Gamepad_SideFromProductNameWithoutController()
        {
            var pad = (Joycon2Gamepad)InputSystem.AddDevice(new UnityEngine.InputSystem.Layouts.InputDeviceDescription
            {
                interfaceName = Joycon2Gamepad.InterfaceName, product = "Joy-Con 2 (R)",
            });
            try { Assert.AreEqual(Joycon2Side.Right, pad.Side); }
            finally { InputSystem.RemoveDevice(pad); }
        }

        [Test]
        public void SetPlayerIndex_IgnoresOtherDevices()
        {
            var keyboard = InputSystem.AddDevice<Keyboard>();
            try { Assert.DoesNotThrow(() => Joycon2Manager.SetPlayerIndex(keyboard, 0)); }
            finally { InputSystem.RemoveDevice(keyboard); }
            Assert.IsNull(Joycon2Manager.FindController(null));
        }

        // ───────────────────────────── Web Bluetooth request ─────────────────────────────

        [Test]
        public void DeviceRequest_FiltersByPidOrNameAndAsksForTheService()
        {
            string both = Joycon2Manager.BuildDeviceRequest(null);
            string left = Joycon2Manager.BuildDeviceRequest(Joycon2Side.Left);
            StringAssert.Contains("\"optionalServices\":[\"" + Joycon2Protocol.ServiceUuid + "\"]", both);
            StringAssert.Contains("\"companyIdentifier\":1363", both);
            StringAssert.Contains("Joy-Con 2 (L)", both);
            StringAssert.Contains("Joy-Con 2 (R)", both);
            StringAssert.Contains("\"dataPrefix\":[0,0,0,0,0,103,32]", left); // PID 0x2067
            StringAssert.DoesNotContain("Joy-Con 2 (R)", left);
            Assert.AreEqual(both.Split('{').Length, both.Split('}').Length, "balanced braces");
            Assert.AreEqual(both.Split('[').Length, both.Split(']').Length, "balanced brackets");
        }

        // ───────────────────────────── Builds ─────────────────────────────

        const string Plist = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
            "<plist version=\"1.0\"><dict><key>CFBundleName</key><string>Game</string>{0}</dict></plist>";

        static string WritePlist(string extra)
        {
            string path = Path.Combine(Path.GetTempPath(), $"joycon2-test-{System.Guid.NewGuid():N}.plist");
            File.WriteAllText(path, string.Format(Plist, extra));
            return path;
        }

        [Test]
        public void IOSInfoPlist_GetsTheUsageDescription()
        {
            string path = WritePlist("");
            try
            {
                Editor.Joycon2BuildProcessor.AddIOSInfoPlistKeys(path, "Joy-Con over BLE", development: false);
                string text = File.ReadAllText(path);
                StringAssert.Contains("<key>NSBluetoothAlwaysUsageDescription</key>", text);
                StringAssert.Contains("<string>Joy-Con over BLE</string>", text);
                StringAssert.DoesNotContain("UIFileSharingEnabled", text);
                StringAssert.Contains("<key>CFBundleName</key>", text);
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void IOSInfoPlist_KeepsTheGamesOwnDescription_DevelopmentSharesFiles()
        {
            string path = WritePlist("<key>NSBluetoothAlwaysUsageDescription</key><string>Mine</string>");
            try
            {
                Editor.Joycon2BuildProcessor.AddIOSInfoPlistKeys(path, "Package text", development: true);
                string text = File.ReadAllText(path);
                StringAssert.Contains("<string>Mine</string>", text);
                StringAssert.DoesNotContain("Package text", text);
                StringAssert.Contains("<key>UIFileSharingEnabled</key>", text);
            }
            finally { File.Delete(path); }
        }
    }
}
