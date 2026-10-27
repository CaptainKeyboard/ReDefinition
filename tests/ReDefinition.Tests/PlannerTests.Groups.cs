using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Engineer;

namespace ReDefinition.Tests
{
    // The planner tests on the budget with the vessel in groups: an Apollo lander between the rest's stages, refills,
    // satellites and landers that fly on their own.
    public partial class PlannerTests
    {
        // A step of `need` flown by the groups given.
        private static BudgetStep Step(double need, params int[] flying)
        {
            BudgetStep step = new BudgetStep { Need = need, Side = new bool[3] };
            foreach (int group in flying) step.Side[group] = true;
            return step;
        }

        // A split with the groups' masses and stage number i owned by owners[i], its fuel and its drop that group's.
        private static VesselSplit Groups(double[] mass, params int[] owners)
        {
            VesselSplit split = new VesselSplit(mass.Length);
            for (int g = 0; g < mass.Length; g++) split.Mass[g] = mass[g];
            for (int number = 0; number < owners.Length; number++)
            {
                split.Owner[number] = owners[number];
                split.Fuel[number] = split.Unit(owners[number]);
                split.Drop[number] = split.Unit(owners[number]);
            }
            return split;
        }

        // The Apollo lander flies between the rest's stages: the whole vessel burns the service module, the
        // lander its own descent and ascent alone, then the rest takes the service module up again for home.
        [TestMethod]
        public void AnApolloLanderFliesBetweenTheRestsStages()
        {
            // S3 the service module (rest, group 0), S2 and S1 the lander's descent and
            // ascent (group 1), S0 the command module (group 2, part of the rest),
            // without engines.
            List<BudgetStage> stages = new List<BudgetStage>
            {
                Stage(3, 18, 12), Stage(2, 12, 10), Stage(1, 9, 8), new BudgetStage { Number = 0, StartMass = 2, EndMass = 2 },
            };
            VesselSplit split = Groups(new[] { 10.0, 6.0, 2.0 }, 2, 1, 1, 0);
            BudgetStep without = Step(300.0, 0, 2);
            without.Gone.Add(new GoneGroup { Group = 1, Stays = true });
            BudgetStep capsule = Step(0.0, 2);
            capsule.Gone.Add(new GoneGroup { Group = 0 });
            StageBudget.Result result = StageBudget.Allocate(stages,
                new List<BudgetStep> { Step(300.0, 0, 1, 2), Step(1500.0, 1), without, capsule }, 0.0, null, split);

            Assert.AreEqual(18.0, result.Uses.Single(u => u.Leg == 0).Mass, 1e-9, "the whole vessel burns the service module");
            Assert.AreEqual(6.0, result.Uses.First(u => u.Leg == 1).Mass, 1e-9, "the lander pushes only itself");
            Assert.AreEqual(2, result.Uses.First(u => u.Leg == 1).Stage);
            Assert.AreEqual(3.0, result.Uses.Single(u => u.Leg == 1 && u.Stage == 1).Mass, 1e-9, "the ascent stage, the descent dropped");
            double burnt = 18.0 * (1.0 - System.Math.Exp(-300.0 / Exhaust));
            StageBudget.StageUse home = result.Uses.Single(u => u.Leg == 2);
            Assert.AreEqual(3, home.Stage, "the service module again, after the lander's stages");
            Assert.AreEqual(12.0 - burnt, home.Mass, 1e-6, "the rest with the capsule");
            Assert.AreEqual(0.0, result.Short[1] + result.Short[2], 1e-6);
            Assert.IsTrue(result.Dropped.ContainsKey(3), "the service module left with what it holds");
        }

        // Each step says what the stages that flew it have left after it.
        [TestMethod]
        public void EachStepSaysWhatWhatFlewItHasLeft()
        {
            // 20 t, stage 1 burns 8 t and drops 4 t; stage 0 burns 4 of the 8 t left.
            List<BudgetStage> stages = new List<BudgetStage> { Stage(1, 20, 12), Stage(0, 8, 4) };
            double total = stages.Sum(stage => stage.DeltaV);
            StageBudget.Result result = Allocate(stages, new[] { 1000.0, 500.0 }, 0.0, null, -1, null);

            Assert.AreEqual(total - 1000.0, result.LeftAfter[0], 1e-6, "all the rest after the first step");
            Assert.AreEqual(total - 1500.0, result.LeftAfter[1], 1e-6);
        }

