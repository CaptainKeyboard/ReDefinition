using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // The cloud motion of the layer pass (E40 MVCLOUD): the place of a pixel's cloud in the previous frame, which is the history's lookup and the motion vector the upscalers and frame
    // generation read. The CPU twin (LayerMotionModel) against answers that do not use its code: the cloud point found by bisection along the ray, seen by a pinhole camera.
    [TestClass]
    public sealed class LayerMotionTests
    {
        private const double PlanetR = 600000.0;
        private const double Width = 1707.0, Height = 960.0;

        // A camera at the given height over the pole x of a planet centred at the origin, looking at the elevation (degrees above the horizon, towards +y), the horizon's own
        // direction being +y and the radial up +x. 60 degree vertical field, 16:9.
        private static ReconCamera CameraAt(double heightM, double elevationDeg, double shiftY = 0, double shiftZ = 0, double shiftX = 0, double yawDeg = 0)
        {
            double e = elevationDeg * Math.PI / 180.0, yaw = yawDeg * Math.PI / 180.0;
            // Forward in the (up = x, ahead = y, side = z) frame turned by the yaw about the up axis.
            double fx = Math.Sin(e), fy = Math.Cos(e) * Math.Cos(yaw), fz = Math.Cos(e) * Math.Sin(yaw);
            // Right: horizontal and perpendicular to the forward (towards -z at yaw 0).
            double rx = 0, ry = Math.Sin(yaw), rz = -Math.Cos(yaw);
            // Up = right x forward.
            double ux = ry * fz - rz * fy, uy = rz * fx - rx * fz, uz = rx * fy - ry * fx;
            double ty = Math.Tan(30.0 * Math.PI / 180.0);
            return new ReconCamera
            {
                Px = PlanetR + heightM + shiftX, Py = shiftY, Pz = shiftZ,
                Fx = fx, Fy = fy, Fz = fz, Rx = rx, Ry = ry, Rz = rz, Ux = ux, Uy = uy, Uz = uz, TanX = ty * Width / Height, TanY = ty
            };
        }

        private static void Ray(ReconCamera c, double u, double v, out double dx, out double dy, out double dz)
        {
            double nx = u * 2 - 1, ny = v * 2 - 1;
            dx = c.Fx + nx * c.Rx * c.TanX + ny * c.Ux * c.TanY;
            dy = c.Fy + nx * c.Ry * c.TanX + ny * c.Uy * c.TanY;
            dz = c.Fz + nx * c.Rz * c.TanX + ny * c.Uz * c.TanY;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            dx /= len; dy /= len; dz /= len;
        }

        // The answer that does not use the model's code: the point of the ray on the sphere found by bisection (the ray starts inside the sphere, so the distance to the centre
        // grows through the radius once), then the pinhole projection of that point (less the drift) by the previous camera.
        private static void Truth(ReconCamera cur, ReconCamera prev, double u, double v, double shellR, double driftX, double driftY, double driftZ, out double pu, out double pv)
        {
            double dx, dy, dz;
            Ray(cur, u, v, out dx, out dy, out dz);
            double lo = 0, hi = 4.0 * PlanetR;
            for (int i = 0; i < 200; i++)
            {
                double mid = 0.5 * (lo + hi);
                double x = cur.Px + dx * mid, y = cur.Py + dy * mid, z = cur.Pz + dz * mid;
                if (Math.Sqrt(x * x + y * y + z * z) < shellR) lo = mid; else hi = mid;
            }
            double t = 0.5 * (lo + hi);
            double wx = cur.Px + dx * t - driftX - prev.Px, wy = cur.Py + dy * t - driftY - prev.Py, wz = cur.Pz + dz * t - driftZ - prev.Pz;
            double f = wx * prev.Fx + wy * prev.Fy + wz * prev.Fz;
            double sx = wx * prev.Rx + wy * prev.Ry + wz * prev.Rz, sy = wx * prev.Ux + wy * prev.Uy + wz * prev.Uz;
            pu = 0.5 + 0.5 * sx / (f * prev.TanX);
            pv = 0.5 + 0.5 * sy / (f * prev.TanY);
        }

        private static MotionLayer Layer(double altitudeM, double presence, double tau = 1.0, double driftX = 0)
        {
            return new MotionLayer { ShellR = PlanetR + altitudeM, TauVertical = tau, Presence = presence, DriftX = driftX };
        }

        private static double Pixels(double du, double dv)
        {
            return Math.Sqrt(du * du * Width * Width + dv * dv * Height * Height);
        }

        [TestMethod]
        public void ASingleLayerGivesTheExactVectorOfACameraThatTranslates()
        {
            // 60 fps: 300 and 2400 m/s are 5 and 40 m a frame, along each axis of the camera and along the picture's diagonal.
            foreach (double metres in new[] { 5.0, 40.0 })
                foreach (double altitude in new[] { 2000.0, 5000.0, 8000.0 })
                    foreach (double elevation in new[] { 35.0, 60.0, 85.0 })
                        for (int axis = 0; axis < 3; axis++)
                        {
                            ReconCamera cur = CameraAt(50.0, elevation);
                            ReconCamera prev = CameraAt(50.0, elevation, axis == 0 ? -metres : 0, axis == 1 ? metres : 0, axis == 2 ? -metres : 0);
                            for (double u = 0.1; u < 0.95; u += 0.4)
                                for (double v = 0.1; v < 0.95; v += 0.4)
                                {
                                    double pu, pv, depth, tu, tv;
                                    var layers = new[] { Layer(altitude, 1.0) };
                                    Assert.IsTrue(LayerMotionModel.Previous(cur, prev, u, v, layers, 1, PlanetR, out pu, out pv, out depth));
                                    Truth(cur, prev, u, v, PlanetR + altitude, 0, 0, 0, out tu, out tv);
                                    Assert.AreEqual(0.0, Pixels(pu - tu, pv - tv), 1e-3, metres + " m, " + altitude + " m, " + elevation + " deg, axis " + axis + " at " + u + "," + v);
                                    Assert.IsTrue(depth > altitude - 100.0, "the weighted distance is the layer's");
                                }
                        }
        }

        [TestMethod]
        public void TheVectorOfAPixelStraightAboveMatchesTheTableOfTheUpscalerCheck()
        {
            // E40-upscaler-fg-check.md section 3: 5 m between two frames (300 m/s at 60 fps) under a layer 5 km up is 0.8 px at 1707 x 960 and a 60 degree field, 2400 m/s 6.7 px.
            foreach (double[] row in new[] { new[] { 5.0, 0.83 }, new[] { 40.0, 6.65 } })
            {
                ReconCamera cur = CameraAt(0.0, 90.0);
                ReconCamera prev = CameraAt(0.0, 90.0, 0, row[0]);
                double pu, pv, depth;
                Assert.IsTrue(LayerMotionModel.Previous(cur, prev, 0.5, 0.5, new[] { Layer(5000.0, 1.0) }, 1, PlanetR, out pu, out pv, out depth));
                Assert.AreEqual(row[1], Pixels(0.5 - pu, 0.5 - pv), 0.03, row[0] + " m");
            }
        }

        [TestMethod]
        public void ACameraThatTurnsGivesTheSameVectorOnEveryLayer()
        {
            // A turn about the camera's own position moves every point alike: depth does not matter. Level view, along the row through the picture's middle: the previous camera
            // has turned by one degree about the vertical, so the ray at the angle a (tan a = the horizontal tangent times the pixel's ndc) was at tan(a + 1 degree) before.
            double turn = 1.0 * Math.PI / 180.0;
            ReconCamera cur = CameraAt(50.0, 0.0);
            ReconCamera prev = CameraAt(50.0, 0.0, yawDeg: 1.0);
            foreach (double u in new[] { 0.2, 0.5, 0.8 })
            {
                double ndc = u * 2 - 1, a = Math.Atan(ndc * cur.TanX);
                double expected = 0.5 + 0.5 * Math.Tan(a + turn) / cur.TanX;
                foreach (double altitude in new[] { 2000.0, 8000.0 })
                {
                    double pu, pv, depth;
                    Assert.IsTrue(LayerMotionModel.Previous(cur, prev, u, 0.5, new[] { Layer(altitude, 1.0) }, 1, PlanetR, out pu, out pv, out depth));
                    Assert.AreEqual(0.0, (pu - expected) * Width, 1e-3, "the turn's vector at u " + u + " on a layer at " + altitude + " m");
                    Assert.AreEqual(0.5, pv, 1e-6, "a turn about the vertical keeps the row");
                }
            }
        }

        [TestMethod]
        public void ACirrusPixelTakenOnTheFirstLayersSphereIsWrongByPixelsAndTheLayerModelIsNot()
        {
            // The old reprojection took every pixel on the first layer's sphere (cumulus at 2 km); at a pixel of the cirrus layer at 8 km the history was read from the wrong place,
            // and a vector built the same way would have been off by this much.
            foreach (double[] row in new[] { new[] { 5.0, 0.3 }, new[] { 40.0, 2.5 } })
            {
                ReconCamera cur = CameraAt(0.0, 35.0);
                ReconCamera prev = CameraAt(0.0, 35.0, 0, row[0]);
                double pu, pv, depth, ou, ov, tu, tv;
                var layers = new[] { Layer(2000.0, 0.0), Layer(8000.0, 1.0) };
                Assert.IsTrue(LayerMotionModel.Previous(cur, prev, 0.5, 0.6, layers, 2, PlanetR, out pu, out pv, out depth));
                Truth(cur, prev, 0.5, 0.6, PlanetR + 8000.0, 0, 0, 0, out tu, out tv);
                Assert.AreEqual(0.0, Pixels(pu - tu, pv - tv), 1e-3, "the cirrus's own vector");
                Assert.IsTrue(ReconReprojection.PreviousUv(cur, prev, 0.5, 0.6, 0, 0, 0, PlanetR + 2000.0, 50000.0, out ou, out ov));
                Assert.IsTrue(Pixels(ou - tu, ov - tv) > row[1], row[0] + " m: the first layer's sphere is off by " + Pixels(ou - tu, ov - tv) + " px");
            }
        }

        [TestMethod]
        public void ALayerInFrontHidesTheOnesBehindItInProportionToItsOpacity()
        {
            ReconCamera cur = CameraAt(0.0, 60.0);
            ReconCamera prev = CameraAt(0.0, 60.0, 0, 5.0);
            double nu, nv, fu, fv, depth;
            Assert.IsTrue(LayerMotionModel.Previous(cur, prev, 0.5, 0.5, new[] { Layer(2000.0, 1.0) }, 1, PlanetR, out nu, out nv, out depth));
            Assert.IsTrue(LayerMotionModel.Previous(cur, prev, 0.5, 0.5, new[] { Layer(8000.0, 1.0) }, 1, PlanetR, out fu, out fv, out depth));
            Assert.IsTrue(Pixels(nu - fu, nv - fv) > 0.3, "the two layers have different vectors");

            double u, v;
            // Thick near layer: only it counts.
            Assert.IsTrue(LayerMotionModel.Previous(cur, prev, 0.5, 0.5, new[] { Layer(2000.0, 1.0, 40.0), Layer(8000.0, 1.0) }, 2, PlanetR, out u, out v, out depth));
            Assert.AreEqual(0.0, Pixels(u - nu, v - nv), 1e-3, "an opaque layer in front hides the one behind");
            // Clear near layer: the two count alike.
            Assert.IsTrue(LayerMotionModel.Previous(cur, prev, 0.5, 0.5, new[] { Layer(2000.0, 1.0, 0.0), Layer(8000.0, 1.0) }, 2, PlanetR, out u, out v, out depth));
            Assert.AreEqual(0.0, Pixels(u - 0.5 * (nu + fu), v - 0.5 * (nv + fv)), 1e-3, "a layer with no optical depth hides nothing");
            // A layer that is only half there lets the far one through by the rest: weights 0.5 and 0.5 x (1 - 0.5 x opacity).
            double cosine = Math.Abs(Ray1(cur, 0.5, 0.5)) ;
            double opacity = LayerMotionModel.Opacity(1.0, cosine);
            Assert.IsTrue(LayerMotionModel.Previous(cur, prev, 0.5, 0.5, new[] { Layer(2000.0, 0.5, 1.0), Layer(8000.0, 1.0, 1.0) }, 2, PlanetR, out u, out v, out depth));
            double wn = 0.5, wf = 1.0 - 0.5 * opacity;
            Assert.AreEqual(0.0, Pixels(u - (wn * nu + wf * fu) / (wn + wf), v - (wn * nv + wf * fv) / (wn + wf)), 1e-2, "the weights follow the share and the opacity of the layer in front");
        }

        // The cosine of the ray to the radial direction at the near layer's hit, as the model takes it (the radial there is close to the camera's up for a shell this size).
        private static double Ray1(ReconCamera c, double u, double v)
        {
            double dx, dy, dz;
            Ray(c, u, v, out dx, out dy, out dz);
            return dx;
        }

        [TestMethod]
        public void NoCloudInThePixelLeavesItToTheFirstLayersSphere()
        {
            ReconCamera cur = CameraAt(0.0, 60.0), prev = CameraAt(0.0, 60.0, 0, 5.0);
            double u, v, depth;
            Assert.IsFalse(LayerMotionModel.Previous(cur, prev, 0.5, 0.5, new[] { Layer(2000.0, 0.0), Layer(8000.0, 0.0) }, 2, PlanetR, out u, out v, out depth));
            Assert.IsFalse(LayerMotionModel.Previous(cur, prev, 0.5, 0.5, new[] { Layer(2000.0, 1.0) }, 0, PlanetR, out u, out v, out depth), "no layers");
        }

        [TestMethod]
        public void ALayerBehindTheGroundIsLeftOut()
        {
            // Looking 30 degrees below the horizon from 100 m: the ray meets the planet long before it leaves the sphere of a layer on the other side.
            ReconCamera cur = CameraAt(100.0, -30.0), prev = CameraAt(100.0, -30.0, -5.0);
            double u, v, depth;
            Assert.IsFalse(LayerMotionModel.Previous(cur, prev, 0.5, 0.5, new[] { Layer(2000.0, 1.0) }, 1, PlanetR, out u, out v, out depth));
        }

        [TestMethod]
        public void TheLayersDriftMovesThePlaceItIsSeenAt()
        {
            ReconCamera cur = CameraAt(50.0, 45.0), prev = CameraAt(50.0, 45.0);
            double pu, pv, depth, tu, tv, su, sv;
            Assert.IsTrue(LayerMotionModel.Previous(cur, prev, 0.5, 0.5, new[] { Layer(3000.0, 1.0, 1.0, 0.0) }, 1, PlanetR, out su, out sv, out depth));
            Assert.AreEqual(0.5, su, 1e-9, "a still camera and a still cloud: the pixel stays");
            Assert.IsTrue(LayerMotionModel.Previous(cur, prev, 0.5, 0.5, new[] { Layer(3000.0, 1.0, 1.0, 2.0) }, 1, PlanetR, out pu, out pv, out depth));
            Truth(cur, prev, 0.5, 0.5, PlanetR + 3000.0, 2.0, 0, 0, out tu, out tv);
            Assert.AreEqual(0.0, Pixels(pu - tu, pv - tv), 1e-3);
            Assert.IsTrue(Pixels(pu - su, pv - sv) > 0.05, "2 m of wind drift under a layer 3 km up is a fraction of a pixel");
        }

        [TestMethod]
        public void ThePresenceFollowsTheCoverageThresholdWithItsSoftEdge()
        {
            Assert.AreEqual(1.0, LayerMotionModel.Presence(0.6, 0.5, 0.2, 0.0), 1e-12, "above the edge");
            Assert.AreEqual(0.5, LayerMotionModel.Presence(0.5, 0.5, 0.2, 0.0), 1e-12, "at the threshold");
            Assert.AreEqual(0.0, LayerMotionModel.Presence(0.3, 0.5, 0.2, 0.0), 1e-12, "below the edge");
            Assert.AreEqual(0.0, LayerMotionModel.Presence(0.1, 0.5, 0.2, 0.3), 1e-12, "the clear-sky rule lowers a map value below its K");
            Assert.AreEqual(LayerMotionModel.Presence(0.9, 0.5, 0.2, 0.0), LayerMotionModel.Presence(0.9, 0.5, 0.2, 0.3), 1e-12, "and leaves a high one alone");
        }

        [TestMethod]
        public void TheOpacityOfAColumnGrowsWithTheSlantAndStopsAtGrazing()
        {
            double vertical = LayerMotionModel.Opacity(0.5, 1.0), slant = LayerMotionModel.Opacity(0.5, 0.25), grazing = LayerMotionModel.Opacity(0.5, 0.0);
            Assert.AreEqual(1.0 - Math.Exp(-0.5), vertical, 1e-12);
            Assert.IsTrue(slant > vertical);
            Assert.AreEqual(1.0 - Math.Exp(-0.5 / LayerMotionModel.GrazingCosine), grazing, 1e-12);
        }

        [TestMethod]
        public void TheMeanVerticalTauOfALayerIsItsColumnAtTheMeanOfTheTypes()
        {
            // sigma 0.001 per m over 1000 m: 1; types whose (top - base) are 0.5 and 1 and whose mean profiles are 0.4 and 0.6: 0.75 x 0.5 = 0.375.
            double tau = CullMath.MeanVerticalTau(0.001, 1000.0, 2, t => t == 0 ? 0.5 : 1.0, t => t == 0 ? 0.4 : 0.6);
            Assert.AreEqual(0.375, tau, 1e-12);
            Assert.AreEqual(0.0, CullMath.MeanVerticalTau(0.001, 1000.0, 0, t => 1.0, t => 1.0), 1e-12);
        }
    }
}
