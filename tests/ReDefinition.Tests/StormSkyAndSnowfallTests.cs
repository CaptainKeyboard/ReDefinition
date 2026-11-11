using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.Tests
{
    // The numbers of the snowfall (layers, rate, size, streaks), the storm sky and ambient light, the tracker that scales a value other
    // code writes too, and the new settings.
    [TestClass]
    public class StormSkyAndSnowfallTests
    {
        private static readonly StormLook.FlakeLayer[] Layers = { StormLook.NearLayer, StormLook.MidLayer, StormLook.FarLayer };

        [TestMethod]
        public void TheLayersShareTheRateAndFitTheSystem()
        {
            Assert.AreEqual(1.0, StormLook.NearLayer.RateShare + StormLook.MidLayer.RateShare + StormLook.FarLayer.RateShare, 1e-6);
            Assert.IsTrue(StormLook.NearLayer.BoxWidth < StormLook.MidLayer.BoxWidth && StormLook.MidLayer.BoxWidth < StormLook.FarLayer.BoxWidth,
                "the layers get wider with the distance");
            Assert.IsTrue(StormLook.NearLayer.SizeMax > StormLook.MidLayer.SizeMax && StormLook.MidLayer.SizeMax >= StormLook.FarLayer.SizeMax,
                "near flakes are the largest, far ones the smallest");
            Assert.IsTrue(StormLook.NearLayer.Alpha < StormLook.MidLayer.Alpha && StormLook.MidLayer.Alpha <= StormLook.FarLayer.Alpha,
                "near flakes are translucent, like out-of-focus discs");
            foreach (StormLook.FlakeLayer layer in Layers)
            {
                for (double i = 0; i <= 1.0001; i += 0.05)
                    Assert.IsTrue(StormLook.FlakesAlive(i, layer) <= StormLook.MaxFlakesPerLayer, "flakes alive " + StormLook.FlakesAlive(i, layer) + " at " + i);
            }
        }

        [TestMethod]
        public void MoreAndLargerFlakesAsTheStormGrows()
        {
            foreach (StormLook.FlakeLayer layer in Layers)
            {
                int a = StormLook.FlakesAlive(0.35, layer), b = StormLook.FlakesAlive(0.7, layer), c = StormLook.FlakesAlive(1.0, layer);
                Assert.IsTrue(a < b && b < c, "alive " + a + ", " + b + ", " + c);
                Assert.IsTrue(StormLook.FlakeRate(0.35, layer) < StormLook.FlakeRate(0.7, layer));
            }
            Assert.IsTrue(StormLook.SizeFactor(0.1) < StormLook.SizeFactor(0.5) && StormLook.SizeFactor(0.5) < StormLook.SizeFactor(1.0));
            Assert.AreEqual(1.0, StormLook.SizeFactor(1.0), 1e-9);
            Assert.AreEqual(1.0, StormLook.SizeFactor(7.0), 1e-9, "an intensity above 1 counts as 1");
            Assert.IsTrue(StormLook.FlakeRate(0.35) > 0.3 * StormLook.FlakesPerSecond, "a half-strength storm already has a third of the flakes");
            Assert.AreEqual(0.0, StormLook.FlakeRate(0.01, StormLook.NearLayer));
            Assert.AreEqual(0.0, StormLook.FlakeRate(double.NaN, StormLook.FarLayer));
        }

        [TestMethod]
        public void AFlakeIsNeverMuchLongerThanWideAndSnowFallsSlowly()
        {
            // The picture of the storm must not read as rain: even the smallest flake at the top speed is at most 1.5 times as long
            // as it is wide, at every intensity.
            foreach (StormLook.FlakeLayer layer in Layers)
            {
                for (double i = 0; i <= 1.0001; i += 0.05)
                {
                    Assert.IsTrue(StormLook.StreakRatio(layer, i) <= StormLook.StreakRatioMax,
                        "streak ratio " + StormLook.StreakRatio(layer, i) + " at " + i + " for a layer of size " + layer.SizeMin);
                    Assert.IsTrue(StormLook.StreakRatio(layer, i) >= 1.0);
                    Assert.IsTrue(StormLook.FallMax(layer, i) <= 2.0 + 1e-9 && StormLook.FallMin(layer, i) >= 0.6, "fall " + StormLook.FallMin(layer, i) + " to " + StormLook.FallMax(layer, i));
                    Assert.IsTrue(StormLook.FallMin(layer, i) < StormLook.FallMax(layer, i), "the flakes differ in speed");
                }
            }
            Assert.IsTrue(StormLook.StreakScale(1.0) > StormLook.StreakScale(0.0));
            Assert.IsTrue(StormLook.NearLayer.FallMax > StormLook.FarLayer.FallMax, "larger flakes fall faster");
            Assert.IsTrue(StormLook.WindSpeed(1.0) > 2 * StormLook.FallMax(StormLook.NearLayer, 1.0), "the wind is strong against the fall: a storm drifts sideways");
        }

        [TestMethod]
        public void TheWindGustsAroundItsMeanAndAnythingElseIsOne()
        {
            double min = 9, max = -9, sum = 0;
            int n = 0;
            for (double t = 0; t < 600; t += 0.25)
            {
                double g = StormLook.GustFactor(t, 1.7);
                min = Math.Min(min, g);
                max = Math.Max(max, g);
                sum += g;
                n++;
                Assert.IsTrue(g >= 1 - StormLook.GustDepth - 1e-9 && g <= 1 + StormLook.GustDepth + 1e-9, "gust " + g + " at " + t);
            }
            Assert.IsTrue(max - min > StormLook.GustDepth, "the gusts swing, " + min + " to " + max);
            Assert.AreEqual(1.0, sum / n, 0.05, "the mean wind is the wind");
            Assert.AreNotEqual(StormLook.GustFactor(5, 0.0), StormLook.GustFactor(5, 3.0), "two seeds do not gust together");
            Assert.AreEqual(1.0, StormLook.GustFactor(double.NaN, 0));
            Assert.AreEqual(1.0, StormLook.GustFactor(double.PositiveInfinity, 0));
            Assert.AreEqual(1.0, StormLook.GustFactor(1, double.NaN));
        }

        [TestMethod]
        public void AFlakeFallsThroughItsBoxAndTheBoxLeansUpwind()
        {
            foreach (StormLook.FlakeLayer layer in Layers)
            {
                double mean = 0.5 * (StormLook.FallMin(layer, 0.5) + StormLook.FallMax(layer, 0.5));
                Assert.AreEqual(layer.BoxHeight, StormLook.Lifetime(layer, 0.5) * mean, 1e-6);
                Assert.IsTrue(StormLook.Lifetime(layer, double.NaN) > 0 && !double.IsInfinity(StormLook.Lifetime(layer, double.NaN)));
                Assert.IsTrue(StormLook.Lifetime(layer, 0) >= StormLook.Lifetime(layer, 1), "a light fall is a little slower");
            }
            double life = 2.0;
            Assert.AreEqual(-9.0, StormLook.UpwindOffset(9.0, life), 1e-9, "half the drift, against the wind");
            Assert.AreEqual(0.0, StormLook.UpwindOffset(0, life), 1e-9);
        }

        [TestMethod]
        public void TheSkyTurnsGreyAndHeavyInsteadOfLighter()
        {
            // Clear sky about 0.6 bright; the storm grey about 0.36. The veil lays that grey over it with the sky alpha.
            double clear = 0.6;
            double grey = 0.2126 * StormLook.SkyGreyDay[0] + 0.7152 * StormLook.SkyGreyDay[1] + 0.0722 * StormLook.SkyGreyDay[2];
            double last = clear;
            for (double i = 0.1; i <= 1.0001; i += 0.1)
            {
                double share = StormLook.SkyGreyShare(1.0, i, 1.0);
                double alpha = Math.Max(StormLook.VeilAlpha(i, StormLook.VeilDefault), StormLook.SkyVeilAlpha(i, StormLook.VeilDefault));
                double colour = grey * share + 0.8 * (1 - share);
                double shown = alpha * colour + (1 - alpha) * clear;
                Assert.IsTrue(shown < last + 1e-9, "the zenith gets darker with the intensity: " + shown + " at " + i);
                last = shown;
            }
            Assert.IsTrue(last < clear - 0.15, "at full intensity the zenith is well below the clear sky, " + last);
        }

        [TestMethod]
        public void TheGreyShareFollowsElevationIntensityAndTheSetting()
        {
            Assert.AreEqual(0.0, StormLook.SkyGreyShare(0.0, 1.0, 1.0), 1e-9, "no grey at the horizon");
            Assert.AreEqual(0.0, StormLook.SkyGreyShare(-0.5, 1.0, 1.0), 1e-9, "none below it");
            Assert.AreEqual(1.0, StormLook.SkyGreyShare(StormLook.SkyFullSinElevation, 1.0, 1.0), 1e-9);
            Assert.AreEqual(1.0, StormLook.SkyGreyShare(1.0, 1.0, 1.0), 1e-9);
            Assert.AreEqual(0.0, StormLook.SkyGreyShare(1.0, 1.0, 0.0), 1e-9, "darkening 0 leaves the sky alone");
            Assert.AreEqual(0.0, StormLook.SkyGreyShare(1.0, 0.0, 1.0), 1e-9);
            Assert.AreEqual(0.0, StormLook.SkyGreyShare(double.NaN, 1.0, 1.0));
            double last = -1;
            for (double s = 0; s <= 1.0001; s += 0.05)
            {
                double v = StormLook.SkyGreyShare(s, 1.0, 1.0);
                Assert.IsTrue(v >= last - 1e-12);
                last = v;
            }
            for (double i = 0; i <= 1.0001; i += 0.1)
                Assert.IsTrue(StormLook.SkyVeilAlpha(i, StormLook.VeilDefault) >= StormLook.VeilAlpha(i, StormLook.VeilDefault) - 1e-9 && StormLook.SkyVeilAlpha(i, 5) <= 0.97 + 1e-9);
        }

        [TestMethod]
        public void TheAmbientLightDropsWithTheIntensityAndOnlyWhenAllowed()
        {
            Assert.AreEqual(1.0, StormLook.AmbientFactor(0, 1), 1e-9);
            Assert.AreEqual(1.0, StormLook.AmbientFactor(1, 0), 1e-9);
            Assert.AreEqual(1 - StormLook.AmbientDropAtFull, StormLook.AmbientFactor(1, 1), 1e-9);
            double last = 2;
            for (double i = 0; i <= 1.0001; i += 0.1)
            {
                double f = StormLook.AmbientFactor(i, 0.6);
                Assert.IsTrue(f <= last + 1e-12 && f > 0.3);
                last = f;
            }
        }

        [TestMethod]
        public void AValueSomeoneElseWritesIsTheNewBaselineAndOnlyOurWriteIsTakenBack()
        {
            var tracker = new AmbientTracker();
            var start = new Color(0.4f, 0.5f, 0.6f, 1f);
            Color written = tracker.Scale(start, 0.5f);
            Assert.AreEqual(0.2f, written.r, 1e-6);
            Assert.AreEqual(1f, written.a, "alpha stays");
            // Nobody wrote: the same baseline, scaled again by another factor.
            written = tracker.Scale(written, 0.25f);
            Assert.AreEqual(0.1f, written.r, 1e-6, "scaled from the baseline, not from the last written value");
            Assert.AreEqual(0, tracker.ExternalWrites);
            // KSP writes a new value: it is the new baseline.
            var other = new Color(0.8f, 0.8f, 0.8f, 1f);
            written = tracker.Scale(other, 0.5f);
            Assert.AreEqual(0.4f, written.r, 1e-6);
            Assert.AreEqual(1, tracker.ExternalWrites);
            Color restore;
            Assert.IsTrue(tracker.Restore(written, out restore));
            Assert.AreEqual(0.8f, restore.r, 1e-6);
            Assert.IsFalse(tracker.Active);

            // A newer value of someone else is left alone at the end.
            tracker.Scale(start, 0.5f);
            Assert.IsFalse(tracker.Restore(new Color(0.9f, 0.9f, 0.9f, 1f), out restore));
            Assert.AreEqual(0.9f, restore.r, 1e-6, "the value comes back as it was found");
            Assert.IsFalse(tracker.Restore(start, out restore), "nothing scaled, nothing to restore");
        }

        [TestMethod]
        public void TheRainIsReplacedFromHalfSnowCoverWithAMarginAtTheEdge()
        {
            Assert.IsFalse(EveRainLink.Cold(false, 0.49));
            Assert.IsTrue(EveRainLink.Cold(false, 0.5));
            Assert.IsTrue(EveRainLink.Cold(true, 0.46), "inside the margin it stays on");
            Assert.IsFalse(EveRainLink.Cold(true, 0.44));
            Assert.IsFalse(EveRainLink.Cold(false, double.NaN));
            Assert.IsFalse(EveRainLink.Cold(true, double.NaN));
            Assert.IsTrue(EveRainLink.ColdOff < EveRainLink.ColdOn);
            // The mask in a northern midwinter: the polar region is cold, the tropics are not.
            double winter = 0.06;
            Assert.IsTrue(EveRainLink.Cold(false, SnowMask.Coverage(70, 0, winter, 0, SnowMask.DefaultWinterLineDegrees)));
            Assert.IsFalse(EveRainLink.Cold(false, SnowMask.Coverage(5, 0, winter, 0, SnowMask.DefaultWinterLineDegrees)));
        }

        [TestMethod]
        public void WithoutEveTheRainLinkIsNotControllableAndDoesNothing()
        {
            Assert.IsFalse(EveRainLink.Controllable);
            StringAssert.Contains(EveRainLink.Summary, "not");
            EveRainLink.Release();
            Assert.IsFalse(EveRainLink.Suppressed);
            Assert.AreEqual(0f, EveRainLink.Coverage);
            StringAssert.Contains(EveRainLink.Describe(), "fields 0");
        }

        [TestMethod]
        public void TheNewSettingsComeBackAndAreBroughtIntoTheirRanges()
        {
            string folder = Path.Combine(Path.GetTempPath(), "storm-sky-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            ConfigNode saved = AtmospheresSettings.Snapshot();
            try
            {
                string file = Path.Combine(folder, "settings.cfg");
                AtmospheresSettings.StormSkyDarkening = 0.25f;
                AtmospheresSettings.StormEveSnow = false;
                AtmospheresSettings.StormEveRainToSnow = false;
                AtmospheresSettings.OrbitSnow = false;
                AtmospheresSettings.OrbitSnowWidth = 2048;
                AtmospheresSettings.Write(file);
                AtmospheresSettings.StormSkyDarkening = 0.6f;
                AtmospheresSettings.StormEveSnow = true;
                AtmospheresSettings.StormEveRainToSnow = true;
                AtmospheresSettings.OrbitSnow = true;
                AtmospheresSettings.OrbitSnowWidth = 4096;
                AtmospheresSettings.Read(file);
                Assert.AreEqual(0.25f, AtmospheresSettings.StormSkyDarkening);
                Assert.IsFalse(AtmospheresSettings.StormEveSnow);
                Assert.IsFalse(AtmospheresSettings.StormEveRainToSnow);
                Assert.IsFalse(AtmospheresSettings.OrbitSnow);
                Assert.AreEqual(2048, AtmospheresSettings.OrbitSnowWidth);

                string range = Path.Combine(folder, "range.cfg");
                File.WriteAllText(range, "stormSkyDarkening = 7\norbitSnowWidth = 100\n");
                AtmospheresSettings.Read(range);
                Assert.AreEqual(1f, AtmospheresSettings.StormSkyDarkening);
                Assert.AreEqual(AtmospheresSettings.OrbitSnowWidthMin, AtmospheresSettings.OrbitSnowWidth);
                File.WriteAllText(range, "stormSkyDarkening = -2\norbitSnowWidth = 90000\n");
                AtmospheresSettings.Read(range);
                Assert.AreEqual(0f, AtmospheresSettings.StormSkyDarkening);
                Assert.AreEqual(AtmospheresSettings.OrbitSnowWidthMax, AtmospheresSettings.OrbitSnowWidth);
                File.WriteAllText(range, "stormSkyDarkening = NaN\norbitSnow = maybe\nstormEveRainToSnow = perhaps\n");
                AtmospheresSettings.Read(range);
                Assert.IsFalse(AtmospheresSettings.StormEveRainToSnow, "not a flag is ignored for the rain replacement too");
                Assert.AreEqual(0f, AtmospheresSettings.StormSkyDarkening, "not a number is ignored");
                Assert.IsFalse(AtmospheresSettings.OrbitSnow, "not a flag is ignored");
            }
            finally
            {
                AtmospheresSettings.Restore(saved);
                Directory.Delete(folder, true);
            }
        }
    }
}
