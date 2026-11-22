using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
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

        private static void Frame(int n, bool fast)
        {
            CloudFastPath.Source = () => fast;
            Tuning.Instance.BeginFrame(n);
        }

        [TestCleanup]
        public void Restore()
        {
            Tuning.ClearOverrides();
            CloudFastPath.Source = () => TierRows.For(QualityProfile.Effective()).CloudFastPath;
            Tuning.Instance.BeginFrame(9300099);
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

        [TestMethod]
        public void TheFastPathSetsTheDefaultsOfItsKeys()
        {
            Tuning.ClearOverrides();
            Frame(9300001, true);
            var frame = new LayerFrame();
            OwnCloudOptions.Apply(frame, Default);
            Assert.AreEqual(CloudFastPath.SpanGrowthPerM, frame.SpanGrowthPerM);
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
            Assert.IsTrue(CloudLookTuning.LightVolume);
            Assert.AreEqual(0f, CloudLookTuning.LvHandOverM);
            Assert.AreEqual(1, CloudLookTuning.LvFarTaps);
        }

        [TestMethod]
        public void TheFullPathKeepsTheDefaultsOfBefore()
        {
            Tuning.ClearOverrides();
            Frame(9300002, false);
            var frame = new LayerFrame();
            OwnCloudOptions.Apply(frame, Default);
            Assert.AreEqual(0f, frame.SpanGrowthPerM);
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
            Assert.IsFalse(CloudLookTuning.LightVolume);
            Assert.AreEqual(8000f, CloudLookTuning.LvHandOverM);
            Assert.AreEqual(3, CloudLookTuning.LvFarTaps);
        }

        [TestMethod]
        public void AKeySetByHandWinsOverTheFastPath()
        {
            Tuning.ClearOverrides();
            Tuning.SetOverride(OwnCloudOptions.TuneSpanGrowthPerM, 0f);
            Tuning.SetOverride(OwnCloudOptions.TuneReconRotate, 0f);
            Tuning.SetOverride(CloudLookTuning.TuneLightVolume, 0f);
            Frame(9300003, true);
            var frame = new LayerFrame();
            OwnCloudOptions.Apply(frame, Default);
            Assert.AreEqual(0f, frame.SpanGrowthPerM);
            var recon = new ReconSettings();
            OwnCloudOptions.Apply(recon, Default);
            Assert.IsFalse(recon.Rotation);
            Assert.IsFalse(CloudLookTuning.LightVolume);
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
