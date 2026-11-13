using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T16
{
    // The settings of the visibility culling and the distance LOD (package T16): every switch is off by default, has a help text, a tier reason and a
    // matrix row, reaches the frame's CullSettings, and the detail LOD takes the far shape mean with it.
    [TestClass]
    public class CullOptionsTests
    {
        private static readonly string[] Keys =
        {
            "cloudSkipEmpty", "cloudSkipClosed", "cloudLayerDistanceCut", "cloudStepGrowth", "cloudTapLod", "cloudDetailLod", "cloudLayerCapDistance"
        };

        private static Func<string, double> Values(params object[] pairs)
        {
            var overrides = new Dictionary<string, double>(StringComparer.Ordinal);
            for (int i = 0; i < pairs.Length; i += 2) overrides[(string)pairs[i]] = Convert.ToDouble(pairs[i + 1]);
            return key =>
            {
                double value;
                if (overrides.TryGetValue(key, out value)) return value;
                SettingDef def = AtmospheresSettings.Definition(key);
                return def.Kind == SettingKind.Text ? 0.0 : def.Default;
            };
        }

        [TestMethod]
        public void EverySwitchIsOffByDefaultAndHasHelpAndATierReason()
        {
            foreach (string key in Keys)
            {
                SettingDef def = AtmospheresSettings.Definition(key);
                Assert.AreEqual(0.0, def.Default, key + " is off until the owner agrees");
                Assert.IsTrue(TierRows.NotSet.ContainsKey(key), key + " says why no tier sets it");
                string help = SettingHelp.For(key);
                Assert.IsTrue(help != null && help.Length > 20, key + " has a help text");
                Assert.AreEqual(AtmospheresSettings.GreyReasons.NeedsOwnClouds, def.GreyReason, key);
            }
        }

        [TestMethod]
        public void TheSwitchesReachTheFramesCullSettings()
        {
            var frame = new LayerFrame();
            OwnCloudOptions.Apply(frame, Values());
            Assert.IsFalse(frame.Cull.AnyOn, "all off by default");
            Assert.IsFalse(frame.Cull.TilesNeeded);
            OwnCloudOptions.Apply(frame, Values("cloudSkipEmpty", 1));
            Assert.IsTrue(frame.Cull.SkipEmpty && frame.Cull.TilesNeeded && !frame.Cull.SkipClosed);
            OwnCloudOptions.Apply(frame, Values("cloudSkipClosed", 1));
            Assert.IsTrue(frame.Cull.SkipClosed && frame.Cull.TilesNeeded && !frame.Cull.SkipEmpty);
            OwnCloudOptions.Apply(frame, Values("cloudLayerDistanceCut", 1, "cloudStepGrowth", 1, "cloudTapLod", 1, "cloudDetailLod", 1));
            Assert.IsTrue(frame.Cull.DistanceCut && frame.Cull.StepGrowth && frame.Cull.TapLod && frame.Cull.DetailLod && !frame.Cull.TilesNeeded);
            OwnCloudOptions.Apply(frame, Values("cloudLayerCapDistance", 120));
            Assert.AreEqual(120000f, frame.Cull.CapDistanceM);
            Assert.IsTrue(frame.Cull.AnyOn);
            OwnCloudOptions.Apply(frame, Values("cloudLayerCapDistance", 9999));
            Assert.AreEqual(400000f, frame.Cull.CapDistanceM, "clamped to the setting's range");
            OwnCloudOptions.Apply(frame, Values());
            Assert.IsFalse(frame.Cull.AnyOn, "a call after a change leaves nothing over");
        }

        [TestMethod]
        public void TheDetailLodSwitchesTheFarShapeMeanOnAndNothingElseDoes()
        {
            var far = new FarViewSettings();
            OwnCloudOptions.Apply(far, Values());
            Assert.IsFalse(far.ShapeMean);
            OwnCloudOptions.Apply(far, Values("cloudDetailLod", 1));
            Assert.IsTrue(far.ShapeMean);
            OwnCloudOptions.Apply(far, Values("cloudSkipEmpty", 1, "cloudStepGrowth", 1));
            Assert.IsFalse(far.ShapeMean);
        }
    }
}