        // A refill comes from the stage the player names first rather than from the one KSP would fire first.
        [TestMethod]
        public void ARefillComesFromTheStageThePlayerNamesFirst()
        {
            // The rest: S3 (4 t propellant) and S2 (4 t); the lander S0 (1 t, burnt on its
            // own step), refilled from S2 rather than from S3, KSP's first.
            List<BudgetStage> stages = new List<BudgetStage> { Stage(3, 20, 16), Stage(2, 16, 12), Stage(0, 4, 3) };
            VesselSplit split = Groups(new[] { 16.0, 4.0 }, 1, 0, 0, 0);
            split.Owner[3] = 0;
            split.Fuel[3] = split.Unit(0);
            split.Drop[3] = split.Unit(0);
            split.Owner[0] = 1;
            split.Fuel[0] = split.Unit(1);
            split.Drop[0] = split.Unit(1);
            System.Func<int, StageBudget.Result> refill = from =>
            {
                BudgetStep alone = Step(Exhaust * System.Math.Log(4.0 / 3.0), 1);
                alone.RefuelGroup = 1;
                alone.RefuelFrom = from;
                return StageBudget.Allocate(stages, new List<BudgetStep> { alone }, 0.0, null, split);
            };
            StageBudget.Result named = refill(2);
            StageBudget.Result inOrder = refill(-1);

            Assert.AreEqual(1.0, named.Refuelled[0], 1e-6, "the lander full again");
            Assert.IsTrue(named.Left[3] > inOrder.Left[3], "S3 keeps its fuel where S2 gives it");
            Assert.IsTrue(named.Left[2] < inOrder.Left[2], "and S2 gives it");
        }

        // A satellite that flies its own step and stays keeps the delta-v it has left.
        [TestMethod]
        public void ASatelliteFlyingItselfKeepsWhatItHoldsAsItStays()
        {
            // S1 the upper stage (rest), S0 the satellite with its own engine (group 1,
            // 2 t with 1 t propellant); the satellite flies its step and stays.
            List<BudgetStage> stages = new List<BudgetStage> { Stage(1, 12, 6), Stage(0, 2, 1) };
            VesselSplit split = Groups(new[] { 10.0, 2.0 }, 1, 0);
            BudgetStep circularise = Step(500.0, 1);
            circularise.Deploy.Add(1);
            StageBudget.Result result = StageBudget.Allocate(stages, new List<BudgetStep> { Step(300.0, 0, 1), circularise }, 0.0,
                null, split);

            double left = Exhaust * System.Math.Log(2.0 / 1.0) - 500.0;
            Assert.AreEqual(left, result.LeftAfter[1], 1e-6, "what the satellite has left as it stays");
            Assert.AreEqual(left, result.Stages[1].Single(stage => stage.Stage == 0).LeftAfter, 1e-6);
        }

        // Two landers flying alone each burn their own stage and push only themselves.
        [TestMethod]
        public void TwoLandersEachBurnTheirOwnStages()
        {
            // S3 the mothership (rest), S2 a lander for the Mun (group 1), S1 one for
            // Minmus (group 2), S0 no stage.
            List<BudgetStage> stages = new List<BudgetStage>
            {
                Stage(3, 20, 14), Stage(2, 8, 7), Stage(1, 5, 4), new BudgetStage { Number = 0, StartMass = 3, EndMass = 3 },
            };
            VesselSplit split = Groups(new[] { 12.0, 3.0, 5.0 }, 0, 2, 1, 0);
            StageBudget.Result result = StageBudget.Allocate(stages,
                new List<BudgetStep> { Step(300.0, 0, 1, 2), Step(200.0, 1), Step(200.0, 2) }, 0.0, null, split);

            Assert.AreEqual(3.0, result.Uses.Single(u => u.Leg == 1).Mass, 1e-9, "the first lander alone");
            Assert.AreEqual(2, result.Uses.Single(u => u.Leg == 1).Stage);
            Assert.AreEqual(5.0, result.Uses.Single(u => u.Leg == 2).Mass, 1e-9, "the second one alone");
            Assert.AreEqual(1, result.Uses.Single(u => u.Leg == 2).Stage);
        }

