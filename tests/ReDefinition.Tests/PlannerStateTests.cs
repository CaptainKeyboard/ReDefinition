using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Engineer;

namespace ReDefinition.Tests
{
    // The planner's plan of a vessel as the file keeps it: what is written is read back as it was.
    [TestClass]
    public class PlannerStateTests
    {
        private static PlannerBodies Bodies()
        {
            var bodies = new PlannerBodies();
            bodies.Bodies.Add(new PlannerBody { Name = "Kerbin", Id = "Kerbin", LowOrbitAltitude = 80000.0 });
            bodies.Bodies.Add(new PlannerBody { Name = "Minmus", Id = "Minmus", LowOrbitAltitude = 30000.0 });
            bodies.Home = 0;
            return bodies;
        }

        [TestMethod]
        public void APlanIsReadAsItWasWritten()
        {
            PlannerBodies bodies = Bodies();
            PlannerState state = PlannerState.Default(bodies);
            state.Margin = 0.15;
            state.Stops[1].Body = 1;
            state.Stops[1].Kind = StopKind.Surface;
            state.Stops[1].Altitude = 30000.0;
            state.Stops[1].OrbitFirst = true;
            state.Stops[1].Refuel[2] = true;
            state.Stops[1].RefuelFrom[2] = 3;
            state.Stops[1].Stages[0].Save.Add(4);
            state.Stops[1].Stages[1].DropAfter.Add(2);
            PlannerState read = PlannerState.FromNode(state.ToNode("Kerbal X", bodies), bodies);
            Assert.AreEqual(2, read.Stops.Count);
            Assert.AreEqual(0.15, read.Margin, 1e-9);
            Assert.AreEqual(0, read.Stops[0].Body);
            Assert.AreEqual(StopKind.Surface, read.Stops[0].Kind);
            Assert.AreEqual(1, read.Stops[1].Body);
            Assert.AreEqual(StopKind.Surface, read.Stops[1].Kind);
            Assert.AreEqual(30000.0, read.Stops[1].Altitude);
            Assert.IsTrue(read.Stops[1].OrbitFirst);
            Assert.IsTrue(read.Stops[1].Refuel[2]);
            Assert.AreEqual(3, read.Stops[1].RefuelFrom[2]);
            Assert.IsTrue(read.Stops[1].Stages[0].Save.Contains(4));
            Assert.IsTrue(read.Stops[1].Stages[1].DropAfter.Contains(2));
        }

        [TestMethod]
        public void AStopAtABodyTheSystemDoesNotHaveIsLeftOut()
        {
            PlannerBodies bodies = Bodies();
            PlannerState state = PlannerState.Default(bodies);
            state.Stops[1].Body = 1;
            ConfigNode node = state.ToNode("Kerbal X", bodies);
            node.GetNodes("STOP")[1].SetValue("body", "Duna");
            PlannerState read = PlannerState.FromNode(node, bodies);
            Assert.AreEqual(1, read.Stops.Count);
        }

        [TestMethod]
        public void TheKeyOfAVesselNameHasNoSlashes()
        {
            Assert.IsFalse(PlannerState.Key("Kerbal X // 2").Contains("/"));
        }
    }
}
