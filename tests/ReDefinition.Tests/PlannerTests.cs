using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Engineer;

namespace ReDefinition.Tests
{
    // The planner's route (RoutePlanner) against the stock system's known
    // delta-v figures, and the stage budget (StageBudget).
    [TestClass]
    public class PlannerTests
    {
        private const int Sun = 0, Kerbin = 1, Mun = 2, Minmus = 3, Duna = 4, Eve = 5, Laythe = 6, Tylo = 7;

        // The stock bodies, as KSP 1.12.5 defines them.
        private static List<PlannerBody> Stock()
        {
            return new List<PlannerBody>
            {
                new PlannerBody { Name = "Sun", Mu = 1.1723328e18, Radius = 261600000 },
                new PlannerBody
                {
                    Name = "Kerbin", Parent = Sun, Mu = 3.5316e12, Radius = 600000, RotationPeriod = 21549.425,
                    AtmosphereDepth = 70000, SurfacePressure = 1.0, SemiMajorAxis = 13599840256, LowOrbitAltitude = 80000,
                },
                new PlannerBody
                {
                    Name = "Mun", Parent = Kerbin, Mu = 6.5138398e10, Radius = 200000, RotationPeriod = 138984.38,
                    SemiMajorAxis = 12000000, LowOrbitAltitude = 14000,
                },
                new PlannerBody
                {
                    Name = "Minmus", Parent = Kerbin, Mu = 1.7658e9, Radius = 60000, RotationPeriod = 40400,
                    SemiMajorAxis = 47000000, Inclination = 6, LowOrbitAltitude = 10000,
                },
                new PlannerBody
                {
                    Name = "Duna", Parent = Sun, Mu = 3.0136321e11, Radius = 320000, RotationPeriod = 65517.859,
                    AtmosphereDepth = 50000, SurfacePressure = 0.0666, SemiMajorAxis = 20726155264, Inclination = 0.06,
                    LowOrbitAltitude = 60000,
                },
                new PlannerBody
                {
                    Name = "Eve", Parent = Sun, Mu = 8.1717302e12, Radius = 700000, RotationPeriod = 80500,
                    AtmosphereDepth = 90000, SurfacePressure = 5.0, SemiMajorAxis = 9832684544, Inclination = 2.1,
                    LowOrbitAltitude = 100000,
                },
                new PlannerBody
                {
                    Name = "Laythe", Parent = Sun, Mu = 1.962e12, Radius = 500000, RotationPeriod = 52980.879,
                    AtmosphereDepth = 50000, SurfacePressure = 0.6, SemiMajorAxis = 27184000, LowOrbitAltitude = 60000,
                },
                new PlannerBody
                {
                    Name = "Tylo", Parent = Sun, Mu = 2.82528e12, Radius = 600000, RotationPeriod = 211926.36,
                    SemiMajorAxis = 68500000, LowOrbitAltitude = 17000,
                },
            };
        }

        private static RouteStop Orbit(int body, double altitude)
        {
            return new RouteStop { Body = body, Kind = StopKind.Orbit, Altitude = altitude };
        }

        private static RouteStop Surface(int body, bool atmosphere = false)
        {
            return new RouteStop { Body = body, Kind = StopKind.Surface, UseAtmosphere = atmosphere };
        }

        private static void Near(double expected, double actual, double tolerance, string what)
        {
            Assert.IsTrue(System.Math.Abs(actual - expected) <= tolerance,
                what + ": expected about " + expected + ", was " + actual.ToString("0"));
        }

        [TestMethod]
        public void LaunchesMatchTheStockMapWithinAFewPerCent()
        {
            List<PlannerBody> bodies = Stock();
            Near(3400, RoutePlanner.Launch(bodies[Kerbin]), 3400 * 0.04, "Kerbin");
            Near(580, RoutePlanner.Launch(bodies[Mun]), 580 * 0.04, "Mun");
            Near(1450, RoutePlanner.Launch(bodies[Duna]), 1450 * 0.04, "Duna");
            Near(8000, RoutePlanner.Launch(bodies[Eve]), 8000 * 0.04, "Eve");
            Near(2900, RoutePlanner.Launch(bodies[Laythe]), 2900 * 0.04, "Laythe");
            Near(2270, RoutePlanner.Launch(bodies[Tylo]), 2270 * 0.04, "Tylo");
        }

        [TestMethod]
        public void KerbinOrbitToMunOrbitIsAboutEightSixtyAndThreeTen()
        {
            Leg leg = RoutePlanner.LegOf(Stock(), Orbit(Kerbin, 80000), Orbit(Mun, 14000), 0, false);

            Near(856, leg.Lines.Single(line => line.Name == "Transfer burn").DeltaV, 5, "transfer");
            Near(310, leg.Lines.Single(line => line.Name == "Capture into orbit").DeltaV, 5, "capture");
        }

        [TestMethod]
        public void KerbinOrbitToDunaOrbitLeavesAndCapturesAcrossTheSun()
        {
            Leg leg = RoutePlanner.LegOf(Stock(), Orbit(Kerbin, 80000), Orbit(Duna, 60000), 0, false);

            Near(1072, leg.Lines.Single(line => line.Name == "Transfer burn").DeltaV, 10, "ejection");
            Near(616, leg.Lines.Single(line => line.Name == "Capture into orbit").DeltaV, 10, "capture");
        }

        [TestMethod]
        public void AerobrakingLeavesOnlyTheCircularisation()
        {
            RouteStop dunaAero = Orbit(Duna, 60000);
            dunaAero.UseAtmosphere = true;
            Leg leg = RoutePlanner.LegOf(Stock(), Orbit(Kerbin, 80000), dunaAero, 0, false);

            Assert.IsTrue(leg.Lines.Single(line => line.Name.StartsWith("Aerobraking")).DeltaV < 50);
        }

        [TestMethod]
        public void FromAMoonHomeTheBurnLeavesTheMoonAndTheCaptureIsAtThePlanet()
        {
            Leg leg = RoutePlanner.LegOf(Stock(), Orbit(Mun, 14000), Orbit(Kerbin, 80000), 0, false);

            Near(310, leg.Lines.Single(line => line.Name == "Transfer burn").DeltaV, 5, "leaving the Mun");
            Near(856, leg.Lines.Single(line => line.Name == "Capture into orbit").DeltaV, 5, "capture at Kerbin");
        }

        [TestMethod]
        public void FromTheMunToDunaTheBurnLeavesBothTheMunAndKerbin()
        {
            List<PlannerBody> bodies = Stock();
            double fromMun = RoutePlanner.LegOf(bodies, Orbit(Mun, 14000), Orbit(Duna, 60000), 0, false)
                                         .Lines.Single(line => line.Name == "Transfer burn").DeltaV;
            double fromKerbin = RoutePlanner.LegOf(bodies, Orbit(Kerbin, 80000), Orbit(Duna, 60000), 0, false)
                                            .Lines.Single(line => line.Name == "Transfer burn").DeltaV;

            Assert.IsTrue(fromMun > 300 && fromMun < fromKerbin + 400,
                "leaving the Mun for Duna takes more than the Mun's escape, less than going down to Kerbin first: "
                + fromMun.ToString("0"));
        }

        [TestMethod]
        public void SurfaceToSurfaceHasALaunchAndALanding()
        {
            Leg leg = RoutePlanner.LegOf(Stock(), Surface(Kerbin), Surface(Mun), 0, false);

            CollectionAssert.AreEqual(new[] { "Launch to 80 km orbit", "Transfer burn", "Capture into orbit", "Landing" },
                leg.Lines.Select(line => line.Name).ToArray());
            Near(3400 + 856 + 310 + 580, leg.DeltaV, 250, "Kerbin to the Mun's surface");
        }

        [TestMethod]
        public void ParachutesOnKerbinNeedNothingAndOnDunaAPoweredEnd()
        {
            List<PlannerBody> bodies = Stock();
            Assert.AreEqual(0.0, RoutePlanner.Landing(bodies[Kerbin], true));
            double duna = RoutePlanner.Landing(bodies[Duna], true);
            Assert.IsTrue(duna > 100 && duna < 400, duna.ToString("0"));
        }

        [TestMethod]
        public void APlaneChangeIsAddedOnlyWhenWanted()
        {
            List<PlannerBody> bodies = Stock();
            Leg flat = RoutePlanner.LegOf(bodies, Orbit(Mun, 14000), Orbit(Minmus, 10000), 0, false);
            Leg tilted = RoutePlanner.LegOf(bodies, Orbit(Mun, 14000), Orbit(Minmus, 10000), 0, true);

            Assert.IsTrue(tilted.DeltaV > flat.DeltaV);
            Assert.IsTrue(tilted.Lines.Any(line => line.Name == "Plane change"));
        }

        [TestMethod]
        public void ARouteOfFourStopsHasThreeLegs()
        {
            List<RouteStop> stops = new List<RouteStop> { Surface(Kerbin), Orbit(Kerbin, 80000), Orbit(Mun, 14000), Surface(Mun) };

            Assert.AreEqual(3, RoutePlanner.Legs(Stock(), stops, false).Count);
        }

        // A stage from KSP's figures: number, start mass, end mass (tonnes), and
        // delta-v; the exhaust velocity is 3000 m/s throughout.
        // Who flies a step on the stage-number model: the whole vessel, the lander (group
        // 1) alone, the rest without it, or the capsule (group 2) alone.
        internal enum LegMode
        {
            Whole,
            Lander,
            WithoutLander,
            Capsule,
        }

