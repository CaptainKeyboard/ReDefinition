using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Tests.Atmospheres.T18;

namespace ReDefinition.Tests.Atmospheres.T04
{
    // The step policy of CloudShadowField (CloudShadowSteps). The finding: with the height variation (T13) the layer's tight interval is the type table's plus the reach
    // of the variation on both sides, several times the cloud's own thickness, and the midpoint rule with a fixed count per span samples the cloud with a handful of
    // points. The tests measure the error of the old fixed policy and of the scaled one against the converged value (256 steps a span), on a sky with the real height maps
    // (HeightMapBuilder, the default HeightTuning) and on the same sky without the variation.
    [TestClass]
    public class CloudShadowQuadratureTests
    {
        private static readonly StringBuilder Table = new StringBuilder();

        // The LatLonSource sky with the height maps the stack would build for it.
        private sealed class VariedSource : ICloudLayerSource, ICloudHeightSource
        {
            private readonly LatLonSource inner;
            private readonly HeightMaps maps;
            private readonly HeightTuning tuning;

            internal VariedSource(LatLonSource inner, Func<double, double, double> cover, bool varied)
            {
                this.inner = inner;
                if (!varied) return;
                tuning = new HeightTuning();
                const int w = 256, h = 128;
                var input = new HeightInputs { Width = w, Height = h, Coverage = new float[w * h], Updraft = new float[w * h] };
                for (int j = 0; j < h; j++)
                    for (int i = 0; i < w; i++)
                    {
                        double lat = 90.0 - (j + 0.5) / h * 180.0, lon = (i + 0.5) / w * 360.0 - 180.0;
                        double c = cover(lat, lon);
                        input.Coverage[j * w + i] = (float)c;
                        input.Updraft[j * w + i] = (float)(1.2 + 2.5 * c);
                    }
                maps = HeightMapBuilder.Build(input, tuning, inner.BodyRadiusM, 5u);
                for (int l = 0; l < inner.LayerCount; l++)
                {
                    inner.LayerAt(l).HeightDownM = tuning.DownM;
                    inner.LayerAt(l).HeightUpM = tuning.UpM;
                }
            }

            public int BodyIndex { get { return inner.BodyIndex; } }
            public double BodyRadiusM { get { return inner.BodyRadiusM; } }
            public bool HasSurface { get { return inner.HasSurface; } }
            public int LayerCount { get { return inner.LayerCount; } }
            public long Version { get { return inner.Version; } }
            public CloudLayerDef LayerAt(int index) { return inner.LayerAt(index); }
            public void SampleMaps(int layerIndex, double latDeg, double lonDeg, out float coverage, out float type) { inner.SampleMaps(layerIndex, latDeg, lonDeg, out coverage, out type); }
            public HeightTuning HeightOf(int layerIndex) { return tuning; }
            public LocalHeights SampleHeights(int layerIndex, double latDeg, double lonDeg) { return maps.Sample(latDeg, lonDeg); }

            internal LayerTypeTable[] Tables() { return inner.Tables(); }

            internal void Position(double lat, double lon, double alt, out double x, out double y, out double z) { inner.Position(lat, lon, alt, out x, out y, out z); }
        }

        // smooth: a cloud type with a bell-shaped profile (the table types of the game are smooth); otherwise the hard-edged slab, the worst case for a
        // midpoint rule (its error is the fraction of a step at each edge).
        private static VariedSource Sky(bool varied, bool smooth = false, double baseM = 1500, double topM = 3500)
        {
            Func<double, double, double> cover = PackScenes.Noise(7, 12);
            CloudTypeDef type = smooth ? new CloudTypeDef("bell", 0f, 1f, new Curve(new[] { 0f, 0.15f, 0.5f, 0.85f, 1f }, new[] { 0f, 0.7f, 1f, 0.7f, 0f }), 0f) : null;
            var s = new LatLonSource(PackScenes.Kerbin).Add("n", baseM, topM, 3.0, cover, 0.15, type);
            return new VariedSource(s, cover, varied);
        }

        // The error of the fixed policy by step count and the scaled policy at the asked counts, against 256 steps: the measurement row of the step policy.
        [TestMethod]
        public void TheErrorByStepCountOfBothPoliciesOnBothClouds()
        {
            Table.Clear();
            foreach (bool smooth in new[] { false, true })
                foreach (bool varied in new[] { true, false })
                {
                    VariedSource s = Sky(varied, smooth);
                    CloudShadowField reference = Field(s, 256, CloudShadowSteps.Fixed);
                    foreach (double elevation in new[] { 45.0, 20.0 })
                    {
                        foreach (int steps in new[] { 8, 16, 24, 48, 64, 128 })
                            Table.AppendLine((smooth ? "bell " : "slab ") + (varied ? "varied " : "flat   ") + "sun " + elevation + " fixed  " + steps + ": " + Line("T", Scan(s, Field(s, steps, CloudShadowSteps.Fixed), reference, elevation, false)));
                        foreach (int steps in new[] { 4, 8, 12, 16 })
                            Table.AppendLine((smooth ? "bell " : "slab ") + (varied ? "varied " : "flat   ") + "sun " + elevation + " scaled " + steps + ": " + Line("T", Scan(s, Field(s, steps, CloudShadowSteps.Scaled), reference, elevation, false)));
                    }
                }
            Console.WriteLine(Table.ToString());
        }

