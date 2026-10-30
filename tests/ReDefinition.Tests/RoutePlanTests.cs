using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Orbits;

namespace ReDefinition.Tests
{
    // What a route plan reports without a running game: the delta-v per stop, its defaults, and the signature of a route that has no body yet.
    [TestClass]
    public class RoutePlanTests
    {
        private static PlannedStep Step(NodePlanner.StepKind kind, int goal, double x, double y, double z)
        {
            return new PlannedStep { Kind = kind, Goal = goal, DeltaV = new V3(x, y, z) };
        }

        // A new plan has no step, no first stop, and has not ended or failed.
        [TestMethod]
        public void ANewPlanIsEmpty()
        {
            var plan = new RoutePlan();
            Assert.AreEqual(-1, plan.FirstGoal);
            Assert.IsFalse(plan.FirstGoalWhole);
            Assert.AreEqual(0, plan.Steps.Count);
            Assert.AreEqual(0, plan.Legs.Count);
            Assert.AreEqual(0.0, plan.DeltaV);
            Assert.IsNull(plan.Ended);
            Assert.IsFalse(plan.Failed);
        }

        // Asking an empty plan for the burns to a stop gives zero, whatever the stop.
        [TestMethod]
        public void APlanWithoutStepsHasNoDeltaVAtAnyStop()
        {
            var plan = new RoutePlan();
            Assert.AreEqual(0.0, plan.DeltaVOf(0));
            Assert.AreEqual(0.0, plan.DeltaVOf(-1));
        }

        // The delta-v of a stop is the sum of the lengths of its burns, not of their components.
        [TestMethod]
        public void TheDeltaVOfAStopIsTheSumOfTheLengthsOfItsBurns()
        {
            var plan = new RoutePlan();
            plan.Steps.Add(Step(NodePlanner.StepKind.Burn, 1, 3.0, 4.0, 0.0));
            plan.Steps.Add(Step(NodePlanner.StepKind.Burn, 1, 0.0, 0.0, 12.0));
            Assert.AreEqual(17.0, plan.DeltaVOf(1), 1e-9);
        }

        // Burns that lead to another stop do not count.
        [TestMethod]
        public void BurnsOfOtherStopsAreLeftOut()
        {
            var plan = new RoutePlan();
            plan.Steps.Add(Step(NodePlanner.StepKind.Burn, 1, 3.0, 4.0, 0.0));
            plan.Steps.Add(Step(NodePlanner.StepKind.Burn, 2, 100.0, 0.0, 0.0));
            Assert.AreEqual(5.0, plan.DeltaVOf(1), 1e-9);
            Assert.AreEqual(100.0, plan.DeltaVOf(2), 1e-9);
            Assert.AreEqual(0.0, plan.DeltaVOf(3));
        }

        // Steps that are not burns carry no delta-v, even where they have a vector set.
        [TestMethod]
        public void StepsThatAreNotBurnsAreLeftOut()
        {
            var plan = new RoutePlan();
            plan.Steps.Add(Step(NodePlanner.StepKind.Burn, 1, 3.0, 4.0, 0.0));
            foreach (NodePlanner.StepKind kind in new[]
            {
                NodePlanner.StepKind.Ascent, NodePlanner.StepKind.Landing, NodePlanner.StepKind.Aerobrake,
                NodePlanner.StepKind.Release, NodePlanner.StepKind.Rendezvous,
            })
                plan.Steps.Add(Step(kind, 1, 50.0, 0.0, 0.0));
            Assert.AreEqual(5.0, plan.DeltaVOf(1), 1e-9);
        }

        // The sign of a burn does not matter: a retrograde burn costs as much as a prograde one.
        [TestMethod]
        public void ARetrogradeBurnCostsAsMuchAsAProgradeOne()
        {
            var plan = new RoutePlan();
            plan.Steps.Add(Step(NodePlanner.StepKind.Burn, 0, -30.0, -40.0, 0.0));
            Assert.AreEqual(50.0, plan.DeltaVOf(0), 1e-9);
        }

        // A route with no stops, or none yet, has the empty signature.
        [TestMethod]
        public void ARouteWithoutStopsHasAnEmptySignature()
        {
            Assert.AreEqual("", RoutePlan.SignatureOf(null));
            Assert.AreEqual("", RoutePlan.SignatureOf(new List<Destination>()));
        }

        // The signature ends at the first stop without a body, so stops that are not picked yet do not change it.
        [TestMethod]
        public void TheSignatureEndsAtTheFirstStopWithoutABody()
        {
            var route = new List<Destination> { new Destination(), new Destination { Surface = true, Altitude = 100.0 } };
            Assert.AreEqual("", RoutePlan.SignatureOf(route));
        }
    }
}
