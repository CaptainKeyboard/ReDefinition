using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Upscaler;
using UnityEngine;

namespace ReDefinition.Tests
{
    // The scene camera's move as a clip-space matrix (SceneMotion), against the
    // geometry it has to reproduce. The geometry is KSP's flight camera with its near plane of
    // 0.21 m and far plane of 750 km, looking at ground 150 km away.
    [TestClass]
    public class SceneMotionTests
    {
        // KSP's flight camera: near and far plane in metres, vertical field of view in degrees, a 16:9 screen.
        private const float Near = 0.21f, Far = 750000f, Fov = 60f, Aspect = 16f / 9f;

        // What GL.GetGPUProjectionMatrix(projection, false) gives on Direct3D 11:
        // reversed depth, 1 at the near plane and 0 at the far one, Unity's camera
        // space looking down -z.
        private static Matrix4x4 Projection()
        {
            float f = 1f / Mathf.Tan(Fov * 0.5f * Mathf.Deg2Rad);
            Matrix4x4 m = Matrix4x4.zero;
            m.m00 = f / Aspect;
            m.m11 = f;
            m.m22 = Near / (Far - Near);
            m.m23 = Far * Near / (Far - Near);
            m.m32 = -1f;
            return m;
        }

        // The depth the projection writes for a point `distance` ahead.
        private static float DepthAt(double distance)
        {
            return (float)(Near * Far / (distance * (Far - Near)) - Near / (double)(Far - Near));
        }

        // Unity's motion vector for a pixel at the screen's centre offset by
        // `ndc`: this frame's viewport position less the frame before's.
        private static Vector2 Motion(Matrix4x4 clipToPrevious, Vector2 ndc, float depth)
        {
            Vector4 clip = new Vector4(ndc.x, ndc.y, depth, 1f);
            Vector4 before = clipToPrevious * clip;
            return new Vector2((clip.x / clip.w - before.x / before.w) * 0.5f, (clip.y / clip.w - before.y / before.w) * 0.5f);
        }

        // A turn by `degrees` about a unit axis; Quaternion.AngleAxis is native code.
        private static Quaternion Turn(float degrees, float x, float y, float z)
        {
            float half = degrees * 0.5f * Mathf.Deg2Rad;
            float length = Mathf.Sqrt(x * x + y * y + z * z);
            float s = Mathf.Sin(half) / length;
            return new Quaternion(x * s, y * s, z * s, Mathf.Cos(half));
        }

        // Two frames with the same placement and the same projection give no motion anywhere on the screen.
        [TestMethod]
        public void NoMoveGivesNoMotion()
        {
            Quaternion turn = Turn(35f, 1f, 2f, 3f);
            Vector3d at = new Vector3d(1000.0, 200000.0, -30000.0);
            Matrix4x4 m = SceneMotion.ClipToPreviousClip(Projection(), Projection(), at, turn, at, turn);
            Vector2 motion = Motion(m, new Vector2(0.3f, -0.2f), DepthAt(150000.0));
            Assert.AreEqual(0f, motion.magnitude, 1e-6f);
        }

        // A 10 m step sideways moves the ground 150 km ahead by its parallax, the angle 10 m make at that distance.
        [TestMethod]
        public void TenMetresSidewaysAt150KilometresMoveTheGroundByItsParallax()
        {
            // The camera looks down the body's +z (Unity's camera space looks down
            // its own -z, the placement turns that onto +z), and moved 10 m to its
            // right since the frame before.
            Quaternion turn = Quaternion.identity;
            Vector3d now = new Vector3d(10.0, 0.0, 0.0);
            Matrix4x4 m = SceneMotion.ClipToPreviousClip(Projection(), Projection(), now, turn, Vector3d.zero, turn);
            Vector2 motion = Motion(m, Vector2.zero, DepthAt(150000.0));

            // The point ahead stood 10 m to the right of the camera before: at
            // x = m00 * 10 / 150000 in the frame before's clip space, now at 0.
            float f = 1f / Mathf.Tan(Fov * 0.5f * Mathf.Deg2Rad) / Aspect;
            Assert.AreEqual(-(f * 10f / 150000f) * 0.5f, motion.x, 1e-8f);
            Assert.AreEqual(0f, motion.y, 1e-8f);
        }

        // The same step and turn give the same motion 150 km from the origin as at it, to a ten-millionth: the
        // matrix is built from the difference of the placements, not from two large positions.
        [TestMethod]
        public void TheSameMoveFarFromTheOriginGivesTheSameMotion()
        {
            // What Unity's own vectors lose: here only the difference of the two
            // placements enters.
            Quaternion before = Turn(40f, 0.3f, -1f, 0.2f);
            Quaternion now = before * Turn(0.02f, 1f, 2f, 0f);
            Vector3d step = new Vector3d(4.0, -7.0, 2.5);
            Vector3d here = new Vector3d(0.0, 0.0, 0.0);
            Vector3d far = new Vector3d(-56259.66, 139028.45, 2369.80);

            Matrix4x4 near = SceneMotion.ClipToPreviousClip(Projection(), Projection(), here + step, now, here, before);
            Matrix4x4 distant = SceneMotion.ClipToPreviousClip(Projection(), Projection(), far + step, now, far, before);
            foreach (Vector2 ndc in new[] { Vector2.zero, new Vector2(0.8f, 0.5f), new Vector2(-0.6f, -0.9f) })
            {
                Vector2 a = Motion(near, ndc, DepthAt(150000.0));
                Vector2 b = Motion(distant, ndc, DepthAt(150000.0));
                Assert.AreEqual(a.x, b.x, 1e-7f);
                Assert.AreEqual(a.y, b.y, 1e-7f);
            }
        }

        // A turn alone moves a point at the far plane across the screen by the tangent of its angle, whatever the
        // distance.
        [TestMethod]
        public void ATurnMovesTheFarPlaneByItsAngle()
        {
            // A turn of 0.1 degree about the camera's up axis: a point at the far
            // plane moves across the screen by that angle, nothing of the distance.
            Quaternion before = Quaternion.identity;
            Quaternion now = Turn(0.1f, 0f, 1f, 0f);
            Matrix4x4 m = SceneMotion.ClipToPreviousClip(Projection(), Projection(), Vector3d.zero, now, Vector3d.zero, before);
            Vector2 motion = Motion(m, Vector2.zero, 0f);
            float f = 1f / Mathf.Tan(Fov * 0.5f * Mathf.Deg2Rad) / Aspect;
            Assert.AreEqual(f * Mathf.Tan(0.1f * Mathf.Deg2Rad) * 0.5f, Mathf.Abs(motion.x), 1e-6f);
            Assert.AreEqual(0f, motion.y, 1e-7f);
        }
    }
}