        // The budget with the lander the stages numbered up to `lander` and the capsule
        // those up to `capsule` (VesselSplit.ByStages), each step flown as `modes` says:
        // the lander flying alone while the rest waits and docking again with the next
        // step of the whole vessel; the rest leaving it for good; the capsule leaving
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

        private static BudgetStage Stage(int number, double start, double end)
        {
            return new BudgetStage { Number = number, StartMass = start, EndMass = end, DeltaV = Exhaust * System.Math.Log(start / end) };
        }

        private const double Exhaust = 3000.0;

        private static double Used(StageBudget.Result result, int leg, int stage)
        {
            return result.Uses.Where(u => u.Leg == leg && u.Stage == stage).Sum(u => u.DeltaV);
        }

        [TestMethod]
        public void BurningAStackThroughGivesKspsDeltaV()
        {
            // 20 t, stage 1 burns 8 t and drops 4 t; stage 0 burns 4 of the 8 t left.
            List<BudgetStage> stages = new List<BudgetStage> { Stage(1, 20, 12), Stage(0, 8, 4) };
            StageBudget.Result result = Allocate(stages, new[] { 100000.0 }, 0.0, null, -1, null);

            Assert.AreEqual(stages.Sum(stage => stage.DeltaV), Used(result, 0, 1) + Used(result, 0, 0), 1e-6);
        }

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

        [TestMethod]
        public void DroppingAStageDropsTheOnesKspFiresBeforeIt()
        {
            List<BudgetStage> stages = new List<BudgetStage> { Stage(2, 20, 16), Stage(1, 10, 6), Stage(0, 4, 3) };
            StageBudget.Result result = Allocate(stages, new[] { 300.0, 300.0 }, 0.0,
                Plans(2, null, new[] { new[] { 1 } }), -1, null);

            Assert.IsTrue(result.Dropped.ContainsKey(2) && result.Dropped.ContainsKey(1));
            Assert.AreEqual(300.0, Used(result, 1, 0), 1e-6);
        }

        [TestMethod]
        public void TheLastStageCannotBeDropped()
        {
            List<BudgetStage> stages = new List<BudgetStage> { Stage(1, 20, 12), Stage(0, 8, 4) };
            StageBudget.Result result = Allocate(stages, new[] { 300.0, 300.0 }, 0.0,
                Plans(2, null, new[] { new[] { 0 } }), -1, null);

            Assert.IsFalse(result.Stages[0].Single(stage => stage.Stage == 0).CanDrop);
            Assert.IsFalse(result.Dropped.ContainsKey(0));
        }

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

        [TestMethod]
        public void WhatIsLeftIsWhatTheStagesStillGiveFromWhereTheRouteEnds()
        {
            List<BudgetStage> stages = new List<BudgetStage> { Stage(2, 20, 14), Stage(1, 10, 6), Stage(0, 4, 3) };
            StageBudget.Result result = Allocate(stages, new[] { stages[0].DeltaV + 100.0 }, 0.0, null, -1, null);

            Assert.AreEqual(0.0, result.Left[2], 1e-9, "burnt out");
            Assert.AreEqual(stages[1].DeltaV, Used(result, 0, 1) + result.Left[1], 1e-6, "used and left make the stage");
            Assert.AreEqual(stages[2].DeltaV, result.Left[0], 1e-6, "untouched");
        }

        [TestMethod]
        public void WithALanderThatNeverFliesAloneTheStagesGiveKspsDeltaV()
        {
            StageBudget.Result result = Allocate(Mothership(), new[] { 100000.0 }, 0.0, null, 0,
                new[] { LegMode.Whole });

            Assert.AreEqual(Mothership().Sum(stage => stage.DeltaV), result.Uses.Sum(u => u.DeltaV), 1e-6);
        }

        [TestMethod]
        public void ASavedStageOfTheRestHoldsBackTheLanderToo()
        {
            List<BudgetStage> stages = new List<BudgetStage> { Stage(3, 16, 12), Stage(2, 10, 8), Stage(1, 5, 4), Stage(0, 3, 2) };
            StageBudget.Result result = Allocate(stages, new[] { 100000.0, 100.0, 100.0 }, 0.0,
                Plans(3, new[] { new[] { 2 }, new[] { 2 } }), 1, new[] { LegMode.Whole, LegMode.Whole, LegMode.Whole });

            Assert.AreEqual(0.0, Used(result, 0, 1) + Used(result, 0, 0), "the lander waits behind the saved stage");
            Assert.AreEqual(100.0, Used(result, 2, 2), 1e-6);
        }

        // A part hanging under `parent` in the part tree.
        private static PartFacts Part(int parent, int decoupleStage, double mass, string title = "part", bool command = false,
                                      bool engine = false, bool legs = false, bool parachute = false,
                                      Separation separator = Separation.None, int crew = 0)
        {
            return new PartFacts
            {
                Parent = parent, DecoupleStage = decoupleStage, Mass = mass, DryMass = mass, Title = title, Command = command,
                Engine = engine, Legs = legs, Parachute = parachute, Separator = separator, Crew = crew,
                Id = (uint)(title.GetHashCode() & 0xffff),
            };
        }

        // The Kerbal X's shape: a pod with a parachute on top, a decoupler under it,
        // the upper stage, another decoupler, the booster.
        private static List<PartFacts> KerbalX()
        {
            return new List<PartFacts>
            {
                Part(-1, -1, 0.8, "Mk1 Command Pod", command: true, crew: 1), Part(0, -1, 0.1, "Mk16 Parachute", parachute: true),
                Part(0, 1, 0.05, "TD-12", separator: Separation.Decoupler), Part(2, 1, 4.5, "FL-T800"),
                Part(3, 1, 1.5, "LV-909", engine: true), Part(4, 3, 0.05, "TD-25", separator: Separation.Decoupler),
                Part(5, 3, 18.0, "Rockomax X200-32"), Part(6, 3, 3.0, "Skipper", engine: true),
            };
        }

        // Apollo's shape: the command module with its parachute (0, 1), a decoupler,
        // the service module with its engine and docking port (2-5); the lander under
        // it by its own docking port (6), its ascent stage (7, 8), a decoupler, its
        // descent stage with legs (9-12); an adapter and the transfer stage (13-15).
        private static List<PartFacts> Apollo()
        {
            return new List<PartFacts>
            {
                Part(-1, -1, 2.0, "Mk1-3 Command Pod", command: true, crew: 3), Part(0, -1, 0.1, "Mk16 Parachute", parachute: true),
                Part(0, 0, 0.05, "TD-12", separator: Separation.Decoupler), Part(2, 0, 4.0, "FL-T400"),
                Part(3, 0, 1.0, "LV-909", engine: true), Part(4, 0, 0.1, "Clamp-O-Tron SM", separator: Separation.DockingPort),
                Part(5, 0, 0.1, "Clamp-O-Tron LM", separator: Separation.DockingPort),
                Part(6, 0, 1.0, "Mk2 Lander Can", command: true, crew: 2), Part(7, 0, 0.5, "Spark", engine: true),
                Part(8, 2, 0.05, "TD-06", separator: Separation.Decoupler), Part(9, 2, 2.0, "FL-T200"),
                Part(10, 2, 0.2, "LT-2 Landing Strut", legs: true), Part(10, 2, 0.5, "Terrier", engine: true),
                Part(12, 4, 0.05, "TD-25", separator: Separation.Decoupler), Part(13, 4, 6.0, "Rockomax X200-16"),
                Part(14, 4, 1.5, "Poodle", engine: true),
            };
        }

        // An uncrewed satellite on a two-stage rocket: the probe core as root with an
        // antenna (0, 1), a decoupler, the upper stage (3, 4), a decoupler, the booster
        // (6, 7), which fires first.
        private static List<PartFacts> SatelliteRocket()
        {
            List<PartFacts> parts = new List<PartFacts>
            {
                Part(-1, -1, 0.1, "OKTO2", command: true), Part(0, -1, 0.05, "Communotron 16"),
                Part(0, 1, 0.05, "TD-06", separator: Separation.Decoupler), Part(2, 1, 2.0, "FL-T400"),
                Part(3, 1, 0.5, "Terrier", engine: true), Part(4, 3, 0.05, "TD-12", separator: Separation.Decoupler),
                Part(5, 3, 4.0, "FL-T800"), Part(6, 3, 1.5, "Swivel", engine: true),
            };
            parts[4].ActivatesAt = 2;
            parts[7].ActivatesAt = 4;
            return parts;
        }

        [TestMethod]
        public void ACrewedVesselsCapsuleIsItsPod()
        {
            List<PartFacts> parts = KerbalX();
            List<PartGroupChoice> groups = VesselShape.Candidates(parts);
            PartGroupChoice capsule = VesselShape.AutoCapsule(groups);

            Assert.IsTrue(VesselShape.Crewed(parts));
            Assert.IsNotNull(capsule);
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, capsule.Members.ToArray(), "the decoupler goes with the stage under it");
            StringAssert.StartsWith(capsule.Name, "Mk1 Command Pod, 2 parts");
        }

