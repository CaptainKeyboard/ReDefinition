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

        [TestMethod]
        public void TheClipKeepsTheHistoryInsideTheNeighbourhood()
        {
            Assert.AreEqual(0.6, ReconReprojection.Clip(0.9, 0.5, 0.1, 1.0), 1e-12);
            Assert.AreEqual(0.4, ReconReprojection.Clip(0.0, 0.5, 0.1, 1.0), 1e-12);
            Assert.AreEqual(0.55, ReconReprojection.Clip(0.55, 0.5, 0.1, 1.0), 1e-12);
        }
    }
}
