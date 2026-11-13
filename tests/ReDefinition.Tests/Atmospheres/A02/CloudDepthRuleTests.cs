using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.A02
{
    // Which optical depth the sky and the sun dimming take: a hook that says 0 is a clear sky and does not fall back to the field's
    // value; only an absent hook or one that cannot read the place does.
    [TestClass]
    public class CloudDepthRuleTests
    {
        private static Func<double, double, double, double, float> Hook(float value)
        {
            return (la, lo, alt, ut) => value;
        }

        [TestMethod]
        public void NoHookTakesTheFieldsValue()
        {
            Assert.AreEqual(2.5f, CloudDepthRule.Resolve(null, 0, 0, 0, 0, 2.5f));
        }

        [TestMethod]
        public void AHookThatSaysZeroIsAClearSkyNotAFallback()
        {
            Assert.AreEqual(0f, CloudDepthRule.Resolve(Hook(0f), 0, 0, 0, 0, 2.5f));
        }

        [TestMethod]
        public void APositiveReadingCounts()
        {
            Assert.AreEqual(3.2f, CloudDepthRule.Resolve(Hook(3.2f), 0, 0, 0, 0, 2.5f));
        }

        [TestMethod]
        public void AHookThatCannotReadThePlaceFallsBackToTheField()
        {
            Assert.AreEqual(2.5f, CloudDepthRule.Resolve(Hook(CloudDepthRule.Unreadable), 0, 0, 0, 0, 2.5f));
            Assert.AreEqual(2.5f, CloudDepthRule.Resolve(Hook(float.NaN), 0, 0, 0, 0, 2.5f));
            Assert.AreEqual(2.5f, CloudDepthRule.Resolve(Hook(float.PositiveInfinity), 0, 0, 0, 0, 2.5f));
        }

        [TestMethod]
        public void TheHookGetsThePlaceAndTheTime()
        {
            double[] seen = null;
            Func<double, double, double, double, float> hook = (la, lo, alt, ut) => { seen = new[] { la, lo, alt, ut }; return 0.5f; };
            CloudDepthRule.Resolve(hook, 10, 20, 30, 40, 1f);
            CollectionAssert.AreEqual(new[] { 10.0, 20.0, 30.0, 40.0 }, seen);
        }

        [TestMethod]
        public void TheOwnHooksUnboundAnswerIsUnreadable()
        {
            OwnCloudOpticalDepth.Bind(null);
            Assert.AreEqual(CloudDepthRule.Unreadable, OwnCloudOpticalDepth.Read(0, 0, 0, 0));
        }

        [TestMethod]
        public void TheHostsHookUsesTheSameUnreadableValue()
        {
            // RtEveClouds (the main mod) answers -1 for a layer it cannot read; the atmospheres' rule must read that as "cannot".
            Assert.AreEqual(ReDefinition.Raytracing.RtEveClouds.OpticalDepthUnreadable, CloudDepthRule.Unreadable);
        }
    }
}
