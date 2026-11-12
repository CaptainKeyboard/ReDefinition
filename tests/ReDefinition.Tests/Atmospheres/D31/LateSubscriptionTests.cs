using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.D31
{
    // K-W3a: the wind and the solar dimming subscribed once in Init, met a null game event in the main menu and stayed deaf. The
    // subscription is now tried at every tick until it holds; the soak warp index is read from the game's table; a wind asked for
    // only once per few seconds never leaves the ramp.
    [TestClass]
    public class LateSubscriptionTests
    {
        private static void Quiet(string message)
        {
        }

        private static void QuietFailed(string kind, string what, Exception e)
        {
        }

        [TestMethod]
        public void ASubscriptionThatFindsNoEventYetIsTriedAgainAndHoldsOnceTheEventExists()
        {
            int added = 0;
            bool eventExists = false;
            var s = new LateSubscription("test-events", "test cannot subscribe", () =>
            {
                if (!eventExists) return false;
                added++;
                return true;
            }, () => added--, Quiet, QuietFailed);

            Assert.IsFalse(s.Ensure(), "no event in the menu");
            Assert.IsFalse(s.Ensure());
            Assert.AreEqual(0, added, "nothing is added while the event is missing");
            eventExists = true;
            Assert.IsTrue(s.Ensure(), "the event exists now");
            Assert.IsTrue(s.Ensure());
            Assert.AreEqual(1, added, "added once, however often it is asked");
            Assert.AreEqual(3, s.Attempts, "it stops trying once it holds");
        }

        [TestMethod]
        public void ASubscriptionThatThrowsIsTriedAgainAndDoesNotThrowOut()
        {
            int calls = 0;
            var s = new LateSubscription("test-events-throw", "test cannot subscribe", () =>
            {
                calls++;
                if (calls < 3) throw new NullReferenceException();
                return true;
            }, () => { }, Quiet, QuietFailed);

            Assert.IsFalse(s.Ensure());
            Assert.IsFalse(s.Ensure());
            Assert.IsTrue(s.Ensure());
            Assert.IsTrue(s.Hooked);
        }

        [TestMethod]
        public void ReleaseTakesTheHandlersOutOnlyOnceAndSurvivesAGoneGame()
        {
            int removed = 0;
            var s = new LateSubscription("test-events-release", "test cannot subscribe", () => true, () =>
            {
                removed++;
                throw new NullReferenceException();
            }, Quiet, QuietFailed);
            s.Release();
            Assert.AreEqual(0, removed, "nothing to take out before it held");
            Assert.IsTrue(s.Ensure());
            s.Release();
            s.Release();
            Assert.AreEqual(1, removed);
            Assert.IsFalse(s.Hooked);
            Assert.IsTrue(s.Ensure(), "it can subscribe again in the next scene");
        }

        [TestMethod]
        public void TheSoakWarpIndexIsTheTableEntryNearestTo3x()
        {
            Assert.AreEqual(2, SoakWarp.IndexOfRate(new[] { 1f, 2f, 3f, 4f }, SoakWarp.RequestedRate));
            Assert.AreEqual(1, SoakWarp.IndexOfRate(new[] { 1f, 2f, 8f, 16f }, 3f), "in a table without 3 the 2 is the nearest");
            Assert.AreEqual(0, SoakWarp.IndexOfRate(null, 3f));
            Assert.AreEqual(0, SoakWarp.IndexOfRate(new float[0], 3f));
            Assert.AreEqual(8f, SoakWarp.RateAt(new[] { 1f, 2f, 8f }, 2));
            Assert.IsTrue(float.IsNaN(SoakWarp.RateAt(new[] { 1f }, 5)));
            Assert.AreEqual("1, 2.5, 8", SoakWarp.Describe(new[] { 1f, 2.5f, 8f }));
        }

        [TestMethod]
        public void AVesselAskedForOnlyOncePerThreeSecondsStaysInTheRampAndOneAskedPerTickReachesTheWind()
        {
            var target = new Vec3(1.0, 0.0, 0.0);
            var v = new Vec3();

            // K-W3a mode table: one ask after each 3 s wait. A gap over a second restarts the ramp, so the value read is always zero.
            var sparse = new VesselWindState();
            Vec3 last = default(Vec3);
            for (int k = 0; k < 4; k++) last = sparse.Step(target, v, 1.2, 0.0, 100.0 + 3.0 * k, 8.0, 1.5);
            Assert.AreEqual(0.0, last.Length, 1e-12, "the ramp restarts at every sparse ask");

            // The probe now asks every frame for 5 s.
            var dense = new VesselWindState();
            for (int k = 0; k <= 250; k++) last = dense.Step(target, v, 1.2, 0.0, 100.0 + 0.02 * k, 8.0, 1.5);
            Assert.AreEqual(1.0, last.Length, 1e-6, "after the ramp the whole wind is applied");
        }
    }
}