        private struct Errors
        {
            internal int N;
            internal double Median, P90, Worst;
        }

        // The relative error of a field against the converged one over the lattice's places where the converged factor is neither near 0 nor near 1.
        private static Errors Scan(VariedSource s, CloudShadowField test, CloudShadowField reference, double elevation, bool tau)
        {
            var diffs = new List<double>();
            for (double lat = -20; lat < 20; lat += 2.1)
                for (double lon = -30; lon < 30; lon += 0.37)
                {
                    double x, y, z, sx, sy, sz;
                    s.Position(lat, lon, 0.0, out x, out y, out z);
                    LatLonSource.SunTowards(lat, lon, elevation, 90.0, out sx, out sy, out sz);
                    double r = reference.DirectTransmission(x, y, z, sx, sy, sz);
                    if (r <= 0.05 || r >= 0.95) continue;
                    double a, b;
                    if (tau) { a = test.VerticalOpticalDepth(lat, lon, 0.0); b = reference.VerticalOpticalDepth(lat, lon, 0.0); }
                    else { a = test.DirectTransmission(x, y, z, sx, sy, sz); b = r; }
                    diffs.Add(Math.Abs(a - b) / Math.Max(1e-9, Math.Abs(b)));
                }
            diffs.Sort();
            return diffs.Count == 0 ? new Errors() : new Errors { N = diffs.Count, Median = diffs[diffs.Count / 2], P90 = diffs[(int)(diffs.Count * 0.9)], Worst = diffs[diffs.Count - 1] };
        }

        private static string Line(string name, Errors e)
        {
            return name + ": n " + e.N + " median " + (100 * e.Median).ToString("0.00") + " % p90 " + (100 * e.P90).ToString("0.00") + " % worst " + (100 * e.Worst).ToString("0.00") + " %";
        }

        private static CloudShadowField Field(VariedSource s, int steps, CloudShadowSteps policy)
        {
            return new CloudShadowField(s, s.Tables()) { StepsPerSpan = steps, StepPolicy = policy };
        }

        // The old policy: the same count in every span, whatever the interval.
        [TestMethod]
        public void TheFixedPolicyIsCoarseWithTheVariationAndAccurateWithout()
        {
            Table.Clear();
            Errors? varied8 = null, flat8 = null;
            foreach (bool varied in new[] { true, false })
            {
                VariedSource s = Sky(varied);
                CloudShadowField reference = Field(s, 256, CloudShadowSteps.Fixed);
                foreach (double elevation in new[] { 45.0, 20.0 })
                {
                    Errors e = Scan(s, Field(s, 8, CloudShadowSteps.Fixed), reference, elevation, false);
                    Table.AppendLine("fixed 8 vs 256, variation " + varied + ", sun " + elevation + ": " + Line("transmission", e));
                    if (elevation == 45.0) { if (varied) varied8 = e; else flat8 = e; }
                }
                Errors t = Scan(s, Field(s, 8, CloudShadowSteps.Fixed), reference, 45.0, true);
                Table.AppendLine("fixed 8 vs 256, variation " + varied + ": " + Line("vertical tau", t));
            }
            Console.WriteLine(Table.ToString());
            Assert.IsTrue(varied8.Value.P90 > 0.05, "the finding: p90 " + varied8.Value.P90);
            Assert.IsTrue(flat8.Value.P90 < 0.01, "without the variation 8 steps are accurate: p90 " + flat8.Value.P90);
        }

        [TestMethod]
        public void TheScaledPolicyBringsTheErrorWithTheVariationDownAndLeavesTheOtherUntouched()
        {
            // The smooth profile (the game's types) reaches p90 <= 5 percent at 8 steps; the hard-edged slab is limited by the edges (the error is a fraction of a step
            // at each of them), so it is held to "not worse than the fixed count" and its numbers are in the table.
            Table.Clear();
            foreach (bool smooth in new[] { true, false })
                foreach (bool varied in new[] { true, false })
                {
                    VariedSource s = Sky(varied, smooth);
                    CloudShadowField reference = Field(s, 256, CloudShadowSteps.Fixed);
                    foreach (double elevation in new[] { 45.0, 20.0 })
                    {
                        Errors e = Scan(s, Field(s, 8, CloudShadowSteps.Scaled), reference, elevation, false);
                        Errors old = Scan(s, Field(s, 8, CloudShadowSteps.Fixed), reference, elevation, false);
                        Table.AppendLine((smooth ? "bell" : "slab") + ", variation " + varied + ", sun " + elevation + ": " + Line("scaled 8", e) + "  | " + Line("fixed 8", old));
                        if (smooth) Assert.IsTrue(e.P90 <= 0.05, "variation " + varied + " sun " + elevation + ": p90 " + e.P90);
                        Assert.IsTrue(e.P90 <= old.P90 + 0.005, "variation " + varied + " sun " + elevation + ": p90 " + e.P90 + " against fixed " + old.P90);
                    }
                }
            Console.WriteLine(Table.ToString());
        }

