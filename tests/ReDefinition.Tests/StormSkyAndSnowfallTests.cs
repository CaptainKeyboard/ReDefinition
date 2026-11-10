using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Seasons;
using UnityEngine;

namespace ReDefinition.Tests
{
    // The numbers of the snowfall (layers, rate, size, streaks), the storm sky and ambient light, the tracker that scales a value other
    // code writes too, and the new settings.
    [TestClass]
    public class StormSkyAndSnowfallTests
    {
        private static readonly StormLook.FlakeLayer[] Layers = { StormLook.NearLayer, StormLook.FarLayer };

        [TestMethod]
        public void TheLayersShareTheRateAndFitTheSystem()
        {
            Assert.AreEqual(1.0, StormLook.NearLayer.RateShare + StormLook.FarLayer.RateShare, 1e-6);
            Assert.IsTrue(StormLook.NearLayer.BoxWidth < StormLook.FarLayer.BoxWidth, "the near layer is the smaller box");
            Assert.IsTrue(StormLook.NearLayer.SizeMax < StormLook.FarLayer.SizeMax, "far flakes are larger so that they stay visible");
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
        public void TheStreakIsLongInAStormAndShortInALightFall()
        {
            double Length(double i)
            {
                double fall = StormLook.FallSpeed(i), wind = StormLook.WindSpeed(i);
                return StormLook.StreakScale(i) * Math.Sqrt(fall * fall + wind * wind);
            }

            Assert.IsTrue(Length(1.0) > 0.6, "a streak of a full storm is " + Length(1.0) + " m");
            Assert.IsTrue(Length(0.1) < 0.15, "a light fall has dots, " + Length(0.1) + " m");
            double last = 0;
            for (double i = 0; i <= 1.0001; i += 0.1)
            {
                Assert.IsTrue(Length(i) >= last);
                last = Length(i);
            }
        }

        [TestMethod]
        public void AFlakeFallsThroughItsBoxAndTheBoxLeansUpwind()
        {
            foreach (StormLook.FlakeLayer layer in Layers)
            {
                double fall = StormLook.FallSpeed(0.5);
                Assert.AreEqual(layer.BoxHeight, StormLook.Lifetime(layer, fall) * fall, 1e-6);
                Assert.IsTrue(StormLook.Lifetime(layer, 0) > 0 && !double.IsInfinity(StormLook.Lifetime(layer, 0)));
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
        public void TheNewSettingsComeBackAndAreBroughtIntoTheirRanges()
        {
            string folder = Path.Combine(Path.GetTempPath(), "storm-sky-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            float darkening = SeasonsSettings.StormSkyDarkening;
            bool eve = SeasonsSettings.StormEveSnow, orbit = SeasonsSettings.OrbitSnow;
            int width = SeasonsSettings.OrbitSnowWidth;
            try
            {
                string file = Path.Combine(folder, "settings.cfg");
                SeasonsSettings.StormSkyDarkening = 0.25f;
                SeasonsSettings.StormEveSnow = false;
                SeasonsSettings.OrbitSnow = false;
                SeasonsSettings.OrbitSnowWidth = 2048;
                SeasonsSettings.Write(file);
                SeasonsSettings.StormSkyDarkening = 0.6f;
                SeasonsSettings.StormEveSnow = true;
                SeasonsSettings.OrbitSnow = true;
                SeasonsSettings.OrbitSnowWidth = 4096;
                SeasonsSettings.Read(file);
                Assert.AreEqual(0.25f, SeasonsSettings.StormSkyDarkening);
                Assert.IsFalse(SeasonsSettings.StormEveSnow);
                Assert.IsFalse(SeasonsSettings.OrbitSnow);
                Assert.AreEqual(2048, SeasonsSettings.OrbitSnowWidth);

                string range = Path.Combine(folder, "range.cfg");
                File.WriteAllText(range, "stormSkyDarkening = 7\norbitSnowWidth = 100\n");
                SeasonsSettings.Read(range);
                Assert.AreEqual(1f, SeasonsSettings.StormSkyDarkening);
                Assert.AreEqual(SeasonsSettings.OrbitSnowWidthMin, SeasonsSettings.OrbitSnowWidth);
                File.WriteAllText(range, "stormSkyDarkening = -2\norbitSnowWidth = 90000\n");
                SeasonsSettings.Read(range);
                Assert.AreEqual(0f, SeasonsSettings.StormSkyDarkening);
                Assert.AreEqual(SeasonsSettings.OrbitSnowWidthMax, SeasonsSettings.OrbitSnowWidth);
                File.WriteAllText(range, "stormSkyDarkening = NaN\norbitSnow = maybe\n");
                SeasonsSettings.Read(range);
                Assert.AreEqual(0f, SeasonsSettings.StormSkyDarkening, "not a number is ignored");
                Assert.IsFalse(SeasonsSettings.OrbitSnow, "not a flag is ignored");
            }
            finally
            {
                SeasonsSettings.StormSkyDarkening = darkening;
                SeasonsSettings.StormEveSnow = eve;
                SeasonsSettings.OrbitSnow = orbit;
                SeasonsSettings.OrbitSnowWidth = width;
                Directory.Delete(folder, true);
            }
        }
    }
}
