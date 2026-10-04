using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Bridges;

namespace ReDefinition.Tests
{
    // DLSS's packet and size query as the proxy lays them out (Dlss.h, DlssPacket and
    // DlssSizeQuery, whose static_asserts name the same sizes): a mismatch stops DLSS
    // in the game, and is cheaper found here.
    [TestClass]
    public class DlssPacketLayoutTests
    {
        [TestMethod]
        public void ThePacketIsLaidOutAsTheProxyReadsIt()
        {
            System.Type packet = typeof(DlssBridge.DlssPacket);
            Assert.AreEqual(88, Marshal.SizeOf(packet));
            Assert.AreEqual(8, Marshal.OffsetOf(packet, "Colour").ToInt32());
            Assert.AreEqual(40, Marshal.OffsetOf(packet, "RenderWidth").ToInt32());
            Assert.AreEqual(56, Marshal.OffsetOf(packet, "Quality").ToInt32());
            Assert.AreEqual(80, Marshal.OffsetOf(packet, "MotionVectorScaleY").ToInt32());
        }

        [TestMethod]
        public void TheSizeQueryIsLaidOutAsTheProxyReadsIt()
        {
            System.Type query = typeof(DlssBridge).GetNestedType("SizeQuery", BindingFlags.NonPublic);
            Assert.IsNotNull(query);
            Assert.AreEqual(24, Marshal.SizeOf(query));
            Assert.AreEqual(8, Marshal.OffsetOf(query, "Texture").ToInt32());
            Assert.AreEqual(16, Marshal.OffsetOf(query, "OutputWidth").ToInt32());
        }
    }
}
