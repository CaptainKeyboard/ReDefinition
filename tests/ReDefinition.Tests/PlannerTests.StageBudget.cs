using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Engineer;

namespace ReDefinition.Tests
{
    // The stage budget (StageBudget) of the planner tests: how a route's legs are allocated to the stages, with the saved
    // and dropped stages and the lander flying alone.
    public partial class PlannerTests
    {
        // Who flies a step on the stage-number model: the whole vessel, the lander (group
        // 1) alone, the rest without it, or the capsule (group 2) alone.
        internal enum LegMode
        {
            // In order: the whole vessel, the lander alone, the rest leaving the lander for good, the capsule alone.
            Whole,
            Lander,
            WithoutLander,
            Capsule,
        }

        // The budget with the lander the stages numbered up to `lander` and the capsule
        // those up to `capsule` (VesselSplit.ByStages). Each step is flown as `modes` says.
        // The lander flies alone while the rest waits, and docks again with the next
        // step of the whole vessel. The rest leaves the lander for good. The capsule leaves
        // everything else, dropped where it was with the capsule, left where it waited.
        private static StageBudget.Result Allocate(IList<BudgetStage> stages, IList<double> legs, double margin, IList<StagePlan> plans,
                                                   int lander, IList<LegMode> modes, IList<bool> refuelAfter = null, int capsule = -1)
        {
            VesselSplit split = VesselSplit.ByStages(stages, lander, capsule);
            bool hasLander = split.Owner.Values.Contains(1);
            bool hasCapsule = split.Owner.Values.Contains(2);
            bool inLander = hasLander && hasCapsule;
            bool landerGone = false, capsuleOn = false, apart = false;
            List<BudgetStep> steps = new List<BudgetStep>();
            for (int leg = 0; leg < legs.Count; leg++)
            {
                LegMode mode = modes != null && leg < modes.Count ? modes[leg] : LegMode.Whole;
                if ((mode == LegMode.Capsule && !hasCapsule) || (mode != LegMode.Capsule && !hasLander)) mode = LegMode.Whole;
                BudgetStep step = new BudgetStep { Need = legs[leg], Side = new bool[3] };
                apart = hasLander && !landerGone && !capsuleOn && (mode == LegMode.Lander || (apart && mode != LegMode.Whole));
                if (mode == LegMode.WithoutLander && !landerGone)
                {
                    step.Gone.Add(new GoneGroup { Group = 1, Stays = true });
                    if (inLander) step.Gone.Add(new GoneGroup { Group = 2, Stays = true });
                    landerGone = true;
                }
                if (mode == LegMode.Capsule && !capsuleOn)
                {
                    bool[] host = apart ? new[] { !inLander, inLander } : new[] { true, true };
                    step.Gone.Add(new GoneGroup { Group = 0, Waiting = !host[0] });
                    if (!landerGone) step.Gone.Add(new GoneGroup { Group = 1, Waiting = !host[1] });
                    capsuleOn = true;
                    landerGone = true;
                    apart = false;
                }
                if (capsuleOn) step.Side[2] = true;
                else if (mode == LegMode.Lander)
                {
                    step.Side[1] = true;
                    step.Side[2] = inLander;
                }
                else
                {
                    step.Side[0] = true;
                    step.Side[1] = mode == LegMode.Whole && hasLander && !landerGone;
                    step.Side[2] = hasCapsule && (!inLander || step.Side[1]);
                }
                if (refuelAfter != null && leg < refuelAfter.Count && refuelAfter[leg] && hasLander && !landerGone) step.RefuelGroup = 1;
                steps.Add(step);
            }
            return StageBudget.Allocate(stages, steps, margin, plans, split);
        }

        // A stage from KSP's figures: number, start mass and end mass (tonnes); its
        // delta-v follows from the exhaust velocity of 3000 m/s used throughout.
        private static BudgetStage Stage(int number, double start, double end)
        {
            return new BudgetStage { Number = number, StartMass = start, EndMass = end, DeltaV = Exhaust * System.Math.Log(start / end) };
        }

        // The exhaust velocity of every stage made by Stage, in metres per second.
        private const double Exhaust = 3000.0;

        // The delta-v a stage gives on a leg.
        private static double Used(StageBudget.Result result, int leg, int stage)
        {
            return result.Uses.Where(u => u.Leg == leg && u.Stage == stage).Sum(u => u.DeltaV);
        }

