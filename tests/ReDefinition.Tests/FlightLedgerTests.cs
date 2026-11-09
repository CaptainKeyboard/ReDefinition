using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Planner.Autopilot.Flight;
using ReDefinition.Planner.Autopilot.Planning;

namespace ReDefinition.Tests
{
    // The count of a flight's burns by what they are for, which the probes hold a route to.
    [TestClass]
    public class FlightLedgerTests
    {
        [TestMethod]
        public void TheBurnsAreCountedByTheirPurposeAndAddUp()
        {
            var ledger = new FlightLedger();
            ledger.Add(NodePlanner.BurnPurpose.Transfer, "a");
            ledger.Add(NodePlanner.BurnPurpose.Correction, "b");
            ledger.Add(NodePlanner.BurnPurpose.Correction, "c");
            ledger.Add(NodePlanner.BurnPurpose.Capture, "d");
            Assert.AreEqual(4, ledger.Total);
            Assert.AreEqual(2, ledger.Count(NodePlanner.BurnPurpose.Correction));
            Assert.AreEqual(0, ledger.Count(NodePlanner.BurnPurpose.Deorbit));
            Assert.AreEqual(3, ledger.UntilEncounter);
            CollectionAssert.AreEqual(new[] { "a", "b", "c", "d" }, new System.Collections.Generic.List<string>(ledger.Lines));
        }

        [TestMethod]
        public void TheFastestTurnOfAllTheCoastsIsKept()
        {
            var ledger = new FlightLedger();
            ledger.Coasted(100.0, 0.003);
            ledger.Coasted(40.0, 0.025);
            ledger.Coasted(500.0, 0.001);
            Assert.AreEqual(0.025, ledger.CoastRate, 1e-12);
            Assert.AreEqual(500.0, ledger.CoastSeconds, 1e-12);
        }

        [TestMethod]
        public void TheSummaryNamesEveryPurposeAndTheTurn()
        {
            var ledger = new FlightLedger();
            ledger.Add(NodePlanner.BurnPurpose.Orbit, "x");
            ledger.Coasted(10.0, 0.0125);
            string text = ledger.Summary();
            StringAssert.StartsWith(text, "1 burns: 0 transfer, 0 correction, 0 capture, 1 orbit, 0 deorbit, 0 other");
            StringAssert.Contains(text, "0.013 rad/s");
        }
    }
}
