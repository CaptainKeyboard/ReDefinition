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
            Assert.AreEqual(848, Marshal.SizeOf(packet));
            Assert.AreEqual(32, Marshal.OffsetOf(packet, "ClipToWorld").ToInt32());
            Assert.AreEqual(304, Marshal.OffsetOf(packet, "BodySphere").ToInt32());
            Assert.AreEqual(320, Marshal.OffsetOf(packet, "Ambient").ToInt32());
            Assert.AreEqual(368, Marshal.OffsetOf(packet, "WorldToView").ToInt32());
            Assert.AreEqual(528, Marshal.OffsetOf(packet, "Lights").ToInt32());
            Assert.AreEqual(592, Marshal.OffsetOf(packet, "LightParams").ToInt32());
            Assert.AreEqual(608, Marshal.OffsetOf(packet, "NightLight").ToInt32());
            Assert.AreEqual(624, Marshal.OffsetOf(packet, "Planet").ToInt32());
            Assert.AreEqual(640, Marshal.OffsetOf(packet, "LightSurface").ToInt32());
            Assert.AreEqual(672, Marshal.OffsetOf(packet, "Sea").ToInt32());
            Assert.AreEqual(720, Marshal.OffsetOf(packet, "Inputs").ToInt32());
        }

        // The ocean's packet is the frame's copied (RtCamera.TraceOcean): every field, the
        // arrays' contents, into the copy's own arrays.
        [TestMethod]
        public void CopyToCopiesEveryField()
        {
            RtNative.FramePacket from = RtNative.NewFramePacket();
            RtNative.FramePacket to = RtNative.NewFramePacket();
            int n = 1;
            object boxed = from;
            foreach (System.Reflection.FieldInfo field in typeof(RtNative.FramePacket).GetFields())
            {
                object value = field.GetValue(boxed);
                if (value is float[] floats) for (int i = 0; i < floats.Length; i++) floats[i] = n++;
                else if (value is System.IntPtr[] pointers) for (int i = 0; i < pointers.Length; i++) pointers[i] = new System.IntPtr(n++);
                else if (value is uint) field.SetValue(boxed, (uint)n++);
                else Assert.Fail("a field CopyTo may not know: " + field.Name);
            }
            from = (RtNative.FramePacket)boxed;
            from.CopyTo(ref to);
            object copied = to;
            foreach (System.Reflection.FieldInfo field in typeof(RtNative.FramePacket).GetFields())
            {
                object a = field.GetValue(boxed), b = field.GetValue(copied);
                if (a is System.Array arrayA)
                {
                    CollectionAssert.AreEqual(arrayA, (System.Array)b, field.Name);
                    Assert.AreNotSame(a, b, field.Name);
                }
                else Assert.AreEqual(a, b, field.Name);
            }
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
