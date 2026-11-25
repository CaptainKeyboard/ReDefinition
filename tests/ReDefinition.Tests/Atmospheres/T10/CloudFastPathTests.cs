using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Atmospheres.Viewer;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.T10
{
    // The fast cloud path of the cheap tiers (owner 2026-11-22, PRUEFPROTOKOLL 68): the tier row names it, it sets the defaults of the keys that make it, and a key
    // set by hand still wins.
    [TestClass]
    public class CloudFastPathTests
    {
        private static double Default(string key)
        {
            return AtmospheresSettings.Definition(key).Default;
        }

        // The settings as a tier's row writes them (QualityProfile.Apply), the defaults for the rest.
        private static Func<string, double> TierSettings(QualityTier tier)
        {
            TierRow row = TierRows.For(tier);
            return key => key == AtmospheresSettings.Keys.LayerStepQuality ? (int)row.CloudStepQuality
                : key == AtmospheresSettings.Keys.CloudShadowQuality ? (int)row.CloudShadowQuality
                : key == AtmospheresSettings.Keys.CloudSamples ? row.CloudSamples : Default(key);
        }

        private static void Frame(int n, bool fast)
        {
            CloudFastPath.Source = () => fast;
            Tuning.Instance.BeginFrame(n);
        }

        [TestCleanup]
        public void Restore()
        {
            Tuning.ClearOverrides();
            CloudFastPath.Source = () => false;
            Tuning.Instance.BeginFrame(9300099);
        }

        [TestMethod]
        public void TheFastPathsClipIsTheFullPathsOneSigma()
        {
            Assert.AreEqual(OwnCloudOptions.ClipSigmaDefault, CloudFastPath.ClipSigma, "the 2-sigma clip drew the grid (PRUEFPROTOKOLL 77); a literal, so the player links the file alone");
        }

        [TestMethod]
        public void LowAndMediumTakeTheFastPathWithTheLowShadowAndTheHigherTiersKeepTheFullMarch()
        {
            Assert.IsTrue(TierRows.For(QualityTier.Low).CloudFastPath);
            Assert.IsTrue(TierRows.For(QualityTier.Medium).CloudFastPath);
            Assert.AreEqual(CloudDetailLevel.Low, TierRows.For(QualityTier.Medium).CloudShadowQuality);
            foreach (QualityTier tier in new[] { QualityTier.High, QualityTier.Ultra, QualityTier.Max })
            {
                Assert.IsFalse(TierRows.For(tier).CloudFastPath, tier.ToString());
                Assert.AreEqual(CloudDetailLevel.Medium, TierRows.For(tier).CloudShadowQuality, tier.ToString());
            }
            CollectionAssert.Contains(TierRows.SetKeys, AtmospheresSettings.Keys.CloudShadowQuality);
            Assert.IsFalse(TierRows.NotSet.ContainsKey(AtmospheresSettings.Keys.CloudShadowQuality));
        }

        // 2026-11-23 (the owner: the quality settings act per graphics profile, the picture of each tier stays): every tier's march and light as they were before the
        // settings acted: the shortest step 12 m with the fast path and 6 m without, 8 taps over 12 km towards the sun, the grid points of the tier's cloudSamples.
        [TestMethod]
        public void EveryTiersMarchAndLightAreThoseOfBeforeTheSettingsActed()
        {
            Tuning.ClearOverrides();
            foreach (QualityTier tier in TierRows.All)
            {
                bool fast = TierRows.For(tier).CloudFastPath;
                Frame(9300020 + (int)tier, fast);
                var frame = new LayerFrame();
                OwnCloudOptions.Apply(frame, TierSettings(tier));
                Assert.AreEqual(fast ? 12f : 6f, frame.MinStepM, tier + " shortest step");
                Assert.AreEqual(12000f, frame.SunMarchM, tier + " sun march");
                Assert.AreEqual(tier == QualityTier.Low ? 48 : 256, frame.MaxSteps, tier + " grid points");
                var light = new CloudLightSettings();
                OwnCloudOptions.Apply(light, TierSettings(tier));
                Assert.AreEqual(8, light.SunStepsOverride, tier + " sun taps");
            }
            CollectionAssert.Contains(TierRows.SetKeys, AtmospheresSettings.Keys.LayerStepQuality);
            Assert.IsFalse(TierRows.NotSet.ContainsKey(AtmospheresSettings.Keys.LayerStepQuality));
            Assert.IsTrue(TierRows.Revision >= 3, "a changed row raises the revision, so the module writes it once more");
        }

        // The three settings act on every tier (they were overwritten by the look's constants until 2026-11-23, antipattern review F1).
        [TestMethod]
        public void TheStepLightAndSunMarchSettingsAct()
        {
            Tuning.ClearOverrides();
            foreach (bool fast in new[] { true, false })
            {
                Frame(fast ? 9300030 : 9300031, fast);
                Func<string, double> low = k => k == AtmospheresSettings.Keys.LayerStepQuality || k == AtmospheresSettings.Keys.CloudLightQuality ? 0.0
                    : k == AtmospheresSettings.Keys.CloudShortSunMarch ? 1.0 : Default(k);
                var frame = new LayerFrame();
                OwnCloudOptions.Apply(frame, low);
                Assert.AreEqual(LayerSwitches.MinStepLowM, frame.MinStepM, "step quality Low");
                Assert.AreEqual(LayerFrame.SunMarchShortM, frame.SunMarchM, "the short sun march");
                var light = new CloudLightSettings();
                OwnCloudOptions.Apply(light, low);
                Assert.AreEqual(CloudLightSettings.SunTapsLow, light.SunStepsOverride, "light quality Low");
                Func<string, double> medium = k => k == AtmospheresSettings.Keys.CloudLightQuality ? 1.0 : Default(k);
                OwnCloudOptions.Apply(light, medium);
                Assert.AreEqual(CloudLightSettings.SunTapsMedium, light.SunStepsOverride, "light quality Medium");
            }
        }

        [TestMethod]
        public void TheFastPathSetsTheDefaultsOfItsKeys()
        {
            Tuning.ClearOverrides();
            Frame(9300001, true);
            var frame = new LayerFrame();
            OwnCloudOptions.Apply(frame, Default);
            Assert.AreEqual(CloudFastPath.SpanGrowthPerM, frame.SpanGrowthPerM);
            OwnCloudOptions.Apply(frame, TierSettings(QualityTier.Medium));
            Assert.AreEqual(12f, frame.MinStepM, "the Medium tier's step quality: the fast path's 12 m of before (PRUEFPROTOKOLL 79)");
            var recon = new ReconSettings();
            OwnCloudOptions.Apply(recon, Default);
            Assert.IsTrue(recon.Rotation, "the rotation is on");
            Assert.AreEqual(4, recon.Block);
            Assert.IsTrue(recon.TrueDistance);
            Assert.IsTrue(recon.Age);
            Assert.IsTrue(recon.ProcMarch);
            Assert.AreEqual(CloudFastPath.DistanceReject, recon.DistanceReject);
            Assert.AreEqual(CloudFastPath.ClipSigma, recon.ClipSigma);
            var shadow = new CloudShadowFrame();
            OwnCloudOptions.Apply(shadow, Default);
            Assert.AreEqual(4, shadow.Divisor);
        }

        [TestMethod]
        public void TheFullPathKeepsTheDefaultsOfBefore()
        {
            Tuning.ClearOverrides();
            Frame(9300002, false);
            var frame = new LayerFrame();
            OwnCloudOptions.Apply(frame, Default);
            Assert.AreEqual(0f, frame.SpanGrowthPerM);
            OwnCloudOptions.Apply(frame, TierSettings(QualityTier.High));
            Assert.AreEqual(6f, frame.MinStepM, "the High tier's step quality: the owner's look of 2026-11-19");
            var recon = new ReconSettings();
            OwnCloudOptions.Apply(recon, Default);
            Assert.AreEqual(OwnCloudOptions.TrueDistanceDefault, recon.TrueDistance);
            Assert.AreEqual(OwnCloudOptions.AgeDefault, recon.Age);
            Assert.AreEqual(OwnCloudOptions.MarchTriangleDefault, recon.ProcMarch);
            Assert.AreEqual(OwnCloudOptions.DistanceRejectDefault, recon.DistanceReject);
            Assert.AreEqual(OwnCloudOptions.ClipSigmaDefault, recon.ClipSigma);
            var shadow = new CloudShadowFrame();
            OwnCloudOptions.Apply(shadow, Default);
            Assert.AreEqual((int)OwnCloudOptions.ShadowResolutionDefault, shadow.Divisor);
        }

        [TestMethod]
        public void AKeySetByHandWinsOverTheFastPath()
        {
            Tuning.ClearOverrides();
            Tuning.SetOverride(OwnCloudOptions.TuneSpanGrowthPerM, 0f);
            Tuning.SetOverride(OwnCloudOptions.TuneReconRotate, 0f);
            Frame(9300003, true);
            var frame = new LayerFrame();
            OwnCloudOptions.Apply(frame, Default);
            Assert.AreEqual(0f, frame.SpanGrowthPerM);
            var recon = new ReconSettings();
            OwnCloudOptions.Apply(recon, Default);
            Assert.IsFalse(recon.Rotation);
        }

        [TestMethod]
        public void AChangeOfTheTierActsWithoutAChangeOfAnyKey()
        {
            Tuning.ClearOverrides();
            Frame(9300005, true);
            Assert.IsTrue(CloudFastPath.On);
            CloudFastPath.Source = () => false;
            Assert.IsFalse(CloudFastPath.On, "the same tuning frame, the tier changed: the full path at once");
            var frame = new LayerFrame();
            OwnCloudOptions.Apply(frame, Default);
            Assert.AreEqual(0f, frame.SpanGrowthPerM);
        }

        [TestMethod]
        public void ATierThatCannotBeReadGivesTheFullPath()
        {
            Tuning.ClearOverrides();
            CloudFastPath.Source = () => { throw new InvalidOperationException("no host"); };
            Tuning.Instance.BeginFrame(9300004);
            Assert.IsFalse(CloudFastPath.On);
        }

        // The viewer's registry shows the default in force (it used to show the full path's on every tier: the shortest step 6 m, the rotation off, ... on a Medium
        // tier that runs the fast path).
        [TestMethod]
        public void TheViewersDefaultsAreThoseOfTheTierInForce()
        {
            Tuning.ClearOverrides();
            var fast = new System.Collections.Generic.Dictionary<string, float>
            {
                { OwnCloudOptions.TuneSpanGrowthPerM, CloudFastPath.SpanGrowthPerM }, { OwnCloudOptions.TuneShadowResolution, CloudFastPath.ShadowResolution },
                { OwnCloudOptions.TuneReconRotate, CloudFastPath.ReconRotate }, { OwnCloudOptions.TuneTrueDistance, 1f }, { OwnCloudOptions.TuneAge, 1f },
                { OwnCloudOptions.TuneMarchTriangle, 1f }, { OwnCloudOptions.TuneDistanceReject, CloudFastPath.DistanceReject }, { OwnCloudOptions.TuneClipSigma, CloudFastPath.ClipSigma }
            };
            Frame(9300010, true);
            foreach (var kv in fast)
                Assert.AreEqual(kv.Value, CloudKeyRegistry.Find(kv.Key).Default, 1e-6, kv.Key + " on the fast path");
            Frame(9300011, false);
            foreach (var kv in fast)
            {
                CloudKey key = CloudKeyRegistry.Find(kv.Key);
                Assert.AreEqual(key.CompiledDefault, key.Default, 1e-6, kv.Key + " on the full path is the compiled default");
            }
        }

        [TestMethod]
        public void TheMarkerCarriesTheTableRevisionAndAnOldMarkerHasNone()
        {
            Assert.AreEqual(0, QualityProfileModule.ParseRevision(null));
            var old = new ConfigNode("root");
            old.AddValue(QualityProfileModule.MarkerKey, "Medium");
            Assert.AreEqual(0, QualityProfileModule.ParseRevision(old), "a marker from before the revision is written once more");
            var now = new ConfigNode("root");
            now.AddValue(QualityProfileModule.MarkerKey, "Medium");
            now.AddValue(QualityProfileModule.RevisionKey, TierRows.Revision.ToString());
            Assert.AreEqual(TierRows.Revision, QualityProfileModule.ParseRevision(now));
            Assert.IsTrue(TierRows.Revision >= 2);
        }
    }
}
