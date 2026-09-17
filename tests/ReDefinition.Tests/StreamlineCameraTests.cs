using Microsoft.VisualStudio.TestTools.UnitTesting;
using UnityEngine;

namespace ReDefinition.Tests
{
    // The camera matrices DLSS frame generation gets (StreamlineCamera): Unity's
    // column-vector matrices as Streamline's row-major row-vector ones, a view
    // space looking down +z, and the reprojection from this frame's clip space
    // into the last one's.
    [TestClass]
    public class StreamlineCameraTests
    {
        private const float Near = 0.3f;
        private const float Far = 750000f;

        // What GL.GetGPUProjectionMatrix gives on Direct3D for a perspective
        // camera, which is native code: view space down -z, clip depth reversed,
        // 1 at the near plane and 0 at the far one.
        private static Matrix4x4 GpuProjection(float verticalFovDegrees, float aspect)
        {
            float yScale = 1f / Mathf.Tan(verticalFovDegrees * Mathf.Deg2Rad * 0.5f);
            Matrix4x4 m = new Matrix4x4();
            m.m00 = yScale / aspect;
            m.m11 = yScale;
            m.m22 = Near / (Far - Near);
            m.m23 = Near * Far / (Far - Near);
            m.m32 = -1f;
            return m;
        }

        // A camera at a position looking along world +z, as worldToCameraMatrix
        // has it: the world moved to the camera, then z turned round.
        private static Matrix4x4 View(Vector3 position)
        {
            Matrix4x4 m = Matrix4x4.identity;
            m.m03 = -position.x;
            m.m13 = -position.y;
            m.m23 = -position.z;
            return StreamlineCamera.FlipZ() * m;
        }

        // A row vector times a row-major matrix, as Streamline multiplies.
        private static Vector4 RowTimes(Vector4 v, float[] m)
        {
            return new Vector4(
                v.x * m[0] + v.y * m[4] + v.z * m[8] + v.w * m[12],
                v.x * m[1] + v.y * m[5] + v.z * m[9] + v.w * m[13],
                v.x * m[2] + v.y * m[6] + v.z * m[10] + v.w * m[14],
                v.x * m[3] + v.y * m[7] + v.z * m[11] + v.w * m[15]);
        }

        private static Vector4 ColumnTimes(Matrix4x4 m, Vector4 v)
        {
            return new Vector4(
                m.m00 * v.x + m.m01 * v.y + m.m02 * v.z + m.m03 * v.w,
                m.m10 * v.x + m.m11 * v.y + m.m12 * v.z + m.m13 * v.w,
                m.m20 * v.x + m.m21 * v.y + m.m22 * v.z + m.m23 * v.w,
                m.m30 * v.x + m.m31 * v.y + m.m32 * v.z + m.m33 * v.w);
        }

        private static void AssertClose(Vector3 expected, Vector3 actual, float tolerance, string what)
        {
            Assert.IsTrue((expected - actual).magnitude <= tolerance, what + ": expected " + expected.ToString("F6")
                                                                     + ", got " + actual.ToString("F6"));
        }

        private static Vector3 Divide(Vector4 clip)
        {
            return new Vector3(clip.x / clip.w, clip.y / clip.w, clip.z / clip.w);
        }

        [TestMethod]
        public void TheInverseUndoesTheMatrix()
        {
            Matrix4x4 m = GpuProjection(60f, 16f / 9f) * View(new Vector3(3f, -2f, 7f));
            Matrix4x4 product = StreamlineCamera.Invert(m) * m;
            for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++)
                    Assert.AreEqual(row == column ? 1f : 0f, product[row, column], 1e-4f, "[" + row + "," + column + "]");
        }

        [TestMethod]
        public void RowMajorIsTheSameTransformForRowVectors()
        {
            Matrix4x4 m = GpuProjection(50f, 1.5f) * View(new Vector3(1f, 2f, 3f));
            float[] rowMajor = new float[16];
            StreamlineCamera.WriteRowMajor(m, rowMajor);
            Vector4 point = new Vector4(4f, -1f, 20f, 1f);
            Vector4 byUnity = ColumnTimes(m, point);
            Vector4 byStreamline = RowTimes(point, rowMajor);
            Assert.AreEqual(byUnity.x, byStreamline.x, 1e-3f);
            Assert.AreEqual(byUnity.y, byStreamline.y, 1e-3f);
            Assert.AreEqual(byUnity.z, byStreamline.z, 1e-3f);
            Assert.AreEqual(byUnity.w, byStreamline.w, 1e-3f);
        }

