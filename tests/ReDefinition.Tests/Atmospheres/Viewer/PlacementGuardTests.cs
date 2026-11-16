using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Atmospheres.Viewer;

namespace ReDefinition.Tests.Atmospheres.Viewer
{
    // When the viewer may move the vessel (PlacementGuard) and what 'the vessel came out whole' means (VesselIntegrity): a packed or just unpacked vessel is not moved, one that
    // lost parts or shed debris fails.
    [TestClass]
    public class PlacementGuardTests
    {
        [TestMethod]
        public void NothingIsPlacedBeforeTheVesselHasBeenLoadedAndUnpackedForThreeSeconds()
        {
            var guard = new PlacementGuard();
            Assert.AreEqual(PlacementGuard.LoadingMessage, guard.Refusal(0.0, true), "never observed");
            guard.Observe(10.0, false);
            Assert.AreEqual(PlacementGuard.LoadingMessage, guard.Refusal(10.5, true), "packed");
            guard.Observe(20.0, true);
            Assert.AreEqual(PlacementGuard.LoadingMessage, guard.Refusal(20.0, true), "unpacked this moment");
            guard.Observe(22.9, true);
            Assert.AreEqual(PlacementGuard.LoadingMessage, guard.Refusal(22.9, true), "unpacked 2.9 s");
            guard.Observe(23.0, true);
            Assert.IsNull(guard.Refusal(23.0, true), "unpacked 3 s");
            Assert.IsNull(guard.Refusal(500.0, true), "and later");
        }

        [TestMethod]
        public void AVesselThatPacksAgainHasToSettleAgain()
        {
            var guard = new PlacementGuard();
            guard.Observe(0.0, true);
            Assert.IsNull(guard.Refusal(5.0, true));
            guard.Observe(6.0, false);
            Assert.AreEqual(PlacementGuard.LoadingMessage, guard.Refusal(6.1, true));
            guard.Observe(7.0, true);
            Assert.AreEqual(PlacementGuard.LoadingMessage, guard.Refusal(9.0, true));
            Assert.IsNull(guard.Refusal(10.0, true));
            guard.Reset();
            Assert.AreEqual(PlacementGuard.LoadingMessage, guard.Refusal(11.0, true));
        }

        [TestMethod]
        public void TimeWarpEvaForAndFastVesselsAreNotPlaced()
        {
            Assert.AreEqual(PlacementGuard.WarpMessage, PlacementGuard.StateRefusal(true, false, true, 0.0));
            Assert.AreEqual(PlacementGuard.EvaMessage, PlacementGuard.StateRefusal(false, true, true, 0.0));
            Assert.AreEqual(PlacementGuard.NotStandingMessage, PlacementGuard.StateRefusal(false, false, false, 0.0), "an aircraft or a capsule on its parachute: Close would put it back at rest");
            StringAssert.Contains(PlacementGuard.StateRefusal(false, false, true, 120.0), "120 m/s");
            Assert.IsNull(PlacementGuard.StateRefusal(false, false, true, 0.0), "a vessel at rest on the pad");
            Assert.IsNull(PlacementGuard.StateRefusal(false, false, true, PlacementGuard.MaxSpeedMs), "the limit itself");
        }

        [TestMethod]
        public void NoVesselIsSaidAsSuch()
        {
            Assert.AreEqual(PlacementGuard.NoVesselMessage, new PlacementGuard().Refusal(0.0, false));
        }

        [TestMethod]
        public void AVesselIsIntactWithTheSamePartsAndNoNewVessel()
        {
            var before = new VesselCount { Valid = true, Parts = 64, Vessels = 1, Name = "Kerbal X" };
            string detail;
            Assert.IsTrue(VesselIntegrity.Intact(before, before, out detail), detail);
            Assert.IsFalse(VesselIntegrity.Intact(before, new VesselCount { Valid = true, Parts = 50, Vessels = 1, Name = "Kerbal X" }, out detail), "lost parts: " + detail);
            Assert.IsFalse(VesselIntegrity.Intact(before, new VesselCount { Valid = true, Parts = 64, Vessels = 6, Name = "Kerbal X" }, out detail), "debris: " + detail);
            Assert.IsFalse(VesselIntegrity.Intact(before, default(VesselCount), out detail), "not readable: " + detail);
            StringAssert.Contains(detail, "could not be read");
            Assert.IsTrue(VesselIntegrity.Intact(before, new VesselCount { Valid = true, Parts = 64, Vessels = 0, Name = "Kerbal X" }, out detail), "debris that was cleaned up is not debris");
        }
    }
}
