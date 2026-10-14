using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Joycon2.Tests
{
    // The manager end to end on a scripted transport: scan → connect → handshake → gamepad → input → silence → drop.
    public class Joycon2ManagerTests
    {
        /// <summary>Answers like a plugin with a Joy-Con 2 that never answers commands (the session skips those steps).</summary>
        sealed class FakeTransport : IJoycon2Transport
        {
            public readonly Queue<(Joycon2TransportEvent e, byte[] payload)> Events = new Queue<(Joycon2TransportEvent, byte[])>();
            public readonly List<byte[]> Writes = new List<byte[]>();
            public readonly List<int> Disconnects = new List<int>();
            public ushort ScanCompany;
            public bool ShutDown;
            int nextHandle = 1;

            public string Name => "fake";
            public bool RequiresUserGesture => false;
            public string LocalAddressPrefix => "fake";
            public bool Initialize() => true;
            public void Shutdown() => ShutDown = true;
            public void StartScan(ushort companyId) => ScanCompany = companyId;
            public void StopScan() { }
            public void RequestDevice(string requestJson, int tag) { }

            public int Connect(ulong address)
            {
                int handle = nextHandle++;
                Push(new Joycon2TransportEvent { Type = Joycon2TransportEventType.Connected, Handle = handle, Address = address, Status = 185 });
                return handle;
            }

            public void Disconnect(int handle) => Disconnects.Add(handle);

            public void Subscribe(int handle, string serviceUuid, string characteristicUuid, int tag) =>
                Push(new Joycon2TransportEvent { Type = Joycon2TransportEventType.Subscribed, Handle = handle, Tag = tag });

            public void Write(int handle, string serviceUuid, string characteristicUuid, byte[] data, int length) =>
                Writes.Add(data);

            public void Push(Joycon2TransportEvent e, byte[] payload = null)
            {
                e.Length = payload?.Length ?? 0;
                Events.Enqueue((e, payload));
            }

            public bool TryDequeueEvent(out Joycon2TransportEvent e, byte[] payload)
            {
                if (Events.Count == 0) { e = default; return false; }
                byte[] data;
                (e, data) = Events.Dequeue();
                if (data != null) Buffer.BlockCopy(data, 0, payload, 0, Math.Min(data.Length, payload.Length));
                return true;
            }
        }

        FakeTransport transport;
        readonly List<Joycon2Controller> connected = new List<Joycon2Controller>();
        readonly List<string> disconnected = new List<string>();

        [SetUp]
        public void SetUp()
        {
            Joycon2Lifecycle.Stop();
            connected.Clear();
            disconnected.Clear();
            transport = new FakeTransport();
            Joycon2Manager.TransportFactory = () => transport;
            var settings = ScriptableObject.CreateInstance<Joycon2Settings>();
            settings.ReportSilenceTimeout = 0.3f;
            settings.DiagnosticsOverlay = Joycon2OverlayMode.Off;
            Joycon2Manager.Settings = settings;
            Joycon2Manager.SessionOptions = new Joycon2SessionOptions { ResponseTimeout = 0.01, CommandAttempts = 1, KeepAliveInterval = 0.05 };
            Joycon2Manager.ControllerConnected += OnConnected;
            Joycon2Manager.ControllerDisconnected += OnDisconnected;
        }

        [TearDown]
        public void TearDown()
        {
            Joycon2Manager.Stop();
            Joycon2Manager.ControllerConnected -= OnConnected;
            Joycon2Manager.ControllerDisconnected -= OnDisconnected;
            Joycon2Manager.TransportFactory = null;
            Joycon2Manager.Settings = null;
            Joycon2Manager.SessionOptions = new Joycon2SessionOptions();
        }

        void OnConnected(Joycon2Controller c) => connected.Add(c);
        void OnDisconnected(Joycon2Controller c, string reason) => disconnected.Add(reason);

        static void PumpUntil(Func<bool> done, double seconds = 3)
        {
            var clock = Stopwatch.StartNew();
            while (!done() && clock.Elapsed.TotalSeconds < seconds)
            {
                InputSystem.Update(); // runs onBeforeUpdate → the manager's pump
                System.Threading.Thread.Sleep(5);
            }
            Assert.IsTrue(done(), "timed out");
        }

        void Advertise(ulong address, bool pairing = true)
        {
            // Manufacturer data after the company id: VID 0x057E, PID 0x2067 (L); a host address at [10..15] = not pairing.
            var data = Joycon2CoreTests.Hex("01 00 03 7E 05 67 20 00 01 00 00 00 00 00 00 00");
            if (!pairing) data[10] = 0x42;
            transport.Push(new Joycon2TransportEvent { Type = Joycon2TransportEventType.DeviceFound, Address = address, Rssi = -50 }, data);
        }

        void Report(int handle, Joycon2Buttons buttons) =>
            transport.Push(new Joycon2TransportEvent { Type = Joycon2TransportEventType.Notification, Handle = handle, Tag = Joycon2Session.TagInput },
                           Joycon2CoreTests.MakeReport((uint)buttons, 2048, 2048, 0, 0));

        [Test]
        public void ConnectsAJoyconInSyncAndFeedsTheInputSystem()
        {
            Joycon2Manager.Start();
            Assert.IsTrue(Joycon2Manager.IsRunning);
            PumpUntil(() => Joycon2Manager.IsScanning);
            Assert.AreEqual(Joycon2Protocol.CompanyId, transport.ScanCompany);

            Advertise(7);
            PumpUntil(() => connected.Count == 1);
            var c = connected[0];
            Assert.AreEqual(Joycon2Side.Left, c.Side);
            Assert.AreEqual("fake#7", c.AddressText);
            Assert.AreEqual(185, c.Mtu);
            Assert.AreEqual(0, c.PlayerIndex, "first free LED: P1");
            Assert.IsTrue(c.Device.added);
            CollectionAssert.Contains(InputSystem.devices, c.Device);
            Assert.AreSame(c, Joycon2Manager.FindController(c.Device));

            Report(c.Handle, Joycon2Buttons.Left); // D-Left on L held sideways = South
            PumpUntil(() => c.Device.buttonSouth.isPressed);

            Joycon2Manager.SetPlayerIndex(c.Device, 1);
            PumpUntil(() => transport.Writes.Exists(w => w[0] == Joycon2Protocol.CmdLed && w[8] == 0x03));
            Assert.AreEqual(1, c.Device.PlayerIndex);
        }

        [Test]
        public void IgnoresJoyconsLookingForTheirConsole()
        {
            Joycon2Manager.Start();
            Advertise(9, pairing: false);
            for (int i = 0; i < 5; i++) InputSystem.Update();
            Assert.AreEqual(0, Joycon2Manager.Controllers.Count);
        }

        [Test]
        public void SilentLinkIsDroppedAndTheGamepadRemoved()
        {
            Joycon2Manager.Start();
            Advertise(7);
            PumpUntil(() => connected.Count == 1);
            var device = connected[0].Device;
            Report(connected[0].Handle, Joycon2Buttons.None);
            PumpUntil(() => connected[0].Session.HasReport);

            PumpUntil(() => disconnected.Count == 1, 2);
            StringAssert.Contains("no reports", disconnected[0]);
            Assert.IsFalse(device.added);
            Assert.AreEqual(0, Joycon2Manager.Controllers.Count);
            CollectionAssert.Contains(transport.Disconnects, connected[0].Handle);
        }

        [Test]
        public void ALongPumpGapIsNotMistakenForSilence()
        {
            Joycon2Manager.Start();
            Advertise(7);
            PumpUntil(() => connected.Count == 1);
            Report(connected[0].Handle, Joycon2Buttons.None);
            PumpUntil(() => connected[0].Session.HasReport);

            System.Threading.Thread.Sleep(700); // the app was suspended: no pumps, no reports
            InputSystem.Update();
            Assert.AreEqual(0, disconnected.Count, "a fresh silence window starts after the gap");
            Assert.AreEqual(1, Joycon2Manager.Controllers.Count);
        }

        [Test]
        public void StopRemovesGamepadsEvenIfAHandlerRestarts()
        {
            Joycon2Manager.Start();
            Advertise(7);
            PumpUntil(() => connected.Count == 1);
            var device = connected[0].Device;
            var first = transport;
            Joycon2Manager.ControllerDisconnected += Restart;
            try
            {
                Joycon2Manager.Stop();
                Assert.IsTrue(first.ShutDown);
                Assert.IsFalse(device.added);
                Assert.IsTrue(Joycon2Manager.IsRunning, "the handler's restart stands");
                Assert.AreSame(transport, Joycon2Manager.Transport);
                Assert.AreNotSame(first, transport);
                InputSystem.Update(); // the restarted manager pumps without errors
            }
            finally { Joycon2Manager.ControllerDisconnected -= Restart; }

            void Restart(Joycon2Controller c, string reason)
            {
                transport = new FakeTransport();
                Joycon2Manager.Start();
            }
        }

        [Test]
        public void HandlerDisconnectingAnotherControllerDuringThePumpIsSafe()
        {
            var settings = Joycon2Manager.Settings;
            settings.MaxControllers = 2;
            Joycon2Manager.Start();
            Advertise(7);
            Advertise(8);
            PumpUntil(() => connected.Count == 2);
            Joycon2Manager.ControllerDisconnected += DropTheOther;
            try
            {
                // Both go silent: dropping the first disconnects the second from inside the handler.
                Report(connected[0].Handle, Joycon2Buttons.None);
                Report(connected[1].Handle, Joycon2Buttons.None);
                PumpUntil(() => Joycon2Manager.Controllers.Count == 0, 2);
            }
            finally { Joycon2Manager.ControllerDisconnected -= DropTheOther; }

            void DropTheOther(Joycon2Controller c, string reason)
            {
                foreach (var other in new List<Joycon2Controller>(Joycon2Manager.Controllers)) Joycon2Manager.Disconnect(other);
            }
        }
    }
}
