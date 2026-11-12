using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // The height variation of the cloud layers (package T13). The acceptance numbers and their sources (written before the build):
    //   LCL: 125 m per K of dew-point spread (Lawrence 2005); the Magnus dew point against Lawrence's (100 - RH) / 5 K rule within his stated 1 K for RH 0.5 to 0.95;
    //   the field-mean base follows the LCL of the mean spread within 15 %.
    //   Base spread within one field: 50 to 300 m standard deviation, above 20 m (cumulus bases lie at one level locally with tens to a few hundred m of spread) [proposed].
    //   Size trend: sign negative, slope within 30 % of the configured slope [proposed].
    //   Top against updraft at cell centres: correlation >= 0.7 [proposed]; no top below its base.
    //   Shadow: the centroid of the shadow within 1 % of h / tan(e) with h from the local base.
    [TestClass]
    public class HeightModelTests
    {
        [TestMethod]
        public void TheDewPointSpreadAgreesWithLawrencesRuleAndTheLclIs125MetresPerKelvin()
        {
            foreach (double rh in new[] { 0.5, 0.6, 0.7, 0.8, 0.9, 0.95 })
            {
                double spread = HeightModel.DewPointSpreadK(293.15, rh);
                // Lawrence (2005) gives the rule's error as under 1 K for RH above 50 %; the Magnus value is the more exact.
                double lawrence = (1.0 - rh) * 100.0 / 5.0;
                Assert.AreEqual(lawrence, spread, 1.0, "rh " + rh);
            }
            Assert.AreEqual(0.0, HeightModel.DewPointSpreadK(288.0, 1.0), 1e-9);
            Assert.AreEqual(1250.0, HeightModel.LclM(10.0, 125.0), 1e-9);
        }

        [TestMethod]
        public void MoistAirGivesALowerBaseThanDryAirAndTheOffsetIsLimited()
        {
            var t = new HeightTuning();
            double moist = HeightModel.PhysicsOffsetM(t, 288.0, 0.92, 4.0), dry = HeightModel.PhysicsOffsetM(t, 288.0, 0.35, 4.0);
            Assert.IsTrue(moist < 0.0 && dry > 0.0 && moist < dry, "moist " + moist + " dry " + dry);
            Assert.AreEqual(t.PhysicsMaxM, HeightModel.PhysicsOffsetM(t, 310.0, 0.05, 4.0), 1e-9);
            Assert.AreEqual(-t.PhysicsMaxM, HeightModel.PhysicsOffsetM(t, 288.0, 1.0, 20.0), 1e-9);
        }

        [TestMethod]
        public void TheRaggedBoundaryIsDeterministicBoundedAndOnlyDependsOnThePlaceOnTheGround()
        {
            double sum = 0.0, sq = 0.0;
            int n = 4000;
            for (int i = 0; i < n; i++)
            {
                double v = HeightModel.Ragged(i * 0.37, i * 0.11 + 3.0, i * 0.23, 7u);
                Assert.IsTrue(v >= -1.0 && v <= 1.0);
                Assert.AreEqual(v, HeightModel.Ragged(i * 0.37, i * 0.11 + 3.0, i * 0.23, 7u), 0.0);
                sum += v;
                sq += v * v;
            }
            Assert.IsTrue(Math.Abs(sum / n) < 0.08, "mean " + sum / n);
            Assert.IsTrue(Math.Sqrt(sq / n) > 0.2, "std " + Math.Sqrt(sq / n));
            // The same direction at two altitudes is one value (the point is projected onto the base sphere).
            double R = 600000.0, base1 = R + 1700.0;
            double a = HeightModel.RaggedAt(base1 * 0.6, base1 * 0.8, 0.0, base1, 220.0, 3u);
            double b = HeightModel.RaggedAt((R + 2100.0) * 0.6, (R + 2100.0) * 0.8, 0.0, base1, 220.0, 3u);
            Assert.AreEqual(a, b, 1e-9);
        }

        [TestMethod]
        public void TheRootWeightIsOneForConvectiveBasesAndZeroForCloudHigherInTheLayer()
        {
            Assert.AreEqual(1.0, HeightModel.RootWeight(0.0), 1e-12);
            Assert.AreEqual(1.0, HeightModel.RootWeight(0.1), 1e-12);
            Assert.AreEqual(0.0, HeightModel.RootWeight(0.4), 1e-12);
            Assert.AreEqual(0.5, HeightModel.RootWeight(0.25), 1e-12);
        }

        [TestMethod]
        public void TheTuningReadsItsKeysAndIgnoresBadOnes()
        {
            var keys = new Dictionary<string, string> { { "heightLclMPerK", "100" }, { "heightRaggedM", "abc" }, { "heightTopMaxM", "-5" } };
            HeightTuning t = HeightTuning.Parse(keys);
            Assert.AreEqual(100f, t.LclMPerK);
            Assert.AreEqual(new HeightTuning().RaggedM, t.RaggedM);
            Assert.AreEqual(0f, t.TopMaxM);
            Assert.AreEqual(new HeightTuning().VariationAmplitudeM, HeightTuning.Parse(null).VariationAmplitudeM);
        }

        [TestMethod]
        public void AFogLayerHasNoHeightVariationAndAThinHighLayerVariesMore()
        {
            var t = new HeightTuning();
            Assert.IsFalse(t.ForLayer(LayerKind.Fog).Any);
            Assert.IsTrue(t.ForLayer(LayerKind.Cheap).VariationAmplitudeM > t.VariationAmplitudeM);
            Assert.IsFalse(t.ForLayer(LayerKind.Cheap).BasePhysics);
        }
    }

    [TestClass]
    public class HeightMapTests
    {
        private const int W = 256, H = 128;
        private const double R = 600000.0;

        private static HeightInputs Uniform(float humidity, float temperature)
        {
            var input = new HeightInputs { Width = W, Height = H, Humidity = new float[W * H], TemperatureK = new float[W * H] };
            for (int i = 0; i < W * H; i++)
            {
                input.Humidity[i] = humidity;
                input.TemperatureK[i] = temperature;
            }
            return input;
        }

        private static double[] Channel(HeightMaps m, int channel)
        {
            double range = channel == 0 ? m.RootRangeM : channel == 1 ? m.TopRangeM : m.VariationRangeM;
            var v = new double[m.Width * m.Height];
            for (int i = 0; i < v.Length; i++) v[i] = HeightMaps.Decode(m.Bytes[i * 3 + channel], range);
            return v;
        }

        private static double Std(IList<double> v)
        {
            double mean = 0.0;
            foreach (double x in v) mean += x;
            mean /= v.Count;
            double s = 0.0;
            foreach (double x in v) s += (x - mean) * (x - mean);
            return Math.Sqrt(s / v.Count);
        }

        [TestMethod]
        public void TheMapsAreDeterministicAndTheSeedChangesThem()
        {
            var t = new HeightTuning();
            HeightMaps a = HeightMapBuilder.Build(Uniform(0.7f, 288f), t, R, 5u), b = HeightMapBuilder.Build(Uniform(0.7f, 288f), t, R, 5u),
                       c = HeightMapBuilder.Build(Uniform(0.7f, 288f), t, R, 6u);
            CollectionAssert.AreEqual(a.Bytes, b.Bytes);
            CollectionAssert.AreNotEqual(a.Bytes, c.Bytes);
        }

        [TestMethod]
        public void TheBaseSpreadWithinAFieldIsInTheRealBandAndNeverFlat()
        {
            var t = new HeightTuning { BasePhysics = false, TopFromUpdraft = false };
            HeightMaps m = HeightMapBuilder.Build(Uniform(0.7f, 288f), t, R, 11u);
            double[] v = Channel(m, 2);
            // One cumulus field: a window of 40 degrees by 20 degrees (about 400 km by 200 km on Kerbin), away from the poles.
            var window = new List<double>();
            for (int y = H / 2 - H / 9; y < H / 2 + H / 9; y++)
                for (int x = W / 2; x < W / 2 + W / 9; x++) window.Add(v[y * W + x]);
            double std = Std(window), all = Std(v);
            Assert.IsTrue(std > 20.0, "field spread " + std);
            Assert.IsTrue(all >= 50.0 && all <= 300.0, "global spread " + all);
            Assert.AreEqual(t.VariationAmplitudeM, all, 0.1 * t.VariationAmplitudeM);
        }

        [TestMethod]
        public void TheFieldMeanBaseFollowsTheLclFromTemperatureAndHumidity()
        {
            // With a fixed reference spread of 4 K (a base of 500 m at the type table), the base is the reference plus the offset.
            var t = new HeightTuning { BaseVariation = false, TopFromUpdraft = false, ReferenceSpreadK = 4f };
            foreach (double rh in new[] { 0.6, 0.75, 0.9 })
            {
                HeightMaps m = HeightMapBuilder.Build(Uniform((float)rh, 290f), t, R, 1u);
                double mean = 0.0;
                double[] root = Channel(m, 0);
                foreach (double x in root) mean += x;
                mean /= root.Length;
                double lcl = HeightModel.LclM(HeightModel.DewPointSpreadK(290.0, rh), t.LclMPerK);
                Assert.AreEqual(lcl, HeightModel.LclM(4.0, t.LclMPerK) + mean, 0.15 * lcl, "rh " + rh);
            }
            // With the automatic reference (the body's mean spread) the difference between a dry and a moist region is the LCL difference.
            var auto = new HeightTuning { BaseVariation = false, TopFromUpdraft = false, PhysicsMaxM = 2000f };
            var input = Uniform(0.85f, 290f);
            for (int y = 0; y < H; y++)
                for (int x = W / 2; x < W; x++) input.Humidity[y * W + x] = 0.55f;
            double[] offsets = Channel(HeightMapBuilder.Build(input, auto, R, 1u), 0);
            double moistMean = offsets[(H / 2) * W + W / 4], dryMean = offsets[(H / 2) * W + 3 * W / 4];
            double expected = HeightModel.LclM(HeightModel.DewPointSpreadK(290.0, 0.55) - HeightModel.DewPointSpreadK(290.0, 0.85), 125.0);
            Assert.IsTrue(moistMean < 0.0 && dryMean > 0.0);
            Assert.AreEqual(expected, dryMean - moistMean, 0.15 * expected);
        }

        [TestMethod]
        public void LargerCloudFieldsHaveALowerBaseByTheConfiguredTrend()
        {
            var t = new HeightTuning { BasePhysics = false, TopFromUpdraft = false, VariationAmplitudeM = 0f };
            var input = new HeightInputs { Width = W, Height = H, Coverage = new float[W * H] };
            // The western half is overcast (1), the eastern half is clear (0), well away from the date line and the poles.
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++) input.Coverage[y * W + x] = x >= W / 4 && x < W / 2 ? 1f : 0f;
            HeightMaps m = HeightMapBuilder.Build(input, t, R, 2u);
            double[] root = Channel(m, 0);
            double covered = root[(H / 2) * W + (3 * W / 8)], clear = root[(H / 2) * W + (3 * W / 4)];
            Assert.IsTrue(covered < clear, "covered " + covered + " clear " + clear);
            double slope = (covered - clear) / (1.0 - 0.0);
            Assert.AreEqual(t.SizeTrendM, slope, 0.3 * Math.Abs(t.SizeTrendM), "slope " + slope);
        }

        [TestMethod]
        public void TopsRiseWithTheUpdraftOfTheCellsAndNeverFallBelowTheBase()
        {
            var t = new HeightTuning { BasePhysics = false };
            var input = new HeightInputs { Width = W, Height = H, Updraft = new float[W * H] };
            var rng = new Random(4);
            var centres = new List<int[]>();
            for (int c = 0; c < 40; c++) centres.Add(new[] { rng.Next(10, W - 10), rng.Next(20, H - 20), c });
            // Gaussian cells of different strength (0.5 to 6 m/s) on a weak background.
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    double u = 0.3;
                    foreach (int[] c in centres)
                    {
                        double strength = 0.5 + 5.5 * (c[2] % 11) / 10.0, d2 = (x - c[0]) * (x - c[0]) + (y - c[1]) * (y - c[1]);
                        u = Math.Max(u, strength * Math.Exp(-d2 / 8.0));
                    }
                    input.Updraft[y * W + x] = (float)u;
                }
            HeightMaps m = HeightMapBuilder.Build(input, t, R, 3u);
            double[] top = Channel(m, 1);
            var up = new List<double>();
            var tp = new List<double>();
            foreach (int[] c in centres)
            {
                up.Add(input.Updraft[c[1] * W + c[0]]);
                tp.Add(top[c[1] * W + c[0]]);
            }
            double r = Correlation(up, tp);
            Assert.IsTrue(r >= 0.7, "correlation " + r);
            // No top below its base: over every texel and for the table types of the shipped schema.
            foreach (double baseShare in new[] { 0.0, 0.05, 0.1, 0.3, 0.54 })
                for (int i = 0; i < W * H; i += 7)
                {
                    var h = new LocalHeights { TopM = (float)top[i], RootM = -500f, VariationM = -440f };
                    double lb, lt;
                    LayerGeometry.LocalBaseTop(baseShare, baseShare + 0.1, 0.5, 2000.0, h, -1.0, t.RaggedM, t.MinThicknessM, out lb, out lt);
                    Assert.IsTrue(lt >= lb + t.MinThicknessM / 2000.0 - 1e-12);
                }
        }

        private static double Correlation(IList<double> a, IList<double> b)
        {
            double ma = 0, mb = 0;
            for (int i = 0; i < a.Count; i++) { ma += a[i]; mb += b[i]; }
            ma /= a.Count;
            mb /= b.Count;
            double sab = 0, saa = 0, sbb = 0;
            for (int i = 0; i < a.Count; i++)
            {
                sab += (a[i] - ma) * (b[i] - mb);
                saa += (a[i] - ma) * (a[i] - ma);
                sbb += (b[i] - mb) * (b[i] - mb);
            }
            return sab / Math.Sqrt(saa * sbb);
        }

        [TestMethod]
        public void AllSwitchesOffGiveNeutralMapsAndTheSampleIsZeroAtByteCentre()
        {
            var t = new HeightTuning { BaseVariation = false, BasePhysics = false, TopFromUpdraft = false, BaseRagged = false };
            Assert.IsFalse(t.Any);
            HeightMaps m = HeightMapBuilder.Build(Uniform(0.5f, 290f), t, R, 1u);
            foreach (byte b in m.Bytes) Assert.AreEqual(128, b);
            LocalHeights h = m.Sample(10.0, 20.0);
            Assert.AreEqual(0f, h.RootM);
            Assert.AreEqual(0f, h.TopM);
            Assert.AreEqual(0f, h.VariationM);
        }

        [TestMethod]
        public void TheSampleWrapsAtTheDateLineAndInterpolates()
        {
            var m = new HeightMaps(4, 2, 100f, 100f, 100f);
            for (int y = 0; y < 2; y++)
                for (int x = 0; x < 4; x++) m.Bytes[(y * 4 + x) * 3] = (byte)(x == 0 ? 255 : x == 3 ? 1 : 128);
            LocalHeights a = m.Sample(45.0, 179.9), b = m.Sample(45.0, -179.9);
            Assert.AreEqual(a.RootM, b.RootM, 0.5);
            Assert.AreEqual(0f, m.Sample(45.0, 0.0).RootM, 0.01);
        }
    }

    [TestClass]
    public class HeightDensityTests
    {
        private const double R = 600000.0;

        private static CloudLayerDef Slab(float inner, float outer, float baseShare = 0f, float topShare = 1f)
        {
            return new CloudLayerDef("l", 1, inner, outer, new[] { CloudTypeDef.Slab("s", baseShare, topShare) }, 0.01f) { Coverage = 0.5f, EdgeSoftness = 1e-4f };
        }

        private static HeightMaps Constant(float root, float top, float variation, float range = 600f)
        {
            var m = new HeightMaps(4, 2, range, range, range);
            for (int i = 0; i < 8; i++)
            {
                m.Bytes[i * 3] = HeightMaps.Encode(root, range);
                m.Bytes[i * 3 + 1] = HeightMaps.Encode(top, range);
                m.Bytes[i * 3 + 2] = HeightMaps.Encode(variation, range);
            }
            return m;
        }

        [TestMethod]
        public void ALayerWithoutHeightVariationHasTheTypeTablesDensity()
        {
            var layer = Slab(1500f, 3000f);
            var table = new LayerTypeTable(layer);
            var source = new LayerStackSource(1, R, true);
            source.Add(layer, null, null);
            for (double alt = 1400.0; alt < 3100.0; alt += 53.0)
                Assert.AreEqual(LayerDensity.Extinction(layer, table, alt, 1.0, 0.0), HeightDensity.Extinction(source, table, 0, alt, 10.0, 20.0), 0.0);
        }

        [TestMethod]
        public void ARaisedBaseCutsTheDensityBelowItAndKeepsTheThicknessOfADeck()
        {
            var layer = Slab(1500f, 3000f);
            var table = new LayerTypeTable(layer);
            var t = new HeightTuning { BaseRagged = false };
            LocalHeights h = new LocalHeights { RootM = 300f };
            // The slab type has base share 0, so the root offset raises its base to 1800 m and (no top term) its top to 3300 m.
            Assert.AreEqual(0.0, HeightDensity.Extinction(layer, table, t, 1700.0, 1.0, 0.0, h, 0.0), 0.0);
            Assert.IsTrue(HeightDensity.Extinction(layer, table, t, 1900.0, 1.0, 0.0, h, 0.0) > 0.0);
            Assert.IsTrue(HeightDensity.Extinction(layer, table, t, 3200.0, 1.0, 0.0, h, 0.0) > 0.0);
            Assert.AreEqual(0.0, HeightDensity.Extinction(layer, table, t, 3400.0, 1.0, 0.0, h, 0.0), 0.0);
            // A type that starts high in the layer (altocumulus) does not take the root offset, only the variation.
            var high = Slab(1500f, 3000f, 0.6f, 0.8f);
            var highTable = new LayerTypeTable(high);
            Assert.IsTrue(HeightDensity.Extinction(high, highTable, t, 1500.0 + 0.61 * 1500.0, 1.0, 0.0, h, 0.0) > 0.0);
            Assert.AreEqual(0.0, HeightDensity.Extinction(high, highTable, t, 1500.0 + 0.59 * 1500.0, 1.0, 0.0, h, 0.0), 0.0);
        }

        [TestMethod]
        public void TheTightIntervalWidensByTheReach()
        {
            var layer = Slab(1500f, 3000f);
            float i0, o0;
            layer.TightAltitudes(out i0, out o0);
            var source = new LayerStackSource(1, R, true);
            source.Add(layer, null, null);
            var t = new HeightTuning();
            source.SetHeights(0, t, Constant(0f, 0f, 0f), 1u);
            float i1, o1;
            layer.TightAltitudes(out i1, out o1);
            Assert.AreEqual(i0 - t.DownM, i1, 1e-3);
            Assert.AreEqual(o0 + t.UpM, o1, 1e-3);
            source.SetHeights(0, null, null, 0u);
            layer.TightAltitudes(out i1, out o1);
            Assert.AreEqual(i0, i1, 1e-3);
        }

        // The shadow of a hard-edged patch of cloud with a raised base lies where the local base puts it: the centroid of the shadow on the
        // ground is at h / tan(e) to the side of the patch, h from the local base and top, within 1 %.
        [TestMethod]
        public void TheShadowDisplacementUsesTheLocalHeight()
        {
            const double Flat = 1e8;
            foreach (float raise in new[] { 0f, 300f, -200f })
            {
                var layer = Slab(1500f, 3000f);
                var source = new FlatHeightSource(Flat, layer, raise);
                var field = new CloudShadowField(source, new[] { new LayerTypeTable(layer) }) { StepsPerSpan = 400 };
                double elev = 30.0 * Math.PI / 180.0, sx = Math.Sin(elev), sy = Math.Cos(elev);
                double sum = 0.0, weighted = 0.0;
                for (double e = -12000.0; e <= 8000.0; e += 20.0)
                {
                    double a = e / Flat;
                    double tau = field.SunPathOpticalDepth(Flat * Math.Cos(a), Flat * Math.Sin(a), 0.0, sx, sy, 0.0);
                    sum += tau;
                    weighted += tau * e;
                }
                double centroid = weighted / sum;
                double mid = 0.5 * (1500.0 + raise + 3000.0 + raise);
                double expected = -mid / Math.Tan(elev);
                Assert.AreEqual(expected, centroid, 0.01 * Math.Abs(expected), "raise " + raise + " centroid " + centroid + " expected " + expected);
            }
        }

        // A flat body with one hard-edged patch of cloud 4 km wide and a constant base raise (the height source of the test).
        private sealed class FlatHeightSource : ICloudLayerSource, ICloudHeightSource
        {
            private readonly CloudLayerDef layer;
            private readonly float raise;
            private readonly HeightTuning tuning = new HeightTuning { BaseRagged = false };

            internal FlatHeightSource(double radius, CloudLayerDef layer, float raise)
            {
                BodyRadiusM = radius;
                this.layer = layer;
                this.raise = raise;
                if (raise != 0f) layer.HeightDownM = layer.HeightUpM = 600f;
            }

            public int BodyIndex { get { return 1; } }
            public double BodyRadiusM { get; }
            public bool HasSurface { get { return true; } }
            public int LayerCount { get { return 1; } }
            public long Version { get { return 1; } }
            public CloudLayerDef LayerAt(int index) { return layer; }

            public void SampleMaps(int layerIndex, double latDeg, double lonDeg, out float coverage, out float type)
            {
                double east = BodyRadiusM * lonDeg * Math.PI / 180.0;
                coverage = Math.Abs(east) < 2000.0 ? 1f : 0f;
                type = 0f;
            }

            public HeightTuning HeightOf(int layerIndex) { return raise == 0f ? null : tuning; }
            public LocalHeights SampleHeights(int layerIndex, double latDeg, double lonDeg) { return new LocalHeights { RootM = raise }; }
        }
    }
}
