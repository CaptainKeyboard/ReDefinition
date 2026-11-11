using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests
{
    // The pure parts of the volume pass (package C21): the step functions, the tier table, the ray interval, the sub-grid rule and the
    // window resampler. The shader mirrors ClipRay and the step growth; the weather player measures the picture.
    [TestClass]
    public class VolumeMathTests
    {
        [TestMethod]
        public void TheSlabStepsSnapToTheThreeQualityLevels()
        {
            Assert.AreEqual(16, VolumeMath.SlabSteps(1));
            Assert.AreEqual(16, VolumeMath.SlabSteps(16));
            Assert.AreEqual(16, VolumeMath.SlabSteps(24));
            Assert.AreEqual(32, VolumeMath.SlabSteps(25));
            Assert.AreEqual(32, VolumeMath.SlabSteps(32));
            Assert.AreEqual(32, VolumeMath.SlabSteps(48));
            Assert.AreEqual(64, VolumeMath.SlabSteps(49));
            Assert.AreEqual(64, VolumeMath.SlabSteps(500));
        }

        [TestMethod]
        public void TheCurtainStepsFollowTheGateShareInThreeBandsAndAreZeroWithoutIt()
        {
            Assert.AreEqual(0, VolumeMath.CurtainSteps(0f));
            Assert.AreEqual(0, VolumeMath.CurtainSteps(-1f));
            Assert.AreEqual(0, VolumeMath.CurtainSteps(float.NaN));
            Assert.AreEqual(4, VolumeMath.CurtainSteps(0.001f));
            Assert.AreEqual(4, VolumeMath.CurtainSteps(0.299f));
            Assert.AreEqual(8, VolumeMath.CurtainSteps(0.3f));
            Assert.AreEqual(8, VolumeMath.CurtainSteps(0.699f));
            Assert.AreEqual(16, VolumeMath.CurtainSteps(0.7f));
            Assert.AreEqual(16, VolumeMath.CurtainSteps(1f));
        }

        [TestMethod]
        public void TheCurtainStepsAreSeparateFromTheSlabQuality()
        {
            foreach (int slab in new[] { 16, 32, 64 })
                Assert.AreEqual(16, VolumeMath.CurtainSteps(1f), "the curtain does not take the slab's " + slab);
        }

        [TestMethod]
        public void TheResolutionSnapsToOneHalfOrAQuarterAndDefaultsToFull()
        {
            Assert.AreEqual(1f, VolumeMath.SnapResolution(1f));
            Assert.AreEqual(1f, VolumeMath.SnapResolution(0.8f));
            Assert.AreEqual(0.5f, VolumeMath.SnapResolution(0.5f));
            Assert.AreEqual(0.5f, VolumeMath.SnapResolution(0.4f));
            Assert.AreEqual(0.25f, VolumeMath.SnapResolution(0.25f));
            Assert.AreEqual(0.25f, VolumeMath.SnapResolution(0.01f));
            Assert.AreEqual(1f, VolumeMath.SnapResolution(0f));
            Assert.AreEqual(1f, VolumeMath.SnapResolution(-3f));
            int w, h;
            VolumeMath.TargetSize(1707, 960, 0.5f, out w, out h);
            Assert.AreEqual(854, w);
            Assert.AreEqual(480, h);
            VolumeMath.TargetSize(10, 10, 0.25f, out w, out h);
            Assert.AreEqual(16, w, "never smaller than 16");
        }

        [TestMethod]
        public void TheTierTableGrowsWithTheTierAndKeepsFullResolution()
        {
            VolumeTier low = VolumeMath.ForTier((int)QualityTier.Low), medium = VolumeMath.ForTier((int)QualityTier.Medium),
                high = VolumeMath.ForTier((int)QualityTier.High), ultra = VolumeMath.ForTier((int)QualityTier.Ultra),
                max = VolumeMath.ForTier((int)QualityTier.Max), auto = VolumeMath.ForTier((int)QualityTier.Auto);
            Assert.AreEqual(16, low.Steps);
            Assert.AreEqual(32, medium.Steps);
            Assert.AreEqual(32, high.Steps);
            Assert.AreEqual(64, ultra.Steps);
            Assert.AreEqual(64, max.Steps);
            Assert.AreEqual(medium.Steps, auto.Steps, "Auto is read as Medium");
            Assert.AreEqual(medium.WindowXz, auto.WindowXz);
            VolumeTier[] tiers = { low, medium, high, ultra, max };
            for (int i = 0; i < tiers.Length; i++)
            {
                Assert.AreEqual(1f, tiers[i].Resolution, "volumeResolution is its own setting, 1.0 until the half-resolution gate passes");
                Assert.IsTrue(tiers[i].WindowXz <= VolumeMath.MaxWindowCells && tiers[i].WindowY <= VolumeMath.MaxWindowCells);
                Assert.IsTrue(tiers[i].SpreadFrames >= 1 && tiers[i].RebuildFrames >= tiers[i].SpreadFrames, "the rebuild is spread inside its period");
                if (i > 0)
                {
                    Assert.IsTrue(tiers[i].WindowXz >= tiers[i - 1].WindowXz, "window cells do not shrink");
                    Assert.IsTrue(tiers[i].Steps >= tiers[i - 1].Steps, "steps do not shrink");
                    Assert.IsTrue(tiers[i].HalfWidthM >= tiers[i - 1].HalfWidthM);
                }
            }
        }

        [TestMethod]
        public void TheRayIntervalClipsToTheCylinderAndTheHeights()
        {
            float t0, t1;
            // From the centre, straight up: the interval is the height range from the start.
            Assert.IsTrue(VolumeMath.ClipRay(0, 0, 0, 0, 1, 0, 1000f, -50f, 4000f, 0f, 1e6f, out t0, out t1));
            Assert.AreEqual(0f, t0, 1e-3f);
            Assert.AreEqual(4000f, t1, 1e-2f);
            // Horizontally through the cylinder from outside: enters at 1000 - 500, leaves at 1000 + 1000 (starting 1500 m away).
            Assert.IsTrue(VolumeMath.ClipRay(-1500f, 100f, 0, 1, 0, 0, 1000f, -50f, 4000f, 0f, 1e6f, out t0, out t1));
            Assert.AreEqual(500f, t0, 1e-2f);
            Assert.AreEqual(2500f, t1, 1e-2f);
            // A ray that misses the cylinder.
            Assert.IsFalse(VolumeMath.ClipRay(-1500f, 100f, 1200f, 1, 0, 0, 1000f, -50f, 4000f, 0f, 1e6f, out t0, out t1));
            // Below the window and looking down: no interval.
            Assert.IsFalse(VolumeMath.ClipRay(0, -200f, 0, 0, -1, 0, 1000f, -50f, 4000f, 0f, 1e6f, out t0, out t1));
            // Parallel to the floor and above the ceiling: none.
            Assert.IsFalse(VolumeMath.ClipRay(0, 5000f, 0, 1, 0, 0, 1000f, -50f, 4000f, 0f, 1e6f, out t0, out t1));
            // The start limit and the end limit shorten the interval.
            Assert.IsTrue(VolumeMath.ClipRay(-1500f, 100f, 0, 1, 0, 0, 1000f, -50f, 4000f, 800f, 1200f, out t0, out t1));
            Assert.AreEqual(800f, t0, 1e-3f);
            Assert.AreEqual(1200f, t1, 1e-3f);
            // A start limit past the exit leaves nothing.
            Assert.IsFalse(VolumeMath.ClipRay(-1500f, 100f, 0, 1, 0, 0, 1000f, -50f, 4000f, 3000f, 5000f, out t0, out t1));
        }

        [TestMethod]
        public void TheStepGrowthSumsToTheRayLength()
        {
            foreach (float g in new[] { 1f, 1.02f, 1.03f, 1.08f })
            {
                foreach (int n in new[] { 16, 32, 64 })
                {
                    float dt0 = VolumeMath.FirstStep(10000f, n, g);
                    double sum = 0, dt = dt0;
                    for (int k = 0; k < n; k++)
                    {
                        sum += dt;
                        dt *= g;
                    }
                    Assert.AreEqual(10000.0, sum, 10000.0 * 1e-3, "g " + g + " n " + n);
                }
            }
            Assert.IsTrue(VolumeMath.FirstStep(10000f, 32, 1.03f) < VolumeMath.FirstStep(10000f, 32, 1f), "the growing step starts finer");
        }

        [TestMethod]
        public void TheCurvedHeightAddsTheGroundsFall()
        {
            Assert.AreEqual(100f, VolumeMath.CurvedHeight(100f, 0f, 1e-6f), 1e-4f);
            Assert.AreEqual(100f + 25000f * 25000f * 1e-6f, VolumeMath.CurvedHeight(100f, 25000f * 25000f, 1e-6f), 1e-2f);
        }

        [TestMethod]
        public void TheJitterKeyedOffsetRepeatsWithTheJitterCycleAndDiffersBetweenIndices()
        {
            Assert.AreEqual(VolumeMath.JitterKeyedOffset(3), VolumeMath.JitterKeyedOffset(3 + 64));
            Assert.AreNotEqual(VolumeMath.JitterKeyedOffset(3), VolumeMath.JitterKeyedOffset(4));
            Assert.AreEqual(0f, VolumeMath.JitterKeyedOffset(0));
        }

        [TestMethod]
        public void EveryKnobHasItsDefaultInsideItsRangeAndTheLookReadsThem()
        {
            var seen = new System.Collections.Generic.HashSet<string>();
            foreach (VolumeKnob knob in VolumeLook.Knobs)
            {
                Assert.IsTrue(seen.Add(knob.Key), "unique " + knob.Key);
                Assert.IsTrue(knob.Key.StartsWith("volume."), knob.Key);
                Assert.IsTrue(knob.Default >= knob.Min && knob.Default <= knob.Max, knob.Key + " default inside its range");
            }
            var asked = new System.Collections.Generic.HashSet<string>();
            VolumeLook look = VolumeLook.Read((key, def) => { asked.Add(key); return def; });
            foreach (VolumeKnob knob in VolumeLook.Knobs) Assert.IsTrue(asked.Contains(knob.Key), "Read asks for " + knob.Key);
            Assert.AreEqual(VolumeLook.Knobs.Length, asked.Count, "no key outside the list");
            Assert.AreEqual(1.3f, look.SunShare);
            VolumeLook changed = VolumeLook.Read((key, def) => key == "volume.erosion" ? 0.2f : def);
            Assert.AreEqual(0.2f, changed.Erosion);
        }
    }

    [TestClass]
    public class VolumeSubGridTests
    {
        [TestMethod]
        public void TheRuleIsDeterministicAndKeepsTheCoreSizes()
        {
            var a = new VolumeCore[VolumeSubGrid.MaxCores];
            var b = new VolumeCore[VolumeSubGrid.MaxCores];
            for (long cell = 0; cell < 400; cell++)
            {
                double up = 2.0 + cell % 30, depth = 3000.0 + 120.0 * (cell % 90);
                int na = VolumeSubGrid.Cores(cell, 123456.0, 7, up, depth, 29.5, 29.5, a);
                int nb = VolumeSubGrid.Cores(cell, 123456.0, 7, up, depth, 29.5, 29.5, b);
                Assert.AreEqual(na, nb);
                Assert.IsTrue(na >= 1 && na <= VolumeSubGrid.MaxCores);
                double area = 0;
                for (int k = 0; k < na; k++)
                {
                    Assert.AreEqual(a[k].RadiusKm, b[k].RadiusKm);
                    Assert.AreEqual(a[k].OffsetEastKm, b[k].OffsetEastKm);
                    Assert.AreEqual(a[k].OffsetNorthKm, b[k].OffsetNorthKm);
                    Assert.AreEqual(a[k].UpdraftMs, b[k].UpdraftMs);
                    Assert.IsTrue(a[k].RadiusKm >= VolumeSubGrid.MinRadiusKm && a[k].RadiusKm <= VolumeSubGrid.MaxRadiusKm);
                    Assert.IsTrue(Math.Abs(a[k].OffsetEastKm) <= 14.75 && Math.Abs(a[k].OffsetNorthKm) <= 14.75, "inside the cell");
                    area += Math.PI * a[k].RadiusKm * a[k].RadiusKm;
                }
                // The nominal radius is bounded by the area rule; the 15 % spread of one core's radius may add a little.
                Assert.IsTrue(area <= VolumeSubGrid.MaxAreaFraction * 29.5 * 29.5 * 1.35 || na == 1, "cell " + cell + " covers " + area);
            }
        }

        [TestMethod]
        public void NoUpdraftOrNoCloudGivesNoCoreAndATimeBucketRedrawsTheCores()
        {
            var cores = new VolumeCore[VolumeSubGrid.MaxCores];
            Assert.AreEqual(0, VolumeSubGrid.Cores(1, 0.0, 7, 0.5, 5000.0, 29.5, 29.5, cores));
            Assert.AreEqual(0, VolumeSubGrid.Cores(1, 0.0, 7, 12.0, 0.0, 29.5, 29.5, cores));
            Assert.AreEqual(0, VolumeSubGrid.Cores(1, 0.0, 7, double.NaN, 5000.0, 29.5, 29.5, cores));
            Assert.AreEqual(0, VolumeSubGrid.Cores(1, 0.0, 7, 12.0, 5000.0, 0.0, 29.5, cores));
            var early = new VolumeCore[VolumeSubGrid.MaxCores];
            var late = new VolumeCore[VolumeSubGrid.MaxCores];
            VolumeSubGrid.Cores(5, 100.0, 7, 12.0, 8000.0, 29.5, 29.5, early);
            VolumeSubGrid.Cores(5, 100.0 + 5 * VolumeSubGrid.LifetimeS, 7, 12.0, 8000.0, 29.5, 29.5, late);
            Assert.AreNotEqual(early[0].OffsetEastKm, late[0].OffsetEastKm, "the slot is drawn again in a later bucket");
            var other = new VolumeCore[VolumeSubGrid.MaxCores];
            VolumeSubGrid.Cores(5, 100.0, 8, 12.0, 8000.0, 29.5, 29.5, other);
            Assert.AreNotEqual(early[0].OffsetEastKm, other[0].OffsetEastKm, "another seed gives other cores");
        }

        [TestMethod]
        public void TheSizeDistributionOverTheFieldsRangeStaysBetweenFiveAndTwentyFiveKilometres()
        {
            var cores = new VolumeCore[VolumeSubGrid.MaxCores];
            double min = double.MaxValue, max = 0, sum = 0;
            int count = 0;
            for (long cell = 0; cell < 2000; cell++)
            {
                double up = 2.0 + (cell * 7) % 29, depth = 2000.0 + (cell * 13) % 12000;
                int n = VolumeSubGrid.Cores(cell, cell * 17.0, 99, up, depth, 29.5, 29.5, cores);
                for (int k = 0; k < n; k++)
                {
                    min = Math.Min(min, cores[k].RadiusKm);
                    max = Math.Max(max, cores[k].RadiusKm);
                    sum += cores[k].RadiusKm;
                    count++;
                }
            }
            Assert.IsTrue(count > 1000);
            Assert.IsTrue(min >= 5.0 && max <= 25.0, "radii " + min + " to " + max);
            Console.WriteLine("sub-grid cores: " + count + ", radius " + min.ToString("0.0") + " to " + max.ToString("0.0") + " km, mean " + (sum / count).ToString("0.0") + " km");
        }
    }

    [TestClass]
    public class VolumeWindowResamplerTests
    {
        // A coarse window of 5 x 5 cells of 29.5 km and 14 levels of 750 m with one deep cell in the middle column and its anvil.
        private static WeatherWindow Storm(float updraft)
        {
            var w = new WeatherWindow();
            w.Resize(5, 5, 14);
            w.SpacingHorizontalM = 29500f;
            w.SpacingVerticalM = 750f;
            w.OriginAltM = 375f;
            for (int iz = 0; iz < 14; iz++)
            {
                float alt = 375f + iz * 750f;
                int i = w.Index(2, 2, iz);
                if (alt >= 1100f && alt <= 9000f)
                {
                    w.CloudWater[i] = 1.2f;
                    w.Updraft[i] = updraft;
                }
                if (alt >= 7800f && alt <= 9000f) for (int d = -1; d <= 1; d++) w.CloudWater[w.Index(2 + d, 2, iz)] = Math.Max(w.CloudWater[w.Index(2 + d, 2, iz)], 0.4f);
                for (int k = 0; k < 25; k++) w.Temperature[iz * 25 + k] = 280f - alt * 0.0065f;
            }
            return w;
        }

        private static VolumeWindowResampler Built(WeatherWindow w, int across = 48, int up = 24, double ut = 1000.0)
        {
            var r = new VolumeWindowResampler(across, up);
            Assert.IsTrue(r.Begin(w, ut, 5UL, 50000f, 11250f, 0f, 0f, 0f, 1f, VolumeLook.Read((k, d) => d)));
            r.Complete();
            return r;
        }

        [TestMethod]
        public void TheSameFieldGivesTheSameVoxelsAndAnotherSeedOtherCores()
        {
            WeatherWindow w = Storm(22f);
            VolumeWindowResampler a = Built(w), b = Built(w);
            CollectionAssert.AreEqual(a.Data, b.Data);
            Assert.IsTrue(a.CoreCount >= 1);
            var c = new VolumeWindowResampler(48, 24);
            c.Begin(w, 1000.0, 6UL, 50000f, 11250f, 0f, 0f, 0f, 1f, VolumeLook.Read((k, d) => d));
            c.Complete();
            CollectionAssert.AreNotEqual(a.Data, c.Data);
        }

        [TestMethod]
        public void TheCoreIsATowerWithAnAnvilAndNothingOutsideTheCloud()
        {
            VolumeWindowResampler r = Built(Storm(22f));
            // The core's centre in the window frame.
            float east, north, radius, baseAlt, depth, updraft;
            r.GetCore(0, out east, out north, out radius, out baseAlt, out depth, out updraft);
            Assert.IsTrue(radius >= 5000f && radius <= 25000f);
            float mid = baseAlt + 0.4f * depth;
            float inside = r.SampleCloudWater(east, north, mid);
            Assert.IsTrue(inside > 0.4f, "cloud water in the tower: " + inside);
            Assert.AreEqual(0f, r.SampleCloudWater(east + 3f * radius + 30000f, north, mid), 1e-6f, "clear air far from the core");
            Assert.IsTrue(r.SampleCloudWater(east, north, 300f) < 0.1f, "next to no cloud below the base (the coarse levels smear one voxel)");
            Assert.AreEqual(0f, r.SampleCloudWater(east, north, 10900f), 1e-6f, "no cloud above the top");
            // The anvil: at the height of the anvil the cloud reaches farther from the axis than the tower does at mid height.
            float anvilH = baseAlt + 0.93f * depth;
            float reachMid = Reach(r, east, north, mid), reachAnvil = Reach(r, east, north, anvilH);
            Assert.IsTrue(reachAnvil > reachMid * 1.2f, "anvil " + reachAnvil + " against tower " + reachMid);
        }

        private static float Reach(VolumeWindowResampler r, float east, float north, float h)
        {
            float last = 0f;
            for (float d = 0f; d < 40000f; d += 500f)
                if (r.SampleCloudWater(east + d, north, h) > 0.05f) last = d;
            return last;
        }

        [TestMethod]
        public void ASpreadRebuildEqualsACompleteOneAndSmoothingMovesTowardsTheTarget()
        {
            WeatherWindow w = Storm(22f);
            VolumeWindowResampler full = Built(w);
            var spread = new VolumeWindowResampler(48, 24);
            spread.Begin(w, 1000.0, 5UL, 50000f, 11250f, 0f, 0f, 0f, 1f, VolumeLook.Read((k, d) => d));
            int calls = 0;
            while (!spread.Step(6)) calls++;
            Assert.AreEqual(48 / 6 - 1, calls, "eight steps of six slices");
            CollectionAssert.AreEqual(full.Data, spread.Data);
            Assert.AreEqual(1, spread.Builds);

            // A second build of an empty window with 50 % smoothing halves the cloud water.
            WeatherWindow empty = new WeatherWindow();
            empty.Resize(5, 5, 14);
            empty.SpacingHorizontalM = 29500f;
            empty.SpacingVerticalM = 750f;
            empty.OriginAltM = 375f;
            float east, north, radius, baseAlt, depth, updraft;
            spread.GetCore(0, out east, out north, out radius, out baseAlt, out depth, out updraft);
            float before = spread.SampleCloudWater(east, north, baseAlt + 0.4f * depth);
            spread.Begin(empty, 1000.0, 5UL, 50000f, 11250f, 0f, 0f, 0f, 0.5f, VolumeLook.Read((k, d) => d));
            spread.Complete();
            float after = spread.SampleCloudWater(east, north, baseAlt + 0.4f * depth);
            Assert.AreEqual(before * 0.5f, after, before * 0.05f);
            Assert.AreEqual(2, spread.Builds);
        }

        [TestMethod]
        public void AWeakUpdraftMakesNoCoresAndTheCoarseCloudIsKept()
        {
            VolumeWindowResampler r = Built(Storm(0.5f));
            Assert.AreEqual(0, r.CoreCount);
            Assert.IsTrue(r.SampleCloudWater(0f, 0f, 4000f) > 0.1f, "the coarse column is still drawn");
        }

        [TestMethod]
        public void StepAndSamplingAllocateNothingOnceBuilt()
        {
            WeatherWindow w = Storm(22f);
            var r = new VolumeWindowResampler(32, 16);
            VolumeLook look = VolumeLook.Read((k, d) => d);
            // Warm up: the first rebuild may touch the jit.
            r.Begin(w, 1.0, 5UL, 50000f, 11250f, 0f, 0f, 0f, 1f, look);
            r.Complete();
            long before = GC.GetTotalMemory(true);
            float sink = 0f;
            for (int i = 0; i < 50; i++)
            {
                r.Begin(w, 1.0 + i * 600.0, 5UL, 50000f, 11250f, 0f, 0f, 0f, 0.5f, look);
                while (!r.Step(4)) { }
                sink += r.SampleCloudWater(1000f * i, 0f, 3000f) + r.SampleCoverage(0f, 0f);
            }
            long after = GC.GetTotalMemory(false);
            Assert.IsTrue(after - before < 16 * 1024, "grew by " + (after - before) + " bytes (sink " + sink + ")");
        }

        [TestMethod]
        public void TheDeckMapTakesCoverageAndTypeFromTheBodysMapsAndTheUniformFill()
        {
            var r = new VolumeWindowResampler(16, 8);
            WeatherWindow w = Storm(0f);
            r.Begin(w, 0.0, 1UL, 50000f, 11250f, 0f, 0f, 0f, 1f, VolumeLook.Read((k, d) => d));
            r.Complete();
            r.FillDeckMapUniform(0.5f, 1f);
            Assert.AreEqual(0.5f, r.SampleCoverage(0f, 0f), 0.01f);
            Assert.AreEqual(0f, r.SampleCoverage(1e6f, 0f), "outside the window");
            var coverage = new WeatherMap();
            coverage.Resize(8, 4);
            for (int i = 0; i < coverage.Values.Length; i++) coverage.Values[i] = 0.8f;
            var type = new WeatherMap();
            type.Resize(8, 4);
            r.FillDeckMap(coverage, type, 10.0, 20.0, 600000.0);
            Assert.AreEqual(0.8f, r.SampleCoverage(0f, 0f), 0.01f);
            Assert.AreEqual(0, r.DeckMap[1], "type 0");
        }

        [TestMethod]
        public void TheCoreProfileWidensIntoAnAnvilAndFadesAtBaseAndTop()
        {
            Assert.AreEqual(0f, VolumeWindowResampler.CoreRadiusShare(-0.1f, 0.18f, 1.8f));
            Assert.AreEqual(0f, VolumeWindowResampler.CoreRadiusShare(1.1f, 0.18f, 1.8f));
            float mid = VolumeWindowResampler.CoreRadiusShare(0.5f, 0.18f, 1.8f);
            float top = VolumeWindowResampler.CoreRadiusShare(1f, 0.18f, 1.8f);
            Assert.AreEqual(1.8f, top, 1e-4f);
            Assert.IsTrue(top > mid);
            Assert.AreEqual(0f, VolumeWindowResampler.CoreVertical(0f, 0.18f), 1e-5f);
            Assert.AreEqual(0f, VolumeWindowResampler.CoreVertical(1f, 0.18f), 1e-5f);
            Assert.IsTrue(VolumeWindowResampler.CoreVertical(0.5f, 0.18f) > 0.99f);
        }
    }
}
