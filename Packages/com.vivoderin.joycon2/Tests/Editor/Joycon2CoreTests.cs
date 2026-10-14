using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Joycon2.Tests
{
    // Pure-core tests (no UnityEngine): protocol bytes, parsing, calibration, mapping, handshake.
    // Byte sequences come from community captures (see Documentation~/PROTOCOL.md). Replace the synthetic
    // reports with recorded ones (F10 in the debug overlay) once verified on hardware.
    public class Joycon2CoreTests
    {
        internal static byte[] Hex(string hex)
        {
            hex = hex.Replace(" ", "");
            var data = new byte[hex.Length / 2];
            for (int i = 0; i < data.Length; i++) data[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return data;
        }

        // ───────────────────────────── Commands ─────────────────────────────

        [Test]
        public void PlayerLedCommand_MatchesCapture()
        {
            CollectionAssert.AreEqual(Hex("09 91 01 07 00 08 00 00 01 00 00 00 00 00 00 00"),
                Joycon2Protocol.SetPlayerLed(0x01));
        }

        [Test]
        public void FeatureCommands_MatchCapture()
        {
            CollectionAssert.AreEqual(Hex("0C 91 01 02 00 04 00 00 37 00 00 00"), Joycon2Protocol.SetFeatureMask(0x37));
            CollectionAssert.AreEqual(Hex("0C 91 01 04 00 04 00 00 37 00 00 00"), Joycon2Protocol.EnableFeatures(0x37));
        }

        [Test]
        public void MemoryRead_MatchesCapture()
        {
            CollectionAssert.AreEqual(Hex("02 91 01 04 00 08 00 00 40 7E 00 00 80 30 01 00"),
                Joycon2Protocol.ReadMemory(0x13080, 0x40));
            Assert.Throws<ArgumentOutOfRangeException>(() => Joycon2Protocol.ReadMemory(0, 0x50));
        }

        [Test]
        public void PlayerLedPatterns()
        {
            Assert.AreEqual(0x01, Joycon2Protocol.PlayerLedPattern(0));
            Assert.AreEqual(0x03, Joycon2Protocol.PlayerLedPattern(1));
            Assert.AreEqual(0x0F, Joycon2Protocol.PlayerLedPattern(7));
            Assert.AreEqual(0x00, Joycon2Protocol.PlayerLedPattern(-1));
        }

        // ───────────────────────────── Advertisement ─────────────────────────────

        [Test]
        public void Advertisement_LeftInPairingMode()
        {
            var data = Hex("01 00 03 7E 05 67 20 00 01 00 00 00 00 00 00 00 0F 00 00 00 00 00");
            Assert.IsTrue(Joycon2Advertisement.TryParse(data, data.Length, out var adv));
            Assert.AreEqual(Joycon2Side.Left, adv.Side);
            Assert.IsTrue(adv.PairingMode);
        }

        [Test]
        public void Advertisement_RightReconnectingToHost()
        {
            var data = Hex("01 00 03 7E 05 66 20 00 01 81 11 22 33 44 55 66 0F 00 00 00 00 00");
            Assert.IsTrue(Joycon2Advertisement.TryParse(data, data.Length, out var adv));
            Assert.AreEqual(Joycon2Side.Right, adv.Side);
            Assert.IsFalse(adv.PairingMode);
        }

        [Test]
        public void Advertisement_RejectsOtherProducts()
        {
            var pro = Hex("01 00 03 7E 05 69 20 00 01 00 00 00 00 00 00 00");
            Assert.IsFalse(Joycon2Advertisement.TryParse(pro, pro.Length, out _));
            var foreign = Hex("01 00 03 4C 00 67 20 00 01 00 00 00 00 00 00 00");
            Assert.IsFalse(Joycon2Advertisement.TryParse(foreign, foreign.Length, out _));
            Assert.IsFalse(Joycon2Advertisement.TryParse(new byte[3], 3, out _));
        }

        // ───────────────────────────── Report ─────────────────────────────

        internal static byte[] MakeReport(uint buttons, int s1x, int s1y, int s2x, int s2y, int length = 63)
        {
            var r = new byte[length];
            r[0] = 0x10; r[1] = 0x20;
            r[4] = (byte)buttons; r[5] = (byte)(buttons >> 8); r[6] = (byte)(buttons >> 16); r[7] = (byte)(buttons >> 24);
            Pack(r, 0x0A, s1x, s1y);
            Pack(r, 0x0D, s2x, s2y);
            if (length > 0x20) { r[0x1F] = 0x10; r[0x20] = 0x0E; } // 3600 mV
            if (length >= Joycon2Report.FullLength) { r[0x29] = 0x01; r[0x34] = 0x00; r[0x35] = 0x10; } // IMU on, accel z = 1 g
            return r;
        }

        static void Pack(byte[] r, int o, int x, int y)
        {
            r[o] = (byte)x;
            r[o + 1] = (byte)((x >> 8) & 0x0F | (y & 0x0F) << 4);
            r[o + 2] = (byte)(y >> 4);
        }

        [Test]
        public void Report_ParsesButtonsSticksBattery()
        {
            var data = MakeReport((uint)(Joycon2Buttons.A | Joycon2Buttons.ZL | Joycon2Buttons.Home), 0x123, 0xABC, 0x7FF, 4095);
            Assert.IsTrue(Joycon2Report.TryParse(data, data.Length, out var r));
            Assert.AreEqual(0x2010u, r.Counter);
            Assert.IsTrue(r.IsPressed(Joycon2Buttons.A));
            Assert.IsTrue(r.IsPressed(Joycon2Buttons.ZL));
            Assert.IsTrue(r.IsPressed(Joycon2Buttons.Home));
            Assert.IsFalse(r.IsPressed(Joycon2Buttons.B));
            Assert.AreEqual(0x123, r.Stick1X);
            Assert.AreEqual(0xABC, r.Stick1Y);
            Assert.AreEqual(0x7FF, r.Stick2X);
            Assert.AreEqual(4095, r.Stick2Y);
            Assert.AreEqual(3600, r.BatteryMillivolts);
            Assert.IsTrue(r.HasMotion);
        }

        [Test]
        public void Report_RejectsShortData()
        {
            Assert.IsFalse(Joycon2Report.TryParse(new byte[15], 15, out _));
            var shortReport = MakeReport(0, 1, 2, 3, 4, Joycon2Report.MinLength);
            Assert.IsTrue(Joycon2Report.TryParse(shortReport, shortReport.Length, out var r));
            Assert.AreEqual(0, r.BatteryMillivolts);
            Assert.IsFalse(r.HasMotion);
        }

        // ───────────────────────────── Calibration ─────────────────────────────

        [Test]
        public void Calibration_DecodesCapturedBlock()
        {
            var data = Hex("B3 67 83 2E 66 5E 3A 06 5F");
            Assert.IsTrue(Joycon2StickCalibration.TryParse(data, 0, data.Length, out var cal));
            Assert.AreEqual(1971, cal.CenterX);
            Assert.AreEqual(2102, cal.CenterY);
            Assert.AreEqual(1582, cal.MaxX);
            Assert.AreEqual(1510, cal.MaxY);
            Assert.AreEqual(1594, cal.MinX);
            Assert.AreEqual(1520, cal.MinY);
        }

        [Test]
        public void Calibration_RejectsErasedAndGarbage()
        {
            var erased = Hex("FF FF FF FF FF FF FF FF FF");
            Assert.IsFalse(Joycon2StickCalibration.TryParse(erased, 0, erased.Length, out _));
            var zeros = new byte[9];
            Assert.IsFalse(Joycon2StickCalibration.TryParse(zeros, 0, zeros.Length, out _));
            Assert.IsFalse(Joycon2StickCalibration.TryParse(zeros, 2, zeros.Length, out _), "out of bounds");
        }

        [Test]
        public void Calibration_NormalizesAndClamps()
        {
            var cal = new Joycon2StickCalibration { CenterX = 2000, CenterY = 2100, MaxX = 1000, MaxY = 1200, MinX = 800, MinY = 600 };
            cal.Normalize(2000, 2100, out float x, out float y);
            Assert.AreEqual(0f, x, 1e-6);
            Assert.AreEqual(0f, y, 1e-6);
            cal.Normalize(2500, 1800, out x, out y);
            Assert.AreEqual(0.5f, x, 1e-6);
            Assert.AreEqual(-0.5f, y, 1e-6);
            cal.Normalize(4095, 0, out x, out y);
            Assert.AreEqual(1f, x);
            Assert.AreEqual(-1f, y);
        }

        // ───────────────────────────── Mapping ─────────────────────────────

        static readonly Joycon2StickCalibration Cal = new Joycon2StickCalibration
        {
            CenterX = 2048, CenterY = 2048, MaxX = 1000, MaxY = 1000, MinX = 1000, MinY = 1000,
        };

        [Test]
        public void Sideways_Left_StickUpBecomesLeft()
        {
            // Vertical "up" on the left Joy-Con points left when it is held sideways.
            var data = MakeReport(0, 2048, 3048, 0x7FF, 0x7FF);
            Joycon2Report.TryParse(data, data.Length, out var r);
            var pad = Joycon2Mapping.Sideways(Joycon2Side.Left, r, Cal);
            Assert.AreEqual(-1f, pad.StickX, 1e-6);
            Assert.AreEqual(0f, pad.StickY, 1e-6);

            data = MakeReport(0, 3048, 2048, 0x7FF, 0x7FF); // vertical right → sideways up
            Joycon2Report.TryParse(data, data.Length, out r);
            pad = Joycon2Mapping.Sideways(Joycon2Side.Left, r, Cal);
            Assert.AreEqual(0f, pad.StickX, 1e-6);
            Assert.AreEqual(1f, pad.StickY, 1e-6);
        }

        [Test]
        public void Sideways_Right_UsesStick2AndRotatesClockwise()
        {
            var data = MakeReport(0, 0x7FF, 0x7FF, 2048, 3048); // vertical up → sideways right
            Joycon2Report.TryParse(data, data.Length, out var r);
            var pad = Joycon2Mapping.Sideways(Joycon2Side.Right, r, Cal);
            Assert.AreEqual(1f, pad.StickX, 1e-6);
            Assert.AreEqual(0f, pad.StickY, 1e-6);

            data = MakeReport(0, 0x7FF, 0x7FF, 3048, 2048); // vertical right → sideways down
            Joycon2Report.TryParse(data, data.Length, out r);
            pad = Joycon2Mapping.Sideways(Joycon2Side.Right, r, Cal);
            Assert.AreEqual(-1f, pad.StickY, 1e-6);
        }

        [Test]
        public void Sideways_Buttons()
        {
            var left = MakeReport((uint)(Joycon2Buttons.Left | Joycon2Buttons.Up | Joycon2Buttons.LeftSL | Joycon2Buttons.ZL | Joycon2Buttons.A), 2048, 2048, 0, 0);
            Joycon2Report.TryParse(left, left.Length, out var r);
            var pad = Joycon2Mapping.Sideways(Joycon2Side.Left, r, Cal);
            Assert.AreEqual(Joycon2PadButtons.South | Joycon2PadButtons.West | Joycon2PadButtons.LeftShoulder | Joycon2PadButtons.RightTrigger,
                pad.Buttons, "right-side buttons must not leak into the left Joy-Con");

            var right = MakeReport((uint)(Joycon2Buttons.A | Joycon2Buttons.X | Joycon2Buttons.RightSR | Joycon2Buttons.Plus | Joycon2Buttons.Down), 0, 0, 2048, 2048);
            Joycon2Report.TryParse(right, right.Length, out r);
            pad = Joycon2Mapping.Sideways(Joycon2Side.Right, r, Cal);
            Assert.AreEqual(Joycon2PadButtons.South | Joycon2PadButtons.East | Joycon2PadButtons.RightShoulder | Joycon2PadButtons.Start,
                pad.Buttons);
        }

        // ───────────────────────────── Session ─────────────────────────────

        sealed class FakeLink : IJoycon2Link
        {
            public readonly List<(string uuid, int tag)> Subscriptions = new List<(string, int)>();
            public readonly List<byte[]> Writes = new List<byte[]>();
            public void Subscribe(string characteristicUuid, int tag) => Subscriptions.Add((characteristicUuid, tag));
            public void Write(string characteristicUuid, byte[] data)
            {
                Assert.AreEqual(Joycon2Protocol.CommandUuid, characteristicUuid);
                Writes.Add(data);
            }
            public byte[] Last => Writes[Writes.Count - 1];
        }

        static byte[] Ack(byte[] command, byte[] data = null)
        {
            // [cmd] 01 01 [sub] 10 78 00 00, memory reads: + [len] 00 00 00 [addr x4] + data
            int extra = command[0] == Joycon2Protocol.CmdMemory ? 8 + command[8] : 0;
            var r = new byte[8 + extra];
            r[0] = command[0]; r[1] = 0x01; r[2] = 0x01; r[3] = command[3]; r[4] = 0x10; r[5] = 0x78;
            if (extra > 0)
            {
                r[8] = command[8];
                Array.Copy(command, 12, r, 12, 4);
                for (int i = 16; i < r.Length; i++) r[i] = 0xFF;
                if (data != null) Array.Copy(data, 0, r, 16, data.Length);
            }
            return r;
        }

        static void Respond(Joycon2Session s, byte[] response, double now) =>
            s.OnNotification(Joycon2Session.TagCommandResponse, response, response.Length, now);

        [Test]
        public void Session_FullHandshake_FactoryCalibration()
        {
            var link = new FakeLink();
            var s = new Joycon2Session(link, Joycon2Side.Right) { PlayerLed = 0x03 };
            s.OnConnected(0);
            Assert.AreEqual(Joycon2Session.SessionState.Handshake, s.State);
            Assert.AreEqual((Joycon2Protocol.CommandResponseUuid, Joycon2Session.TagCommandResponse), link.Subscriptions[0]);

            s.OnSubscribed(Joycon2Session.TagCommandResponse, 0, 0.1);
            CollectionAssert.AreEqual(Joycon2Protocol.ReadMemory(Joycon2Protocol.UserStickCalibrationAddress, 0x30), link.Last);
            Respond(s, Ack(link.Last), 0.2); // user slot erased (FF)

            CollectionAssert.AreEqual(Joycon2Protocol.ReadMemory(Joycon2Protocol.FactoryStickCalibrationAddress, 0x49), link.Last);
            Respond(s, Ack(link.Last, Hex("B3 67 83 2E 66 5E 3A 06 5F")), 0.3);
            Assert.AreEqual(Joycon2Session.CalibrationSource.Factory, s.CalibratedFrom);
            Assert.AreEqual(1971, s.Calibration.CenterX);

            CollectionAssert.AreEqual(Joycon2Protocol.SetPlayerLed(0x03), link.Last);
            Respond(s, Ack(link.Last), 0.4);
            CollectionAssert.AreEqual(Joycon2Protocol.SetFeatureMask(Joycon2Protocol.DefaultFeatureMask), link.Last);
            Respond(s, Ack(link.Last), 0.5);
            CollectionAssert.AreEqual(Joycon2Protocol.EnableFeatures(Joycon2Protocol.DefaultFeatureMask), link.Last);
            Respond(s, Ack(link.Last), 0.6);

            Assert.AreEqual((Joycon2Protocol.InputReportUuid, Joycon2Session.TagInput), link.Subscriptions[1]);
            s.OnSubscribed(Joycon2Session.TagInput, 0, 0.7);
            Assert.AreEqual(Joycon2Session.SessionState.Ready, s.State);

            var report = MakeReport((uint)Joycon2Buttons.A, 0x7FF, 0x7FF, 2000, 2100);
            Assert.IsTrue(s.OnNotification(Joycon2Session.TagInput, report, report.Length, 0.8));
            Assert.IsTrue(s.LastReport.IsPressed(Joycon2Buttons.A));
            Assert.AreEqual(1, s.ReportCount);
        }

        [Test]
        public void Session_UnknownSide_ReadFromInfoBlock()
        {
            var link = new FakeLink();
            var s = new Joycon2Session(link, null);
            Assert.IsFalse(s.SideKnown);
            s.OnConnected(0);
            s.OnSubscribed(Joycon2Session.TagCommandResponse, 0, 0);
            CollectionAssert.AreEqual(Joycon2Protocol.ReadMemory(Joycon2Protocol.InfoAddress, Joycon2Protocol.InfoReadLength), link.Last);

            var info = new byte[Joycon2Protocol.InfoReadLength];
            info[0x12] = 0x7E; info[0x13] = 0x05; // VID
            info[0x14] = 0x66; info[0x15] = 0x20; // PID: Joy-Con 2 (R)
            Respond(s, Ack(link.Last, info), 0.1);
            Assert.IsTrue(s.SideKnown);
            Assert.AreEqual(Joycon2Side.Right, s.Side);
            CollectionAssert.AreEqual(Joycon2Protocol.ReadMemory(Joycon2Protocol.UserStickCalibrationAddress, 0x30), link.Last,
                                      "handshake continues with the calibration");
        }

        [Test]
        public void Session_KnownSide_SkipsInfoRead()
        {
            var link = new FakeLink();
            var s = new Joycon2Session(link, Joycon2Side.Left);
            s.OnConnected(0);
            s.OnSubscribed(Joycon2Session.TagCommandResponse, 0, 0);
            CollectionAssert.AreEqual(Joycon2Protocol.ReadMemory(Joycon2Protocol.UserStickCalibrationAddress, 0x30), link.Last);
        }

        [Test]
        public void Session_UserCalibrationSkipsFactoryRead()
        {
            var link = new FakeLink();
            var s = new Joycon2Session(link, Joycon2Side.Left);
            s.OnConnected(0);
            s.OnSubscribed(Joycon2Session.TagCommandResponse, 0, 0);
            var block = new byte[0x30];
            for (int i = 0; i < block.Length; i++) block[i] = 0xFF;
            Array.Copy(Hex("B3 67 83 2E 66 5E 3A 06 5F"), 0, block, Joycon2Protocol.UserStick1Offset, 9);
            Respond(s, Ack(link.Last, block), 0.1);
            Assert.AreEqual(Joycon2Session.CalibrationSource.User, s.CalibratedFrom);
            Assert.AreEqual(Joycon2Protocol.CmdLed, link.Last[0], "factory read must be skipped");
        }

        [Test]
        public void Session_IgnoresResponseToAnotherCommand()
        {
            var link = new FakeLink();
            var s = new Joycon2Session(link, Joycon2Side.Left);
            s.OnConnected(0);
            s.OnSubscribed(Joycon2Session.TagCommandResponse, 0, 0);
            int writes = link.Writes.Count;
            Respond(s, Ack(Joycon2Protocol.SetPlayerLed(1)), 0.1);
            Respond(s, Ack(Joycon2Protocol.ReadMemory(0x13000, 0x40)), 0.1); // same command, other address
            Assert.AreEqual(writes, link.Writes.Count);
            Assert.AreEqual(Joycon2Session.CalibrationSource.Default, s.CalibratedFrom);
        }

        [Test]
        public void Session_TimeoutsRetryThenSkip_AndKeepAlive()
        {
            var link = new FakeLink();
            var s = new Joycon2Session(link, Joycon2Side.Left);
            s.OnConnected(0);
            s.OnSubscribed(Joycon2Session.TagCommandResponse, 0, 0);
            double t = 0;
            for (int i = 0; i < 40 && s.State == Joycon2Session.SessionState.Handshake && link.Subscriptions.Count < 2; i++)
                s.Update(t += 0.6);

            Assert.AreEqual(2, link.Subscriptions.Count, "handshake must reach the input subscription without any responses");
            // 5 command steps × 2 attempts each (user calibration, factory calibration, LED, mask, enable).
            Assert.AreEqual(10, link.Writes.Count);
            Assert.AreEqual(Joycon2Session.CalibrationSource.Default, s.CalibratedFrom);

            s.OnSubscribed(Joycon2Session.TagInput, 0, t);
            Assert.AreEqual(Joycon2Session.SessionState.Ready, s.State);
            int before = link.Writes.Count;
            s.Update(t + 0.5);
            Assert.AreEqual(before, link.Writes.Count);
            s.Update(t + 1.01);
            Assert.AreEqual(before + 1, link.Writes.Count);
            Assert.AreEqual(Joycon2Protocol.CmdLed, link.Last[0], "keep-alive re-sends the LED command");
        }

        [Test]
        public void Session_BlindModeWhenResponsesUnavailable()
        {
            var link = new FakeLink();
            var s = new Joycon2Session(link, Joycon2Side.Left);
            s.OnConnected(0);
            s.OnSubscribed(Joycon2Session.TagCommandResponse, -1, 0);
            Assert.AreEqual(Joycon2Protocol.CmdLed, link.Last[0], "no memory reads without responses");
            s.Update(0.35);
            s.Update(0.7);
            s.Update(1.05);
            Assert.AreEqual(3, link.Writes.Count);
            Assert.AreEqual(2, link.Subscriptions.Count);
        }

        [Test]
        public void Session_FailsWhenInputSubscriptionFails()
        {
            var link = new FakeLink();
            var s = new Joycon2Session(link, Joycon2Side.Left);
            s.OnConnected(0);
            s.OnSubscribed(Joycon2Session.TagCommandResponse, -1, 0);
            s.Update(1); s.Update(2); s.Update(3);
            s.OnSubscribed(Joycon2Session.TagInput, 3, 3);
            Assert.AreEqual(Joycon2Session.SessionState.Failed, s.State);
        }

        [Test]
        public void Session_PlayerLedChangeIsSentWhenReady()
        {
            var link = new FakeLink();
            var s = new Joycon2Session(link, Joycon2Side.Left);
            s.OnConnected(0);
            s.OnSubscribed(Joycon2Session.TagCommandResponse, -1, 0);
            s.Update(1); s.Update(2); s.Update(3);
            s.OnSubscribed(Joycon2Session.TagInput, 0, 3);
            s.PlayerLed = 0x07;
            CollectionAssert.AreEqual(Joycon2Protocol.SetPlayerLed(0x07), link.Last);
        }

        // ───────────────────────────── Motion ─────────────────────────────

        static byte[] MakeMotionReport(short ax, short ay, short az, short gx, short gy, short gz)
        {
            var r = MakeReport(0, 2048, 2048, 2048, 2048);
            short[] v = { ax, ay, az, gx, gy, gz };
            for (int i = 0; i < 6; i++)
            {
                r[0x30 + i * 2] = (byte)v[i];
                r[0x31 + i * 2] = (byte)(v[i] >> 8);
            }
            return r;
        }

        [Test]
        public void Motion_RequiresMarkerAndNonZeroAccel()
        {
            var on = MakeMotionReport(0, 0, 4096, 0, 0, 0);
            Joycon2Report.TryParse(on, on.Length, out var r);
            Assert.IsTrue(r.HasMotion);

            var off = MakeMotionReport(0, 0, 0, 0, 0, 0); // IMU disabled: all zero
            Joycon2Report.TryParse(off, off.Length, out r);
            Assert.IsFalse(r.HasMotion);

            var noMarker = MakeMotionReport(0, 0, 4096, 0, 0, 0);
            noMarker[0x29] = 0;
            Joycon2Report.TryParse(noMarker, noMarker.Length, out r);
            Assert.IsFalse(r.HasMotion);
        }

        [Test]
        public void Motion_FlatFaceUpReadsUp_BothSides()
        {
            var data = MakeMotionReport(0, 0, 4096, 0, 0, 0);
            Joycon2Report.TryParse(data, data.Length, out var r);
            foreach (var side in new[] { Joycon2Side.Left, Joycon2Side.Right })
            {
                Joycon2MotionMapping.Sideways(side, r, null, out _, out var a);
                Assert.AreEqual(0f, a.X, 1e-4);
                Assert.AreEqual(1f, a.Y, 1e-4);
                Assert.AreEqual(0f, a.Z, 1e-4);
            }
        }

        [Test]
        public void Motion_SidesAreMirroredAlongTheLongAxis()
        {
            // IMU +Y (towards the shoulder buttons) points left on the left Joy-Con, right on the right one.
            var data = MakeMotionReport(0, 4096, 0, 0, 0, 0);
            Joycon2Report.TryParse(data, data.Length, out var r);
            Joycon2MotionMapping.Sideways(Joycon2Side.Left, r, null, out _, out var left);
            Joycon2MotionMapping.Sideways(Joycon2Side.Right, r, null, out _, out var right);
            AssertVector(-1f, 0f, 0f, left, 1e-4f);
            AssertVector(1f, 0f, 0f, right, 1e-4f);

            // IMU +X (right edge, upright) is the rail on the left Joy-Con → forward; on the right one → towards the player.
            data = MakeMotionReport(4096, 0, 0, 0, 0, 0);
            Joycon2Report.TryParse(data, data.Length, out r);
            Joycon2MotionMapping.Sideways(Joycon2Side.Left, r, null, out _, out left);
            Joycon2MotionMapping.Sideways(Joycon2Side.Right, r, null, out _, out right);
            AssertVector(0f, 0f, 1f, left, 1e-4f);
            AssertVector(0f, 0f, -1f, right, 1e-4f);
        }

        [Test]
        public void Motion_GyroScaleAndUnitySign()
        {
            // Right-hand rotation about the IMU's face normal (+Z) = counter-clockwise seen from above
            // = turning left; in Unity's sign that is negative Y.
            short raw = (short)Math.Round(90f / Joycon2Report.GyroDpsPerLsb);
            var data = MakeMotionReport(0, 0, 4096, 0, 0, raw);
            Joycon2Report.TryParse(data, data.Length, out var r);
            Joycon2MotionMapping.Sideways(Joycon2Side.Left, r, null, out var g, out _);
            Assert.AreEqual(-90f, g.Y, 0.05f);
            Assert.AreEqual(0f, g.X, 1e-4);
            Assert.AreEqual(0f, g.Z, 1e-4);
        }

        [Test]
        public void GyroBias_LearnsOnlyWhileStill()
        {
            var bias = new Joycon2GyroBias();
            for (int i = 0; i < 600; i++) bias.Update(1.5f, -0.5f, 0.2f, 1f, 1f / 60f); // 10 s at rest (τ = 2 s)
            Assert.IsTrue(bias.IsStill);
            Assert.AreEqual(1.5f, bias.X, 0.1f);
            Assert.AreEqual(-0.5f, bias.Y, 0.1f);

            float learned = bias.X;
            for (int i = 0; i < 300; i++) bias.Update(40f, 0f, 0f, 1f, 1f / 60f); // real rotation
            Assert.IsFalse(bias.IsStill);
            Assert.AreEqual(learned, bias.X, 1e-5);

            for (int i = 0; i < 300; i++) bias.Update(1.5f, -0.5f, 0.2f, 1.4f, 1f / 60f); // shaken: not ~1 g
            Assert.IsFalse(bias.IsStill);
        }

        // ───────────────────────────── Orientation ─────────────────────────────

        static readonly Joycon2Vector3 Up = new Joycon2Vector3(0f, 1f, 0f);
        static readonly Joycon2Vector3 Forward = new Joycon2Vector3(0f, 0f, 1f);

        static void AssertVector(float x, float y, float z, Joycon2Vector3 v, float tolerance = 1e-3f)
        {
            Assert.AreEqual(x, v.X, tolerance, "x");
            Assert.AreEqual(y, v.Y, tolerance, "y");
            Assert.AreEqual(z, v.Z, tolerance, "z");
        }

        [Test]
        public void Quaternion_MatchesUnityConventions()
        {
            // Unity: Quaternion.Euler(0, 90, 0) * Vector3.forward == Vector3.right.
            var yaw = Joycon2Quaternion.FromRotationVector(new Joycon2Vector3(0f, (float)Math.PI / 2f, 0f));
            AssertVector(1f, 0f, 0f, yaw.Rotate(Forward));
            // Unity: Quaternion.Euler(90, 0, 0) * Vector3.forward == Vector3.down (+X pitches the nose down).
            var pitch = Joycon2Quaternion.FromRotationVector(new Joycon2Vector3((float)Math.PI / 2f, 0f, 0f));
            AssertVector(0f, -1f, 0f, pitch.Rotate(Forward));
            AssertVector(0f, 90f, 0f, yaw.EulerDegrees, 0.01f);
            var back = yaw.Conjugate * yaw;
            Assert.AreEqual(1f, back.W, 1e-5f);
        }

        [Test]
        public void Orientation_IntegratesGyro()
        {
            var o = new Joycon2Orientation { GravityCorrectionTime = 0f };
            for (int i = 0; i < 100; i++) o.Update(new Joycon2Vector3(0f, 90f, 0f), Up, 0.01f); // 90°/s for 1 s
            AssertVector(1f, 0f, 0f, o.Current.Rotate(Forward), 1e-3f);
        }

        [Test]
        public void Orientation_RecenterZeroesRelative()
        {
            var o = new Joycon2Orientation { GravityCorrectionTime = 0f, AutoCenterTime = 0f };
            for (int i = 0; i < 50; i++) o.Update(new Joycon2Vector3(0f, 0f, 60f), Up, 0.01f);
            Assert.Less(o.Relative.Rotate(Up).Y, 0.9f, "rolled 30°");
            o.Recenter();
            AssertVector(0f, 1f, 0f, o.Relative.Rotate(Up));
            for (int i = 0; i < 50; i++) o.Update(new Joycon2Vector3(-60f, 0f, 0f), Up, 0.01f); // rail edge up 30°
            var face = o.Relative.Rotate(Up);
            Assert.AreEqual(Math.Cos(Math.PI / 6), face.Y, 1e-3);
            Assert.Less(face.Z, -0.4f, "rail edge up tilts the face towards the player");
        }

        [Test]
        public void Orientation_GravityPullsTiltBack()
        {
            // Estimate wrongly believes it is rolled 30°, but the accelerometer says level: it must converge.
            var o = new Joycon2Orientation();
            for (int i = 0; i < 50; i++) o.Update(new Joycon2Vector3(0f, 0f, 60f), Up, 0.01f);
            for (int i = 0; i < 600; i++) o.Update(default, Up, 0.01f);
            Assert.AreEqual(1f, o.Current.Rotate(Up).Y, 1e-3f);
        }

        [Test]
        public void Orientation_IgnoresShakesAndStalls()
        {
            var o = new Joycon2Orientation();
            o.Update(new Joycon2Vector3(0f, 0f, 1000f), Up, 1f); // a 1 s gap is a stall, not a rotation
            Assert.AreEqual(1f, o.Current.W, 1e-6f);
            for (int i = 0; i < 50; i++) o.Update(new Joycon2Vector3(0f, 0f, 60f), new Joycon2Vector3(0f, 3f, 0f), 0.01f);
            var up = o.Current.Rotate(Up);
            Assert.AreEqual(Math.Cos(Math.PI / 6), up.Y, 1e-3, "3 g shake must not correct the tilt");
        }

        static void Spin(Joycon2Orientation o, float x, float y, float z, float seconds)
        {
            for (int i = 0; i < Math.Round(seconds / 0.01f); i++) o.Update(new Joycon2Vector3(x, y, z), Up, 0.01f);
        }

        [Test]
        public void Orientation_RelativeIgnoresHeading()
        {
            var o = new Joycon2Orientation { GravityCorrectionTime = 0f, AutoCenterTime = 0f };
            o.Recenter();
            Spin(o, 0f, 90f, 0f, 1f); // the player turns 90° with the Joy-Con
            Assert.Less(Joycon2Orientation.AngleDegrees(o.Relative), 0.5f, "turning around is not a tilt");
            Spin(o, 0f, 0f, -60f, 0.5f); // then steers right 30° (right end down)
            var face = o.Relative.Rotate(Up);
            Assert.AreEqual(0.5f, face.X, 1e-3f, "face tilts right, as before the turn");
            Assert.AreEqual(0f, face.Z, 1e-3f);
        }

        [Test]
        public void Orientation_InitialCenterAfterSettling()
        {
            var o = new Joycon2Orientation { GravityCorrectionTime = 0f, AutoCenterTime = 0f };
            Spin(o, 0f, 0f, 40f, 0.5f);  // picked up rolled 20°
            Spin(o, 0f, 0f, 0f, 1.1f);   // held still: zero is taken at 1.5 s
            Assert.Less(Joycon2Orientation.AngleDegrees(o.Relative), 0.5f);
        }

        [Test]
        public void Orientation_AutoCenterFollowsUsualGripOnly()
        {
            var o = new Joycon2Orientation { GravityCorrectionTime = 0f, AutoCenterTime = 2f, InitialCenterDelay = 0f };
            Spin(o, 0f, 0f, 0f, 0.02f);   // zero taken
            Spin(o, 0f, 0f, 40f, 0.5f);   // the grip settles 20° rolled
            Spin(o, 0f, 0f, 0f, 20f);     // ...and stays there
            Assert.Less(Joycon2Orientation.AngleDegrees(o.Relative), 0.5f, "the zero moved to the usual grip");

            Spin(o, 0f, 0f, 120f, 0.5f);  // a deliberate 60° tilt, held
            Spin(o, 0f, 0f, 0f, 20f);
            // ~2° may leak while the tilt passes through the 30° cone (τ = 2 s here; 20 s by default).
            Assert.Greater(Joycon2Orientation.AngleDegrees(o.Relative), 55f, "big tilts do not drag the zero");

            o.AutoCenter = false;         // and nothing moves while steering
            Spin(o, 0f, 0f, -100f, 0.5f); // back to 10° from the zero
            float before = Joycon2Orientation.AngleDegrees(o.Relative);
            Spin(o, 0f, 0f, 0f, 20f);
            Assert.AreEqual(before, Joycon2Orientation.AngleDegrees(o.Relative), 0.01f);
        }
    }
}
