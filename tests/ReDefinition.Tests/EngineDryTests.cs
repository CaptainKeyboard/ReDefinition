using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Dashboard;
using ReDefinition.Engineer;

namespace ReDefinition.Tests
{
    // Where the dashboard's resource tapes mark engines running dry (FuelTelemetry.Marks): the
    // planner's own simulation run on the vessel as it flies.
    [TestClass]
    public class EngineDryTests
    {
        // The Kerbal X on the pad: its three pairs of boosters, asparagus-staged, run dry one pair
        // after the other, then the core, then the upper stage.
        [TestMethod]
        public void TheKerbalXsBoostersRunDryPairByPair()
        {
            List<SimPart> sim = Copy(StockCrafts.Named("Kerbal X").Sim);
            Dictionary<string, List<object>> marks = FuelTelemetry.Marks(sim, StockCrafts.Density, 8, new HashSet<int>(), Totals(sim));
            CollectionAssert.AreEqual(new object[] { 7200.0, 6120.0, 5040.0, 720.0, 0.0 }, marks["LiquidFuel"]);
            CollectionAssert.AreEqual(new object[] { 8800.0, 7480.0, 6160.0, 880.0, 0.0 }, marks["Oxidizer"]);
        }

        // In flight, the first pair dropped: the engines lit by now burn on with the current stage.
        [TestMethod]
        public void AfterTheFirstPairTheOthersFollow()
        {
            List<SimPart> sim = Without(StockCrafts.Named("Kerbal X").Sim, part => part.Clamp || part.DecoupleStage >= 6);
            Dictionary<string, List<object>> marks = FuelTelemetry.Marks(sim, StockCrafts.Density, 6, new HashSet<int>(), Totals(sim));
            CollectionAssert.AreEqual(new object[] { 6120.0, 5040.0, 720.0, 0.0 }, marks["LiquidFuel"]);
        }

        // The engines lit by now switched off: the boosters are dropped full, and the upper stage's
        // engine running dry is no level of the tape that shows them.
        [TestMethod]
        public void NoMarkOnceFuelIsDropped()
        {
            List<SimPart> sim = Without(StockCrafts.Named("Kerbal X").Sim, part => part.Clamp || part.DecoupleStage >= 6);
            HashSet<int> off = new HashSet<int>(Enumerable.Range(0, sim.Count).Where(i => sim[i].Rocket != null && sim[i].ActivatesAt >= 6));
            Assert.IsTrue(off.Count > 0);
            Dictionary<string, List<object>> marks = FuelTelemetry.Marks(sim, StockCrafts.Density, 6, off, Totals(sim));
            Assert.IsFalse(marks.ContainsKey("LiquidFuel"));
        }

        // Their keys, as the dashboard asks for them.
        [TestMethod]
        public void TheKeysAreKnown()
        {
            Telemetry telemetry = new Telemetry();
            Assert.IsTrue(telemetry.IsKnown("r.engineDry[LiquidFuel]"));
            Assert.IsTrue(telemetry.IsKnown("r.engineEmpty[SolidFuel]"));
        }

        // Solid boosters of two sizes lit with the core: the small ones burn out first, with half of
        // the solid fuel left in the big ones.
        [TestMethod]
        public void SmallSolidBoostersBurnOutFirst()
        {
            List<SimPart> sim = new List<SimPart>
            {
                Tank(-1, -1, "LiquidFuel", 900.0, "Oxidizer", 1100.0),
                Engine(0, -1, Rocket(200.0, 300.0, false, "LiquidFuel", 0.9, SimFlow.Stack, "Oxidizer", 1.1, SimFlow.Stack)),
                Booster(0, 1, 100.0), Booster(0, 1, 100.0),
                Booster(0, 0, 300.0), Booster(0, 0, 300.0),
            };
            Dictionary<string, double> density = new Dictionary<string, double> { { "LiquidFuel", 0.005 }, { "Oxidizer", 0.005 }, { "SolidFuel", 0.0075 } };
            Dictionary<string, List<object>> marks = FuelTelemetry.Marks(StockCrafts.Linked(sim), density, 3, new HashSet<int>(), Totals(sim));
            CollectionAssert.AreEqual(new object[] { 400.0, 0.0 }, marks["SolidFuel"]);
            CollectionAssert.AreEqual(new object[] { 0.0 }, marks["LiquidFuel"]);
        }

        private static SimPart Tank(int parent, int decouple, string a, double amountA, string b, double amountB)
        {
            SimPart part = new SimPart { Parent = parent, DecoupleStage = decouple, Fixed = 1.0 };
            part.Amount[a] = amountA;
            part.Amount[b] = amountB;
            return part;
        }

        private static SimPart Engine(int parent, int decouple, SimEngine rocket)
        {
            return new SimPart { Parent = parent, DecoupleStage = decouple, ActivatesAt = 2, Fixed = 1.0, Rocket = rocket };
        }

        private static SimPart Booster(int parent, int decouple, double solid)
        {
            SimPart part = Engine(parent, decouple, Rocket(100.0, 200.0, true, "SolidFuel", 1.0, SimFlow.Own, null, 0.0, SimFlow.Own));
            part.Amount["SolidFuel"] = solid;
            return part;
        }

        private static SimEngine Rocket(double thrust, double isp, bool solid, string a, double ratioA, SimFlow flowA, string b, double ratioB, SimFlow flowB)
        {
            SimEngine engine = new SimEngine { Thrust = thrust, Isp = isp, Solid = solid };
            engine.Propellants.Add(new SimPropellant { Name = a, Ratio = ratioA, Flow = flowA });
            if (b != null) engine.Propellants.Add(new SimPropellant { Name = b, Ratio = ratioB, Flow = flowB });
            return engine;
        }

        private static Dictionary<string, double> Totals(List<SimPart> sim)
        {
            Dictionary<string, double> total = new Dictionary<string, double>();
            foreach (SimPart part in sim.Where(part => !part.Clamp))
                foreach (KeyValuePair<string, double> held in part.Amount)
                {
                    double sum;
                    total.TryGetValue(held.Key, out sum);
                    total[held.Key] = sum + held.Value;
                }
            return total;
        }

        // A copy to change, the stock craft's parts left as they are.
        private static List<SimPart> Copy(List<SimPart> parts)
        {
            return Without(parts, part => false);
        }

        // The parts without those `gone` takes, their indices renumbered, linked.
        private static List<SimPart> Without(List<SimPart> parts, System.Func<SimPart, bool> gone)
        {
            Dictionary<int, int> index = new Dictionary<int, int>();
            for (int i = 0; i < parts.Count; i++)
                if (!gone(parts[i])) index[i] = index.Count;
            List<SimPart> kept = new List<SimPart>();
            foreach (KeyValuePair<int, int> at in index.OrderBy(at => at.Value))
            {
                SimPart part = parts[at.Key];
                int parent;
                SimPart copy = new SimPart
                {
                    Parent = part.Parent >= 0 && index.TryGetValue(part.Parent, out parent) ? parent : -1,
                    Crossfeed = part.Crossfeed, DecoupleStage = part.DecoupleStage, ActivatesAt = part.ActivatesAt, Clamp = part.Clamp,
                    Fixed = part.Fixed, Rocket = part.Rocket, Air = part.Air,
                };
                foreach (KeyValuePair<string, double> held in part.Amount) copy.Amount[held.Key] = held.Value;
                foreach (int source in part.FuelLineSources)
                    if (index.ContainsKey(source)) copy.FuelLineSources.Add(index[source]);
                kept.Add(copy);
            }
            return StockCrafts.Linked(kept);
        }
    }
}
