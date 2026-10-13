using System;
using NUnit.Framework;

namespace Joycon2.Tests
{
    public class Joycon2AndroidEventTests
    {
        static sbyte[] Frame(string hex)
        {
            hex = hex.Replace(" ", "");
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            var signed = new sbyte[bytes.Length];
            Buffer.BlockCopy(bytes, 0, signed, 0, bytes.Length);
            return signed;
        }

        [Test]
        public void JniFrame_PreservesSignedBytesMacAddressAndRssi()
        {
            var frame = Frame("07 00 00 00 02 00 00 00 01 00 00 00 00 00 00 00 " +
                "FF EE DD CC BB AA 00 00 D6 FF FF FF 04 00 00 00 00 7F 80 FF");
            var payload = new byte[4];
            Assert.IsTrue(Joycon2AndroidEventCodec.TryDecode(frame, payload, out var e));
            Assert.AreEqual(Joycon2TransportEventType.Notification, e.Type);
            Assert.AreEqual(2, e.Handle);
            Assert.AreEqual(1, e.Tag);
            Assert.AreEqual(0xAABBCCDDEEFFUL, e.Address);
            Assert.AreEqual(-42, e.Rssi);
            CollectionAssert.AreEqual(new byte[] { 0, 127, 128, 255 }, payload);
        }

        [Test]
        public void JniFrame_ShortDestinationKeepsOriginalPayloadLength()
        {
            var frame = Frame("01 00 00 00 00 00 00 00 00 00 00 00 01 00 00 00 " +
                "00 00 00 00 00 00 00 00 00 00 00 00 03 00 00 00 41 42 43");
            var payload = new byte[1];
            Assert.IsTrue(Joycon2AndroidEventCodec.TryDecode(frame, payload, out var e));
            Assert.AreEqual(3, e.Length);
            Assert.AreEqual(65, payload[0]);
        }

        [TestCase("07 00 00 00")]
        [TestCase("07 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 FF FF FF FF")]
        [TestCase("07 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 02 00 00 00 80")]
        [TestCase("63 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00")]
        public void JniFrame_RejectsMalformedHeaderOrPayload(string hex)
        {
            Assert.IsFalse(Joycon2AndroidEventCodec.TryDecode(Frame(hex), new byte[512], out _));
        }
    }
}
