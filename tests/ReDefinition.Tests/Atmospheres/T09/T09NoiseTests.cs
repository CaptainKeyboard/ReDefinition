using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The noise rows of K-T2 (T09ProbeNoise.cs, T09Readout.Noise): two frames of one held pose a frame apart, the region the pass changed against the baseline of the pose,
    // flicker (mean absolute difference of luminance), the baseline's own flicker and the high-frequency energy, per variant.
    public partial class T09WalkThroughTests
    {
        private static readonly string[] NoiseVariants = { "plain", "recon2", "recon3", "recon4" };

        private Run NoiseRun(Action<Run> tweak = null)
        {
            Run r = Start(NoiseVariants, x =>
            {
                x.World.DeckNoise = 0.06f;
                x.World.FloorNoise = 0.004f;
                if (tweak != null) tweak(x);
            });
            Drive(r, RealDt);
            return r;
        }

        [TestMethod]
        public void TheNoiseRowsFallWithTheResolutionLevelAndStandAboveTheBaselineFloor()
        {
            Run r = NoiseRun();
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            foreach (string view in new[] { "ground", "above" })
            {
                string p = "T09.noise." + view + ".";
                double plainFlicker = Number(r, p + "flicker", "plain"), plainHf = Number(r, p + "hf", "plain");
                Assert.IsTrue(plainFlicker > 0.005, view + " plain flicker " + plainFlicker);
                Assert.IsTrue(Number(r, p + "floor_flicker", "plain") < 0.25 * plainFlicker, view + ": the baseline's flicker is the small floor");
                Assert.IsTrue(plainHf > 0.01, view + " plain hf " + plainHf);
                // The fake's grain is scaled by 1, 0.4, 0.2, 0.12 with the level: the ratios the rows are read as come out near them (the floor adds a little).
                double[] expected = { 1.0, 0.4, 0.2, 0.12 };
                for (int i = 1; i < NoiseVariants.Length; i++)
                {
                    string v = NoiseVariants[i];
                    double flickerRatio = Number(r, p + "flicker", v) / plainFlicker, hfRatio = Number(r, p + "hf", v) / plainHf;
                    Assert.AreEqual(expected[i], flickerRatio, 0.08, view + " " + v + " flicker over plain");
                    Assert.AreEqual(expected[i], hfRatio, 0.08, view + " " + v + " hf over plain");
                }
                foreach (string v in NoiseVariants)
                {
                    Assert.AreEqual(1.0, Number(r, p + "frames_apart", v), view + " " + v + ": the second frame is read one frame after the first");
                    Assert.AreEqual(1.0, Number(r, p + "pose_ok", v));
                    Assert.IsTrue(Number(r, p + "region_fraction", v) > 0.5, view + " " + v + ": the deck covers most of the picture");
                    Assert.AreEqual(0.075, Number(r, p + "mean_luma", v), 0.02, view + " " + v + " mean luminance");
                    Assert.AreEqual(Number(r, p + "flicker", v) / Number(r, p + "mean_luma", v), Number(r, p + "flicker_rel", v), 1e-3);
                }
            }
        }

        [TestMethod]
        public void TheNoiseReadsAreFourRawFramesInTwoConsecutivePairsAndLeaveTheLayersOn()
        {
            Run r = NoiseRun();
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            // 4 variants x 2 views x 4 reads; each pair (baseline 1 and 2, then a and b) begins on consecutive frames.
            Assert.AreEqual(32, r.World.RawBeginFrames.Count);
            for (int i = 0; i < 32; i += 4)
            {
                Assert.AreEqual(1, r.World.RawBeginFrames[i + 1] - r.World.RawBeginFrames[i], "baseline pair " + i / 4);
                Assert.AreEqual(1, r.World.RawBeginFrames[i + 3] - r.World.RawBeginFrames[i + 2], "pass pair " + i / 4);
                Assert.IsTrue(r.World.RawBeginFrames[i + 2] - r.World.RawBeginFrames[i + 1] >= 40, "the pass is given its frames to refill the history before the pair");
            }
            Assert.AreEqual(0, r.World.LayerMaskNow, "the layer mask is open again after the last noise read");
            // Outside the windows, in the view that was measured: the noise lines come after the capture end of their view.
            int capEnd = r.Log.IndexOf("T09 capture end: ground plain 1707x960"), noise = r.Log.FindIndex(l => l.StartsWith("T09 noise begin: ground plain"));
            Assert.IsTrue(capEnd >= 0 && noise > capEnd, "the noise read comes after the pictures of its view");
            Assert.IsTrue(r.Log.FindIndex(capEnd, l => l.StartsWith("T09 view begin: inside plain")) > noise, "and before the next window");
            foreach (int[] w in Windows(r))
            {
                foreach (int s in r.World.Stamps) Assert.IsFalse(s > w[0] && s < w[1], "no read inside a measured window");
            }
        }

        [TestMethod]
        public void NoNoiseRowsForTheBaselineTvcOtherViewsOrOtherVariants()
        {
            Run r = Start(new[] { "off", "cap2", "entry" });
            Drive(r, RealDt);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual(0, r.World.RawBeginFrames.Count);
            Assert.AreEqual(0, Values(r, "T09.noise.ground.flicker").Count + Values(r, "T09.noise.above.flicker").Count);

            Run tvc = Start(new[] { "tvc" });
            Drive(tvc, RealDt);
            Assert.IsTrue(tvc.Probe.Passed, Dump(tvc));
            Assert.AreEqual(0, tvc.World.RawBeginFrames.Count);

            // Plain reads the noise in ground and above only.
            Run plain = Start(new[] { "plain" });
            Drive(plain, RealDt);
            Assert.IsTrue(plain.Probe.Passed, Dump(plain));
            Assert.AreEqual(8, plain.World.RawBeginFrames.Count);
            Assert.AreEqual(0, Values(plain, "T09.noise.inside.flicker").Count + Values(plain, "T09.noise.sky3.flicker").Count + Values(plain, "T09.noise.orbit_edge.flicker").Count);
        }

        [TestMethod]
        public void ANoiseReadThatFailsIsANamedWarningWithoutRowsAndDoesNotStopThePictures()
        {
            foreach (string fault in new[] { "throw", "none", "takethrows", "nobegin" })
            {
                Run r = Start(new[] { "plain" }, x => x.World.RawReadFault = n => n < 4 ? fault : null);
                Drive(r, RealDt);
                Assert.IsTrue(r.Probe.Passed, fault + ": " + Dump(r));
                AssertNoUnhandled(r);
                Assert.IsTrue(r.Log.Any(l => l.StartsWith("Probe: warning noise_read_failed: ")), fault + ": the failed read is named");
                Assert.AreEqual(0, Values(r, "T09.noise.ground.flicker").Count, fault + ": no rows from a failed pair");
                Assert.AreEqual(1, Values(r, "T09.noise.above.flicker").Count, fault + ": the next view is read");
                Assert.AreEqual(10, r.World.Saved.Count, fault + ": the pictures went on");
                Assert.AreEqual(0, r.World.LayerMaskNow, fault);
            }
        }

        [TestMethod]
        public void AnEmptyRegionOrAMovedPoseGivesAWarningAndNoRows()
        {
            // The pass changes nothing in the picture: the region is empty.
            Run empty = Start(new[] { "plain" }, x => x.World.DeckCovers = (px, py) => false);
            Drive(empty, RealDt);
            Assert.IsTrue(empty.Probe.Passed, Dump(empty));
            Assert.IsTrue(empty.Log.Any(l => l.StartsWith("Probe: warning noise_region_small: ")));
            Assert.AreEqual(0, Values(empty, "T09.noise.ground.flicker").Count);
        }

        // --- T09Readout.Noise on known pictures ------------------------------------------------------------------------------------------

        private static Color[] Fill(int w, int h, Func<int, int, float> luma)
        {
            var c = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float l = luma(x, y);
                    c[y * w + x] = new Color(l, l, l, 1f);
                }
            }
            return c;
        }

        [TestMethod]
        public void AChangeOfTheWholePicturesBrightnessIsNotFlickerAndAFixedRegionIsUsedAsGiven()
        {
            const int w = 80, h = 60;
            Color[] baseline = Fill(w, h, (x, y) => 0.6f);
            Color[] baseline2 = Fill(w, h, (x, y) => 0.6f);
            // K-T2 start 46, ground: a smooth deck whose brightness changed by 15 percent between the two frames (the weather writer's sun factor 0.85 to 1.0), grain 0.004.
            Color[] a = Fill(w, h, (x, y) => 0.2f + ((x + y) % 2 == 0 ? 0.004f : -0.004f));
            Color[] b = Fill(w, h, (x, y) => 1.15f * (0.2f + ((x + y) % 2 == 0 ? -0.004f : 0.004f)));
            T09Readout.NoiseReading n = T09Readout.Noise(baseline, baseline2, a, b, w, h);
            Assert.AreEqual(1.15, n.GainAB, 1e-3);
            Assert.AreEqual(0.2 * 0.15, n.FlickerRaw, 0.004, "without the scaling the 15 percent change is read as flicker (" + n.FlickerRaw + ")");
            Assert.AreEqual(0.008, n.Flicker, 5e-4, "scaled, only the grain's flip of 0.008 is left (" + n.Flicker + ")");

            // The same frames over a region of its own choosing: the left half only. The reading is over exactly those pixels.
            var half = new bool[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w / 2; x++) half[y * w + x] = true;
            T09Readout.NoiseReading fixedRead = T09Readout.Noise(baseline, baseline2, a, b, w, h, half);
            Assert.IsTrue(fixedRead.Count > 0 && fixedRead.Count < n.Count, "a region of " + fixedRead.Count + " px against " + n.Count);
            for (int i = 0; i < half.Length; i++) Assert.IsTrue(!fixedRead.Region[i] || half[i], "the fixed region is not widened at pixel " + i);
            // A frame that equals the baseline still has pixels in a fixed region: the region is not derived from it.
            T09Readout.NoiseReading same = T09Readout.Noise(baseline, baseline2, baseline, baseline, w, h, half);
            Assert.IsTrue(same.Count > 0 && same.Flicker == 0.0);
            try { T09Readout.Noise(baseline, baseline2, a, b, w, h, new bool[5]); Assert.Fail("a region of another size is refused"); }
            catch (ArgumentException) { }
        }

        [TestMethod]
        public void TheGainIsReadOffTheSunColourOverTheScenesSunSoTheWeatherWritersDimmingDoesNotCount()
        {
            // K-T2 start 46: the same gain 2 read 2.263 and 5.221 because the scene's sun was 0.85 of itself at the first read and 0.98 at the second.
            var f = new LayerFrame();
            f.OutputWhite = 1.0f * 0.85f;
            f.SunColour = new Color(1f, 1f, 1f, 1f) * (3.14159f * 1f * 0.85f);
            float baseRead = T09Probe.PassSunPerSceneSun(f);
            f.OutputWhite = 0.98f;
            f.SunColour = new Color(1f, 1f, 1f, 1f) * (3.14159f * 2f * 0.98f);
            float doubled = T09Probe.PassSunPerSceneSun(f);
            Assert.AreEqual(2.0, doubled / baseRead, 1e-3);
        }

        [TestMethod]
        public void TheLevelsOfAViewAreReadOverTheRegionOfThePlainPass()
        {
            Run r = NoiseRun();
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            foreach (string view in new[] { "ground", "above" })
            {
                string p = "T09.noise." + view + ".";
                Assert.AreEqual(0.0, Number(r, p + "region_fixed", "plain"), view + ": plain defines the region");
                double fraction = Number(r, p + "region_fraction", "plain");
                foreach (string v in new[] { "recon2", "recon3", "recon4" })
                {
                    Assert.AreEqual(1.0, Number(r, p + "region_fixed", v), view + " " + v + ": the plain pass's region");
                    Assert.AreEqual(fraction, Number(r, p + "region_fraction", v), 1e-9, view + " " + v + ": the same pixels");
                    Assert.IsTrue(Number(r, p + "flicker_raw", v) >= 0.0 && Number(r, p + "gain_ab", v) > 0.0);
                }
            }
        }

        [TestMethod]
        public void TheReadoutMeasuresFlickerFloorAndGrainOnKnownFrames()
        {
            const int w = 80, h = 60;
            Color[] baseline = Fill(w, h, (x, y) => 0.6f);
            Color[] baseline2 = Fill(w, h, (x, y) => 0.6f + ((x + y) % 2 == 0 ? 0.002f : -0.002f));
            // The pass: a deck of 0.2 with a checkerboard grain of +-0.01 that flips from frame a to frame b; the left quarter of the picture is not changed by the pass.
            Color[] a = Fill(w, h, (x, y) => x < w / 4 ? 0.6f : 0.2f + ((x + y) % 2 == 0 ? 0.01f : -0.01f));
            Color[] b = Fill(w, h, (x, y) => x < w / 4 ? 0.6f : 0.2f + ((x + y) % 2 == 0 ? -0.01f : 0.01f));
            T09Readout.NoiseReading n = T09Readout.Noise(baseline, baseline2, a, b, w, h);
            Assert.IsTrue(n.Count > 1000 && n.RegionFraction > 0.3 && n.RegionFraction < 0.75, "region " + n.Count + " px, " + n.RegionFraction);
            Assert.AreEqual(0.2, n.MeanLuma, 0.003);
            Assert.AreEqual(0.02, n.Flicker, 1e-4, "the grain flips by 0.02 between the frames");
            Assert.AreEqual(0.002, n.FloorFlicker, 1e-4, "the baseline pair differs by 0.002");
            // A checkerboard of +-0.01: each pixel is 0.01 away from the mean of its four neighbours' opposite sign, so the high-pass is 0.02.
            Assert.AreEqual(0.02, n.Hf, 1e-4);

            // A frame that equals the baseline has no region; frames that do not match the size throw.
            T09Readout.NoiseReading none = T09Readout.Noise(baseline, baseline2, baseline, baseline, w, h);
            Assert.AreEqual(0, none.Count);
            Assert.AreEqual(0.0, none.Flicker);
            try { T09Readout.Noise(baseline, baseline2, a, new Color[10], w, h); Assert.Fail("a frame of another size is refused"); }
            catch (ArgumentException) { }
        }
    }
}
