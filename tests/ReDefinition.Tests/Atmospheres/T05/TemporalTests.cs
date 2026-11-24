using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // The CPU parts of the temporal reconstruction (package T05): the sampling order and the reprojection maths that the shader repeats.
    [TestClass]
    public sealed class TemporalTests
    {
        private static ReconCamera Camera(double px, double py, double pz, double yawDeg, double fovDeg = 60.0, double aspect = 16.0 / 9.0)
        {
            // Looking along +x of the planet frame rotated about +z by yaw, up is +z.
            double a = yawDeg * Math.PI / 180.0;
            double t = Math.Tan(fovDeg * 0.5 * Math.PI / 180.0);
            return new ReconCamera
            {
                Px = px, Py = py, Pz = pz,
                Fx = Math.Cos(a), Fy = Math.Sin(a), Fz = 0,
                Rx = Math.Sin(a), Ry = -Math.Cos(a), Rz = 0,
                Ux = 0, Uy = 0, Uz = 1,
                TanX = t * aspect, TanY = t
            };
        }

        [TestMethod]
        public void BlockOffsetVisitsEveryCellOncePerCycle()
        {
            for (int n = 2; n <= 4; n++)
            {
                var seen = new HashSet<int>();
                for (int i = 0; i < n * n; i++)
                {
                    int x, y;
                    ReconPattern.BlockOffset(n, i, out x, out y);
                    Assert.IsTrue(x >= 0 && x < n && y >= 0 && y < n, "cell inside the block");
                    seen.Add(y * n + x);
                }
                Assert.AreEqual(n * n, seen.Count, "block " + n);
                // The next cycle repeats the first.
                int x0, y0, x1, y1;
                ReconPattern.BlockOffset(n, 0, out x0, out y0);
                ReconPattern.BlockOffset(n, n * n, out x1, out y1);
                Assert.AreEqual(x0, x1);
                Assert.AreEqual(y0, y1);
            }
        }

        [TestMethod]
        public void HaltonStaysInTheUnitInterval()
        {
            for (int i = 1; i < 64; i++)
            {
                float h2 = ReconPattern.Halton(i, 2), h3 = ReconPattern.Halton(i, 3);
                Assert.IsTrue(h2 > 0f && h2 < 1f && h3 > 0f && h3 < 1f);
            }
        }

        [TestMethod]
        public void AStillCameraAndNoWindProjectBackOntoTheSamePixel()
        {
            var cam = Camera(0, 0, 6371000.0 + 2.0, 0);
            foreach (double u in new[] { 0.1, 0.5, 0.9 })
                foreach (double v in new[] { 0.55, 0.7, 0.95 })
                {
                    double pu, pv;
                    Assert.IsTrue(ReconReprojection.PreviousUv(cam, cam, u, v, 0, 0, 0, 6371000.0 + 2000.0, 50000.0, out pu, out pv));
                    Assert.AreEqual(u, pu, 1e-9);
                    Assert.AreEqual(v, pv, 1e-9);
                }
        }

        [TestMethod]
        public void ATurnMovesTheHistoryByTheTurnAtTheCentre()
        {
            // The camera turned by 2 degrees to the left (yaw +2) since the last frame: what is in the middle now was 2 degrees to the right of the
            // old centre, so the previous uv lies right of 0.5 by tan(2 deg) / tanX / 2.
            double r = 6371000.0;
            var prev = Camera(0, 0, r + 2.0, 0);
            var cur = Camera(0, 0, r + 2.0, 2.0);
            double pu, pv;
            Assert.IsTrue(ReconReprojection.PreviousUv(cur, prev, 0.5, 0.5, 0, 0, 0, r + 2000.0, 50000.0, out pu, out pv));
            double expected = Math.Tan(2.0 * Math.PI / 180.0) / prev.TanX * 0.5 + 0.5;
            // The right axis of the camera points to -y for a yaw of 0, so a yaw of +2 degrees turns toward +y: the old picture shows that
            // direction on its left; the sign follows the basis, the size follows the tangent.
            Assert.AreEqual(0.5, pv, 1e-6);
            Assert.AreEqual(Math.Abs(expected - 0.5), Math.Abs(pu - 0.5), 1e-4);
            Assert.IsTrue(pu < 0.5, "toward +y is the old picture's left");
        }

        [TestMethod]
        public void TheWindCarriesTheHistoryWithTheCloud()
        {
            // A still camera, the cloud has moved by +100 m along +y since the last frame: the cloud now at a pixel was 100 m toward -y, which
            // the old picture shows to its right (the right axis points to -y).
            double r = 6371000.0;
            var cam = Camera(0, 0, r + 2.0, 0);
            double pu, pv;
            Assert.IsTrue(ReconReprojection.PreviousUv(cam, cam, 0.5, 0.7, 0, 100.0, 0, r + 2000.0, 50000.0, out pu, out pv));
            Assert.IsTrue(pu > 0.5, "the old place lies to the right");
            double still;
            ReconReprojection.PreviousUv(cam, cam, 0.5, 0.7, 0, 0, 0, r + 2000.0, 50000.0, out still, out pv);
            Assert.AreEqual(0.5, still, 1e-9);
        }

        // An OpenGL-style perspective projection, as Camera.projectionMatrix has it (EveCloudMotionTests).
        private static UnityEngine.Matrix4x4 Projection(float verticalFovDegrees, float aspect, float near, float far)
        {
            float yScale = 1f / (float)Math.Tan(verticalFovDegrees * Math.PI / 180.0 * 0.5);
            var m = new UnityEngine.Matrix4x4();
            m.m00 = yScale / aspect;
            m.m11 = yScale;
            m.m22 = -(far + near) / (far - near);
            m.m23 = -2f * far * near / (far - near);
            m.m32 = -1f;
            return m;
        }

        // The place in render pixels (x right, y up from the lower left corner) where the projection draws a view-space point.
        private static UnityEngine.Vector2 PixelOf(UnityEngine.Matrix4x4 projection, UnityEngine.Vector3 view, int w, int h)
        {
            UnityEngine.Vector4 clip = projection * new UnityEngine.Vector4(view.x, view.y, view.z, 1f);
            return new UnityEngine.Vector2((clip.x / clip.w + 1f) * 0.5f * w, (clip.y / clip.w + 1f) * 0.5f * h);
        }

        // The ray of the layer shader's RayDir in view space (forward -z, right +x, up +y; right and up scaled by the tangents) through the pixel's centre, with the
        // offset LayerRenderer.SetCamera puts into _CamRight.w and _CamUp.w, at distance t along the forward axis.
        private static UnityEngine.Vector3 ShaderRay(int px, int py, int w, int h, float tanX, float tanY, UnityEngine.Vector2 offset, float t)
        {
            float u = (px + 0.5f) / w, v = (py + 0.5f) / h;
            float nx = u * 2f - 1f, ny = v * 2f - 1f;
            return new UnityEngine.Vector3((nx + offset.x) * tanX * t, (ny + offset.y) * tanY * t, -t);
        }

        // recon.hostJitter: the scene and its depth render with the projection CameraRedirect.Jittered makes from the published jitter (UpscalerIntegration.ChooseJitter: the
        // upscaler's sequence at the frame count, 2 j / size in ndc). The raster samples each pixel at its centre, so the depth texel of a pixel holds what lies along the
        // ray the jittered projection draws at that centre. The march's ray through the pixel's centre with RayJitter's offset must be that ray: the jittered projection
        // draws it at the pixel's centre. Without the offset it lands j pixels away (the defect: the cut against the depth is that far off).
        [TestMethod]
        public void TheJitteredRayThroughAPixelCentreHitsTheDepthTexelRenderedForIt()
        {
            const int w = 1707, h = 960, display = 2560;
            const float fov = 60f;
            float aspect = (float)w / h;
            float tanY = (float)Math.Tan(fov * 0.5 * Math.PI / 180.0), tanX = tanY * aspect;
            UnityEngine.Matrix4x4 unjittered = Projection(fov, aspect, 0.21f, 750000f);
            int phases = FidelityFX.FSR3.Fsr3Upscaler.GetJitterPhaseCount(w, display);
            UnityEngine.Vector2 saved = ReDefinition.Upscaler.CameraRedirect.JitterNdc;
            try
            {
                for (int frame = 0; frame < phases; frame++)
                {
                    float jx, jy;
                    FidelityFX.FSR3.Fsr3Upscaler.GetJitterOffset(out jx, out jy, frame, phases);
                    var host = new UnityEngine.Vector2(2f * jx / w, 2f * jy / h);
                    ReDefinition.Upscaler.CameraRedirect.JitterNdc = host;
                    UnityEngine.Matrix4x4 jittered = ReDefinition.Upscaler.CameraRedirect.Jittered(unjittered);
                    UnityEngine.Vector2 offset = ReconSettings.RayJitter(host);
                    foreach (int[] p in new[] { new[] { 0, 0 }, new[] { w - 1, h - 1 }, new[] { 853, 479 }, new[] { 120, 777 }, new[] { 1600, 30 } })
                    {
                        foreach (float t in new[] { 3f, 2500f, 120000f })
                        {
                            UnityEngine.Vector2 at = PixelOf(jittered, ShaderRay(p[0], p[1], w, h, tanX, tanY, offset, t), w, h);
                            Assert.AreEqual(p[0] + 0.5f, at.x, 2e-3f, "x: frame " + frame + " pixel " + p[0] + "," + p[1] + " at " + t + " m");
                            Assert.AreEqual(p[1] + 0.5f, at.y, 2e-3f, "y: frame " + frame + " pixel " + p[0] + "," + p[1] + " at " + t + " m");
                            Assert.AreEqual(p[0], (int)Math.Floor(at.x), "the depth texel's column");
                            Assert.AreEqual(p[1], (int)Math.Floor(at.y), "the depth texel's row");
                            UnityEngine.Vector2 plain = PixelOf(jittered, ShaderRay(p[0], p[1], w, h, tanX, tanY, UnityEngine.Vector2.zero, t), w, h);
                            Assert.AreEqual(p[0] + 0.5f + jx, plain.x, 2e-3f, "the unjittered ray lands the jitter's x away");
                            Assert.AreEqual(p[1] + 0.5f + jy, plain.y, 2e-3f, "the unjittered ray lands the jitter's y away");
                        }
                    }
                }
            }
            finally
            {
                ReDefinition.Upscaler.CameraRedirect.JitterNdc = saved;
            }
        }

        // Native rendering (no jitter published): the offset is +0 in both axes, the same bits SetCamera got before the switch, so the march's uniforms are unchanged.
        [TestMethod]
        public void WithoutAJitterTheRayOffsetIsPositiveZero()
        {
            UnityEngine.Vector2 o = ReconSettings.RayJitter(UnityEngine.Vector2.zero);
            Assert.AreEqual(0, BitConverter.ToInt32(BitConverter.GetBytes(o.x), 0), "x is +0");
            Assert.AreEqual(0, BitConverter.ToInt32(BitConverter.GetBytes(o.y), 0), "y is +0");
            UnityEngine.Vector2 j = ReconSettings.RayJitter(new UnityEngine.Vector2(1e-3f, -2e-4f));
            Assert.AreEqual(-1e-3f, j.x);
            Assert.AreEqual(2e-4f, j.y);
        }

        // The switch is off by default, a probe camera never takes the host's jitter, and the per-frame jitter is not a setting pass.json carries (the switch is).
        [TestMethod]
        public void TheHostJitterSwitchIsOffByDefaultAndTheProbeDoesNotTakeIt()
        {
            Assert.IsFalse(OwnCloudOptions.HostJitterDefault);
            Assert.IsFalse(new ReconSettings().HostJitter);
            Assert.IsFalse(ReconSettings.ForProbe().HostJitter);
            var s = new ReconSettings { HostJitter = true, HostJitterNdc = new UnityEngine.Vector2(0.001f, 0.002f) };
            ReconSettings c = s.Clone();
            Assert.IsTrue(c.HostJitter);
            Assert.AreEqual(0.002f, c.HostJitterNdc.y, "Configure's clone keeps the frame's jitter");
            var paths = new HashSet<string>();
            foreach (LayerPassSetting p in LayerPassSettings.Collect(null, s, null).Settings) paths.Add(p.path);
            Assert.IsTrue(paths.Contains("Recon.HostJitter"));
            foreach (string p in paths) Assert.IsFalse(p.StartsWith("Recon.HostJitterNdc", StringComparison.Ordinal), p);
        }

        [TestMethod]
        public void TheClipKeepsTheHistoryInsideTheNeighbourhood()
        {
            Assert.AreEqual(0.6, ReconReprojection.Clip(0.9, 0.5, 0.1, 1.0), 1e-12);
            Assert.AreEqual(0.4, ReconReprojection.Clip(0.0, 0.5, 0.1, 1.0), 1e-12);
            Assert.AreEqual(0.55, ReconReprojection.Clip(0.55, 0.5, 0.1, 1.0), 1e-12);
        }
    }
}