        [TestMethod]
        public void ViewSpaceLooksDownPlusZWithDepthReversed()
        {
            float[] viewToClip = new float[16], clipToView = new float[16], toPrevious = new float[16],
                fromPrevious = new float[16];
            Matrix4x4 projection = GpuProjection(60f, 16f / 9f);
            Matrix4x4 viewProjection = projection * View(Vector3.zero);
            StreamlineCamera.Fill(projection, viewProjection, viewProjection, viewToClip, clipToView, toPrevious,
                                  fromPrevious);

            // In front of the camera, as Streamline's view space has it: +z.
            Vector4 near = RowTimes(new Vector4(0f, 0f, 1f, 1f), viewToClip);
            Vector4 far = RowTimes(new Vector4(0f, 0f, 1000f, 1f), viewToClip);
            Assert.IsTrue(near.w > 0f && far.w > 0f, "points in front have positive w");
            Assert.IsTrue(near.z / near.w > far.z / far.w, "the nearer point has the larger depth");
            Assert.AreEqual(1f, RowTimes(new Vector4(0f, 0f, Near, 1f), viewToClip).z
                                / RowTimes(new Vector4(0f, 0f, Near, 1f), viewToClip).w, 1e-4f, "depth 1 at the near plane");

            // Up is up: a point above the axis lands in the upper half of clip space.
            Assert.IsTrue(RowTimes(new Vector4(0f, 1f, 10f, 1f), viewToClip).y > 0f);

            // And clipToView takes it back.
            Vector4 back = RowTimes(RowTimes(new Vector4(2f, -3f, 40f, 1f), viewToClip), clipToView);
            AssertClose(new Vector3(2f, -3f, 40f), new Vector3(back.x / back.w, back.y / back.w, back.z / back.w), 1e-2f,
                        "clipToView");
        }

        [TestMethod]
        public void ClipToPrevClipReprojectsIntoTheLastFrame()
        {
            Matrix4x4 projection = GpuProjection(60f, 16f / 9f);
            Matrix4x4 previous = projection * View(new Vector3(0f, 0f, 0f));
            Matrix4x4 current = projection * View(new Vector3(0.5f, 0.2f, 1.5f));
            float[] viewToClip = new float[16], clipToView = new float[16], toPrevious = new float[16],
                fromPrevious = new float[16];
            StreamlineCamera.Fill(projection, current, previous, viewToClip, clipToView, toPrevious, fromPrevious);

            Vector4 world = new Vector4(3f, 1f, 25f, 1f);
            Vector4 currentClip = ColumnTimes(current, world);
            Vector3 expected = Divide(ColumnTimes(previous, world));

            AssertClose(expected, Divide(RowTimes(currentClip, toPrevious)), 1e-4f, "clipToPrevClip");
            AssertClose(Divide(currentClip), Divide(RowTimes(ColumnTimes(previous, world), fromPrevious)), 1e-4f,
                        "prevClipToClip");
        }

        [TestMethod]
        public void AStillCameraHasNoReprojection()
        {
            Matrix4x4 projection = GpuProjection(70f, 1.25f);
            Matrix4x4 viewProjection = projection * View(new Vector3(10f, 20f, 30f));
            float[] viewToClip = new float[16], clipToView = new float[16], toPrevious = new float[16],
                fromPrevious = new float[16];
            StreamlineCamera.Fill(projection, viewProjection, viewProjection, viewToClip, clipToView, toPrevious,
                                  fromPrevious);
            for (int i = 0; i < 16; i++)
            {
                Assert.AreEqual(i % 5 == 0 ? 1f : 0f, toPrevious[i], 1e-4f, "clipToPrevClip[" + i + "]");
                Assert.AreEqual(i % 5 == 0 ? 1f : 0f, fromPrevious[i], 1e-4f, "prevClipToClip[" + i + "]");
            }
        }
    }
}
