using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.C23
{
    // The pure parts of the cloud-map painter (package C23): the layout math against EVE's formula, the slew limit, the CPU twin of the
    // painter pass and the mirror's resampling error, without the game.
    [TestClass]
    public class MapPainterMathTests
    {
        // A field map from a function of latitude and longitude in degrees, 0 to 1.
        private static float[] Field(int w, int h, Func<double, double, double> f)
        {
            var values = new float[w * h];
            for (int j = 0; j < h; j++)
            {
                double lat = 90.0 - (j + 0.5) / h * 180.0;
                for (int i = 0; i < w; i++)
                {
                    double lon = (i + 0.5) / w * 360.0 - 180.0;
                    values[j * w + i] = (float)Math.Max(0.0, Math.Min(1.0, f(lat, lon)));
                }
            }
            return values;
        }

        private static double Bump(double lat, double lon, double lat0, double lon0, double radiusDeg)
        {
            double dl = lon - lon0;
            if (dl > 180) dl -= 360;
            if (dl < -180) dl += 360;
            double dlat = lat - lat0;
            double d = Math.Sqrt(dlat * dlat + dl * dl * Math.Cos(lat0 * Math.PI / 180.0) * Math.Cos(lat0 * Math.PI / 180.0));
            return d >= radiusDeg ? 0.0 : 0.5 + 0.5 * Math.Cos(Math.PI * d / radiusDeg);
        }

        // Five test fields that differ in size, position and structure.
        private static float[][] FiveFields(int w, int h)
        {
            return new[]
            {
                Field(w, h, (lat, lon) => Bump(lat, lon, 10, 20, 25)),
                Field(w, h, (lat, lon) => Bump(lat, lon, -40, -170, 30)),                       // across the date line
                Field(w, h, (lat, lon) => 0.5 + 0.5 * Math.Sin(lon * Math.PI / 45.0) * Math.Cos(lat * Math.PI / 180.0)),
                Field(w, h, (lat, lon) => Math.Max(Bump(lat, lon, 55, 100, 18), Bump(lat, lon, -20, -60, 22))),
                Field(w, h, (lat, lon) => lat > 60 ? 0.9 : 0.1)
            };
        }

        [TestMethod]
        public void LayoutMatchesEvesFormula()
        {
            // TextureWrapper.cs:596-604: uv.y = acos(n.y) / pi, uv.x = 0.5 + atan2(n.x, n.z) / (2 pi).
            var rng = new Random(7);
            for (int k = 0; k < 200; k++)
            {
                double x = rng.NextDouble() * 2 - 1, y = rng.NextDouble() * 2 - 1, z = rng.NextDouble() * 2 - 1;
                double length = Math.Sqrt(x * x + y * y + z * z);
                if (length < 0.1) continue;
                x /= length; y /= length; z /= length;
                double u, v;
                EveMapLayout.LayerCoordinates(x, y, z, out u, out v);
                Assert.AreEqual(Math.Acos(y) / Math.PI, v, 1e-12);
                Assert.AreEqual(0.5 + Math.Atan2(x, z) / (2 * Math.PI), u, 1e-12);
                double lat = EveMapLayout.LatitudeOfV(v);
                Assert.AreEqual(Math.Asin(y) * 180.0 / Math.PI, lat, 1e-9);
            }
        }

        [TestMethod]
        public void ShiftAndMirrorAreInverse()
        {
            foreach (bool mirrored in new[] { false, true })
            {
                foreach (double shift in new[] { 0.0, 33.0, -120.0, 179.0 })
                {
                    for (double lon = -179; lon < 180; lon += 17)
                    {
                        double u = EveMapLayout.LayerU(lon, shift, mirrored);
                        double fieldU = EveMapLayout.FieldU(u, shift, mirrored);
                        double expected = (lon + 180.0) / 360.0;
                        double d = Math.Abs(fieldU - expected);
                        Assert.IsTrue(d < 1e-9 || Math.Abs(d - 1) < 1e-9, "shift " + shift + " mirrored " + mirrored + " lon " + lon);
                        Assert.AreEqual(shift, EveMapLayout.ShiftFrom(lon, u, mirrored), 1e-6, "ShiftFrom");
                    }
                }
            }
        }

        [TestMethod]
        public void CalibrationFindsShiftMirrorAndRejectsATiltedLayer()
        {
            foreach (bool mirrored in new[] { false, true })
            {
                const double Shift = 41.5;
                EveMapLayout.LayerOfPlace layerOf = delegate (double lat, double lon, out double u, out double v)
                {
                    u = EveMapLayout.LayerU(lon, Shift, mirrored);
                    v = EveMapLayout.VOfLatitude(lat);
                };
                double shift, worst;
                bool m;
                Assert.IsTrue(EveMapLayout.Calibrate(layerOf, 0.5, out shift, out m, out worst));
                Assert.AreEqual(mirrored, m);
                Assert.AreEqual(Shift, shift, 1e-6);
                Assert.IsTrue(worst < 1e-6);
            }
            // A layer whose pole axis is tilted by 20 degrees: latitude does not follow, no shift fits.
            EveMapLayout.LayerOfPlace tilted = delegate (double lat, double lon, out double u, out double v)
            {
                double la = lat * Math.PI / 180.0, lo = lon * Math.PI / 180.0;
                double x = Math.Cos(la) * Math.Sin(lo), y = Math.Sin(la), z = Math.Cos(la) * Math.Cos(lo);
                double t = 20.0 * Math.PI / 180.0;
                double y2 = y * Math.Cos(t) - z * Math.Sin(t), z2 = y * Math.Sin(t) + z * Math.Cos(t);
                EveCoverageMath.Uv(x, y2, z2, out u, out v);
            };
            double s2, w2;
            bool m2;
            Assert.IsFalse(EveMapLayout.Calibrate(tilted, 1.0, out s2, out m2, out w2));
        }

        [TestMethod]
        public void SlewNeverMovesATexelFasterThanTheRate()
        {
            const float Rate = MapSlew.DefaultRate;
            var current = new byte[256];
            var target = new byte[256];
            var rng = new Random(3);
            for (int i = 0; i < 256; i++) { current[i] = (byte)rng.Next(256); target[i] = (byte)rng.Next(256); }
            for (int step = 0; step < 100; step++)
            {
                var before = (byte[])current.Clone();
                MapSlew.StepMap(current, target, Rate, MapSlew.DefaultIntervalS);
                float maxLevels = Math.Max(1f, MapSlew.StepLevels(Rate, MapSlew.DefaultIntervalS));
                for (int i = 0; i < 256; i++)
                {
                    Assert.IsTrue(Math.Abs(current[i] - before[i]) <= Math.Ceiling(maxLevels), "step " + step + " texel " + i);
                    // Never past the target, and never away from it.
                    Assert.IsTrue(Math.Abs(current[i] - target[i]) <= Math.Abs(before[i] - target[i]));
                }
            }
            for (int i = 0; i < 256; i++) Assert.AreEqual(target[i], current[i], "converges");
        }

        [TestMethod]
        public void TheDefaultSlewMovesTexelsAndTakesLongerThanEvesReconstruction()
        {
            Assert.IsTrue(MapSlew.Quantises(MapSlew.DefaultRate, MapSlew.DefaultIntervalS), "the limited step is below one level");
            // A full-scale jump takes 12.5 s: at 60 fps far more than the 12 frames of the x12 reconstruction and the 16 of the direct light.
            Assert.IsTrue(MapSlew.FramesFor(1f, MapSlew.DefaultRate, 60f) > 16f * 10);
            Assert.IsFalse(MapSlew.Quantises(0.01f, 1f / 60f), "a per-frame limit of this rate would stall in 8 bit");
            Assert.AreEqual(0f, MapSlew.MaxDelta(0f, 1f));
            Assert.AreEqual(0.5f, MapSlew.Step(0f, 1f, 0.5f), 1e-6);
            Assert.AreEqual(0.2f, MapSlew.Step(0.3f, 0.2f, 0.5f), 1e-6);
        }

        [TestMethod]
        public void AStaticFieldIsReachedAndHeld()
        {
            float[] field = FiveFields(256, 128)[0];
            var model = new MapPaintModel(512, 256);
            int open = int.MaxValue;
            for (int i = 0; i < 80 && open > 0; i++) open = model.Update(field, 256, 128, 17.0, false, 0.25f, MapSlew.DefaultRate);
            Assert.AreEqual(0, open);
            var held = (byte[])model.Current.Clone();
            model.Update(field, 256, 128, 17.0, false, 0.25f, MapSlew.DefaultRate);
            CollectionAssert.AreEqual(held, model.Current);
        }

        [TestMethod]
        public void AFrontPassingChangesEveryTexelSmoothly()
        {
            // A band of cloud moves east 20 m/s on Kerbin (a texel of 2048 is 1.84 km): the target moves 1 texel per 92 s. The painted
            // map follows it without a texel changing by more than the slew in one update.
            const int W = 512, H = 256;
            var model = new MapPaintModel(W, H);
            byte[] last = null;
            double worst = 0;
            for (int step = 0; step < 400; step++)
            {
                double lon0 = -30.0 + step * 0.25 * (20.0 / (2 * Math.PI * 600000.0) * 360.0);
                float[] field = Field(128, 64, (lat, lon) => Bump(lat, lon, 0, lon0, 20));
                model.Update(field, 128, 64, 0, false, 0.25f, MapSlew.DefaultRate);
                if (last != null)
                {
                    for (int i = 0; i < last.Length; i++) worst = Math.Max(worst, Math.Abs(model.Current[i] - last[i]));
                }
                last = (byte[])model.Current.Clone();
            }
            double maxLevels = Math.Ceiling(MapSlew.StepLevels(MapSlew.DefaultRate, 0.25f));
            Assert.IsTrue(worst <= maxLevels + 1, "largest step " + worst + " levels, limit " + maxLevels);
        }

        [TestMethod]
        public void MirrorFollowsThePaintedMapWithCorrelationOverNinetyNine()
        {
            // The GPU map (2048 x 1024, the CPU twin of the pass) against the mirror (512 x 256): five fields.
            float[][] fields = FiveFields(256, 128);
            for (int k = 0; k < fields.Length; k++)
            {
                var big = new MapPaintModel(2048, 1024);
                var small = new MapPaintModel(CpuMirror.Width, CpuMirror.Height);
                big.Snap(fields[k], 256, 128, 63.0, k % 2 == 1);
                small.Snap(fields[k], 256, 128, 63.0, k % 2 == 1);
                double c = EveMapLayout.CorrelationAcrossSizes(small.Current, small.Width, small.Height, big.Current, big.Width, big.Height);
                Assert.IsTrue(c >= 0.99, "field " + k + " correlation " + c);
            }
        }

        [TestMethod]
        public void MirrorBandsGiveTheSameMapAsAWholeUpdate()
        {
            float[] field = FiveFields(256, 128)[3];
            var whole = new MapPaintModel(CpuMirror.Width, CpuMirror.Height);
            var bands = new MapPaintModel(CpuMirror.Width, CpuMirror.Height);
            whole.Update(field, 256, 128, 10.0, false, 1f, MapSlew.DefaultRate);
            for (int row = 0; row < CpuMirror.Height; row += CpuMirror.RowsPerFrame)
            {
                bands.UpdateRows(field, 256, 128, 10.0, false, row, CpuMirror.RowsPerFrame, 1f, MapSlew.DefaultRate);
            }
            CollectionAssert.AreEqual(whole.Current, bands.Current);
        }

        [TestMethod]
        public void ResamplingErrorOfTheMirrorIsSmall()
        {
            float[] field = FiveFields(256, 128)[2];
            var reference = new byte[2048 * 1024];
            var mirror = new byte[CpuMirror.Width * CpuMirror.Height];
            EveMapLayout.Resample(field, 256, 128, reference, 2048, 1024, 0, false);
            EveMapLayout.Resample(field, 256, 128, mirror, CpuMirror.Width, CpuMirror.Height, 0, false);
            double sum = 0;
            int n = 0;
            for (int j = 0; j < CpuMirror.Height; j++)
            {
                for (int i = 0; i < CpuMirror.Width; i++)
                {
                    // The reference's four texels that lie inside the mirror's texel.
                    double mean = 0;
                    for (int dj = 0; dj < 4; dj++) for (int di = 0; di < 4; di++) mean += reference[(j * 4 + dj) * 2048 + i * 4 + di];
                    sum += Math.Abs(mean / 16.0 - mirror[j * CpuMirror.Width + i]);
                    n++;
                }
            }
            Assert.IsTrue(sum / n < 1.0, "mean error " + sum / n + " levels");
        }

        [TestMethod]
        public void ADifferentShiftMovesTheMap()
        {
            float[] field = FiveFields(256, 128)[0];
            var a = new MapPaintModel(512, 256);
            var b = new MapPaintModel(512, 256);
            a.Snap(field, 256, 128, 0, false);
            b.Snap(field, 256, 128, 90, false);
            Assert.IsTrue(EveMapLayout.Correlation(a.Current, b.Current) < 0.5);
            // A shift of a full circle is the same map.
            var c = new MapPaintModel(512, 256);
            c.Snap(field, 256, 128, 360, false);
            Assert.IsTrue(EveMapLayout.Correlation(a.Current, c.Current) > 0.999);
        }

        [TestMethod]
        public void CorrelationHandlesConstantMaps()
        {
            var a = new byte[100];
            var b = new byte[100];
            Assert.AreEqual(1.0, EveMapLayout.Correlation(a, b));
            for (int i = 0; i < 100; i++) b[i] = (byte)i;
            Assert.AreEqual(0.0, EveMapLayout.Correlation(a, b));
            Assert.AreEqual(1.0, EveMapLayout.Correlation(b, b), 1e-9);
        }
    }
}
