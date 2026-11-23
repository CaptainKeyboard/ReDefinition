using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.Tests
{
    // The tracker that scales a value other code writes too (the ambient light), and the EVE rain link without EVE.
    [TestClass]
    public class AmbientTrackerAndEveRainLinkTests
    {
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
        public void WithoutEveTheRainLinkIsNotControllableAndDoesNothing()
        {
            Assert.IsFalse(EveRainLink.Controllable);
            StringAssert.Contains(EveRainLink.Summary, "not");
            EveRainLink.Release();
            Assert.IsFalse(EveRainLink.Suppressed);
            Assert.AreEqual(0f, EveRainLink.Coverage);
            StringAssert.Contains(EveRainLink.Describe(), "fields 0");
        }
    }
}
