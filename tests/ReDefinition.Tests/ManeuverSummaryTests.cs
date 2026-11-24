using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Planner.Autopilot.Flight;
using ReDefinition.Planner.Autopilot.Planning;

namespace ReDefinition.Tests
{
    // The count of a flight's burns by what they are for, which the probes hold a route to.
    [TestClass]
    public class ManeuverSummaryTests
    {
        [TestMethod]
        public void TheBurnsAreCountedByTheirPurposeAndAddUp()
        {
            var summary = new ManeuverSummary();
            summary.Add(NodePlanner.BurnPurpose.Transfer, "a");
            summary.Add(NodePlanner.BurnPurpose.Correction, "b");
            summary.Add(NodePlanner.BurnPurpose.Correction, "c");
            summary.Add(NodePlanner.BurnPurpose.Capture, "d");
            Assert.AreEqual(4, summary.Total);
            Assert.AreEqual(2, summary.Count(NodePlanner.BurnPurpose.Correction));
            Assert.AreEqual(0, summary.Count(NodePlanner.BurnPurpose.Deorbit));
            Assert.AreEqual(3, summary.UntilEncounter);
            CollectionAssert.AreEqual(new[] { "a", "b", "c", "d" }, new System.Collections.Generic.List<string>(summary.Lines));
        }

        [TestMethod]
        public void TheFastestTurnOfAllTheCoastsIsKept()
        {
            var summary = new ManeuverSummary();
            summary.Coasted(100.0, 0.003);
            summary.Coasted(40.0, 0.025);
            summary.Coasted(500.0, 0.001);
            Assert.AreEqual(0.025, summary.CoastRate, 1e-12);
            Assert.AreEqual(500.0, summary.CoastSeconds, 1e-12);
        }

        [TestMethod]
        public void TheSummaryNamesEveryPurposeAndTheTurn()
        {
            var summary = new ManeuverSummary();
            summary.Add(NodePlanner.BurnPurpose.Orbit, "x");
            summary.Coasted(10.0, 0.0125);
            string text = summary.Summary();
            StringAssert.StartsWith(text, "1 burns: 0 transfer, 0 correction, 0 capture, 1 orbit, 0 deorbit, 0 other");
            StringAssert.Contains(text, "0.013 rad/s");
        }
    }
}
