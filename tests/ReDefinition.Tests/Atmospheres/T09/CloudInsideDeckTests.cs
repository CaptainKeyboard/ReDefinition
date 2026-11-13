using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // E40 start 51 follow-up (offline, no game): the rain deck L0 draws nothing for a ray that starts inside it (camera 264 m, map 0.859, type 0.229, the Fog and Stratus blend 96 to 594 m)
    // although the CPU oracle and the weather player say closed. This file holds the CPU side of that question and the zenith optical depth readout that a later start prints beside the
    // colour-change measure.
    //
    // EXPECTED RESULTS, written before the first run:
    //   I1 span: a camera inside a layer's tight shell (tight inner 96 m below it, outer 594 m above it) looking up gets ONE span that starts at the camera (T0 = 0) and ends at the outer
    //      sphere; with the tight inner at 0 the same. (The shader's LayerSpans is the same arithmetic; a ray that cannot hit the inner sphere upward takes the far-wall branch with a
    //      negative i1 and keeps the near stretch.) Looking down, the span ends at the inner sphere.
    //   I2 height variation (T13): for every combination of the root offset, the top term, the variation and the ragged unit at their full ranges (3 values each, 81 cases), the ray from the
    //      camera at 18 degrees up through the blend at map 0.859 collects tau >= 0.92 (the thinnest cloud is 120 m: a slab of 120 m seen at 18 degrees is 388 m long at sigma 0.045 times a
    //      profile that is not zero over most of it). A height variation alone therefore cannot empty the deck for this ray.
    //      MISSED in the first run (tau 0.000 at the all-negative corner: the slab was moved under the ground); the fix and the revised expectations I2a to I2c are at the test below.
    //   I3 zenith: the optical depth straight up at the high pick is >= 4.6 at every one of 8 sites (the deck's 500 m vertical column at sigma 0.045), at the low pick (the camera above the
    //      Fog, 210 m) under 0.05; the function equals the oracle's one-pixel frame at pitch 90.
    //   I4 aid decode: for tau from 0.001 to 31.6 over a sky colour the decoded tau is within 10 percent (the grid's step); no cloud decodes as 0, tau above the scale as 31.6.
    [TestClass]
    public class CloudInsideDeckTests
    {
        private const double R = 600000.0;

        private static void Report(string line)
        {
            Console.WriteLine(line);
            string path = Environment.GetEnvironmentVariable("E40_REPORT");
            if (!string.IsNullOrEmpty(path)) System.IO.File.AppendAllText(path, line + Environment.NewLine);
        }

        [TestMethod]
        public void ARayStartingInsideTheTightShellKeepsItsNearStretchFromTheCamera()
        {
            double oz = R + 264.0;
            double up = 18.0 * Math.PI / 180.0;
            LayerSpan a, b;
            int count = LayerGeometry.ShellSpans(0, 0, oz, Math.Cos(up), 0, Math.Sin(up), R, 96f, 594f, 1e9, out a, out b);
            Assert.AreEqual(1, count, "one span looking up from inside the shell");
            Assert.AreEqual(0.0, a.T0, 1e-9, "the span starts at the camera");
            Assert.IsTrue(a.T1 > 400.0 && a.T1 < 1200.0, "ends at the outer sphere: " + a.T1);
            count = LayerGeometry.ShellSpans(0, 0, oz, Math.Cos(up), 0, Math.Sin(up), R, 0f, 594f, 1e9, out a, out b);
            Assert.AreEqual(1, count);
            Assert.AreEqual(0.0, a.T0, 1e-9);
            double down = -18.0 * Math.PI / 180.0;
            // The pass ends a ray at the ground sphere (264 m below the camera: 264 / sin 18 = 854 m along this ray), as March does with tmax.
            count = LayerGeometry.ShellSpans(0, 0, oz, Math.Cos(down), 0, Math.Sin(down), R, 96f, 594f, 854.0, out a, out b);
            Assert.AreEqual(1, count, "one span looking down: from the camera to the inner sphere");
            Assert.AreEqual(0.0, a.T0, 1e-9);
            Assert.IsTrue(a.T1 > 100.0 && a.T1 < 1000.0, "ends at the inner sphere: " + a.T1);
        }

        // The tau of the ray from the camera at 264 m, 18 degrees up, through the rain deck with the height variation at fixed offsets (noise-free shape: the map is far above the threshold).
        private static double TauWithHeights(CloudLayerDef layer, LayerTypeTable table, HeightTuning tuning, LocalHeights heights, double ragged)
        {
            double up = 18.0 * Math.PI / 180.0;
            double dx = Math.Cos(up), dz = Math.Sin(up), ox = 0.0, oz = R + 264.0;
            double tau = 0.0, ds = 4.0;
            for (double t = ds * 0.5; t < 4000.0; t += ds)
            {
                double x = ox + dx * t, z = oz + dz * t;
                double alt = Math.Sqrt(x * x + z * z) - R;
                if (alt > 2300.0) break;
                tau += HeightDensity.Extinction(layer, table, tuning, alt, 0.859, 0.229, heights, ragged) * ds;
            }
            return tau;
        }

        // FINDING of the first run (the expectation I2 above missed): at the corner of all-negative offsets (root, top term and variation at their full negative range) the deck's tau is 0.000,
        // because the offsets move the whole Fog and Stratus slab (96 to 594 m) below the ground: LocalBaseTop moved the base AND the top by the root offset (-500 m for a humid place:
        // the lifting condensation level of near-saturated air is far under the body's mean), and nothing above the surface is left. A wet place is exactly where the rain deck is wanted.
        // Expected for the fix (a type whose base the offsets lower below the sea level is lifted as a whole until the base rests on it; cases above the floor unchanged):
        //   I2a  the wet place (root -500 m, top term -144 m = an updraft of 0, no variation, no ragged unit): without the floor the type's top lies under the ground (< 0 m); with it the base is 0 and
        //        the top is at least the camera's 264 m (the deck is 0 to 353 m, the camera inside it).
        //   I2b  the ray from the camera through that deck collects tau >= 0.92 (it is the case the game drew nothing for).
        //   I2c  offsets that leave the base above the floor give the same numbers with and without it.
        [TestMethod]
        public void AWetPlacesRootOffsetNoLongerMovesTheRainDeckUnderTheGround()
        {
            CloudLayerDef layer = CloudLookFixtures.Rain();
            var table = new LayerTypeTable(layer);
            HeightTuning tuning = new HeightTuning().ForLayer(layer.Kind);
            double thickness = layer.ThicknessM;
            double b, t;
            table.InterpolatedHeights(0.229, out b, out t);
            Assert.AreEqual(96.0, b * thickness, 2.0, "the blend's base");
            Assert.AreEqual(594.0, t * thickness, 2.0, "the blend's top");
            var wet = new LocalHeights { RootM = -500f, TopM = -144f, VariationM = 0f };
            double lb, lt, fb, ft;
            LayerGeometry.LocalBaseTop(b, t, 0.18, thickness, wet, 0.0, 0.0, tuning.MinThicknessM, out lb, out lt);
            Assert.IsTrue(lt * thickness < 0.0, "without the floor the whole deck lies under the ground: top " + (lt * thickness).ToString("0") + " m");
            LayerGeometry.LocalBaseTop(b, t, 0.18, thickness, wet, 0.0, 0.0, tuning.MinThicknessM, out fb, out ft, 0.0);
            Assert.AreEqual(0.0, fb, 1e-12, "the base rests on the ground");
            Assert.IsTrue(ft * thickness >= 264.0, "the top reaches the camera: " + (ft * thickness).ToString("0") + " m");
            Report("I2a wet place: without the floor " + (lb * thickness).ToString("0") + " to " + (lt * thickness).ToString("0") + " m, with it " + (fb * thickness).ToString("0") + " to " + (ft * thickness).ToString("0") + " m");
            double tau = TauWithHeights(layer, table, tuning, wet, 0.0);
            Report("I2b tau of the ray from 264 m at 18 degrees through the wet deck: " + tau.ToString("0.00"));
            Assert.IsTrue(tau >= 0.92, "the ray from inside is closed: " + tau);
            // I2c: positive and small offsets (base stays above the floor) are not touched.
            foreach (float root in new[] { 0f, 100f, 300f, -50f })
            {
                var h = new LocalHeights { RootM = root, TopM = 30f, VariationM = 20f };
                LayerGeometry.LocalBaseTop(b, t, 0.18, thickness, h, 0.3, 110.0, tuning.MinThicknessM, out lb, out lt);
                LayerGeometry.LocalBaseTop(b, t, 0.18, thickness, h, 0.3, 110.0, tuning.MinThicknessM, out fb, out ft, 0.0);
                Assert.AreEqual(lb, fb, 1e-12, "base, root " + root);
                Assert.AreEqual(lt, ft, 1e-12, "top, root " + root);
            }
            // The corner that emptied the deck in the first run: over the ground now (the deck lies below a camera at 264 m only where its thickness is the thinnest cloud's).
            var corner = new LocalHeights { RootM = -tuning.RootRangeM, TopM = -tuning.TopRangeM, VariationM = -tuning.VariationRangeM };
            LayerGeometry.LocalBaseTop(b, t, 0.18, thickness, corner, -1.0, tuning.RaggedM, tuning.MinThicknessM, out fb, out ft, 0.0);
            Assert.IsTrue(fb >= 0.0 && (ft - fb) * thickness >= tuning.MinThicknessM - 1e-6, "the all-negative corner keeps the thinnest cloud on the ground");
        }

        private static NoiseLayerParams ParamsOf(CloudLayerDef layer, int index)
        {
            double bx, by, bz;
            NoiseSeeds.BaseOffset(1u, index, out bx, out by, out bz);
            return new NoiseLayerParams
            {
                ScaleM = layer.EffectiveNoiseScaleM, Untile = true, Curl = true, OffsetX = bx, OffsetY = by, OffsetZ = bz, Seed = NoiseSeeds.LayerSeed(1u, index), SphereScale = 1.0,
                ReferenceRadiusM = 600000.0, DensityRemap = CloudLookTuning.DensityRemap
            };
        }

        [TestMethod]
        public void TheZenithTauOfTheRainDeckIsDeepAtTheHighPickAndNearZeroAtTheLowPick()
        {
            CloudLayerDef rain = CloudLookFixtures.Rain();
            var table = new LayerTypeTable(rain);
            NoiseVolumeSet set = NoiseVolumeSet.Generate(NoiseSizes.ForLevel(1), 1u);
            double minHigh = double.MaxValue, maxLow = 0.0;
            for (int site = 0; site < 8; site++)
            {
                NoiseLayerParams prm = ParamsOf(rain, 0);
                prm.OffsetX += site * 250000.0;
                prm.OffsetY += site * 130000.0;
                double high = CloudCoverOracle.ZenithTau(rain, table, set, prm, 264.0, R, CloudCoverOracle.Constant(0.859, 0.229), rain.EffectiveCoverage, CloudCoverOracle.March.Medium);
                double low = CloudCoverOracle.ZenithTau(rain, table, set, prm, 264.0, R, CloudCoverOracle.Constant(0.4056, 0.0), rain.EffectiveCoverage, CloudCoverOracle.March.Medium);
                minHigh = Math.Min(minHigh, high);
                maxLow = Math.Max(maxLow, low);
                var frame = new CloudCoverOracle.Frame { HeightM = 264.0, PitchDeg = 90.0, FovVDeg = 1.0, BodyRadiusM = R, Width = 1, Height = 1 };
                double same = CloudCoverOracle.Render(rain, table, set, prm, frame, CloudCoverOracle.Constant(0.859, 0.229), rain.EffectiveCoverage, CloudCoverOracle.March.Medium).Tau[0];
                Assert.AreEqual(same, high, 1e-9, "the zenith function is the one-pixel frame at pitch 90");
            }
            Report("I3 zenith tau of the rain deck at 264 m: high pick min over 8 sites " + minHigh.ToString("0.00") + ", low pick max " + maxLow.ToString("0.0000"));
            Assert.IsTrue(minHigh >= 4.6, "high pick: " + minHigh);
            Assert.IsTrue(maxLow < 0.05, "low pick: " + maxLow);
        }

        [TestMethod]
        public void TheAidPictureDecodesToTheOpticalDepthItWasDrawnFrom()
        {
            Color sky = new Color(0.337f, 0.541f, 0.754f, 1f);
            foreach (double tau in new[] { 0.001, 0.01, 0.1, 0.5, 0.92, 2.0, 4.6, 10.0, 25.0 })
            {
                double got = T09Readout.AidTauOf(T09Readout.AidPixel(tau, sky), sky);
                Assert.AreEqual(1.0, got / tau, 0.12, "tau " + tau + " decoded " + got);
            }
            Assert.AreEqual(0.0, T09Readout.AidTauOf(T09Readout.AidPixel(0.0, sky), sky), 0.0, "no cloud");
            Assert.AreEqual(T09Readout.AidTauSaturated, T09Readout.AidTauOf(T09Readout.AidPixel(500.0, sky), sky), 1e-6, "the heat map's top");
        }

        [TestMethod]
        public void TheAidReadoutCountsTheSkyShareAtOrAboveTheClosedDepths()
        {
            int w = 64, h = 64;
            var scene = new Color[w * h];
            var aid = new Color[w * h];
            Color sky = new Color(0.337f, 0.541f, 0.754f, 1f);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    scene[i] = sky;
                    // The left third has no cloud, the middle tau 2 (closed, not deep), the right a deck of tau 12 (closed and deep).
                    double tau = x < w / 3 ? 0.0 : x < 2 * w / 3 ? 2.0 : 12.0;
                    aid[i] = T09Readout.AidPixel(tau, sky);
                }
            // The horizon at 0.75 of the height keeps the sky region above the vessel's box (the box leaves out the middle of the picture).
            T09Readout.AidTauReading r = T09Readout.MeasureAidTau(aid, scene, w, h, 0.75, 1);
            Assert.IsTrue(r.Count > 500);
            Assert.AreEqual(2.0 / 3.0, r.ShareClosed, 0.06, "the two right thirds are closed");
            Assert.AreEqual(1.0 / 3.0, r.ShareDeep, 0.06, "the right third is deep");
            Assert.AreEqual(0.0, r.ShareSaturated, 0.001);
            Assert.IsTrue(r.TopCount > 0 && r.TopMedian >= 1.5, "the top rows' median " + r.TopMedian);
        }
    }
}
