using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Bridges;

namespace ReDefinition.Tests
{
    // The frame packet as the proxy lays it out (FrameGeneration.h, FramePacket,
    // whose static_asserts name the same sizes): a mismatch turns frame generation
    // off in the game, and is cheaper found here.
    [TestClass]
    public class FramePacketLayoutTests
    {
        [TestMethod]
        public void ThePacketIsLaidOutAsTheProxyReadsIt()
        {
            System.Type packet = typeof(FrameGenerationBridge.FramePacket);
            Assert.AreEqual(360, Marshal.SizeOf(packet));
            Assert.AreEqual(104, Marshal.OffsetOf(packet, "ViewToClip").ToInt32());
            Assert.AreEqual(168, Marshal.OffsetOf(packet, "ClipToView").ToInt32());
            Assert.AreEqual(232, Marshal.OffsetOf(packet, "ClipToPrevClip").ToInt32());
            Assert.AreEqual(296, Marshal.OffsetOf(packet, "PrevClipToClip").ToInt32());
        }

        [TestMethod]
        public void TheMatricesTravelWithThePacket()
        {
            float[] matrix = new float[16];
            for (int i = 0; i < 16; i++) matrix[i] = i + 0.5f;
            FrameGenerationBridge.FramePacket packet = new FrameGenerationBridge.FramePacket
            {
                ViewToClip = matrix,
                ClipToView = new float[16],
                ClipToPrevClip = new float[16],
                PrevClipToClip = matrix,
            };

            System.IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FrameGenerationBridge.FramePacket)));
            try
            {
                Marshal.StructureToPtr(packet, memory, false);
                float[] read = new float[16];
                Marshal.Copy(new System.IntPtr(memory.ToInt64() + 104), read, 0, 16);
                CollectionAssert.AreEqual(matrix, read);
                Marshal.Copy(new System.IntPtr(memory.ToInt64() + 296), read, 0, 16);
                CollectionAssert.AreEqual(matrix, read);
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }
        }
    }
}