        [TestMethod]
        public void APodWithoutParachutesIsStillTheCapsule()
        {
            List<PartFacts> parts = KerbalX();
            parts[1].Parachute = false;
            PartGroupChoice capsule = VesselShape.AutoCapsule(VesselShape.Candidates(parts));

            Assert.IsNotNull(capsule);
            Assert.IsFalse(capsule.Parachute, "the planner says it lacks parachutes");
        }

        [TestMethod]
        public void TheLanderIsWhatItsDockingPortSeparatesWithoutTheStagesDroppedBeforeIt()
        {
            List<PartGroupChoice> groups = VesselShape.Candidates(Apollo());
            PartGroupChoice capsule = VesselShape.AutoCapsule(groups);
            PartGroupChoice lander = groups.Single(group => group.DockingPort && group.Legs);

            CollectionAssert.AreEquivalent(new[] { 6, 7, 8, 9, 10, 11, 12 }, lander.Members.ToArray(),
                "the lander under its own port, without the transfer stage");
            StringAssert.StartsWith(lander.Name, "Mk2 Lander Can");
            Assert.IsTrue(lander.Command && lander.Engine);
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, capsule.Members.ToArray(), "the command module, the lightest crewed part");
            Assert.IsTrue(VesselShape.Fits(lander, capsule));
            Assert.AreEqual(-1, VesselShape.Parent(new List<PartGroupChoice> { lander, capsule }, 1), "the capsule belongs to the rest");
        }

        [TestMethod]
        public void TheKerbalXsUpperStageWithItsLegsCanBeTheLander()
        {
            // The legs on the upper stage: the pod and the upper stage land together.
            List<PartFacts> parts = KerbalX();
            parts.Add(Part(3, 1, 0.2, "LT-1 Landing Struts", legs: true));
            List<PartGroupChoice> groups = VesselShape.Candidates(parts);
            PartGroupChoice capsule = VesselShape.AutoCapsule(groups);
            PartGroupChoice lander = groups.Single(group => group.Legs && group.Command);

            CollectionAssert.AreEquivalent(new[] { 0, 1, 2, 3, 4, 8 }, lander.Members.ToArray(), "the pod, the upper stage, its legs");
            Assert.IsFalse(lander.DockingPort);
            Assert.AreEqual(0, VesselShape.Parent(new List<PartGroupChoice> { lander, capsule }, 1), "the capsule is part of the lander");
        }

        [TestMethod]
        public void AnUncrewedVesselsOneSatelliteIsFoundByItself()
        {
            List<PartFacts> parts = SatelliteRocket();
            List<PartGroupChoice> groups = VesselShape.Candidates(parts);
            PartGroupChoice satellite = VesselShape.AutoSatellite(parts, groups);

            Assert.IsFalse(VesselShape.Crewed(parts));
            Assert.IsNotNull(satellite);
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, satellite.Members.ToArray(), "the probe core and its antenna");
        }

        [TestMethod]
        public void TwoSatellitesAreLeftToThePlayer()
        {
            // A second probe core under a decoupler on the upper stage.
            List<PartFacts> parts = SatelliteRocket();
            parts.Add(Part(3, 2, 0.05, "TT-38K", separator: Separation.Decoupler));
            parts.Add(Part(8, 2, 0.1, "OKTO", command: true));

            Assert.IsNull(VesselShape.AutoSatellite(parts, VesselShape.Candidates(parts)));
        }

        [TestMethod]
        public void ALanderThatFliesOnAloneClimbsToTheDestinationNotToTheRest()
        {
            RouteStop high = Orbit(Mun, 500000);
            List<RouteStop> goals = new List<RouteStop> { Surface(Kerbin), Surface(Mun), high };
            List<RouteStep> steps = Route(Stock(), goals, true, false, false, true, false);

            Assert.AreEqual(500000.0, steps[3].To.Altitude, "up to the 500 km orbit");
        }

        [TestMethod]
        public void ALanderThatFliesOnAloneGoesHomeByItself()
        {
            // The Kerbal X to the Mun and home: the lander lands, climbs and flies home
            // alone, and the capsule lands on its parachutes; no docking.
            List<RouteStop> goals = new List<RouteStop> { Surface(Kerbin), Surface(Mun), Surface(Kerbin, true) };
            List<RouteStep> alone = Route(Stock(), goals, true, false, true, true, false);
            List<RouteStep> docks = Route(Stock(), goals, true, false, true, true, true);

            Assert.AreEqual("1L:S 1T:S 1L:L< 2L:L 2T:L 2L:C<", Shape(alone));
            Assert.IsFalse(alone.Any(step => step.Docks || step.Leg.Lines.Any(line => line.Name == "Rendezvous and docking")));
            Assert.IsTrue(alone.All(step => step.Problem == null));
            Assert.AreEqual("1L:S 1T:S 1L:L< 2L:L> 2T:S 2L:C<", Shape(docks), "one that docks comes back to the rest");
        }

        // The pod on top with its parachute (0), a decoupler (1), the lander with a
        // probe core, an engine and legs (2-4), a decoupler (5), the transfer stage (6),
        // and a launch clamp on it (7).
        private static List<PartFacts> LanderOnTop(bool clampKnown)
        {
            List<PartFacts> parts = new List<PartFacts>
            {
                Part(-1, -1, 1.0, "Mk1 Command Pod", command: true, crew: 1), Part(0, 1, 0.05, "TD-06", separator: Separation.Decoupler),
                Part(1, 1, 0.2, "OKTO", command: true), Part(2, 1, 0.5, "Terrier", engine: true),
                Part(2, 1, 0.2, "LT-2 Landing Strut", legs: true), Part(3, 3, 0.05, "TD-12", separator: Separation.Decoupler),
                Part(5, 3, 6.0, "FL-T800"), Part(6, -1, 1.0, "TT18-A Launch Stability Enhancer"),
            };
            parts[7].Clamp = clampKnown;
            return parts;
        }

        [TestMethod]
        public void ALanderLeavesOutTheLaunchClampsAndWhatIsNoLongerJoinedToIt()
        {
            foreach (bool known in new[] { true, false })
            {
                List<PartGroupChoice> groups = VesselShape.Candidates(LanderOnTop(known));
                PartGroupChoice lander = groups.Single(group => group.Members.Contains(3) && group.Members.Contains(2)
                                                                && !group.Members.Contains(0));
                CollectionAssert.AreEquivalent(new[] { 2, 3, 4 }, lander.Members.ToArray(), "clamp known: " + known);
                Assert.IsFalse(groups.Any(group => group.Members.Contains(7) && group.Command), "no clamp with a command part");
            }
        }

        [TestMethod]
        public void ADockingPortWithNothingBeyondItCutsNothingOff()
        {
            // A port on the pod's nose, and legs on the lander stage under the pod: the
            // lander is separated by the decoupler, not by the port.
            List<PartFacts> parts = LanderOnTop(true);
            parts.Add(Part(0, -1, 0.05, "Clamp-O-Tron Jr", separator: Separation.DockingPort));
            List<PartGroupChoice> groups = VesselShape.Candidates(parts);

            Assert.IsFalse(groups.Any(group => group.DockingPort), "no cut at the nose port");
            Assert.IsTrue(groups.Any(group => group.Legs && group.Command && !group.DockingPort), "the decoupler separates the lander");
        }

        [TestMethod]
        public void TwoDockingPortsJoinedInTheEditorGiveTheLanderOnce()
        {
            List<PartGroupChoice> groups = VesselShape.Candidates(Apollo());

            Assert.IsTrue(groups.Any(group => group.Members.SetEquals(new[] { 6, 7, 8, 9, 10, 11, 12 })), "the lower port's cut");
            Assert.IsFalse(groups.Any(group => group.Members.SetEquals(new[] { 5, 6, 7, 8, 9, 10, 11, 12 })), "not the upper port's");
        }

        [TestMethod]
        public void ALandersDropTanksStayWithIt()
        {
            // Radial drop tanks on the descent stage (16), decoupled a stage before its
            // legs, while its engine fires from that stage on.
            List<PartFacts> parts = Apollo();
            parts[12].ActivatesAt = 3;
            parts.Add(Part(10, 3, 1.0, "FL-T100 drop tank"));
            PartGroupChoice lander = VesselShape.Candidates(parts).Single(group => group.DockingPort && group.Legs);

            Assert.IsTrue(lander.Members.Contains(16), "the drop tank flies with the lander");
            Assert.IsFalse(lander.Members.Contains(14), "the transfer stage does not");
        }

        // A step of `need` flown by the groups given.
        private static BudgetStep Step(double need, params int[] flying)
        {
            BudgetStep step = new BudgetStep { Need = need, Side = new bool[3] };
            foreach (int group in flying) step.Side[group] = true;
            return step;
        }

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

        [TestMethod]
        public void APlaneChangeToAPlanetsOwnMoonIsTheMoonsTilt()
        {
            Leg leg = RoutePlanner.LegOf(Stock(), Orbit(Kerbin, 80000), Orbit(Minmus, 10000), 0, true);

            // At the transfer's far end, where the craft is slowest: a few m/s.
            Assert.IsTrue(leg.Lines.Any(line => line.Name == "Plane change" && line.DeltaV > 1 && line.DeltaV < 50));
        }

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

        // ------------------------------------------------ the steps of a route
        //
        // Five missions, each set up as the player sets it in the planner: the
        // destinations, and the steps whose vehicle the player clicks (Choose).

        private static RouteStop Choose(RouteStop stop, StepRole role, Flyer flyer)
        {
            stop.By[(int)role] = flyer.Code;
            return stop;
        }

        // The lander as section 1, the capsule as section 2.
        private static readonly Flyer LanderFlies = Flyer.Alone(1);
        private static readonly Flyer CapsuleFlies = Flyer.Alone(2);
        private static readonly Flyer WithoutLander = Flyer.Without(1);

        // The route with a lander (section 1, docking again or flying on alone) and a
        // capsule (section 2, part of the lander or of the rest), as the planner hands
        // them to RouteSteps.
        private static List<RouteStep> Route(IList<PlannerBody> bodies, IList<RouteStop> goals, bool hasLander, bool planeChanges,
                                             bool hasCapsule = false, bool capsuleInLander = true, bool landerDocks = true)
        {
            List<RouteSection> sections = new List<RouteSection>();
            if (hasLander) sections.Add(new RouteSection { Id = 1, Kind = SectionKind.Lander, Name = "Lander", Docks = landerDocks });
            if (hasCapsule)
                sections.Add(new RouteSection
                {
                    Id = 2, Kind = SectionKind.Capsule, Name = "Capsule", Parent = hasLander && capsuleInLander ? 1 : 0,
                });
            return RouteSteps.Build(bodies, goals, sections, planeChanges);
        }

        // S the whole vessel, L section 1, C section 2, R without section 1.
        private static string Letter(Flyer flyer)
        {
            if (flyer.Kind == FlyerKind.Ship) return "S";
            if (flyer.Kind == FlyerKind.Without) return "R";
            return flyer.Section == 1 ? "L" : flyer.Section == 2 ? "C" : "X" + flyer.Section;
        }

        private static string Shape(IEnumerable<RouteStep> steps)
        {
            return string.Join(" ", steps.Select(step => step.Goal + step.Role.ToString().Substring(0, 1) + ":"
                                                         + Letter(step.Flyer)
                                                         + (step.Separates ? "<" : "") + (step.Docks ? ">" : "")));
        }

        private const int JKerbin = 1, Jool = 2, JLaythe = 3, Vall = 4, JTylo = 5;

        // The Jool system, as KSP 1.12.5 defines it.
        private static List<PlannerBody> JoolSystem()
        {
            List<PlannerBody> stock = Stock();
            return new List<PlannerBody>
            {
                stock[Sun], stock[Kerbin],
                new PlannerBody
                {
                    Name = "Jool", Parent = Sun, Mu = 2.82528e14, Radius = 6000000, RotationPeriod = 36000,
                    AtmosphereDepth = 200000, SurfacePressure = 15, SemiMajorAxis = 68773560320, Inclination = 1.304,
                    LowOrbitAltitude = 210000,
                },
                new PlannerBody
                {
                    Name = "Laythe", Parent = 2, Mu = 1.962e12, Radius = 500000, RotationPeriod = 52980.879,
                    AtmosphereDepth = 50000, SurfacePressure = 0.6, SemiMajorAxis = 27184000, LowOrbitAltitude = 60000,
                },
                new PlannerBody
                {
                    Name = "Vall", Parent = 2, Mu = 2.074815e11, Radius = 300000, RotationPeriod = 105962.09,
                    SemiMajorAxis = 43152000, LowOrbitAltitude = 15000,
                },
                new PlannerBody
                {
                    Name = "Tylo", Parent = 2, Mu = 2.82528e12, Radius = 600000, RotationPeriod = 211926.36,
                    SemiMajorAxis = 68500000, LowOrbitAltitude = 17000,
                },
            };
        }

        [TestMethod]
        public void OneVesselToMinmusAndHomeOnItsParachutes()
        {
            // A Kerbin surface, B Minmus surface, C Kerbin surface with Chutes; no lander.
            List<RouteStop> goals = new List<RouteStop> { Surface(Kerbin), Surface(Minmus), Surface(Kerbin, true) };
            List<RouteStep> steps = Route(Stock(), goals, false, false);

            Assert.AreEqual("1L:S 1T:S 1L:S 2L:S 2T:S 2L:S", Shape(steps));
            Assert.AreEqual(StopKind.Entry, steps[4].To.Kind, "home straight into Kerbin's air");
            Assert.AreEqual(0.0, steps[5].Leg.DeltaV, 1e-9, "down on the parachutes");
            Assert.IsTrue(steps.All(step => step.Problem == null));
        }

        [TestMethod]
        public void ASatelliteStaysInItsOrbitAndTheCapsuleComesHome()
        {
            // A Kerbin surface, B Kerbin orbit at 2,863 km, C Kerbin surface with Chutes;
            // the capsule marked as the lander, and the landing clicked to Lander.
            RouteStop home = Choose(Surface(Kerbin, true), StepRole.Land, LanderFlies);
            List<RouteStop> goals = new List<RouteStop> { Surface(Kerbin), Orbit(Kerbin, 2863334), home };
            List<RouteStep> steps = Route(Stock(), goals, true, false);

            Assert.AreEqual("1L:S 2L:L<", Shape(steps));
            Assert.AreEqual(2863334.0, steps[0].To.Altitude, "the launch goes straight up to the destination's orbit");
            Assert.AreEqual(LanderFlies.Code, steps[1].Flyer.Code, "the capsule alone; the satellite stays");
            Assert.IsTrue(steps.All(step => step.Problem == null));
        }

        [TestMethod]
        public void TheDunaLanderFliesHomeByItselfAndLeavesTheTransferStage()
        {
            // A Kerbin surface, B Duna orbit, C Duna surface with Chutes, D Kerbin surface
            // with Chutes; the Duna landing clicked to Lander, and the way across home
            // too, where the planner would have it dock.
            RouteStop landing = Choose(Surface(Duna, true), StepRole.Land, LanderFlies);
            RouteStop home = Choose(Surface(Kerbin, true), StepRole.Transfer, LanderFlies);
            List<RouteStop> goals = new List<RouteStop> { Surface(Kerbin), Orbit(Duna, 60000), landing, home };
            List<RouteStep> steps = Route(Stock(), goals, true, false);

            Assert.AreEqual("1L:S 1T:S 2L:L< 3L:L 3T:L 3L:L", Shape(steps));
            Assert.AreEqual(60000.0, steps[3].To.Altitude, "up to where the transfer stage waits");
            Assert.IsTrue(steps.All(step => step.Problem == null));

            // Without the click, the lander docks with the transfer stage, and the
            // whole vessel flies home.
            home.By[(int)StepRole.Transfer] = -1;
            Assert.AreEqual("1L:S 1T:S 2L:L< 3L:L> 3T:S 3L:S", Shape(Route(Stock(), goals, true, false)));
        }

        [TestMethod]
        public void FromAMunBaseALanderHopsOnMinmusAndComesBack()
        {
            // A Mun surface, B Minmus orbit, C Minmus surface, D Minmus surface, E Mun
            // surface; the first Minmus landing clicked to Lander.
            RouteStop first = Choose(Surface(Minmus), StepRole.Land, LanderFlies);
            List<RouteStop> goals = new List<RouteStop> { Surface(Mun), Orbit(Minmus, 10000), first, Surface(Minmus), Surface(Mun) };
            List<RouteStep> steps = Route(Stock(), goals, true, false);

            Assert.AreEqual("1L:S 1T:S 2L:L< 3L:L 3L:L 4L:L> 4T:S 4L:S", Shape(steps));
            Assert.AreEqual(Mun, steps[0].From.Body, "the start is the Mun");
            RouteStep back = steps[5];
            Assert.IsTrue(back.Leg.Lines.Any(line => line.Name == "Rendezvous and docking"));
            Assert.IsTrue(steps.All(step => step.Problem == null));
        }

        [TestMethod]
        public void AroundJoolTheLanderLandsTwiceIsRefilledAndTheCapsuleLandsAtHome()
        {
            // A Kerbin surface, B Kerbin orbit, C Jool orbit with Aerobrake, D Laythe
            // orbit, E Laythe surface with Chutes, F Vall orbit, G Vall surface, H Tylo
            // orbit, I Kerbin surface with Chutes. Clicked: the two landings and the
            // landing at home to Lander, Refuel where the lander docks after Laythe.
            RouteStop jool = Orbit(Jool, 210000);
            jool.UseAtmosphere = true;
            RouteStop laythe = Choose(Surface(JLaythe, true), StepRole.Land, LanderFlies);
            RouteStop vall = Orbit(Vall, 15000);
            vall.Refuel[(int)StepRole.Launch] = true;
            RouteStop vallSurface = Choose(Surface(Vall), StepRole.Land, LanderFlies);
            RouteStop home = Choose(Surface(JKerbin, true), StepRole.Land, LanderFlies);
            List<RouteStop> goals = new List<RouteStop>
            {
                Surface(JKerbin), Orbit(JKerbin, 80000), jool, Orbit(JLaythe, 60000), laythe, vall, vallSurface,
                Orbit(JTylo, 17000), home,
            };
            List<RouteStep> steps = Route(JoolSystem(), goals, true, false);

            Assert.AreEqual("1L:S 2T:S 3T:S 4L:L< 5L:L> 5T:S 6L:L< 7L:L> 7T:S 8T:S 8L:L<", Shape(steps));
            Assert.IsTrue(steps[4].RefuelAfter, "refilled after Laythe");
            Assert.IsFalse(steps[7].RefuelAfter);
            Assert.IsTrue(steps[1].Leg.Lines.Any(line => line.Name.StartsWith("Aerobraking")), "into Jool's orbit by aerobraking");
            Assert.AreEqual(StopKind.Entry, steps[9].To.Kind, "the whole vessel into Kerbin's air");
            Assert.IsTrue(steps.All(step => step.Problem == null));
        }

        [TestMethod]
        public void ALanderThatWasLeftBehindCannotFlyOn()
        {
            // The rest flies on without the lander (Rest, behind "..."), and a later step
            // is clicked to Lander.
            RouteStop onward = Choose(Orbit(Kerbin, 80000), StepRole.Transfer, WithoutLander);
            RouteStop later = Choose(Orbit(Minmus, 10000), StepRole.Transfer, LanderFlies);
            List<RouteStop> goals = new List<RouteStop> { Orbit(Mun, 14000), onward, later };
            List<RouteStep> steps = Route(Stock(), goals, true, false);

            Assert.AreEqual(WithoutLander.Code, steps[0].Flyer.Code);
            Assert.IsTrue(steps[0].Separates);
            Assert.AreEqual("The lander was left in Mun orbit", steps[1].Problem);
            Assert.AreEqual(0.0, steps[1].Leg.DeltaV, "never flown");
        }

        [TestMethod]
        public void TheWholeVesselCannotGoOnWhereTheLanderIsNotWithTheRest()
        {
            // The lander flies alone from Kerbin's orbit to the Mun's, and the next
            // step is clicked to Ship: the rest waits back at Kerbin.
            RouteStop mun = Choose(Orbit(Mun, 14000), StepRole.Transfer, LanderFlies);
            RouteStop minmus = Choose(Orbit(Minmus, 10000), StepRole.Transfer, Flyer.Ship);
            List<RouteStep> steps = Route(Stock(), new List<RouteStop> { Orbit(Kerbin, 80000), mun, minmus }, true, false);

            Assert.AreEqual("The lander is not with the rest of the vessel in Kerbin orbit", steps[1].Problem);
        }

        [TestMethod]
        public void ALandingOnParachutesFromAnOrbitGoesDownIntoTheAir()
        {
            Leg high = RoutePlanner.LegOf(Stock(), Orbit(Kerbin, 2863334), Surface(Kerbin, true), 0, false);
            Leg low = RoutePlanner.LegOf(Stock(), Orbit(Kerbin, 80000), Surface(Kerbin, true), 0, false);

            Assert.AreEqual("Deorbit into the atmosphere", high.Lines[0].Name);
            Near(450, high.DeltaV, 40, "from a synchronous orbit");
            Near(40, low.DeltaV, 25, "from a low orbit");
        }

        [TestMethod]
        public void TheRestLeftBehindByTheLanderHasItsOwnMassLeft()
        {
            // Mothership: S1 16 t to 10 t, the lander S0 4 t to 2 t; the lander flies off.
            LegMode[] modes = { LegMode.Whole, LegMode.Lander };
            StageBudget.Result result = Allocate(Mothership(), new[] { 500.0, 300.0 }, 0.0, null, 0, modes);

            double mass = 16.0 * System.Math.Exp(-500.0 / Exhaust) - 4.0;
            double fuel = 6.0 - 16.0 * (1.0 - System.Math.Exp(-500.0 / Exhaust));
            Assert.AreEqual(Exhaust * System.Math.Log(mass / (mass - fuel)), result.Left[1], 1e-6);
        }

        [TestMethod]
        public void TheJoolTourWithEveryOrbitAsADestinationGivesTheSameSteps()
        {
            // As listed: Laythe orbit and Vall orbit also after the landings, 11 stops.
            RouteStop jool = Orbit(Jool, 210000);
            jool.UseAtmosphere = true;
            RouteStop laytheBack = Orbit(JLaythe, 60000);
            laytheBack.Refuel[(int)StepRole.Launch] = true;
            List<RouteStop> goals = new List<RouteStop>
            {
                Surface(JKerbin), Orbit(JKerbin, 80000), jool, Orbit(JLaythe, 60000),
                Choose(Surface(JLaythe, true), StepRole.Land, LanderFlies), laytheBack, Orbit(Vall, 15000),
                Choose(Surface(Vall), StepRole.Land, LanderFlies), Orbit(Vall, 15000), Orbit(JTylo, 17000),
                Choose(Surface(JKerbin, true), StepRole.Land, LanderFlies),
            };
            List<RouteStep> steps = Route(JoolSystem(), goals, true, false);

            Assert.AreEqual("1L:S 2T:S 3T:S 4L:L< 5L:L> 6T:S 7L:L< 8L:L> 9T:S 10T:S 10L:L<", Shape(steps));
            Assert.IsTrue(steps[4].RefuelAfter, "refilled as it docks after Laythe");
            Assert.IsTrue(steps.All(step => step.Problem == null));
        }

        [TestMethod]
        public void TheCapsuleLandsAtHomeOnItsParachutesByItself()
        {
            // A Kerbin surface, B Mun surface, C Kerbin surface with Chutes; a capsule
            // marked, nothing clicked on the steps.
            List<RouteStop> goals = new List<RouteStop> { Surface(Kerbin), Surface(Mun), Surface(Kerbin, true) };
            List<RouteStep> steps = Route(Stock(), goals, false, false, true);

            Assert.AreEqual("1L:S 1T:S 1L:S 2L:S 2T:S 2L:C<", Shape(steps));
            Assert.AreEqual(CapsuleFlies.Code, steps[5].Flyer.Code);
            Assert.AreEqual(StopKind.Entry, steps[5].From.Kind, "it leaves the vessel in Kerbin's air");
        }

        [TestMethod]
        public void NothingButTheCapsuleFliesOnceItHasLeft()
        {
            RouteStop home = Choose(Surface(Kerbin, true), StepRole.Land, CapsuleFlies);
            RouteStop onward = Choose(Orbit(Kerbin, 80000), StepRole.Launch, Flyer.Ship);
            List<RouteStep> steps = Route(Stock(), new List<RouteStop> { Orbit(Kerbin, 80000), home, onward }, false, false, true);

            Assert.AreEqual("The capsule has left the vessel", steps[1].Problem);
        }

        [TestMethod]
        public void TheCapsuleLeavesEverythingElseWithWhatItHolds()
        {
            // S2 the transfer stage, S1 the lander, S0 the capsule without engines.
            List<BudgetStage> stages = new List<BudgetStage>
            {
                Stage(2, 20, 14), Stage(1, 6, 4), new BudgetStage { Number = 0, StartMass = 1.5, EndMass = 1.5 },
            };
            LegMode[] modes = { LegMode.Whole, LegMode.Capsule };
            StageBudget.Result result = Allocate(stages, new[] { 500.0, 0.0 }, 0.0, null, 1, modes, null, 0);

            Assert.IsTrue(result.Dropped.ContainsKey(2) && result.Dropped.ContainsKey(1), "left with what they hold");
            Assert.AreEqual(0.0, result.TotalShort, 1e-9, "the capsule lands on its parachutes");
        }

        [TestMethod]
        public void FromAnOrbitTheVesselBringsTheCapsuleDownRatherThanTheCapsuleItself()
        {
            // The satellite's mission with a capsule marked: from 2,863 km the whole
            // vessel lands; the capsule would have to deorbit on its own.
            List<RouteStop> goals = new List<RouteStop> { Surface(Kerbin), Orbit(Kerbin, 2863334), Surface(Kerbin, true) };
            Assert.AreEqual("1L:S 2L:S", Shape(Route(Stock(), goals, false, false, true)));
        }

        [TestMethod]
        public void TheCapsuleBurnsItsOwnStagesPushingOnlyItself()
        {
            // S1 the rest, 20 t to 12 t; S0 the capsule with a small engine, 2 t to 1.5 t.
            List<BudgetStage> stages = new List<BudgetStage> { Stage(1, 20, 12), Stage(0, 2, 1.5) };
            StageBudget.Result result = Allocate(stages, new[] { 500.0, 100.0 }, 0.0, null, -1,
                new[] { LegMode.Whole, LegMode.Capsule }, null, 0);

            StageBudget.StageUse use = result.Uses.Single(u => u.Leg == 1);
            Assert.AreEqual(0, use.Stage);
            Assert.AreEqual(2.0, use.Mass, 1e-9, "the capsule alone");
            Assert.IsTrue(result.Dropped.ContainsKey(1), "the rest left with what it holds");
        }

        [TestMethod]
        public void FromThePartsTheTransferStageDroppedBeforeTheLanderIsGoneForTheRest()
        {
            // The rest: the transfer stage S2 (6 t propellant, 3 t dropped after it) and
            // the service module S0 (2 t propellant, 2 t dry); the lander S1 between
            // them (1 t propellant, 2 t dry).
            List<BudgetStage> stages = new List<BudgetStage> { Stage(2, 16, 10), Stage(1, 7, 6), Stage(0, 4, 2) };
            VesselSplit split = Groups(new[] { 13.0, 3.0 }, 0, 1, 0);
            BudgetStep without = Step(100.0, 0);
            without.Gone.Add(new GoneGroup { Group = 1, Stays = true });
            StageBudget.Result result = StageBudget.Allocate(stages,
                new List<BudgetStep> { Step(stages[0].DeltaV + 50.0, 0, 1), Step(100.0, 1), without }, 0.0, null, split);

            Assert.AreEqual(4.0, result.Uses.Single(u => u.Leg == 2).Mass, 1e-6, "the service module without the transfer stage");
        }

        [TestMethod]
        public void ASplitTransferIsTheBurnThatLeavesAndTheOneThatArrives()
        {
            RouteStop high = Orbit(Kerbin, 2863334);
            List<RouteStop> goals = new List<RouteStop> { Surface(Kerbin), Orbit(Kerbin, 80000), high };
            double together = Route(Stock(), goals, false, false).Sum(step => step.Leg.DeltaV);
            high.SplitBurns = true;
            List<RouteStep> steps = Route(Stock(), goals, false, false);

            Assert.AreEqual("1L:S 2T:S 2A:S", Shape(steps));
            Assert.AreEqual(StopKind.Transfer, steps[1].To.Kind);
            Assert.AreEqual("Kerbin transfer orbit", RouteSteps.Place(Stock(), steps[1].To));
            Assert.AreEqual(RoutePlanner.LeavingBurn, steps[1].Leg.Lines.Single().Name);
            Assert.AreEqual("Circularise", steps[2].Leg.Lines.Single().Name);
            Assert.AreEqual(together, steps.Sum(step => step.Leg.DeltaV), 1e-6, "the same burns, in two steps");
            Assert.IsTrue(steps.All(step => step.Problem == null));
        }

        [TestMethod]
        public void TheSatelliteCircularisesByItselfAfterTheUpperStage()
        {
            // Scenario 3: the upper stage burns into the transfer orbit, the satellite,
            // marked as the capsule, flies the arrival alone.
            RouteStop high = Choose(Orbit(Kerbin, 2863334), StepRole.Arrive, CapsuleFlies);
            high.SplitBurns = true;
            List<RouteStop> goals = new List<RouteStop> { Surface(Kerbin), Orbit(Kerbin, 80000), high };
            List<RouteStep> steps = Route(Stock(), goals, false, false, true);

            Assert.AreEqual("1L:S 2T:S 2A:C<", Shape(steps));
            Assert.AreEqual(CapsuleFlies.Code, steps[2].Flyer.Code);
            Assert.AreEqual(StopKind.Transfer, steps[2].From.Kind, "it leaves the upper stage on the way up");
            Assert.IsTrue(steps.All(step => step.Problem == null));
        }

        [TestMethod]
        public void AWayStraightIntoTheAirCannotBeSplit()
        {
            Assert.IsFalse(RouteSteps.Splits(Stock(), Orbit(Mun, 14000), Surface(Kerbin, true)));
            Assert.IsTrue(RouteSteps.Splits(Stock(), Orbit(Kerbin, 80000), Orbit(Mun, 14000)));
        }

        [TestMethod]
        public void TheApolloCapsuleComesHomeFromTheRestAfterTheLanderIsLeft()
        {
            // Scenario 4: Kerbin surface, Kerbin orbit, Mun orbit, Mun surface, Mun
            // orbit, Kerbin surface with Chutes; the way home clicked to Without lander.
            // The capsule is part of the rest.
            RouteStop home = Choose(Surface(Kerbin, true), StepRole.Transfer, WithoutLander);
            List<RouteStop> goals = new List<RouteStop>
            {
                Surface(Kerbin), Orbit(Kerbin, 80000), Orbit(Mun, 14000), Surface(Mun), Orbit(Mun, 14000), home,
            };
            List<RouteStep> steps = Route(Stock(), goals, true, false, true, false);

            Assert.AreEqual("1L:S 2T:S 3L:L< 4L:L> 5T:R< 5L:C<", Shape(steps), "the lander docks, and is left from there");
            Assert.IsTrue(steps[3].Leg.Lines.Any(line => line.Name == "Rendezvous and docking"));
            Assert.AreEqual(StopKind.Entry, steps[5].From.Kind, "the capsule leaves the rest in Kerbin's air");
            Assert.IsTrue(steps.All(step => step.Problem == null));
        }

        [TestMethod]
        public void AroundJoolTheLanderIsUsedTwiceAndLeftBeforeTheCapsuleComesHome()
        {
            // Scenario 5: the lander lands on Laythe and Vall, docks and is refilled
            // after Laythe; the way home clicked to Without lander; the capsule is part
            // of the mothership and lands at home by itself.
            RouteStop jool = Orbit(Jool, 210000);
            jool.UseAtmosphere = true;
            RouteStop laytheBack = Orbit(JLaythe, 60000);
            laytheBack.Refuel[(int)StepRole.Launch] = true;
            RouteStop home = Choose(Surface(JKerbin, true), StepRole.Transfer, WithoutLander);
            List<RouteStop> goals = new List<RouteStop>
            {
                Surface(JKerbin), Orbit(JKerbin, 80000), jool, Orbit(JLaythe, 60000), Surface(JLaythe, true), laytheBack,
                Orbit(Vall, 15000), Surface(Vall), Orbit(Vall, 15000), home,
            };
            List<RouteStep> steps = Route(JoolSystem(), goals, true, false, true, false);

            Assert.AreEqual("1L:S 2T:S 3T:S 4L:L< 5L:L> 6T:S 7L:L< 8L:L> 9T:R< 9L:C<", Shape(steps));
            Assert.IsTrue(steps[4].RefuelAfter, "refilled as it docks after Laythe");
            Assert.IsTrue(steps.All(step => step.Problem == null));
        }

        [TestMethod]
        public void ASatelliteStaysAtItsDestinationAndTheRestFliesOnWithoutIt()
        {
            // Kerbin surface, 80 km, 2,863 km, Kerbin surface; the satellite (section 3)
            // stays at the synchronous orbit.
            List<RouteSection> sections = new List<RouteSection>
            {
                new RouteSection { Id = 3, Kind = SectionKind.Satellite, Name = "Satellite", DeployAt = 2 },
            };
            List<RouteStop> goals = new List<RouteStop> { Surface(Kerbin), Orbit(Kerbin, 80000), Orbit(Kerbin, 2863334), Surface(Kerbin, true) };
            List<RouteStep> steps = RouteSteps.Build(Stock(), goals, sections, false);

            RouteStep there = steps.Single(step => step.Goal == 2);
            CollectionAssert.AreEqual(new[] { 3 }, there.Deploys.ToArray());
            Assert.IsTrue(there.Flying.Contains(3), "carried there");
            Assert.IsFalse(steps.Last().Flying.Contains(3), "not on the way home");
            Assert.IsTrue(steps.All(step => step.Problem == null));
        }

        [TestMethod]
        public void ASatelliteLeftWithTheRestIsNotCarriedToItsDestination()
        {
            // The satellite is part of the rest, which waits in Mun orbit while the lander
            // lands, as the player sets it; it is set to stay on the Mun's surface, where it
            // is not carried.
            List<RouteSection> sections = new List<RouteSection>
            {
                new RouteSection { Id = 1, Kind = SectionKind.Lander, Name = "Lander", Docks = true },
                new RouteSection { Id = 3, Kind = SectionKind.Satellite, Name = "Satellite", DeployAt = 2 },
            };
            List<RouteStop> goals = new List<RouteStop> { Orbit(Kerbin, 80000), Orbit(Mun, 14000), Choose(Surface(Mun), StepRole.Land, LanderFlies) };
            List<RouteStep> steps = RouteSteps.Build(Stock(), goals, sections, false);

            Assert.AreEqual("The satellite does not reach Mun surface", steps.Last().Problem);
        }

        [TestMethod]
        public void ASatelliteFlyingItsOwnLastStepStaysThere()
        {
            // The satellite circularises itself after the split transfer, and stays.
            List<RouteSection> sections = new List<RouteSection>
            {
                new RouteSection { Id = 3, Kind = SectionKind.Satellite, Name = "Satellite", DeployAt = 2 },
            };
            RouteStop high = Choose(Orbit(Kerbin, 2863334), StepRole.Arrive, Flyer.Alone(3));
            high.SplitBurns = true;
            List<RouteStop> goals = new List<RouteStop> { Surface(Kerbin), Orbit(Kerbin, 80000), high, Surface(Kerbin, true) };
            List<RouteStep> steps = RouteSteps.Build(Stock(), goals, sections, false);

            RouteStep arrive = steps.Single(step => step.Role == StepRole.Arrive);
            CollectionAssert.AreEqual(new[] { 3 }, arrive.Deploys.ToArray(), "it stays, for the budget too");
            Assert.IsFalse(steps.Last().Flying.Contains(3));
            Assert.IsTrue(steps.All(step => step.Problem == null));
        }

        [TestMethod]
        public void ALanderInsideASatelliteLeavesItWhereTheSatelliteStays()
        {
            // An orbiter (section 3) carrying a probe lander (section 1): the orbiter stays
            // in Mun orbit, the lander lands from there.
            List<RouteSection> sections = new List<RouteSection>
            {
                new RouteSection { Id = 3, Kind = SectionKind.Satellite, Name = "Satellite", DeployAt = 1 },
                new RouteSection { Id = 1, Kind = SectionKind.Lander, Name = "Lander", Parent = 3 },
            };
            RouteStop surface = Choose(Surface(Mun), StepRole.Land, LanderFlies);
            List<RouteStop> goals = new List<RouteStop> { Orbit(Kerbin, 80000), Orbit(Mun, 14000), surface };
            List<RouteStep> steps = RouteSteps.Build(Stock(), goals, sections, false);

            CollectionAssert.AreEqual(new[] { 3 }, steps[0].Deploys.ToArray(), "the orbiter alone stays");
            Assert.AreEqual(LanderFlies.Code, steps[1].Flyer.Code);
            Assert.IsTrue(steps.All(step => step.Problem == null), string.Join("; ", steps.Select(step => step.Problem).ToArray()));
        }

        [TestMethod]
        public void TwoLandersLandWhereEachIsPicked()
        {
            // Two landers under a mothership: the first lands on the Mun by itself; the
            // player sends the second to Minmus.
            List<RouteSection> sections = new List<RouteSection>
            {
                new RouteSection { Id = 1, Kind = SectionKind.Lander, Name = "Lander 1", Docks = true },
                new RouteSection { Id = 4, Kind = SectionKind.Lander, Name = "Lander 2", Docks = true },
            };
            RouteStop minmus = Choose(Surface(Minmus), StepRole.Land, Flyer.Alone(4));
            List<RouteStop> goals = new List<RouteStop>
            {
                Orbit(Kerbin, 80000), Orbit(Mun, 14000), Surface(Mun), Orbit(Mun, 14000), Orbit(Minmus, 10000), minmus,
                Orbit(Minmus, 10000),
            };
            List<RouteStep> steps = RouteSteps.Build(Stock(), goals, sections, false);

            Assert.AreEqual("1T:S 2L:L< 3L:L> 4T:S 5L:X4< 6L:X4", Shape(steps));
            Assert.IsFalse(steps[4].Flying.Contains(1), "the first lander waits with the rest");
            Assert.IsTrue(steps.All(step => step.Problem == null));
        }

        [TestMethod]
        public void TheStepAfterALandersClimbSaysWhetherItDocks()
        {
            // The rest waits in a 500 km Mun orbit; the lander lands and climbs. With the
            // whole vessel flying home after it, it climbs to the rest and docks; with the
            // lander flying home itself, it climbs to its own low orbit and goes on.
            RouteStop home = Surface(Kerbin, true);
            List<RouteStop> goals = new List<RouteStop> { Orbit(Kerbin, 80000), Orbit(Mun, 500000), Surface(Mun), home };
            List<RouteStep> docks = Route(Stock(), goals, true, false);
            home.By[(int)StepRole.Transfer] = LanderFlies.Code;
            List<RouteStep> alone = Route(Stock(), goals, true, false);

            Assert.AreEqual("1T:S 2L:L< 3L:L> 3T:S 3L:S", Shape(docks));
            Assert.AreEqual(500000.0, docks[2].To.Altitude, "up to the rest");
            Assert.AreEqual("1T:S 2L:L< 3L:L 3T:L 3L:L", Shape(alone));
            Assert.AreEqual(Stock()[Mun].LowOrbitAltitude, alone[2].To.Altitude, "up to its own low orbit");
            Assert.IsFalse(alone[2].Leg.Lines.Any(line => line.Name == "Rendezvous and docking"));
            Assert.IsTrue(docks.All(step => step.Problem == null) && alone.All(step => step.Problem == null));
        }

        [TestMethod]
        public void TwoLandersOneAfterTheOtherBothDockAsTheVesselFliesOn()
        {
            // Lander 1 lands and climbs; the player sends lander 2 down right after; then
            // the whole vessel flies home. Both climb to the rest and dock.
            List<RouteSection> sections = new List<RouteSection>
            {
                new RouteSection { Id = 1, Kind = SectionKind.Lander, Name = "Lander 1", Docks = true },
                new RouteSection { Id = 4, Kind = SectionKind.Lander, Name = "Lander 2", Docks = true },
            };
            RouteStop second = Choose(Surface(Mun), StepRole.Land, Flyer.Alone(4));
            List<RouteStop> goals = new List<RouteStop>
            {
                Orbit(Kerbin, 80000), Orbit(Mun, 500000), Surface(Mun), Orbit(Mun, 500000), second, Orbit(Mun, 500000),
                Choose(Orbit(Kerbin, 80000), StepRole.Transfer, Flyer.Ship),
            };
            List<RouteStep> steps = RouteSteps.Build(Stock(), goals, sections, false);

            Assert.AreEqual("1T:S 2L:L< 3L:L> 4L:X4< 5L:X4> 6T:S", Shape(steps));
            Assert.AreEqual(500000.0, steps[2].To.Altitude, "lander 1 climbs to the rest, though lander 2 flies next");
            Assert.IsTrue(steps[5].Flying.Contains(1) && steps[5].Flying.Contains(4), "both carried home");
            Assert.IsTrue(steps.All(step => step.Problem == null));
        }

        [TestMethod]
        public void ALandingShortOfTheEndIsTheLandersByItself()
        {
            // Kerbin surface, Mun surface, Kerbin surface with Chutes; a lander marked,
            // nothing clicked on the steps: the lander lands on the Mun, climbs back,
            // docks, and the whole vessel comes home.
            List<RouteStop> goals = new List<RouteStop> { Surface(Kerbin), Surface(Mun), Surface(Kerbin, true) };
            Assert.AreEqual("1L:S 1T:S 1L:L< 2L:L> 2T:S 2L:S", Shape(Route(Stock(), goals, true, false)));
        }

        [TestMethod]
        public void TheLanderIsFoundByItself()
        {
            // Apollo's lander, whose engines fire after the Poodle's launch stage; the
            // Kerbal X's upper stage with legs, the capsule inside it.
            List<PartFacts> apollo = Firing(Apollo(), 15);
            List<PartGroupChoice> groups = VesselShape.Candidates(apollo);
            PartGroupChoice lander = VesselShape.AutoLander(apollo, groups, VesselShape.AutoCapsule(groups), null);
            Assert.IsNotNull(lander);
            CollectionAssert.AreEquivalent(new[] { 6, 7, 8, 9, 10, 11, 12 }, lander.Members.ToArray());
            Assert.IsTrue(VesselShape.CanDock(apollo, lander), "ports on the lander and on the rest");

            List<PartFacts> kerbalX = Firing(KerbalX(), 7);
            kerbalX.Add(Part(3, 1, 0.2, "LT-1 Landing Struts", legs: true));
            groups = VesselShape.Candidates(kerbalX);
            lander = VesselShape.AutoLander(kerbalX, groups, VesselShape.AutoCapsule(groups), null);
            Assert.IsNotNull(lander);
            CollectionAssert.AreEquivalent(new[] { 0, 1, 2, 3, 4, 8 }, lander.Members.ToArray(), "the pod, the upper stage, its legs");
            Assert.IsFalse(VesselShape.CanDock(kerbalX, lander), "no docking port: it flies on alone");
        }

        [TestMethod]
        public void NoLanderWithoutLegsOrOnABooster()
        {
            List<PartFacts> parts = Firing(KerbalX(), 7);
            List<PartGroupChoice> groups = VesselShape.Candidates(parts);
            Assert.IsNull(VesselShape.AutoLander(parts, groups, VesselShape.AutoCapsule(groups), null), "no landing legs");

            // A radial booster that lands itself: probe core, engine and legs under a
            // radial decoupler, its engine firing at launch.
            parts.Add(Part(6, 3, 0.05, "TT-38K", separator: Separation.Anchored));
            parts.Add(Part(8, 3, 0.1, "OKTO", command: true));
            parts.Add(Part(9, 3, 1.5, "Swivel", engine: true));
            parts.Add(Part(9, 3, 0.2, "LT-1 Landing Struts", legs: true));
            parts[10].ActivatesAt = 4;
            groups = VesselShape.Candidates(parts);
            Assert.IsNull(VesselShape.AutoLander(parts, groups, VesselShape.AutoCapsule(groups), null));

            // Legs on the core stage under the upper stage, which fires at launch.
            parts = Firing(KerbalX(), 7);
            parts.Add(Part(6, 3, 0.2, "LT-1 Landing Struts", legs: true));
            groups = VesselShape.Candidates(parts);
            PartGroupChoice lander = VesselShape.AutoLander(parts, groups, VesselShape.AutoCapsule(groups), null);
            Assert.IsTrue(lander == null || !lander.Members.Contains(7), "not with the launch stage's engine");
        }

        [TestMethod]
        public void AProbeLanderUnderARelayIsTheLanderNotTheRelay()
        {
            // The relay's probe core (0, 1) found as the satellite, a decoupler, the
            // probe lander with its engine and legs (3-6), a decoupler, the booster.
            List<PartFacts> parts = new List<PartFacts>
            {
                Part(-1, -1, 0.1, "OKTO2", command: true), Part(0, -1, 0.3, "RA-2 Relay Antenna"),
                Part(0, 1, 0.05, "TD-06", separator: Separation.Decoupler), Part(2, 1, 0.2, "HECS", command: true),
                Part(3, 1, 0.5, "Spark", engine: true), Part(3, 1, 0.1, "LT-05 Micro Landing Strut", legs: true),
                Part(3, 1, 0.3, "FL-T100"), Part(4, 3, 0.05, "TD-12", separator: Separation.Decoupler),
                Part(7, 3, 4.0, "FL-T800"), Part(8, 3, 1.5, "Swivel", engine: true),
            };
            parts[4].ActivatesAt = 2;
            parts[9].ActivatesAt = 4;
            List<PartGroupChoice> groups = VesselShape.Candidates(parts);
            PartGroupChoice satellite = VesselShape.AutoSatellite(parts, groups);
            PartGroupChoice lander = VesselShape.AutoLander(parts, groups, null, satellite);

            Assert.IsNotNull(lander);
            Assert.IsFalse(lander.Members.Contains(0), "the relay stays");
            Assert.IsTrue(lander.Members.Contains(3) && lander.Members.Contains(4) && lander.Members.Contains(5));
        }

        // The parts with the engine at `launch` firing first, the others after it.
        private static List<PartFacts> Firing(List<PartFacts> parts, int launch)
        {
            for (int i = 0; i < parts.Count; i++)
                if (parts[i].Engine) parts[i].ActivatesAt = i == launch ? 4 : 1;
            return parts;
        }

        [TestMethod]
        public void TheBurnsOfAWayToTheMunKnowTheOrbitTheyBurnIn()
        {
            Leg leg = RoutePlanner.LegOf(Stock(), Orbit(Kerbin, 80000), Orbit(Mun, 14000), 0, false);
            LegLine leave = leg.Lines.Single(line => line.Name == RoutePlanner.LeavingBurn);
            LegLine capture = leg.Lines.Single(line => line.Name == "Capture into orbit");

            Assert.AreEqual(Kerbin, leave.Body);
            Near(680000, leave.Radius, 1, "low Kerbin orbit");
            Assert.AreEqual(Mun, capture.Body);
            Near(214000, capture.Radius, 1, "low Mun orbit");
        }

        [TestMethod]
        public void ABurnTakesAsLongAsItsPropellantFlows()
        {
            // One stage of 10 t, 5 t of it propellant, 3000 m/s exhaust speed, 100 kN:
            // 1000 m/s burn 2.83 t in 85 s; after the plane change 1.93 t are left, 58 s.
            BurnStage stage = new BurnStage { Stage = 1, Mass = 10, EndMass = 5, DeltaV = 3000 * System.Math.Log(2), Thrust = 100 };
            Leg leg = new Leg();
            leg.Add(RoutePlanner.LeavingBurn, 1000, Kerbin, 680000);
            leg.Add("Plane change", 100);
            leg.Add("Capture into orbit", 2000, Mun, 214000);
            List<Burn> burns = BurnCheck.Burns(Stock(), leg, new List<BurnStage> { stage });

            Assert.AreEqual(2, burns.Count, "the plane change on the way is no burn in orbit");
            Near(85.04, burns[0].Seconds, 0.05, "the transfer burn");
            Near(1874.8, burns[0].Period, 0.5, "the low Kerbin orbit");
            Assert.IsFalse(burns[0].Short);
            Near(57.91, burns[1].Seconds, 0.05, "the capture, as far as the propellant goes");
            Assert.IsTrue(burns[1].Short);
            Near(150, stage.Seconds, 0.05, "the stage's own burn time");
        }

        [TestMethod]
        public void ALongBurnLosesWhatPointsAwayOverItsArc()
        {
            Assert.AreEqual(0.0, new Burn { Seconds = 0, Period = 1800 }.Loss);
            Near(1.0 - 2.0 / System.Math.PI, new Burn { Seconds = 900, Period = 1800 }.Loss, 1e-9, "half an orbit");
            Assert.AreEqual(1.0, new Burn { Seconds = 2000, Period = 1800 }.Loss, "more than an orbit");
            Assert.IsTrue(new Burn { Seconds = 90, Period = 1800 }.Loss < BurnCheck.WarnLoss, "a twentieth of the orbit loses little");
            Assert.IsTrue(new Burn { Seconds = 180, Period = 1800 }.Loss < BurnCheck.WarnLoss, "a tenth still under the warning");
        }
        [TestMethod]
        public void ALanderWithoutADockingPortCannotDock()
        {
            // Set to dock with the rest, as the player can on the step after it.
            List<RouteStop> goals = new List<RouteStop> { Surface(Kerbin), Surface(Mun), Surface(Kerbin, true) };
            List<RouteSection> sections = new List<RouteSection>
            {
                new RouteSection { Id = 1, Kind = SectionKind.Lander, Name = "Lander", Docks = true, CanDock = false },
                new RouteSection { Id = 2, Kind = SectionKind.Capsule, Name = "Capsule", Parent = 1 },
            };
            List<RouteStep> steps = RouteSteps.Build(Stock(), goals, sections, false);

            Assert.AreEqual("1L:S 1T:S 1L:L< 2L:L> 2T:S 2L:C<", Shape(steps));
            Assert.IsTrue(steps.Any(step => step.Problem != null && step.Problem.Contains("no docking port")));
        }
        // The rocket from the game: S4 lights the core on the pad (2,240 kN, Isp 336 s,
        // 180 t of fuel), S3 fires four boosters (2,680 kN, Isp 220 s, 56.4 t) and
        // releases the clamps, S2 drops the boosters (25.5 t).
        private static List<StageRow> PadRows()
        {
            return new List<StageRow>
            {
                new StageRow { Number = 4, StartMass = 321.9, EndMass = 141.9, DeltaVVacuum = 336 * 9.80665 * System.Math.Log(321.9 / 141.9), ThrustVacuum = 2240, Thrust = 2240 },
                new StageRow { Number = 3, StartMass = 141.9, EndMass = 85.5, DeltaVVacuum = 220 * 9.80665 * System.Math.Log(141.9 / 85.5), ThrustVacuum = 2680, Thrust = 2680 },
                new StageRow { Number = 2, StartMass = 60.0, EndMass = 60.0 },
                new StageRow { Number = 0, StartMass = 12.0, EndMass = 9.0, DeltaVVacuum = 297, ThrustVacuum = 60 },
            };
        }

        private static List<PartFacts> PadParts()
        {
            PartFacts core = Part(-1, 1, 5.0, "Core engine", engine: true);
            core.ActivatesAt = 4;
            PartFacts booster = Part(0, 3, 3.0, "Booster", engine: true);
            booster.ActivatesAt = 3;
            PartFacts clamp = Part(0, -1, 1.0, "Clamp");
            clamp.Clamp = true;
            clamp.ActivatesAt = 3;
            return new List<PartFacts> { core, booster, clamp };
        }

        [TestMethod]
        public void EnginesLitOnThePadBurnWithTheBoostersThatReleaseTheClamps()
        {
            Dictionary<int, string> labels = new Dictionary<int, string>();
            List<StageRow> rows = ClampedLaunch.Parallel(PadRows(), PadParts(), labels);

            Assert.AreEqual(4, rows[0].Number, "the lift-off");
            Near(321.9, rows[0].StartMass, 0.01, "lifts off full");
            Near(4920, rows[0].ThrustVacuum, 0.1, "at the core's and the boosters' thrust");
            Near(45.4, rows[0].BurnTime, 0.2, "until the boosters are empty");
            Near(234.6, rows[0].EndMass, 0.3, "both burning");
            Assert.AreEqual(3, rows[1].Number, "the core burns on");
            Near(rows[0].EndMass - 25.5, rows[1].StartMass, 0.01, "after the boosters are dropped");
            Near(60.0, rows[1].EndMass, 0.01, "down to the mass KSP has after both");
            Near(2240, rows[1].ThrustVacuum, 0.1, "the core alone");
            Assert.AreEqual("S4+3", labels[4]);
            Assert.AreEqual("S4", labels[3]);
            Assert.AreEqual(2, rows[2].Number);
            Assert.AreEqual(0, rows[3].Number);
            Assert.IsTrue(rows[0].ThrustVacuum / (rows[0].StartMass * 9.81) > 1.5, "the vessel lifts off");
        }

        [TestMethod]
        public void AStageOnThePadThatBurnsNothingDropsNothing()
        {
            // S5 lights the core, S4 does nothing, S3 fires the boosters.
            List<StageRow> rows = PadRows();
            rows[0].Number = 5;
            rows.Insert(1, new StageRow { Number = 4, StartMass = 141.9, EndMass = 141.9 });
            List<PartFacts> parts = PadParts();
            parts[0].ActivatesAt = 5;
            List<StageRow> result = ClampedLaunch.Parallel(rows, parts, new Dictionary<int, string>());

            CollectionAssert.AreEqual(new[] { 5, 3, 2, 0 }, result.Select(row => row.Number).ToArray());
        }

        [TestMethod]
        public void ClampsStagedAfterADropLeaveTheStagesAlone()
        {
            // Something dropped between the core and the release: not a lift-off.
            List<StageRow> rows = PadRows();
            rows[1].StartMass = 120.0;
            rows[1].DeltaVVacuum = 220 * 9.80665 * System.Math.Log(120.0 / 85.5);

            Assert.AreSame(rows, ClampedLaunch.Parallel(rows, PadParts(), new Dictionary<int, string>()));
        }

        [TestMethod]
        public void WithoutLaunchClampsTheStagesStayAsKspBurnsThem()
        {
            List<PartFacts> parts = PadParts();
            parts[2].Clamp = false;
            List<StageRow> rows = PadRows();

            Assert.AreSame(rows, ClampedLaunch.Parallel(rows, parts, new Dictionary<int, string>()));
        }
    }
}
