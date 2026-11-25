using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Proxy;

namespace ReDefinition.Tests
{
    // The frame packet as the proxy lays it out (FrameGeneration.h, FramePacket,
    // whose static_asserts name the same sizes): a mismatch turns frame generation
    // off in the game, and is cheaper found here.
    [TestClass]
    public class FramePacketLayoutTests
    {
        // FramePacket's size and the offsets of its four matrices match FramePacket in FrameGeneration.h: the size and the first and last matrix as the
        // header's static_asserts give them, the two between as the header declares them, four consecutive inline matrices of 16 floats.
        [TestMethod]
        public void ThePacketIsLaidOutAsTheProxyReadsIt()
        {
            var header = NativeLayoutAsserts.Read("src", "DxgiProxy", "FrameGeneration", "FrameGeneration.h");
            System.Type packet = typeof(FrameGenerationBridge.FramePacket);
            const int matrix = 16 * sizeof(float);
            int viewToClip = Marshal.OffsetOf(packet, "ViewToClip").ToInt32();
            Assert.AreEqual(NativeLayoutAsserts.Of(header, "FramePacket"), Marshal.SizeOf(packet));
            Assert.AreEqual(NativeLayoutAsserts.Of(header, "FramePacket.viewToClip"), viewToClip);
            Assert.AreEqual(viewToClip + matrix, Marshal.OffsetOf(packet, "ClipToView").ToInt32());
            Assert.AreEqual(viewToClip + 2 * matrix, Marshal.OffsetOf(packet, "ClipToPrevClip").ToInt32());
            Assert.AreEqual(NativeLayoutAsserts.Of(header, "FramePacket.prevClipToClip"), Marshal.OffsetOf(packet, "PrevClipToClip").ToInt32());
            Assert.AreEqual(viewToClip + 3 * matrix, Marshal.OffsetOf(packet, "PrevClipToClip").ToInt32());
        }

        // The matrix arrays are marshalled inline: written to memory, a matrix's sixteen floats stand at its
        // offset, where the proxy reads them, not a pointer to them.
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
