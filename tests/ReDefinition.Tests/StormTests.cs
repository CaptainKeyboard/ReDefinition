using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // The storm schedule, the numbers that follow from an intensity, and the storm settings.
    [TestClass]
    public class StormTests
    {
        private const double Year = 9203545;
        private const double Line = 42;
        private const double Slot = StormModel.SlotHours * StormModel.HourSeconds;

        private static double At(double ut, double lat, int seed = 1, double frequency = 1, double forced = -1)
        {
            return StormModel.Intensity(ut, lat, Year, forced, Line, seed, frequency);
        }

        // The slot starts of a span in which the latitude has a storm somewhere in the slot (sampled every ten minutes).
        private static int StormSlots(double from, double to, double lat, int seed = 1, double frequency = 1, double forced = -1)
        {
            int count = 0;
            for (double slot = Math.Floor(from / Slot); slot * Slot < to; slot++)
            {
                for (double t = slot * Slot; t < (slot + 1) * Slot; t += 600)
                {
                    if (At(t, lat, seed, frequency, forced) > 0) { count++; break; }
                }
            }
            return count;
        }

        [TestMethod]
        public void TheScheduleIsTheSameEveryTime()
        {
            for (double t = 0; t < 3 * Year; t += 12345.0)
            {
                Assert.AreEqual(At(t, 61.5), At(t, 61.5));
                Assert.AreEqual(At(t, -55), At(t, -55));
            }
            Assert.IsTrue(StormSlots(0, Year, 60, seed: 1) > 0, "a year at 60 degrees north has storms");
            // Another seed gives another schedule (the storm slots differ somewhere in two years).
            bool differs = false;
            for (double t = 0; t < 2 * Year && !differs; t += 3600) differs = At(t, 60, 1) != At(t, 60, 2);
            Assert.IsTrue(differs);
        }

        [TestMethod]
        public void TheIntensityStaysInZeroToOneForAnyInput()
        {
            var rng = new Random(5);
            for (int i = 0; i < 20000; i++)
            {
                double t = rng.NextDouble() * 6 * Year - Year;
                double lat = rng.NextDouble() * 200 - 100;
                double v = StormModel.Intensity(t, lat, Year, rng.Next(3) == 0 ? rng.NextDouble() : -1, 30 + rng.NextDouble() * 30, rng.Next(), rng.NextDouble() * 5 - 1);
                Assert.IsTrue(v >= 0 && v <= 1, "intensity " + v);
            }
            Assert.AreEqual(0, StormModel.Intensity(double.NaN, 60, Year, -1, Line, 1, 1));
            Assert.AreEqual(0, StormModel.Intensity(1000, double.NaN, Year, -1, Line, 1, 1));
            Assert.AreEqual(0, StormModel.Intensity(1000, 60, Year, -1, Line, 1, double.NaN));
            Assert.AreEqual(0, StormModel.Intensity(1000, 60, 0, -1, Line, 1, 1), "a year length of 0 means phase 0");
        }

        [TestMethod]
        public void SeveralStormsFallInAWinterAndNoneInASummer()
        {
            // Phase 0 is the northern midwinter. The northern half year around it, at 60 N.
            double winterFrom = -0.25 * Year, winterTo = 0.25 * Year;
            double summerFrom = 0.3 * Year, summerTo = 0.7 * Year;
            int winter = StormSlots(winterFrom, winterTo, 60);
            int summer = StormSlots(summerFrom, summerTo, 60);
            Assert.IsTrue(winter >= 3 && winter <= 30, "winter storms: " + winter);
            Assert.AreEqual(0, summer, "northern summer storms");
            // The southern hemisphere runs half a year later: its winter is the north's summer.
            Assert.IsTrue(StormSlots(summerFrom, summerTo, -60) >= 3);
            Assert.AreEqual(0, StormSlots(winterFrom, winterTo, -60));
        }

        [TestMethod]
        public void NoStormsNearTheEquatorOrOnTheCapsInSummer()
        {
            Assert.AreEqual(0, StormSlots(0, 3 * Year, 0));
            Assert.AreEqual(0, StormSlots(0, 3 * Year, 10));
            Assert.AreEqual(0, StormSlots(0, 3 * Year, -10));
            // 85 N has snow all year (the cap), but not storms in the northern summer.
            Assert.AreEqual(0, StormSlots(0.3 * Year, 0.7 * Year, 85));
            Assert.IsTrue(StormSlots(-0.25 * Year, 0.25 * Year, 85) >= 1);
        }

        [TestMethod]
        public void TheGateFollowsTheSnowLineAndTheSeason()
        {
            Assert.AreEqual(0, StormModel.Gate(0, 0, Line), 1e-9);
            Assert.AreEqual(0, StormModel.Gate(60, 0.5, Line), 1e-9);
            Assert.IsTrue(StormModel.Gate(60, 0.06, Line) > 0.9);
            Assert.IsTrue(StormModel.Gate(30, 0.06, Line) < 0.05, "south of the winter snow line");
            Assert.AreEqual(0, StormModel.Gate(double.NaN, 0, Line));
            // A fixed phase (the diagnostics phase) is used for the whole slot.
            Assert.IsTrue(StormSlots(0, Year, 60, forced: 0.06) > StormSlots(0, Year, 60, forced: 0.56));
            Assert.AreEqual(0, StormSlots(0, Year, 60, forced: 0.56));
        }

        [TestMethod]
        public void AStormRampsUpPeaksAndDecaysSmoothlyInsideItsSlot()
        {
            // Find the first stormy slot in a winter and walk through it every 30 s.
            double found = double.NaN;
            for (double slot = Math.Floor(-0.25 * Year / Slot); slot * Slot < 0.25 * Year && double.IsNaN(found); slot++)
                for (double t = slot * Slot; t < (slot + 1) * Slot; t += 600) if (At(t, 60) > 0) { found = slot * Slot; break; }
            Assert.IsFalse(double.IsNaN(found), "no storm in a winter");
            double previous = 0, peak = 0;
            bool risen = false, fallen = false;
            for (double t = found; t < found + Slot; t += 30)
            {
                double v = At(t, 60);
                Assert.IsTrue(Math.Abs(v - previous) < 0.03, "a step of " + Math.Abs(v - previous) + " in 30 s");
                if (v > peak) peak = v;
                if (v > 0.05 && !fallen) risen = true;
                if (risen && v < peak - 0.05) fallen = true;
                if (fallen && v > 0) Assert.IsTrue(v <= previous + 1e-9, "the decay rises again");
                previous = v;
            }
            Assert.IsTrue(peak >= StormModel.PeakIntensityMin - 1e-9 && peak <= 1);
            Assert.IsTrue(risen && fallen);
            Assert.AreEqual(0, At(found, 60), 1e-9, "a storm starts from 0 inside its slot");
            Assert.AreEqual(0, At(found + Slot - 1, 60), 1e-9, "and is over at the end of the slot");
        }

        [TestMethod]
        public void TheEnvelopeHasTheShapeOfItsParts()
        {
            Assert.AreEqual(0, StormModel.Envelope(-1, 10, 20, 30));
            Assert.AreEqual(0, StormModel.Envelope(0, 10, 20, 30));
            Assert.AreEqual(0.5, StormModel.Envelope(5, 10, 20, 30), 1e-9);
            Assert.AreEqual(1, StormModel.Envelope(10, 10, 20, 30), 1e-9);
            Assert.AreEqual(1, StormModel.Envelope(29, 10, 20, 30), 1e-9);
            Assert.AreEqual(0.5, StormModel.Envelope(45, 10, 20, 30), 1e-9);
            Assert.AreEqual(0, StormModel.Envelope(60, 10, 20, 30));
            double last = 0;
            for (double t = 0; t <= 10; t += 0.5) { double v = StormModel.Envelope(t, 10, 20, 30); Assert.IsTrue(v >= last); last = v; }
        }

        [TestMethod]
        public void TheFrequencySettingScalesTheNumberOfStorms()
        {
            int none = StormSlots(-0.25 * Year, 0.25 * Year, 60, frequency: 0);
            int normal = StormSlots(-0.25 * Year, 0.25 * Year, 60, frequency: 1);
            int more = StormSlots(-0.25 * Year, 0.25 * Year, 60, frequency: 2);
            Assert.AreEqual(0, none);
            Assert.IsTrue(more > normal && normal > 0, "frequency 2: " + more + ", 1: " + normal);
            // Above the largest value it is held at the largest.
            Assert.AreEqual(more, StormSlots(-0.25 * Year, 0.25 * Year, 60, frequency: 50));
        }

        [TestMethod]
        public void TheForcedStormReplacesTheScheduleAndTheSettings()
        {
            Assert.AreEqual(0.7, StormModel.Resolve(0.7, true, 0), 1e-9);
            Assert.AreEqual(0.7, StormModel.Resolve(0.7, false, 0.2), 1e-9, "forced even with storms off");
            Assert.AreEqual(1, StormModel.Resolve(3, true, 0), 1e-9);
            Assert.AreEqual(0, StormModel.Resolve(0, true, 0.9), 1e-9, "forced 0 is a clear sky in the middle of a storm");
            Assert.AreEqual(0.4, StormModel.Resolve(-1, true, 0.4), 1e-9);
            Assert.AreEqual(0, StormModel.Resolve(-1, false, 0.4), 1e-9);
            Assert.AreEqual(0, StormModel.Resolve(double.NaN, true, double.NaN), 1e-9);
        }

        [TestMethod]
        public void VisibilityFallsWithTheIntensityToTheSetMinimum()
        {
            double last = double.MaxValue;
            for (double i = 0; i <= 1.0001; i += 0.05)
            {
                double v = StormLook.VisibilityMetres(i, 60);
                Assert.IsTrue(v < last || i == 0, "visibility must fall");
                last = v;
            }
            Assert.AreEqual(StormLook.ClearVisibilityM, StormLook.VisibilityMetres(0, 60), 1e-6);
            Assert.AreEqual(60, StormLook.VisibilityMetres(1, 60), 1e-6);
            double half = StormLook.VisibilityMetres(0.5, 60);
            Assert.IsTrue(half > 200 && half < 300, "half intensity: " + half + " m");
            // A minimum outside the range is brought into it.
            Assert.AreEqual(20, StormLook.VisibilityMetres(1, 1), 1e-6);
            Assert.AreEqual(500, StormLook.VisibilityMetres(1, 9000), 1e-6);
        }

        [TestMethod]
        public void TheFogDensityLeavesFivePercentAtTheVisibility()
        {
            foreach (double v in new[] { 60.0, 250, 2000 })
            {
                double d = StormLook.FogDensity(v);
                Assert.AreEqual(0.05, StormLook.Transmittance(d, v), 0.002);
                Assert.IsTrue(StormLook.Transmittance(d, v * 0.25) > 0.8);
            }
            Assert.IsTrue(StormLook.FogDensity(0) > 0 && !double.IsInfinity(StormLook.FogDensity(0)));
        }

        [TestMethod]
        public void TheVeilTheFlakesAndTheHeightFollowTheIntensity()
        {
            Assert.AreEqual(0, StormLook.VeilAlpha(0, 0.6), 1e-9);
            Assert.AreEqual(0.6, StormLook.VeilAlpha(1, 0.6), 1e-9);
            Assert.AreEqual(StormLook.VeilMax, StormLook.VeilAlpha(1, 5), 1e-9);
            Assert.IsTrue(StormLook.VeilAlpha(0.35, 0.6) < StormLook.VeilAlpha(0.7, 0.6));
            Assert.AreEqual(0, StormLook.FlakeRate(0.01));
            Assert.AreEqual(StormLook.FlakesPerSecond, StormLook.FlakeRate(1), 1e-6);
            Assert.AreEqual(1, StormLook.HeightFactor(0), 1e-9);
            Assert.AreEqual(1, StormLook.HeightFactor(StormLook.FadeStartM), 1e-9);
            Assert.AreEqual(0, StormLook.HeightFactor(StormLook.FadeEndM), 1e-9);
            Assert.AreEqual(0, StormLook.HeightFactor(double.NaN));
            Assert.IsTrue(StormLook.FogBrightness(1) > StormLook.FogBrightness(-1));
            Assert.IsTrue(StormLook.FogBrightness(-1) >= 0.1);
        }

        [TestMethod]
        public void TheStormSettingsComeBackAndAreBroughtIntoTheirRanges()
        {
            string folder = Path.Combine(Path.GetTempPath(), "storm-settings-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(folder, "settings.cfg");
            ConfigNode saved = AtmospheresSettings.Snapshot();
            try
            {
                AtmospheresSettings.StormSeed = 77;
                AtmospheresSettings.StormVisibilityM = 90f;
                AtmospheresSettings.StormVeil = 0.4f;
                AtmospheresSettings.StormSnowfall = false;
                AtmospheresSettings.ForceStorm = 0.35f;
                AtmospheresSettings.Write(file);
                AtmospheresSettings.StormSeed = 1;
                AtmospheresSettings.StormVisibilityM = 60f;
                AtmospheresSettings.StormVeil = 0.6f;
                AtmospheresSettings.StormSnowfall = true;
                AtmospheresSettings.ForceStorm = -1f;
                AtmospheresSettings.Read(file);
                Assert.AreEqual(77, AtmospheresSettings.StormSeed);
                Assert.AreEqual(90f, AtmospheresSettings.StormVisibilityM);
                Assert.AreEqual(0.4f, AtmospheresSettings.StormVeil);
                Assert.IsFalse(AtmospheresSettings.StormSnowfall);
                Assert.AreEqual(0.35f, AtmospheresSettings.ForceStorm);

                File.WriteAllText(file, "stormVisibility = 5\nstormVeil = 7\nforceStorm = 4\nstormSeed = x\n");
                AtmospheresSettings.Read(file);
                Assert.AreEqual(StormLook.MinVisibilityLow, AtmospheresSettings.StormVisibilityM);
                Assert.AreEqual(StormLook.VeilMax, AtmospheresSettings.StormVeil);
                Assert.AreEqual(1f, AtmospheresSettings.ForceStorm);
                Assert.AreEqual(77, AtmospheresSettings.StormSeed, "a seed that is not a number is ignored");
                File.WriteAllText(file, "forceStorm = -3\n");
                AtmospheresSettings.Read(file);
                Assert.AreEqual(-1f, AtmospheresSettings.ForceStorm);
            }
            finally
            {
                AtmospheresSettings.Restore(saved);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
    }
}
