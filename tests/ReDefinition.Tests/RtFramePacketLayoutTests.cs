using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Proxy;
using ReDefinition.Raytracing;

namespace ReDefinition.Tests
{
    // The raytracing frame packet's copy; its layout is RtContractTests'.
    [TestClass]
    public class RtFramePacketLayoutTests
    {
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
    }
}
