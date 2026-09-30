using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Raytracing;

namespace ReDefinition.Tests
{
    // The raytracing frame packet as the proxy lays it out (Raytracing.h, RtFramePacket,
    // whose static_asserts name the same sizes): the proxy refuses a packet of another size.
    [TestClass]
    public class RtFramePacketLayoutTests
    {
        [TestMethod]
        public void ThePacketIsLaidOutAsTheProxyReadsIt()
        {
            System.Type packet = typeof(RtNative.FramePacket);
            Assert.AreEqual(496, Marshal.SizeOf(packet));
            Assert.AreEqual(32, Marshal.OffsetOf(packet, "ClipToWorld").ToInt32());
            Assert.AreEqual(304, Marshal.OffsetOf(packet, "BodySphere").ToInt32());
            Assert.AreEqual(320, Marshal.OffsetOf(packet, "Ambient").ToInt32());
            Assert.AreEqual(368, Marshal.OffsetOf(packet, "Inputs").ToInt32());
        }
    }
}
