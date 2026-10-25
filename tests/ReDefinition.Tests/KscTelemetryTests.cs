using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Dashboard;

namespace ReDefinition.Tests
{
    // The dashboard's Space Center page as the mod gives it: the buildings by their facility, the
    // vessels it lists, and its keys and commands known in every scene.
    [TestClass]
    public class KscTelemetryTests
    {
        [TestMethod]
        public void ABuildingGoesByItsFacilityOrElseByItsOwnName()
        {
            Assert.AreEqual("LaunchPad", KscTelemetry.FacilityName("SpaceCenter/LaunchPad", "launchpad"));
            Assert.AreEqual("Runway", KscTelemetry.FacilityName("Runway", "runway"));
            Assert.AreEqual("FlagPole", KscTelemetry.FacilityName(null, "FlagPole"));
            Assert.AreEqual("FlagPole", KscTelemetry.FacilityName("", "FlagPole"));
        }

        [TestMethod]
        public void TheVesselsListedAreNoDebrisFlagsOrSpaceObjects()
        {
            foreach (VesselType type in new[] { VesselType.Ship, VesselType.Probe, VesselType.Relay, VesselType.Rover, VesselType.Lander,
                                                VesselType.Plane, VesselType.Station, VesselType.Base, VesselType.EVA })
                Assert.IsTrue(KscTelemetry.Flyable(type), type.ToString());
            foreach (VesselType type in new[] { VesselType.Debris, VesselType.Flag, VesselType.SpaceObject, VesselType.Unknown, VesselType.DroppedPart })
                Assert.IsFalse(KscTelemetry.Flyable(type), type.ToString());
        }

        [TestMethod]
        public void TheSpaceCentersKeysAndCommandsAreKnownInEveryScene()
        {
            Telemetry telemetry = new Telemetry();
            foreach (string key in new[]
                     {
                         "g.mode", "g.funds", "g.science", "g.reputation", "g.ut", "g.date", "g.calendar", "t.currentRate", "t.currentRateIndex",
                         "t.timeWarp[2]", "g.quicksave", "g.mainMenu", "k.buildings", "k.vessels", "k.alarms", "k.contracts", "k.crew",
                         "k.enter[VehicleAssemblyBuilding]", "k.fly[0123]", "k.warpTo[1]",
                     })
                Assert.IsTrue(telemetry.IsKnown(key), key);
        }

        // The game's numbers go out no finer than the page shows them.
        [TestMethod]
        public void TheGamesNumbersAreRoundedAsShown()
        {
            Assert.AreEqual(143626.0, Telemetry.Round("g.funds", 143625.9040));
            Assert.AreEqual(17293.0, Telemetry.Round("g.ut", 17293.1568));
            Assert.AreEqual(412.5, (double)Telemetry.Round("g.science", 412.4666), 1e-9);
        }
    }
}
