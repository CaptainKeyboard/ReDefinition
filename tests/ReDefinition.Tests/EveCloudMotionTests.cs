using Microsoft.VisualStudio.TestTools.UnitTesting;
using UnityEngine;

namespace ReDefinition.Tests
{
    // The jitter handed to EVE's clouds (EveCloudMotion): what it does to EVE's
    // motion vectors, which CloudMotionVectors takes out again.
    [TestClass]
    public class EveCloudMotionTests
    {
        // An OpenGL-style perspective projection, as Camera.projectionMatrix has it.
        private static Matrix4x4 Projection(float verticalFovDegrees, float aspect, float near, float far)
        {
            float yScale = 1f / Mathf.Tan(verticalFovDegrees * Mathf.Deg2Rad * 0.5f);
            Matrix4x4 m = new Matrix4x4();
            m.m00 = yScale / aspect;
            m.m11 = yScale;
            m.m22 = -(far + near) / (far - near);
            m.m23 = -2f * far * near / (far - near);
            m.m32 = -1f;
            return m;
        }

        private static Vector2 Ndc(Matrix4x4 projection, Vector3 viewPosition)
        {
            Vector4 clip = projection * new Vector4(viewPosition.x, viewPosition.y, viewPosition.z, 1f);
            return new Vector2(clip.x / clip.w, clip.y / clip.w);
        }

        // EVE's motion vectors are half the change in normalized device coordinates
        // (Scatterer-EVE/RaymarchCloud): with the jitter in both frames' projections
        // a point that stands still gets half the change of the jitter, the value
        // _ReDefinitionCloudJitterDelta takes out.
        [TestMethod]
        public void AStillPointMovesByHalfTheChangeOfTheJitter()
        {
            Matrix4x4 unjittered = Projection(60f, 16f / 9f, 0.21f, 750000f);
            Vector2 previousJitter = new Vector2(0.25f / 960f, -0.4f / 540f);
            Vector2 jitter = new Vector2(-0.3f / 960f, 0.1f / 540f);
            // As CameraRedirect.ApplyJitter makes them.
            Matrix4x4 previous = Matrix4x4.Translate(new Vector3(previousJitter.x, previousJitter.y, 0f)) * unjittered;
            Matrix4x4 current = Matrix4x4.Translate(new Vector3(jitter.x, jitter.y, 0f)) * unjittered;

            foreach (Vector3 point in new[] { new Vector3(10f, 5f, -2000f), new Vector3(-300f, 80f, -40000f) })
            {
                Vector2 motion = 0.5f * (Ndc(current, point) - Ndc(previous, point));
                Vector2 expected = 0.5f * (jitter - previousJitter);
                Assert.AreEqual(expected.x, motion.x, 1e-6f);
                Assert.AreEqual(expected.y, motion.y, 1e-6f);
            }
        }
    }
}