        // Two stages burnt through give the delta-v KSP shows for them together.
        [TestMethod]
        public void BurningAStackThroughGivesKspsDeltaV()
        {
            // 20 t, stage 1 burns 8 t and drops 4 t; stage 0 burns 4 of the 8 t left.
            List<BudgetStage> stages = new List<BudgetStage> { Stage(1, 20, 12), Stage(0, 8, 4) };
            StageBudget.Result result = Allocate(stages, new[] { 100000.0 }, 0.0, null, -1, null);

            Assert.AreEqual(stages.Sum(stage => stage.DeltaV), Used(result, 0, 1) + Used(result, 0, 0), 1e-6);
        }

        // A stage that outlasts a leg carries on into the next, and splitting a burn over legs leaves the total as
        // KSP counts it.
        [TestMethod]
        public void AStageCarriesOnIntoTheNextLegWithTheSameDeltaVInAll()
        {
            List<BudgetStage> stages = new List<BudgetStage> { Stage(1, 20, 12), Stage(0, 8, 4) };
            StageBudget.Result result = Allocate(stages, new[] { 1000.0, 1000.0, 100000.0 }, 0.0, null, -1, null);

            Assert.AreEqual(1000.0, Used(result, 0, 1), 1e-6);
            Assert.IsTrue(Used(result, 1, 1) > 0.0 && Used(result, 1, 0) > 0.0, "stage 1 runs out in leg 1, stage 0 takes over");
            Assert.AreEqual(stages.Sum(stage => stage.DeltaV), result.Uses.Sum(u => u.DeltaV), 1e-6,
                "splitting a burn over legs changes nothing about the total");
        }

        // The margin is added to every leg's need, and what the stages lack of it is the shortfall.
        [TestMethod]
        public void TheMarginIsAddedToEveryLeg()
        {
            StageBudget.Result result = Allocate(new List<BudgetStage> { Stage(0, 10, 5) }, new[] { 3000.0 }, 0.1, null,
                -1, null);

            Assert.AreEqual(3300.0, result.Needed[0], 1e-9);
            Assert.AreEqual(3300.0 - Exhaust * System.Math.Log(2.0), result.Short[0], 1e-6);
        }

        // Per leg: the stages saved on it; `drop` the same for the stages dropped
        // after it.
        private static List<StagePlan> Plans(int legs, int[][] save = null, int[][] drop = null)
        {
            List<StagePlan> plans = new List<StagePlan>();
            for (int i = 0; i < legs; i++)
            {
                StagePlan plan = new StagePlan();
                if (save != null && i < save.Length && save[i] != null) plan.Save.UnionWith(save[i]);
                if (drop != null && i < drop.Length && drop[i] != null) plan.DropAfter.UnionWith(drop[i]);
                plans.Add(plan);
            }
            return plans;
        }

        // A stage saved on a step does not burn there, nor do the stages after it: the step comes up short, and
        // the next step burns the saved stage.
        [TestMethod]
        public void ASavedStageStopsTheStepAndTheStagesAfterIt()
        {
            List<BudgetStage> stages = new List<BudgetStage> { Stage(2, 20, 14), Stage(1, 10, 6), Stage(0, 4, 3) };
            StageBudget.Result result = Allocate(stages, new[] { 500.0, 100000.0, 600.0 }, 0.0,
                Plans(3, new[] { null, new[] { 1 } }), -1, null);

            Assert.AreEqual(0.0, Used(result, 1, 1), "the saved stage does not burn");
            Assert.AreEqual(0.0, Used(result, 1, 0), "nor the stages after it");
            Assert.IsTrue(result.Short[1] > 0.0, "the step is short");
            Assert.AreEqual(600.0, Used(result, 2, 1), 1e-6, "the next step burns it");
        }

        // A stage dropped after a step goes with what it still holds, and the next step pushes only what is left.
        [TestMethod]
        public void ADroppedStageGoesWithWhatItHolds()
        {
            List<BudgetStage> stages = new List<BudgetStage> { Stage(1, 20, 12), Stage(0, 8, 4) };
            StageBudget.Result result = Allocate(stages, new[] { 400.0, 600.0 }, 0.0,
                Plans(2, null, new[] { new[] { 1 } }), -1, null);

            Assert.IsTrue(result.Dropped[1] > 0.0);
            Assert.AreEqual(0, result.GoneAfter[1]);
            Assert.IsTrue(result.Stages[0].Single(stage => stage.Stage == 1).Dropped);
            Assert.AreEqual(result.Dropped[1], result.Stages[0].Single(stage => stage.Stage == 1).LeftAfter, 1e-9);
            Assert.AreEqual(600.0, Used(result, 1, 0), 1e-6);
            Assert.AreEqual(8.0, result.Uses.Single(u => u.Leg == 1).Mass, 1e-9, "the next step pushes only what is left");
        }

