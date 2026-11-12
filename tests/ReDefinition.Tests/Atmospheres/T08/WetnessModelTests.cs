using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.T08
{
    // The surface wetness against references that do not come from the code under test (package T08).
    //
    // Reference expectations (written before the model was built):
    //   * the potential evaporation at 15 C, 60 % humidity, 3 m/s and full sun is 0.12 mm/h (a clear autumn day over wet bare ground
    //     lies between 0.05 and 0.3 mm/h); cold, damp, calm and dark weather evaporates less, in that order of every single factor;
    //   * steady rain of 5 mm/h on dry terrain saturates the surface layer (2 mm) after 2 / 5 h = 24 minutes (water mass balance) and
    //     only then does water stand;
    //   * the layer dries in the closed-form time of dM/dt = -E (0.25 + 0.75 M / S) (DryTimeS), which the stepping reproduces within
    //     5 %;
    //   * the diffuse factor of fully wet ground equals the configured wetDiffuse (real wet bare ground: 0.5 to 0.8 of dry) and returns
    //     to dry with the drying time; the drying time of terrain in the reference weather lies between 3 and 48 hours;
    //   * a jump of 6 hours reaches the state the same hours in 1 s frames reach within 5 %; a catch-up over the lookback from a dry
    //     state equals the replay over the same interval;
    //   * snow below freezing wets nothing, snow above it wets by its melt; frozen water darkens less than liquid; snow cover takes
    //     away puddles.
    [TestClass]
    public class WetnessModelTests
    {
        private static WetInput Reference()
        {
            return new WetInput { WaterMmH = 0f, TemperatureK = 288.15f, FreezeK = 273.15f, WindMs = 3f, Humidity01 = 0.6f, Sun01 = 1f };
        }

        [TestMethod]
        public void EvaporationAtTheReferenceWeatherIsInTheRealBand()
        {
            WetnessTuning t = WetnessTuning.Defaults();
            WetInput w = Reference();
            float e = WetnessModel.Evaporation(ref w, ref t);
            Assert.AreEqual(0.12f, e, 0.01f);
            Assert.IsTrue(e > 0.05f && e < 0.3f);
        }

        [TestMethod]
        public void EveryFactorMovesTheEvaporationTheRightWay()
        {
            WetnessTuning t = WetnessTuning.Defaults();
            WetInput r = Reference();
            float e0 = WetnessModel.Evaporation(ref r, ref t);
            WetInput cold = r; cold.TemperatureK = 268f;
            WetInput damp = r; damp.Humidity01 = 0.95f;
            WetInput calm = r; calm.WindMs = 0f;
            WetInput dark = r; dark.Sun01 = 0f;
            WetInput windy = r; windy.WindMs = 12f;
            WetInput warm = r; warm.TemperatureK = 300f;
            Assert.IsTrue(WetnessModel.Evaporation(ref cold, ref t) < e0 * 0.3f);
            Assert.IsTrue(WetnessModel.Evaporation(ref damp, ref t) < e0 * 0.2f);
            Assert.IsTrue(WetnessModel.Evaporation(ref calm, ref t) < e0);
            Assert.IsTrue(WetnessModel.Evaporation(ref dark, ref t) < e0 * 0.4f);
            Assert.IsTrue(WetnessModel.Evaporation(ref windy, ref t) > e0);
            Assert.IsTrue(WetnessModel.Evaporation(ref warm, ref t) > e0);
        }

        [TestMethod]
        public void SteadyRainSaturatesTheLayerByMassBalanceThenWaterStands()
        {
            WetnessTuning t = WetnessTuning.Defaults();
            WetInput w = Reference();
            w.WaterMmH = 5f;
            w.Humidity01 = 1f; // no evaporation: the balance is rain alone
            var s = new WetState();
            float full = -1f;
            for (int sec = 1; sec <= 3600 && full < 0f; sec++)
            {
                WetnessModel.Step(ref s, ref w, WetClass.Terrain, ref t, 1f);
                if (s.StoredMm >= t.SoilCapMm - 1e-3f) full = sec;
            }
            Assert.AreEqual(24f * 60f, full, 0.05f * 24f * 60f, "2 mm at 5 mm/h is 24 minutes");
            Assert.AreEqual(0f, s.StandingMm, 0.05f, "nothing stands before the layer is full");
            for (int sec = 0; sec < 7200; sec++) WetnessModel.Step(ref s, ref w, WetClass.Terrain, ref t, 1f);
            Assert.IsTrue(s.StandingMm > 3f, "after two more hours water stands: " + s.StandingMm);
            Assert.IsTrue(s.StandingMm <= t.PuddleCapMm + 1e-3f);
        }

        [TestMethod]
        public void TheLayerDriesInTheClosedFormTime()
        {
            WetnessTuning t = WetnessTuning.Defaults();
            WetInput w = Reference();
            float e = WetnessModel.Evaporation(ref w, ref t);
            var s = new WetState { StoredMm = t.SoilCapMm };
            double expected = WetnessModel.DryTimeS(t.SoilCapMm, t.SoilCapMm, e);
            double elapsed = 0;
            while (s.StoredMm > 0f && elapsed < 10 * expected)
            {
                WetnessModel.Step(ref s, ref w, WetClass.Terrain, ref t, 30f);
                elapsed += 30;
            }
            Assert.AreEqual(expected, elapsed, 0.05 * expected);
            double hours = expected / 3600.0;
            Assert.IsTrue(hours > 3.0 && hours < 48.0, "drying time in the reference weather: " + hours + " h");
        }

        [TestMethod]
        public void DiffuseFactorFallsToTheConfiguredValueAndComesBack()
        {
            WetnessTuning t = WetnessTuning.Defaults();
            WetLook wet = WetnessLookMath.Eval(1f, 0f, 0f, 288f, 273.15f, 0f, ref t);
            Assert.AreEqual(t.WetDiffuse, wet.DiffuseFactor, 0.02f * t.WetDiffuse);
            Assert.IsTrue(wet.DiffuseFactor >= 0.5f && wet.DiffuseFactor <= 0.8f, "the real band of wet bare ground");
            WetLook dry = WetnessLookMath.Eval(0f, 0f, 0f, 288f, 273.15f, 0f, ref t);
            Assert.AreEqual(1f, dry.DiffuseFactor, 1e-6f);
            Assert.AreEqual(0f, dry.SmoothAdd, 1e-6f);

            // The way back: dry the layer in the reference weather; at the closed-form time the factor is back to dry, half way it is not.
            WetInput w = Reference();
            float e = WetnessModel.Evaporation(ref w, ref t);
            double total = WetnessModel.DryTimeS(t.SoilCapMm, t.SoilCapMm, e);
            var s = new WetState { StoredMm = t.SoilCapMm };
            double done = 0;
            WetLook half = default(WetLook);
            while (done < total * 1.02)
            {
                WetnessModel.Step(ref s, ref w, WetClass.Terrain, ref t, 30f);
                done += 30;
                if (done >= total * 0.5 && half.DiffuseFactor == 0f)
                    half = WetnessLookMath.Eval(WetnessModel.Saturation(ref s, WetClass.Terrain, ref t), 0f, 0f, 288f, 273.15f, 0f, ref t);
            }
            WetLook end = WetnessLookMath.Eval(WetnessModel.Saturation(ref s, WetClass.Terrain, ref t), 0f, 0f, 288f, 273.15f, 0f, ref t);
            Assert.IsTrue(half.DiffuseFactor < 0.95f, "half way it is still dark: " + half.DiffuseFactor);
            Assert.IsTrue(end.DiffuseFactor > 0.995f, "at the drying time it is dry: " + end.DiffuseFactor);
        }

        // A smooth shower: a sin-squared bump of rain 1.5 h long, starting 1 h in, peak 8 mm/h, on a mild breezy day.
        private static WetInput Shower(double ut)
        {
            WetInput w = Reference();
            double u = (ut - 3600.0) / 5400.0;
            if (u > 0.0 && u < 1.0) w.WaterMmH = (float)(8.0 * Math.Pow(Math.Sin(Math.PI * u), 2.0));
            w.Humidity01 = w.WaterMmH > 0f ? 0.95f : 0.6f;
            return w;
        }

        [TestMethod]
        public void ASixHourJumpEqualsTheSameHoursInFrames()
        {
            WetnessTuning t = WetnessTuning.Defaults();
            // Frames: 1 s steps, the weather read at the middle of each.
            var framed = new WetState();
            for (int sec = 0; sec < 6 * 3600; sec++)
            {
                WetInput w = Shower(sec + 0.5);
                WetnessModel.Step(ref framed, ref w, WetClass.Terrain, ref t, 1f);
            }
            // The jump: one call over 6 h with the history step of the tuning.
            var jumped = WetnessCatchUp.Resume(Shower, new WetState(), 0.0, WetClass.Terrain, ref t, 6 * 3600.0);
            Assert.AreEqual(framed.StoredMm, jumped.StoredMm, 0.05f * Math.Max(framed.StoredMm, 0.1f), "layer");
            Assert.AreEqual(framed.StandingMm, jumped.StandingMm, 0.05f * Math.Max(framed.StandingMm, 0.5f), "standing");
            Assert.IsTrue(framed.StoredMm > 0.3f, "the scenario wets the ground: " + framed.StoredMm);
        }

        [TestMethod]
        public void ACatchUpFromNothingReplaysTheLookback()
        {
            WetnessTuning t = WetnessTuning.Defaults();
            double now = 5 * 3600.0;
            // Last known state is stale (older than the lookback) and wet: the catch-up must not use it.
            var stale = new WetState { StoredMm = 2f, StandingMm = 5f };
            WetState fromStale = WetnessCatchUp.Resume(Shower, stale, now - 1000 * 3600.0, WetClass.Terrain, ref t, now);
            WetState fromDry = WetnessCatchUp.Resume(Shower, new WetState(), now - t.LookbackH * 3600.0, WetClass.Terrain, ref t, now);
            Assert.AreEqual(fromDry.StoredMm, fromStale.StoredMm, 1e-4f);
            Assert.AreEqual(fromDry.StandingMm, fromStale.StandingMm, 1e-4f);
        }

        [TestMethod]
        public void SnowWetsOnlyAsFarAsItMelts()
        {
            Assert.AreEqual(0f, WetnessModel.Intake(PrecipType.Snow, 0f, 0f, 1f, 2f, 268f, 273.15f), 1e-6f);
            float half = WetnessModel.Intake(PrecipType.Snow, 0f, 0f, 1f, 2f, 274.4f, 273.15f);
            Assert.IsTrue(half > 0.5f && half < 1.5f, "partial melt: " + half);
            Assert.AreEqual(2f, WetnessModel.Intake(PrecipType.Snow, 0f, 0f, 1f, 2f, 278f, 273.15f), 1e-6f);
            Assert.AreEqual(2f, WetnessModel.Intake(PrecipType.Rain, 1f, 0f, 0f, 2f, 285f, 273.15f), 1e-6f);
            Assert.AreEqual(0f, WetnessModel.Intake(PrecipType.Dust, 0f, 0f, 0f, 2f, 285f, 273.15f), 1e-6f);
        }

        [TestMethod]
        public void FrozenWaterDarkensLessAndSnowTakesAwayThePuddles()
        {
            WetnessTuning t = WetnessTuning.Defaults();
            WetLook liquid = WetnessLookMath.Eval(1f, 6f, 0.3f, 285f, 273.15f, 0f, ref t);
            WetLook frozen = WetnessLookMath.Eval(1f, 6f, 0.3f, 265f, 273.15f, 0f, ref t);
            WetLook snowy = WetnessLookMath.Eval(1f, 6f, 0.3f, 285f, 273.15f, 1f, ref t);
            Assert.IsTrue(frozen.DiffuseFactor > liquid.DiffuseFactor + 0.15f, "frozen " + frozen.DiffuseFactor + " liquid " + liquid.DiffuseFactor);
            Assert.AreEqual(0f, frozen.Ripples, 1e-6f);
            Assert.IsTrue(liquid.Ripples > 0.5f);
            Assert.IsTrue(snowy.PuddleCover < liquid.PuddleCover * 0.2f);
            Assert.IsTrue(snowy.DiffuseFactor > liquid.DiffuseFactor + 0.2f);
            Assert.AreEqual(liquid.PuddleCover, t.PuddleMax, 0.02f);
        }

        [TestMethod]
        public void SurfaceClassesDryInTheOrderCraftSceneryTerrain()
        {
            WetnessTuning t = WetnessTuning.Defaults();
            WetInput w = Reference();
            w.WaterMmH = 4f;
            w.Humidity01 = 1f;
            var terrain = new WetState(); var scenery = new WetState(); var craft = new WetState();
            for (int i = 0; i < 3600; i++)
            {
                WetnessModel.Step(ref terrain, ref w, WetClass.Terrain, ref t, 1f);
                WetnessModel.Step(ref scenery, ref w, WetClass.Scenery, ref t, 1f);
                WetnessModel.Step(ref craft, ref w, WetClass.Craft, ref t, 1f);
            }
            w.WaterMmH = 0f;
            w.Humidity01 = 0.6f;
            double[] gone = { -1, -1, -1 };
            for (int sec = 0; sec < 72 * 3600; sec += 60)
            {
                WetnessModel.Step(ref terrain, ref w, WetClass.Terrain, ref t, 60f);
                WetnessModel.Step(ref scenery, ref w, WetClass.Scenery, ref t, 60f);
                WetnessModel.Step(ref craft, ref w, WetClass.Craft, ref t, 60f);
                if (gone[0] < 0 && terrain.StoredMm <= 0f && terrain.StandingMm <= 0f) gone[0] = sec;
                if (gone[1] < 0 && scenery.StoredMm <= 0f && scenery.StandingMm <= 0f) gone[1] = sec;
                if (gone[2] < 0 && craft.StoredMm <= 0f && craft.StandingMm <= 0f) gone[2] = sec;
            }
            Assert.IsTrue(gone[2] >= 0 && gone[1] >= 0 && gone[0] >= 0, "all dry within 72 h");
            Assert.IsTrue(gone[2] < gone[1] && gone[1] < gone[0], "craft " + gone[2] + " scenery " + gone[1] + " terrain " + gone[0]);
        }

        [TestMethod]
        public void RateFromLevelInvertsLevelFromRate()
        {
            float[] bands = { 0.05f, 0.25f, 1.0f, 2.5f, 6.0f };
            for (float level = 0.02f; level <= 1f; level += 0.037f)
            {
                float rate = PrecipRate.FromLevel(level, bands);
                Assert.AreEqual(level, PrecipScale.LevelFromRate(rate, bands), 0.01f, "level " + level);
            }
            Assert.AreEqual(0f, PrecipRate.FromLevel(0f, bands), 1e-6f);
        }

        [TestMethod]
        public void StepsOfAnyLengthKeepTheStateFiniteAndInRange()
        {
            WetnessTuning t = WetnessTuning.Defaults();
            var s = new WetState();
            WetInput w = Reference();
            w.WaterMmH = 50f;
            WetnessModel.Step(ref s, ref w, WetClass.Terrain, ref t, 400000f);
            Assert.IsTrue(s.StoredMm <= t.SoilCapMm + 1e-3f && s.StandingMm <= t.PuddleCapMm + 1e-3f);
            w.TemperatureK = float.NaN;
            WetnessModel.Step(ref s, ref w, WetClass.Craft, ref t, 10f);
            Assert.IsFalse(float.IsNaN(s.StoredMm) || float.IsNaN(s.StandingMm));
        }
    }
}