        [TestMethod]
        public void WithoutTheVariationTheScaledPolicyIsTheFixedOneToTheBit()
        {
            foreach (bool smooth in new[] { false, true })
            {
                VariedSource s = Sky(false, smooth);
                CloudShadowField fixedField = Field(s, 8, CloudShadowSteps.Fixed), scaled = Field(s, 8, CloudShadowSteps.Scaled);
                for (double lon = -30; lon < 30; lon += 0.91)
                {
                    double x, y, z, sx, sy, sz;
                    s.Position(3.0, lon, 0.0, out x, out y, out z);
                    LatLonSource.SunTowards(3.0, lon, 33.0, 90.0, out sx, out sy, out sz);
                    Assert.AreEqual(fixedField.DirectTransmission(x, y, z, sx, sy, sz), scaled.DirectTransmission(x, y, z, sx, sy, sz), 0.0);
                    Assert.AreEqual(fixedField.VerticalOpticalDepth(3.0, lon, 0.0), scaled.VerticalOpticalDepth(3.0, lon, 0.0), 0.0);
                }
            }
        }

        [TestMethod]
        public void TheStepCountFollowsTheWideningAndIsCapped()
        {
            Assert.AreEqual(8, CloudShadowStepPolicy.Count(CloudShadowSteps.Scaled, 8, 2000.0, 2000.0, 64));
            Assert.AreEqual(8, CloudShadowStepPolicy.Count(CloudShadowSteps.Fixed, 8, 2000.0, 9000.0, 64));
            Assert.AreEqual(24, CloudShadowStepPolicy.Count(CloudShadowSteps.Scaled, 8, 2000.0, 6000.0, 64));
            Assert.AreEqual(64, CloudShadowStepPolicy.Count(CloudShadowSteps.Scaled, 8, 200.0, 6000.0, 64));
            Assert.AreEqual(8, CloudShadowStepPolicy.Count(CloudShadowSteps.Scaled, 8, 0.0, 6000.0, 64), "no thickness to compare with: unchanged");
            Assert.AreEqual(8, CloudShadowStepPolicy.Count(CloudShadowSteps.Scaled, 8, 2000.0, 1000.0, 64), "never fewer than asked");
        }

        // WeatherHooks.CloudOpticalDepth is the vertical optical depth of the bound field (16 steps a span): how far its values move with the scaled policy.
        [TestMethod]
        public void TheHookValueMovesByTheErrorOfTheFixedPolicy()
        {
            Table.Clear();
            foreach (bool smooth in new[] { true, false })
            {
                VariedSource s = Sky(true, smooth);
                CloudShadowField reference = Field(s, 256, CloudShadowSteps.Fixed);
                Errors old = Scan(s, Field(s, 16, CloudShadowSteps.Fixed), reference, 45.0, true), now = Scan(s, Field(s, 16, CloudShadowSteps.Scaled), reference, 45.0, true);
                Table.AppendLine((smooth ? "bell" : "slab") + " vertical tau at 16 steps: " + Line("fixed ", old) + "  | " + Line("scaled", now));
                Assert.IsTrue(now.P90 <= old.P90 + 1e-9);
            }
            Console.WriteLine(Table.ToString());
        }

        // The cost of one call on the CPU, ms, per policy and step count: DirectTransmission over the lattice.
        [TestMethod]
        public void TheCostOfTheScaledPolicyOnTheCpu()
        {
            Table.Clear();
            VariedSource s = Sky(true);
            foreach (CloudShadowSteps policy in new[] { CloudShadowSteps.Fixed, CloudShadowSteps.Scaled })
                foreach (int steps in new[] { 4, 8, 12 })
                {
                    CloudShadowField f = Field(s, steps, policy);
                    int calls = 0;
                    double sink = 0.0;
                    var watch = Stopwatch.StartNew();
                    for (int rep = 0; rep < 3; rep++)
                        for (double lat = -20; lat < 20; lat += 2.1)
                            for (double lon = -30; lon < 30; lon += 0.37)
                            {
                                double x, y, z, sx, sy, sz;
                                s.Position(lat, lon, 0.0, out x, out y, out z);
                                LatLonSource.SunTowards(lat, lon, 45.0, 90.0, out sx, out sy, out sz);
                                sink += f.DirectTransmission(x, y, z, sx, sy, sz);
                                calls++;
                            }
                    watch.Stop();
                    Table.AppendLine(policy + " " + steps + " steps: " + (watch.Elapsed.TotalMilliseconds / calls * 1000.0).ToString("0.0") + " us per call (" + calls + " calls, sink " + sink.ToString("0.0") + ")");
                }
            Console.WriteLine(Table.ToString());
        }
    }
}