        // Dropping a stage drops the ones KSP fires before it as well.
        [TestMethod]
        public void DroppingAStageDropsTheOnesKspFiresBeforeIt()
        {
            List<BudgetStage> stages = new List<BudgetStage> { Stage(2, 20, 16), Stage(1, 10, 6), Stage(0, 4, 3) };
            StageBudget.Result result = Allocate(stages, new[] { 300.0, 300.0 }, 0.0,
                Plans(2, null, new[] { new[] { 1 } }), -1, null);

            Assert.IsTrue(result.Dropped.ContainsKey(2) && result.Dropped.ContainsKey(1));
            Assert.AreEqual(300.0, Used(result, 1, 0), 1e-6);
        }

        // The last stage cannot be dropped: there is nothing to fly on.
        [TestMethod]
        public void TheLastStageCannotBeDropped()
        {
            List<BudgetStage> stages = new List<BudgetStage> { Stage(1, 20, 12), Stage(0, 8, 4) };
            StageBudget.Result result = Allocate(stages, new[] { 300.0, 300.0 }, 0.0,
                Plans(2, null, new[] { new[] { 0 } }), -1, null);

            Assert.IsFalse(result.Stages[0].Single(stage => stage.Stage == 0).CanDrop);
            Assert.IsFalse(result.Dropped.ContainsKey(0));
        }

        // A step lists the stages that still hold fuel as it starts, with what each gives in it and what it has
        // left after.
        [TestMethod]
        public void AStepListsTheStagesThatStillHoldFuel()
        {
            List<BudgetStage> stages = new List<BudgetStage> { Stage(1, 20, 12), Stage(0, 8, 4) };
            StageBudget.Result result = Allocate(stages, new[] { stages[0].DeltaV + 100.0, 100.0 }, 0.0, null, -1, null);

            CollectionAssert.AreEqual(new[] { 1, 0 }, result.Stages[0].Select(stage => stage.Stage).ToArray());
            CollectionAssert.AreEqual(new[] { 0 }, result.Stages[1].Select(stage => stage.Stage).ToArray(), "stage 1 burnt out");
            Assert.AreEqual(stages[0].DeltaV, result.Stages[0][0].Given, 1e-6);
            Assert.AreEqual(0.0, result.Stages[0][0].LeftAfter, 1e-9);
        }

        // Dropping the rest's stage after a step leaves the lander, which pushes only itself, to fly on alone.
        [TestMethod]
        public void DroppingTheRestLeavesTheLanderToFlyAlone()
        {
            StageBudget.Result result = Allocate(Mothership(), new[] { 500.0, 500.0 }, 0.0,
                Plans(2, null, new[] { new[] { 1 } }), 0, new[] { LegMode.Whole, LegMode.Whole });

            Assert.IsTrue(result.Dropped.ContainsKey(1), "the rest goes with what it holds");
            Assert.AreEqual(4.0, result.Uses.Single(u => u.Leg == 1).Mass, 1e-9, "the lander pushes only itself");
        }

        // A mothership (stage 1, 12 t of which 6 t propellant, 2 t structure
        // that stays) and a 4 t lander (stage 0, 2 t propellant).
        private static List<BudgetStage> Mothership()
        {
            return new List<BudgetStage> { Stage(1, 16, 10), Stage(0, 4, 2) };
        }

        // The lander flies its steps alone on its own stage, pushing only itself, while the rest waits, and the
        // mothership carries it on afterwards.
        [TestMethod]
        public void TheLanderFliesAloneAndTheRestWaits()
        {
            LegMode[] modes = { LegMode.Whole, LegMode.Lander, LegMode.Lander, LegMode.Whole };
            StageBudget.Result result = Allocate(Mothership(), new[] { 500.0, 600.0, 600.0, 500.0 }, 0.0, null, 0, modes);

            Assert.AreEqual(0.0, result.TotalShort);
            Assert.AreEqual(500.0, Used(result, 0, 1), 1e-6, "the whole vessel burns the mothership's stage");
            Assert.AreEqual(600.0, Used(result, 1, 0), 1e-6);
            Assert.AreEqual(4.0, result.Uses.First(u => u.Leg == 1).Mass, 1e-9, "the lander pushes only itself");
            Assert.AreEqual(500.0, Used(result, 3, 1), 1e-6, "and is carried on by the mothership");
        }

