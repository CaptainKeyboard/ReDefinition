using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.C23
{
    // The detection of EVE's failing renderer (EVE without Scatterer, RECON run2 and run3) and the fall back to cloudSource Ours.
    [TestClass]
    public class EveRenderCompatTests
    {
        [TestMethod]
        public void EveWithoutScattererAndWithVolumetricLayersIsBlockedAtTheFirstLook()
        {
            string why;
            Assert.AreEqual(EveRenderStatus.NoScatterer, EveRenderCompat.Decide(true, false, 8, false, out why));
            StringAssert.Contains(why, "Scatterer");
            StringAssert.Contains(why, "8 volumetric");
        }

        [TestMethod]
        public void EveWithScattererIsReadyWhateverTheLayerCount()
        {
            string why;
            Assert.AreEqual(EveRenderStatus.Ready, EveRenderCompat.Decide(true, true, 8, false, out why));
            Assert.AreEqual(EveRenderStatus.Ready, EveRenderCompat.Decide(true, true, 0, false, out why));
        }

        [TestMethod]
        public void EveWithoutScattererAndWithoutVolumetricLayersIsReady()
        {
            // The failing pass belongs to the raymarch material: a pack with 2D layers only never runs it.
            string why;
            Assert.AreEqual(EveRenderStatus.Ready, EveRenderCompat.Decide(true, false, 0, false, out why));
        }

        [TestMethod]
        public void WithoutEveNothingIsBlocked()
        {
            string why;
            Assert.AreEqual(EveRenderStatus.NotUsable, EveRenderCompat.Decide(false, false, 8, true, out why));
        }

        [TestMethod]
        public void TheErrorLinesBlockEvenWithScatterer()
        {
            string why;
            Assert.AreEqual(EveRenderStatus.RenderFailing, EveRenderCompat.Decide(true, true, 8, true, out why));
            StringAssert.Contains(why, RenderFailureCounter.Threshold.ToString());
        }

        [TestMethod]
        public void TheCounterTripsOnTheInvalidPassLineOnlyAfterTheThreshold()
        {
            var counter = new RenderFailureCounter();
            const string line = "Invalid pass number (6) for Graphics.Blit (Material \"EVE/RaymarchCloud\" with 2 passes)";
            Assert.IsTrue(RenderFailureCounter.IsInvalidPass(line));
            Assert.IsFalse(RenderFailureCounter.IsInvalidPass("Invalid pass number (1) for Graphics.Blit (Material \"Hidden/Other\")"));
            Assert.IsFalse(RenderFailureCounter.IsInvalidPass("EVE/RaymarchCloud is fine"));
            Assert.IsFalse(RenderFailureCounter.IsInvalidPass(null));
            bool tripped = false;
            for (int i = 0; i < RenderFailureCounter.Threshold - 1; i++) tripped |= counter.Add(line);
            Assert.IsFalse(tripped);
            Assert.IsFalse(counter.Tripped);
            Assert.IsTrue(counter.Add(line), "the threshold line trips it");
            Assert.IsTrue(counter.Tripped);
            Assert.IsFalse(counter.Add(line), "it trips once");
            Assert.AreEqual(RenderFailureCounter.Threshold + 1, counter.Count);
            Assert.IsFalse(counter.Add("something else"));
        }

        [TestMethod]
        public void WhereEveCannotRenderEveAndBothBecomeOursAndTheOthersStay()
        {
            Assert.AreEqual(CloudSourceKind.Ours, CloudModeModel.Effective(CloudSourceKind.Eve, true));
            Assert.AreEqual(CloudSourceKind.Ours, CloudModeModel.Effective(CloudSourceKind.Both, true));
            Assert.AreEqual(CloudSourceKind.Ours, CloudModeModel.Effective(CloudSourceKind.Ours, true));
            Assert.AreEqual(CloudSourceKind.None, CloudModeModel.Effective(CloudSourceKind.None, true));
            Assert.AreEqual(CloudSourceKind.Eve, CloudModeModel.Effective(CloudSourceKind.Eve, false));
            Assert.AreEqual(CloudSourceKind.Both, CloudModeModel.Effective(CloudSourceKind.Both, false));
        }

        [TestMethod]
        public void ABlockedEveBindsNothingAndTheOwnShellCarriesThePicture()
        {
            bool eveMode, ownMode;
            CloudSourceKind effective = CloudModeModel.Effective(CloudSourceKind.Eve, true);
            CloudModeModel.Decide(effective, true, out eveMode, out ownMode);
            Assert.IsFalse(eveMode, "no layer is bound");
            Assert.IsTrue(ownMode, "the own picture runs");
            Assert.IsTrue(CloudModeModel.ShellWanted(effective, false));
        }

        [TestMethod]
        public void TheFreshStatusIsNotBlocking()
        {
            // Before the first look nothing is known: the combination is not reported and nothing falls back.
            Assert.IsFalse(EveRenderCompat.Blocked);
            Assert.AreEqual(CloudSourceKind.Eve, EveRenderCompat.EffectiveSource(CloudSourceKind.Eve));
        }
    }
}