        // A satellite deployed after a step stays with what it weighs, and the next step pushes that much less.
        [TestMethod]
        public void ADeployedSatelliteNoLongerWeighsOnWhatFliesOn()
        {
            // S1 the upper stage with a 1 t satellite (group 1), S0 no stage.
            List<BudgetStage> stages = new List<BudgetStage> { Stage(1, 11, 5), new BudgetStage { Number = 0, StartMass = 1, EndMass = 1 } };
            VesselSplit split = Groups(new[] { 10.0, 1.0 }, 0, 0);
            BudgetStep up = Step(400.0, 0, 1);
            up.Deploy.Add(1);
            StageBudget.Result deployed = StageBudget.Allocate(stages, new List<BudgetStep> { up, Step(400.0, 0) }, 0.0, null, split);
            StageBudget.Result kept = StageBudget.Allocate(stages, new List<BudgetStep> { Step(400.0, 0, 1), Step(400.0, 0, 1) }, 0.0,
                null, split);

            Assert.AreEqual(1.0, deployed.Payload[0], 1e-9, "the satellite stays with what it weighs");
            Assert.AreEqual(kept.Uses.Single(u => u.Leg == 1).Mass - 1.0, deployed.Uses.Single(u => u.Leg == 1).Mass, 1e-9);
        }

        // A plane change to Minmus, Kerbin's own tilted moon, is its tilt at the transfer's far end: a few m/s.
        [TestMethod]
        public void APlaneChangeToAPlanetsOwnMoonIsTheMoonsTilt()
        {
            Leg leg = RoutePlanner.LegOf(Stock(), Orbit(Kerbin, 80000), Orbit(Minmus, 10000), 0, true);

            // At the transfer's far end, where the craft is slowest: a few m/s.
            Assert.IsTrue(leg.Lines.Any(line => line.Name == "Plane change" && line.DeltaV > 1 && line.DeltaV < 50));
        }

        // Aerobraking from a 500 km orbit down to 80 km is a deorbit burn where it starts and a circularisation
        // where it ends, cheaper than burning down.
        [TestMethod]
        public void AerobrakingDownToALowerOrbitCostsLessThanBurning()
        {
            RouteStop low = Orbit(Kerbin, 80000);
            Leg burnt = RoutePlanner.LegOf(Stock(), Orbit(Kerbin, 500000), low, 0, false);
            low.UseAtmosphere = true;
            Leg braked = RoutePlanner.LegOf(Stock(), Orbit(Kerbin, 500000), low, 0, false);

            CollectionAssert.AreEqual(new[] { "Deorbit into the atmosphere", "Aerobraking, then circularise" },
                braked.Lines.Select(line => line.Name).ToArray(), "down where it starts, circularised where it ends");
            Near(1100000, braked.Lines[0].Radius, 1, "the deorbit in the 500 km orbit");
            Assert.IsTrue(braked.DeltaV < burnt.DeltaV, braked.DeltaV.ToString("0") + " < " + burnt.DeltaV.ToString("0"));
        }

        // A refill after the lander docks fills it back to what it held at the start, from the rest's tanks.
        [TestMethod]
        public void RefuellingFillsTheLanderFromTheRestAfterItDocks()
        {
            LegMode[] modes = { LegMode.Whole, LegMode.Lander, LegMode.Lander, LegMode.Whole };
            double[] legs = { 500.0, 600.0, 600.0, 100.0 };
            StageBudget.Result plain = Allocate(Mothership(), legs, 0.0, null, 0, modes);
            StageBudget.Result refilled = Allocate(Mothership(), legs, 0.0, null, 0, modes,
                new[] { false, false, true, false });

            double landerBurnt = 4.0 * (1.0 - System.Math.Exp(-1200.0 / Exhaust));
            Assert.AreEqual(landerBurnt, refilled.Refuelled[2], 1e-6, "back to what it held at the start");
            Assert.AreEqual(Mothership()[1].DeltaV, refilled.Left[0], 1e-6, "the lander full again");
            Assert.IsTrue(refilled.Left[1] < plain.Left[1], "from the rest's tanks");
        }
    }
}
