using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The runtime knobs of the cloud look (CloudLookTuning): every default reproduces today's output exactly, every key does what its comment says, and the oracle sees the
    // keys through the same getters as the pass.
    [TestClass]
    public class CloudLookTuningTests
    {
        private static int frameCounter = 4000000;

        private static void Set(string key, float value)
        {
            Tuning.SetOverride(key, value);
            Tuning.Instance.BeginFrame(++frameCounter);
        }

        // These tests are about the knobs' arithmetic on the layer's own numbers, which is the old density's (E40 soft density: with the soft water content the uploaded numbers are
        // derived from them; CloudSoftDensityTests pins that); the baseline here is layers.softDensity 0.
        private static void Clear()
        {
            Tuning.ClearOverrides();
            Tuning.SetOverride(CloudSoftDensity.TuneOn, 0f);
            Tuning.Instance.BeginFrame(++frameCounter);
        }

        [TestInitialize]
        public void Setup()
        {
            Clear();
        }

        [TestCleanup]
        public void Cleanup()
        {
            Tuning.ClearOverrides();
            Tuning.Instance.BeginFrame(++frameCounter);
        }

        private static CloudLayerDef[] Stack()
        {
            return new[] { CloudLookFixtures.Rain(), CloudLookFixtures.Cumulus(), CloudLookFixtures.Cumulonimbus(), CloudLookFixtures.Cirrus() };
        }

        // layers.profileShape (E40 lead investigation): off by default, clamped to 0..2, and no part of the layers' uploaded numbers (the stamp stays).
        [TestMethod]
        public void TheProfileShapeIsOffByDefaultClampedAndLeavesTheStampAlone()
        {
            Clear();
            Assert.AreEqual(0f, CloudLookTuning.ProfileShape);
            long stamp = CloudLookTuning.Stamp;
            Set(CloudLookTuning.TuneProfileShape, 0.8f);
            Assert.AreEqual(0.8f, CloudLookTuning.ProfileShape);
            Assert.AreEqual(stamp, CloudLookTuning.Stamp, "the shader reads it every frame; the layers are not uploaded again");
            Set(CloudLookTuning.TuneProfileShape, 9f);
            Assert.AreEqual(2f, CloudLookTuning.ProfileShape);
            Set(CloudLookTuning.TuneProfileShape, -1f);
            Assert.AreEqual(0f, CloudLookTuning.ProfileShape);
            Set(CloudLookTuning.TuneProfileShape, float.NaN);
            Assert.AreEqual(0f, CloudLookTuning.ProfileShape);
            Clear();
            Assert.AreEqual(0f, CloudLookTuning.ProfileShape);
        }

        [TestMethod]
        public void EveryDefaultGivesTheConfiguredNumbers()
        {
            Clear();
            foreach (CloudLayerDef l in Stack())
            {
                Assert.AreEqual(l.SigmaPerM, l.EffectiveSigmaPerM, 0f, l.Name);
                Assert.AreEqual(l.EdgeSoftness, l.EffectiveEdgeSoftness, 0f);
                Assert.AreEqual(l.NoiseAmount, l.EffectiveNoiseAmount, 0f);
                Assert.AreEqual(l.NoiseScaleM, l.EffectiveNoiseScaleM, 0f);
                Assert.AreEqual(l.Coverage, l.EffectiveCoverage, 0f);
                Assert.AreEqual(l.FadeNearM, l.EffectiveFadeNearM, 0f);
                Assert.IsTrue(float.IsPositiveInfinity(l.EffectiveFadeFarM), "no fade by default");
                Assert.AreEqual(-1f, CloudLookTuning.TypeFairOf(l.Name));
                Assert.AreEqual(-1f, CloudLookTuning.TypeOverrideOf(l.Name));
                Assert.AreEqual(0f, CloudLookTuning.ShiftOf(l.Name));
                Assert.AreEqual(-1f, CloudLookTuning.MapOverrideOf(l.Name));
                Assert.AreEqual(1f, CloudLookTuning.SigmaScaleOf(l.Name));
                Assert.AreEqual(1f, CloudLookTuning.SoftnessScaleOf(l.Name));
                Assert.AreEqual(1f, CloudLookTuning.NoiseAmountScaleOf(l.Name));
                Assert.AreEqual(1f, CloudLookTuning.NoiseSizeScaleOf(l.Name));
            }
            Assert.AreEqual(0f, CloudLookTuning.DensityRemap);
            Assert.AreEqual(0f, CloudLookTuning.CoverCombine);
            Assert.AreEqual(-1f, CloudLookTuning.FarHazeForce);
            Assert.AreEqual(1f, CloudLookTuning.HazeStrengthScale);
            float nearM, farM;
            Assert.IsFalse(CloudLookTuning.FadeFor(out nearM, out farM));
        }

        [TestMethod]
        public void TheDefaultsLeaveTheCpuDensityAndTheFrameUnchanged()
        {
            Clear();
            CloudLayerDef l1 = CloudLookFixtures.Cumulus();
            var table = new LayerTypeTable(l1);
            double sum0 = 0.0;
            for (double alt = 1800.0; alt < 2300.0; alt += 25.0)
                sum0 += LayerDensity.Extinction(l1, table, alt, 0.665, 0.0);
            // The same by the configured numbers, written out: the getters add nothing at their defaults.
            double sum1 = 0.0;
            for (double alt = 1800.0; alt < 2300.0; alt += 25.0)
            {
                double share = (alt - l1.InnerAltitudeM) / l1.ThicknessM;
                sum1 += l1.SigmaPerM * LayerDensity.Shape(0.665, l1.Coverage, l1.EdgeSoftness) * table.Profile(0.0, share);
            }
            Assert.AreEqual(sum1, sum0, 0.0);
            Assert.AreEqual(sum1 > 0.0, true);
            // The noise model's share at a default parameter set equals the share with the remap field never set (a struct default).
            NoiseVolumeSet set = NoiseVolumeSet.Generate(NoiseSizes.ForLevel(0), 3u);
            NoiseLayerParams a = NoiseLayerParams.Default(5000.0), b = NoiseLayerParams.Default(5000.0);
            b.DensityRemap = 0.0;
            for (int i = 0; i < 300; i++)
            {
                double x = 600000.0 + i * 913.0, y = i * 1777.0, z = i * 331.0;
                Assert.AreEqual(CloudShapeModel.Share(set, ref a, x, y, z, 0.8, 0.35, 0.15, 0.35, 0.3, true, 40.0), CloudShapeModel.Share(set, ref b, x, y, z, 0.8, 0.35, 0.15, 0.35, 0.3, true, 40.0), 0.0);
            }
        }

        [TestMethod]
        public void TheOracleAtTheDefaultsDoesNotDependOnTheKnobsBeingRead()
        {
            Clear();
            CloudLayerDef l1 = CloudLookFixtures.Cumulus();
            var table = new LayerTypeTable(l1);
            NoiseVolumeSet set = NoiseVolumeSet.Generate(NoiseSizes.ForLevel(0), 5u);
            NoiseLayerParams prm = NoiseLayerParams.Default(l1.NoiseScaleM);
            CloudCoverOracle.Frame frame = CloudCoverOracle.Frame.Ground(24, 14, 30.0);
            var first = CloudCoverOracle.Render(l1, table, set, prm, frame, CloudCoverOracle.Constant(0.665, 0.0), l1.EffectiveCoverage, CloudCoverOracle.March.Medium);
            // Setting a key and clearing it again returns the very same picture.
            Set(CloudLookTuning.TuneSigma, 2f);
            Clear();
            var second = CloudCoverOracle.Render(l1, table, set, prm, frame, CloudCoverOracle.Constant(0.665, 0.0), l1.EffectiveCoverage, CloudCoverOracle.March.Medium);
            CollectionAssert.AreEqual(first.Tau, second.Tau);
        }

        [TestMethod]
        public void TheScaleKeysScaleTheEffectiveNumbersAndRaiseTheStamp()
        {
            Clear();
            CloudLayerDef l1 = CloudLookFixtures.Cumulus();
            long stamp = CloudCoverShift.Stamp;
            Set(CloudLookTuning.TuneSigma, 0.7f);
            Set(CloudLookTuning.TuneSoftness, 2f);
            Set(CloudLookTuning.TuneNoiseAmount, 0.7f);
            Set(CloudLookTuning.TuneNoiseSize, 0.5f);
            Assert.AreEqual(0.035f, l1.EffectiveSigmaPerM, 1e-6f);
            Assert.AreEqual(0.30f, l1.EffectiveEdgeSoftness, 1e-6f);
            Assert.AreEqual(l1.NoiseAmount * 0.7f, l1.EffectiveNoiseAmount, 1e-6f);
            Assert.AreEqual(l1.NoiseScaleM * 0.5f, l1.EffectiveNoiseScaleM, 1e-3f);
            Assert.AreEqual(l1.SigmaPerM, l1.SigmaPerM, 0f, "the configured number is untouched");
            Assert.IsTrue(CloudCoverShift.Stamp > stamp);
            long again = CloudCoverShift.Stamp;
            Assert.AreEqual(again, CloudCoverShift.Stamp, "no change, no new stamp");
            // The CPU density follows.
            var table = new LayerTypeTable(l1);
            Set(CloudLookTuning.TuneSigma, 1f);
            Set(CloudLookTuning.TuneSoftness, 1f);
            double plain = LayerDensity.Extinction(l1, table, 1800.0, 0.665, 0.0);
            Set(CloudLookTuning.TuneSigma, 0.5f);
            Assert.AreEqual(plain * 0.5, LayerDensity.Extinction(l1, table, 1800.0, 0.665, 0.0), plain * 1e-6);
        }

        [TestMethod]
        public void ThePerLayerScalesActOnTheNamedLayerOnlyAndMultiplyWithTheGeneralOnes()
        {
            Clear();
            CloudLayerDef[] stack = Stack();
            Set(CloudLookTuning.TuneSigma + ".Kerbin-cumulus", 0.7f);
            Set(CloudLookTuning.TuneSoftness + ".Kerbin-cumulus", 2f);
            Set(CloudLookTuning.TuneNoiseAmount + ".Kerbin-cumulus", 0.7f);
            Set(CloudLookTuning.TuneNoiseSize + ".Kerbin-cumulus", 0.5f);
            Assert.AreEqual(0.035f, stack[1].EffectiveSigmaPerM, 1e-6f);
            Assert.AreEqual(0.30f, stack[1].EffectiveEdgeSoftness, 1e-6f);
            Assert.AreEqual(stack[1].NoiseAmount * 0.7f, stack[1].EffectiveNoiseAmount, 1e-6f);
            Assert.AreEqual(stack[1].NoiseScaleM * 0.5f, stack[1].EffectiveNoiseScaleM, 1e-3f);
            Assert.AreEqual(stack[2].SigmaPerM, stack[2].EffectiveSigmaPerM, 0f, "another layer is untouched");
            Assert.AreEqual(stack[0].EdgeSoftness, stack[0].EffectiveEdgeSoftness, 0f);
            Set(CloudLookTuning.TuneSigma, 2f);
            Assert.AreEqual(0.07f, stack[1].EffectiveSigmaPerM, 1e-6f, "the general key multiplies on top");
            Assert.AreEqual(stack[2].SigmaPerM * 2f, stack[2].EffectiveSigmaPerM, 1e-6f);
            Clear();
            Assert.AreEqual(stack[1].SigmaPerM, stack[1].EffectiveSigmaPerM, 0f, "cleared: the configured number");
        }

        [TestMethod]
        public void TheMapOverrideKeyIsPerLayerAndOffByDefault()
        {
            Clear();
            Assert.AreEqual(-1f, CloudLookTuning.MapOverrideOf("Kerbin-cumulus"));
            long stamp = CloudLookTuning.Stamp;
            Set(CloudLookTuning.TuneMapOverride + "Kerbin-cumulus", 0.665f);
            Assert.AreEqual(0.665f, CloudLookTuning.MapOverrideOf("Kerbin-cumulus"), 1e-6f);
            Assert.AreEqual(-1f, CloudLookTuning.MapOverrideOf("Kerbin-cirrus"));
            Assert.IsTrue(CloudLookTuning.Stamp > stamp, "the module fetches the maps again");
            Set(CloudLookTuning.TuneMapOverride + "Kerbin-cumulus", -3f);
            Assert.AreEqual(-1f, CloudLookTuning.MapOverrideOf("Kerbin-cumulus"), "negative: off");
        }

        [TestMethod]
        public void TheLookSetsAreTheSixCandidatesWithTheKeysTheOracleAndThePlayerUse()
        {
            Assert.AreEqual(6, CloudLookModel.Sets.Length);
            CollectionAssert.AreEqual(new[] { "C0", "C1", "C2", "C3", "C4", "C5" }, Array.ConvertAll(CloudLookModel.Sets, s => s.Tag));
            Assert.AreEqual(0, CloudLookModel.SetByTag("C0").Keys.Length, "C0 is the current numbers: no key");
            Assert.IsTrue(CloudLookModel.SetByTag("c1").ChangesTypes);
            Assert.IsFalse(CloudLookModel.SetByTag("C3").ChangesTypes);
            Assert.AreEqual(3, CloudLookModel.Selected("C0, C4 ,CX,C4,C5").Count, "unknown tags skipped, repeats once");
            Assert.AreEqual(6, CloudLookModel.Selected(null).Count);
            // C4 applied to the cumulus layer gives the numbers of the plan's proposal table.
            Clear();
            foreach (System.Collections.Generic.KeyValuePair<string, float> k in CloudLookModel.SetByTag("C4").Keys) Tuning.SetOverride(k.Key, k.Value);
            Tuning.Instance.BeginFrame(++frameCounter);
            CloudLayerDef l1 = CloudLookFixtures.Cumulus();
            Assert.AreEqual(0.25f, l1.EffectiveCoverage, 1e-6f);
            Assert.AreEqual(0.35f, l1.EffectiveEdgeSoftness, 1e-6f);
            Assert.AreEqual(0.035f, l1.EffectiveSigmaPerM, 1e-6f);
            Assert.AreEqual(0.7f, l1.EffectiveNoiseAmount, 1e-5f);
            Assert.AreEqual(700f, l1.EffectiveNoiseScaleM, 1e-2f);
            Assert.AreEqual(CloudLookFixtures.Cumulonimbus().SigmaPerM, CloudLookFixtures.Cumulonimbus().EffectiveSigmaPerM, 0f, "the other layers keep their numbers");
        }

        // The sets' noise factors on the cfg's base: no set leaves the reader's range of the amount (0 to 1) or the cell sizes the model is made for (about 300 m to 15 km).
        [TestMethod]
        public void EveryLookSetKeepsTheCumulusNoiseInsideTheModelsRange()
        {
            foreach (LookSet set in CloudLookModel.Sets)
            {
                Clear();
                foreach (System.Collections.Generic.KeyValuePair<string, float> k in set.Keys) Tuning.SetOverride(k.Key, k.Value);
                Tuning.Instance.BeginFrame(++frameCounter);
                CloudLayerDef l1 = CloudLookFixtures.Cumulus();
                Assert.IsTrue(l1.EffectiveNoiseAmount >= 0f && l1.EffectiveNoiseAmount <= 1f, set.Tag + " amount " + l1.EffectiveNoiseAmount);
                Assert.IsTrue(l1.EffectiveNoiseScaleM >= 300f && l1.EffectiveNoiseScaleM <= 15000f, set.Tag + " scale " + l1.EffectiveNoiseScaleM);
            }
        }

        [TestMethod]
        public void TheGreatCircleWalkMovesByTheDistanceAlongTheBearing()
        {
            double lat, lon;
            CloudLookModel.Walk(0.0, 0.0, 90.0, 600000.0 * Math.PI / 180.0 * 10.0, 600000.0, out lat, out lon);
            Assert.AreEqual(0.0, lat, 1e-9);
            Assert.AreEqual(10.0, lon, 1e-9, "ten degrees of arc east along the equator");
            CloudLookModel.Walk(10.0, 20.0, 0.0, 600000.0 * Math.PI / 180.0 * 5.0, 600000.0, out lat, out lon);
            Assert.AreEqual(15.0, lat, 1e-9);
            Assert.AreEqual(20.0, lon, 1e-9, "north along a meridian");
            CloudLookModel.Walk(0.0, 175.0, 90.0, 600000.0 * Math.PI / 180.0 * 10.0, 600000.0, out lat, out lon);
            Assert.AreEqual(-175.0, lon, 1e-9, "the longitude wraps");
            CloudLookModel.Walk(30.0, 40.0, 123.0, 0.0, 600000.0, out lat, out lon);
            Assert.AreEqual(30.0, lat, 1e-9);
            Assert.AreEqual(40.0, lon, 1e-9);
        }

        [TestMethod]
        public void TheLookExpectationFollowsTheMapAndTheShiftAndEqualsTheOracleForOneLayer()
        {
            Clear();
            CloudLayerDef l1 = CloudLookFixtures.Cumulus();
            double bx, by, bz;
            NoiseSeeds.BaseOffset(1u, 0, out bx, out by, out bz);
            Func<double, T09LookInput> make = m => new T09LookInput
            {
                Layers = new[] { l1 },
                Tables = new[] { new LayerTypeTable(l1) },
                Noise = new[] { new NoiseLayerParams { ScaleM = l1.NoiseScaleM, Untile = true, Curl = true, OffsetX = bx, OffsetY = by, OffsetZ = bz, Seed = NoiseSeeds.LayerSeed(1u, 0), SphereScale = 1.0, ReferenceRadiusM = 600000.0 } },
                Cover = new[] { m },
                Type = new[] { 0.0 }
            };
            LookExpectation low = CloudLookModel.Expect(make(0.3), 264.0, 18.0, 60.0, 4, 24, 14);
            LookExpectation high = CloudLookModel.Expect(make(0.9), 264.0, 18.0, 60.0, 4, 24, 14);
            Assert.IsTrue(low.Mean < high.Mean, low.Mean + " < " + high.Mean);
            Assert.IsTrue(high.Mean > 0.9, "a map of 0.9 closes the sky");
            Assert.AreEqual(4, low.Sites);
            Assert.IsTrue(low.P10 <= low.Mean && low.Mean <= low.P90 + 1e-9);
            Set(CloudCoverShift.TuneShift, -0.2f);
            LookExpectation shifted = CloudLookModel.Expect(make(0.9), 264.0, 18.0, 60.0, 4, 24, 14);
            Assert.IsTrue(shifted.Mean < high.Mean, "the shift opens the sky in the expectation too");
        }

        [TestMethod]
        public void TheLayerShiftActsOnTheNamedLayerOnTopOfTheGeneralShift()
        {
            Clear();
            CloudLayerDef[] stack = Stack();
            Set(CloudLookTuning.TuneLayerShift + "Kerbin-cumulus", -0.10f);
            Assert.AreEqual(0.25f, stack[1].EffectiveCoverage, 1e-6f);
            Assert.AreEqual(0.50f, stack[0].EffectiveCoverage, 1e-6f);
            Assert.AreEqual(0.30f, stack[3].EffectiveCoverage, 1e-6f);
            Set(CloudCoverShift.TuneShift, -0.05f);
            Assert.AreEqual(0.20f, stack[1].EffectiveCoverage, 1e-6f);
            Assert.AreEqual(0.45f, stack[0].EffectiveCoverage, 1e-6f);
            Set(CloudLookTuning.TuneLayerShift + "Kerbin-cumulus", -0.9f);
            Assert.AreEqual(0f, stack[1].EffectiveCoverage, "clamped at 0");
        }

        [TestMethod]
        public void TheTypeKeysGiveTheFairCurveAndTheOverride()
        {
            Assert.AreEqual(0.4f, CloudLookTuning.TypeValue(0.4f, -1f, -1f), 0f, "default: the precipitation itself");
            Assert.AreEqual(0.25f, CloudLookTuning.TypeValue(0f, 0.25f, -1f), 1e-6f, "fair weather: the fair type");
            Assert.AreEqual(1f, CloudLookTuning.TypeValue(1f, 0.25f, -1f), 1e-6f, "storm: the last type");
            Assert.AreEqual(0.25f + 0.75f * 0.2f, CloudLookTuning.TypeValue(0.2f, 0.25f, -1f), 1e-6f);
            Assert.AreEqual(0.6f, CloudLookTuning.TypeValue(0.9f, 0.25f, 0.6f), "the override wins");
            byte[] precip = { 0, 51, 255 }, scratch = null;
            Assert.AreSame(precip, CloudLookTuning.TypeBytes(precip, -1f, -1f, ref scratch), "default: the same array, no copy");
            byte[] fair = CloudLookTuning.TypeBytes(precip, 0.25f, -1f, ref scratch);
            CollectionAssert.AreEqual(new byte[] { 64, (byte)(64 + (int)Math.Round(0.75 * 51)), 255 }, fair);
            Clear();
            Set(CloudLookTuning.TuneTypeFair + "Kerbin-cumulus", 0.25f);
            Set(CloudLookTuning.TuneTypeOverride + "Kerbin-cumulonimbus", 0.5f);
            Assert.AreEqual(0.25f, CloudLookTuning.TypeFairOf("Kerbin-cumulus"));
            Assert.AreEqual(-1f, CloudLookTuning.TypeFairOf("Kerbin-cirrus"));
            Assert.AreEqual(0.5f, CloudLookTuning.TypeOverrideOf("Kerbin-cumulonimbus"));
        }

        [TestMethod]
        public void TheRainTypeEndsThePrecipitationAxisAtTheTypeOfFullPrecipitation()
        {
            // E40 B3: fair weather shows the fair type, full precipitation the rain type, a straight line between; rainType 1 is the old curve.
            Assert.AreEqual(0.25f, CloudLookTuning.TypeValue(0f, 0.25f, -1f, 0.75f), 1e-6f);
            Assert.AreEqual(0.75f, CloudLookTuning.TypeValue(1f, 0.25f, -1f, 0.75f), 1e-6f);
            Assert.AreEqual(0.5f, CloudLookTuning.TypeValue(0.5f, 0.25f, -1f, 0.75f), 1e-6f);
            Assert.AreEqual(CloudLookTuning.TypeValue(0.3f, 0.25f, -1f), CloudLookTuning.TypeValue(0.3f, 0.25f, -1f, 1f), 0f, "the default rain type is the old curve");
            Assert.AreEqual(0.3f, CloudLookTuning.TypeValue(0.6f, -1f, -1f, 0.5f), 1e-6f, "without a fair type the precipitation times the rain type");
            Assert.AreEqual(0.6f, CloudLookTuning.TypeValue(0.9f, 0.25f, 0.6f, 0.75f), "the override still wins");
            byte[] precip = { 0, 255 }, scratch = null;
            byte[] bytes = CloudLookTuning.TypeBytes(precip, 0.25f, -1f, ref scratch, 0.75f);
            CollectionAssert.AreEqual(new byte[] { 64, 191 }, bytes);
            Assert.AreSame(precip, CloudLookTuning.TypeBytes(precip, -1f, -1f, ref scratch, 1f), "no fair type, no override, rain type 1: the precipitation bytes themselves");
        }

        [TestMethod]
        public void TheShippedCumulusLayerShowsHumilisInFairWeatherAndCongestusInHeavyRain()
        {
            CloudLayerSpec cumulus = ReDefinition.Tests.Atmospheres.T10.CloudLayerSchemaTests.ParseKerbin().Climate.Clouds.Layers.Single(l => l.Name == "Kerbin-cumulus");
            int last = cumulus.Types.Count - 1;
            Func<float, string> at = precipitation => cumulus.Types[(int)Math.Round(CloudLookTuning.TypeValue(precipitation, cumulus.FairType, -1f, cumulus.RainType) * last)].Name;
            Assert.AreEqual("Cumulus humilis", at(0f), "fair weather");
            Assert.AreEqual("Cumulus mediocris", at(0.5f), "moderate rain");
            Assert.AreEqual("Cumulus congestus", at(1f), "the heaviest rain: a tower, not the middle-level sheet that ends the table");
            Assert.AreEqual("Altocumulus", cumulus.Types[last].Name, "the table still ends with the altocumulus sheet (drawn where a rule picks it, not by precipitation)");
        }

        [TestMethod]
        public void TheCoverCombinationMakesTheUnionOfTheLayersMeetTheFieldsCover()
        {
            // The low pick of K-T2d: maps 0.406 / 0.665 / 0.110 / 0.501 against the field's cover 0.473 (union 0.91 by random overlap).
            byte[][] maps =
            {
                new[] { (byte)Math.Round(0.406 * 255) }, new[] { (byte)Math.Round(0.665 * 255) }, new[] { (byte)Math.Round(0.110 * 255) }, new[] { (byte)Math.Round(0.501 * 255) }
            };
            byte[] shared = { (byte)Math.Round(0.473 * 255) };
            double keep = 1.0;
            for (int l = 0; l < 4; l++) keep *= 1.0 - maps[l][0] / 255.0;
            Assert.AreEqual(0.91, 1.0 - keep, 0.01, "today's union");
            byte[][] untouched = { (byte[])maps[0].Clone(), (byte[])maps[1].Clone(), (byte[])maps[2].Clone(), (byte[])maps[3].Clone() };
            CloudLookTuning.CombineCover(maps, 4, shared, 0f);
            for (int l = 0; l < 4; l++) CollectionAssert.AreEqual(untouched[l], maps[l], "weight 0 changes nothing");
            CloudLookTuning.CombineCover(maps, 4, shared, 1f);
            keep = 1.0;
            for (int l = 0; l < 4; l++) keep *= 1.0 - maps[l][0] / 255.0;
            Assert.AreEqual(0.473, 1.0 - keep, 0.02, "the union meets the cover");
            Assert.IsTrue(maps[1][0] < untouched[1][0], "the layers are thinned");
            // A union already under the cover is not raised.
            byte[][] thin = { new byte[] { 20 }, new byte[] { 10 } };
            CloudLookTuning.CombineCover(thin, 2, new byte[] { 200 }, 1f);
            CollectionAssert.AreEqual(new byte[] { 20 }, thin[0]);
        }

        [TestMethod]
        public void TheFadeKeyGivesLayersWithoutFadeADistanceFadeAndKeepsTheirOwn()
        {
            Clear();
            CloudLayerDef l1 = CloudLookFixtures.Cumulus();
            Set(CloudLookTuning.TuneFadeFar, 100000f);
            Assert.AreEqual(50000f, l1.EffectiveFadeNearM, 1e-3f);
            Assert.AreEqual(100000f, l1.EffectiveFadeFarM, 1e-3f);
            Assert.AreEqual(1.0, LayerDensity.DistanceFade(l1, 40000.0), 1e-12);
            Assert.AreEqual(0.0, LayerDensity.DistanceFade(l1, 100000.0), 1e-12);
            Assert.AreEqual(0.5, LayerDensity.DistanceFade(l1, 75000.0), 1e-9);
            l1.FadeNearM = 10000f;
            l1.FadeFarM = 20000f;
            Assert.AreEqual(20000f, l1.EffectiveFadeFarM, "a layer's own fade wins");
        }

        [TestMethod]
        public void TheDensityRemapThinsAClosedDeckInsideAndLeavesTheDefaultAlone()
        {
            Clear();
            NoiseVolumeSet set = NoiseVolumeSet.Generate(NoiseSizes.ForLevel(0), 3u);
            NoiseLayerParams plain = NoiseLayerParams.Default(5000.0), remap = NoiseLayerParams.Default(5000.0);
            remap.DensityRemap = 0.6;
            int closedPlain = 0, thinned = 0, equalOutside = 0;
            for (int i = 0; i < 400; i++)
            {
                double x = 600000.0 + i * 977.0, y = i * 1493.0, z = i * 211.0;
                // map 1.0: the soft edge is far below the noise, the share is 1 everywhere (a closed deck).
                double a = CloudShapeModel.Share(set, ref plain, x, y, z, 1.0, 0.7, 0.15, 0.2, 0.5, true, 40.0);
                double b = CloudShapeModel.Share(set, ref remap, x, y, z, 1.0, 0.7, 0.15, 0.2, 0.5, true, 40.0);
                if (a >= 1.0) closedPlain++;
                if (b < a) thinned++;
                Assert.IsTrue(b <= a + 1e-12 && b >= 0.0);
                // map 0: nothing there, the remap adds nothing.
                if (CloudShapeModel.Share(set, ref remap, x, y, z, 0.0, 0.1, 0.15, 0.2, 0.5, true, 40.0) == 0.0) equalOutside++;
            }
            Assert.AreEqual(400, closedPlain, "a closed deck keeps the share 1 without the remap");
            Assert.IsTrue(thinned > 380, "the remap thins the interior (" + thinned + ")");
            Assert.AreEqual(400, equalOutside);
            Assert.AreEqual(1.0 - 0.6 * 0.5 * (1.0 - 0.4), CloudShapeModel.InteriorThinning(0.6, 0.5, 0.4), 1e-12);
            // The far view's mean follows: the same factor.
            double m0 = FarViewMath.ShapeMean(1.0, 0.7, 0.15, 0.2, 0.5, 0.45, 1.0, 4.0), m1 = FarViewMath.ShapeMean(1.0, 0.7, 0.15, 0.2, 0.5, 0.45, 1.0, 4.0, 0.6);
            Assert.AreEqual(m0 * CloudShapeModel.InteriorThinning(0.6, 0.5, 0.45), m1, 1e-12);
            Assert.AreEqual(m0, FarViewMath.ShapeMean(1.0, 0.7, 0.15, 0.2, 0.5, 0.45, 1.0, 4.0, 0.0), 0.0, "default 0: unchanged");
        }

        [TestMethod]
        public void TheHazeKeysDefaultToTheSettings()
        {
            Clear();
            var far = new FarViewSettings();
            OwnCloudOptions.Apply(far, key => 0.0);
            Assert.IsFalse(far.Haze);
            Assert.AreEqual(1f, far.HazeStrength);
            Set(CloudLookTuning.TuneFarHaze, 1f);
            Set(CloudLookTuning.TuneHazeStrength, 0.5f);
            OwnCloudOptions.Apply(far, key => 0.0);
            Assert.IsTrue(far.Haze);
            Assert.AreEqual(0.5f, far.HazeStrength);
            Clear();
            OwnCloudOptions.Apply(far, key => 1.0);
            Assert.IsTrue(far.Haze, "the setting decides again");
            Assert.AreEqual(1f, far.HazeStrength);
            Set(CloudLookTuning.TuneFarHaze, 0f);
            OwnCloudOptions.Apply(far, key => 1.0);
            Assert.IsFalse(far.Haze, "forced off");
        }
    }
}
