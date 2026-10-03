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
            Assert.AreEqual(752, Marshal.SizeOf(packet));
            Assert.AreEqual(32, Marshal.OffsetOf(packet, "ClipToWorld").ToInt32());
            Assert.AreEqual(304, Marshal.OffsetOf(packet, "BodySphere").ToInt32());
            Assert.AreEqual(320, Marshal.OffsetOf(packet, "Ambient").ToInt32());
            Assert.AreEqual(368, Marshal.OffsetOf(packet, "WorldToView").ToInt32());
            Assert.AreEqual(528, Marshal.OffsetOf(packet, "Lights").ToInt32());
            Assert.AreEqual(592, Marshal.OffsetOf(packet, "LightParams").ToInt32());
            Assert.AreEqual(608, Marshal.OffsetOf(packet, "NightLight").ToInt32());
            Assert.AreEqual(624, Marshal.OffsetOf(packet, "Inputs").ToInt32());
        }

        // RtReconstructPacket's static_asserts: 640 bytes, the matrices from 48, the
        // textures from 576.
        [TestMethod]
        public void TheReconstructionPacketIsLaidOutAsTheProxyReadsIt()
        {
            System.Type packet = typeof(RtNative.ReconstructPacket);
            Assert.AreEqual(640, Marshal.SizeOf(packet));
            Assert.AreEqual(48, Marshal.OffsetOf(packet, "ClipToWorld").ToInt32());
            Assert.AreEqual(576, Marshal.OffsetOf(packet, "Colour").ToInt32());
            Assert.AreEqual(632, Marshal.OffsetOf(packet, "Output").ToInt32());
        }
    }
}