        // The lander comes back lighter by what it burnt alone, so the mothership pushes less on the way on.
        [TestMethod]
        public void AnEmptierLanderMakesTheMothershipGoFurther()
        {
            LegMode[] modes = { LegMode.Whole, LegMode.Lander, LegMode.Lander, LegMode.Whole };
            StageBudget.Result result = Allocate(Mothership(), new[] { 500.0, 600.0, 600.0, 500.0 }, 0.0, null, 0, modes);

            double before = result.Uses.First(u => u.Leg == 0).Mass;
            double after = result.Uses.First(u => u.Leg == 3).Mass;
            double landerBurnt = 4.0 * (1.0 - System.Math.Exp(-1200.0 / Exhaust));
            double motherBurnt = 16.0 * (1.0 - System.Math.Exp(-500.0 / Exhaust));
            Assert.AreEqual(before - motherBurnt - landerBurnt, after, 1e-6);
        }

        // With the rest flying on without the lander, the lander stays behind with its stages, and the rest is
        // lighter by its four tonnes.
        [TestMethod]
        public void WithoutTheLanderTheRestFliesLighterAndTheLanderStaysBehind()
        {
            LegMode[] modes = { LegMode.Whole, LegMode.WithoutLander };
            StageBudget.Result result = Allocate(Mothership(), new[] { 500.0, 500.0 }, 0.0, null, 0, modes);

            Assert.IsTrue(result.LeftBehind.Contains(0));
            Assert.IsTrue(result.Dropped.ContainsKey(0));
            double before = result.Uses.First(u => u.Leg == 0).Mass;
            double burnt = 16.0 * (1.0 - System.Math.Exp(-500.0 / Exhaust));
            Assert.AreEqual(before - burnt - 4.0, result.Uses.First(u => u.Leg == 1).Mass, 1e-6);
        }

        // What is left is the delta-v each stage still gives where the route ends: none of a stage burnt out, the
        // rest of one burning, all of one untouched.
        [TestMethod]
        public void WhatIsLeftIsWhatTheStagesStillGiveFromWhereTheRouteEnds()
        {
            List<BudgetStage> stages = new List<BudgetStage> { Stage(2, 20, 14), Stage(1, 10, 6), Stage(0, 4, 3) };
            StageBudget.Result result = Allocate(stages, new[] { stages[0].DeltaV + 100.0 }, 0.0, null, -1, null);

            Assert.AreEqual(0.0, result.Left[2], 1e-9, "burnt out");
            Assert.AreEqual(stages[1].DeltaV, Used(result, 0, 1) + result.Left[1], 1e-6, "used and left make the stage");
            Assert.AreEqual(stages[2].DeltaV, result.Left[0], 1e-6, "untouched");
        }

        // With a lander that never flies alone the stages burn through as KSP counts them, every stage's delta-v used.
        [TestMethod]
        public void WithALanderThatNeverFliesAloneTheStagesGiveKspsDeltaV()
        {
            StageBudget.Result result = Allocate(Mothership(), new[] { 100000.0 }, 0.0, null, 0,
                new[] { LegMode.Whole });

            Assert.AreEqual(Mothership().Sum(stage => stage.DeltaV), result.Uses.Sum(u => u.DeltaV), 1e-6);
        }

        // A stage of the rest saved on a step holds back the lander's stages behind it too, as KSP fires them in
        // order; the step after the saving burns it.
        [TestMethod]
        public void ASavedStageOfTheRestHoldsBackTheLanderToo()
        {
            List<BudgetStage> stages = new List<BudgetStage> { Stage(3, 16, 12), Stage(2, 10, 8), Stage(1, 5, 4), Stage(0, 3, 2) };
            StageBudget.Result result = Allocate(stages, new[] { 100000.0, 100.0, 100.0 }, 0.0,
                Plans(3, new[] { new[] { 2 }, new[] { 2 } }), 1, new[] { LegMode.Whole, LegMode.Whole, LegMode.Whole });

            Assert.AreEqual(0.0, Used(result, 0, 1) + Used(result, 0, 0), "the lander waits behind the saved stage");
            Assert.AreEqual(100.0, Used(result, 2, 2), 1e-6);
        }
    }
}
