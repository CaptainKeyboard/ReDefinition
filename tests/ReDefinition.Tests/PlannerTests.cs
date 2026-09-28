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
            StageBudget.Result result = StageBudget.Allocate(stages, new[] { 100000.0 }, 0.0, null, -1, null);

            Assert.AreEqual(stages.Sum(stage => stage.DeltaV), Used(result, 0, 1) + Used(result, 0, 0), 1e-6);
        }

        [TestMethod]
        public void AStageCarriesOnIntoTheNextLegWithTheSameDeltaVInAll()
        {
            List<BudgetStage> stages = new List<BudgetStage> { Stage(1, 20, 12), Stage(0, 8, 4) };
            StageBudget.Result result = StageBudget.Allocate(stages, new[] { 1000.0, 1000.0, 100000.0 }, 0.0, null, -1, null);

            Assert.AreEqual(1000.0, Used(result, 0, 1), 1e-6);
            Assert.IsTrue(Used(result, 1, 1) > 0.0 && Used(result, 1, 0) > 0.0, "stage 1 runs out in leg 1, stage 0 takes over");
            Assert.AreEqual(stages.Sum(stage => stage.DeltaV), result.Uses.Sum(u => u.DeltaV), 1e-6,
                "splitting a burn over legs changes nothing about the total");
        }

        [TestMethod]
        public void TheMarginIsAddedToEveryLeg()
        {
            StageBudget.Result result = StageBudget.Allocate(new List<BudgetStage> { Stage(0, 10, 5) }, new[] { 3000.0 }, 0.1, null,
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
            StageBudget.Result result = StageBudget.Allocate(stages, new[] { 500.0, 100000.0, 600.0 }, 0.0,
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
            StageBudget.Result result = StageBudget.Allocate(stages, new[] { 400.0, 600.0 }, 0.0,
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
            StageBudget.Result result = StageBudget.Allocate(stages, new[] { 300.0, 300.0 }, 0.0,
                Plans(2, null, new[] { new[] { 1 } }), -1, null);

            Assert.IsTrue(result.Dropped.ContainsKey(2) && result.Dropped.ContainsKey(1));
            Assert.AreEqual(300.0, Used(result, 1, 0), 1e-6);
        }

        [TestMethod]
        public void TheLastStageCannotBeDropped()
        {
            List<BudgetStage> stages = new List<BudgetStage> { Stage(1, 20, 12), Stage(0, 8, 4) };
            StageBudget.Result result = StageBudget.Allocate(stages, new[] { 300.0, 300.0 }, 0.0,
                Plans(2, null, new[] { new[] { 0 } }), -1, null);

            Assert.IsFalse(result.Stages[0].Single(stage => stage.Stage == 0).CanDrop);
            Assert.IsFalse(result.Dropped.ContainsKey(0));
        }

        [TestMethod]
        public void AStepListsTheStagesThatStillHoldFuel()
        {
            List<BudgetStage> stages = new List<BudgetStage> { Stage(1, 20, 12), Stage(0, 8, 4) };
            StageBudget.Result result = StageBudget.Allocate(stages, new[] { stages[0].DeltaV + 100.0, 100.0 }, 0.0, null, -1, null);

            CollectionAssert.AreEqual(new[] { 1, 0 }, result.Stages[0].Select(stage => stage.Stage).ToArray());
            CollectionAssert.AreEqual(new[] { 0 }, result.Stages[1].Select(stage => stage.Stage).ToArray(), "stage 1 burnt out");
            Assert.AreEqual(stages[0].DeltaV, result.Stages[0][0].Given, 1e-6);
            Assert.AreEqual(0.0, result.Stages[0][0].LeftAfter, 1e-9);
        }

        [TestMethod]
        public void DroppingTheRestLeavesTheLanderToFlyAlone()
        {
            StageBudget.Result result = StageBudget.Allocate(Mothership(), new[] { 500.0, 500.0 }, 0.0,
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
            StageBudget.Result result = StageBudget.Allocate(Mothership(), new[] { 500.0, 600.0, 600.0, 500.0 }, 0.0, null, 0, modes);

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
            StageBudget.Result result = StageBudget.Allocate(Mothership(), new[] { 500.0, 600.0, 600.0, 500.0 }, 0.0, null, 0, modes);

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
            StageBudget.Result result = StageBudget.Allocate(Mothership(), new[] { 500.0, 500.0 }, 0.0, null, 0, modes);

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
            StageBudget.Result result = StageBudget.Allocate(stages, new[] { stages[0].DeltaV + 100.0 }, 0.0, null, -1, null);

            Assert.AreEqual(0.0, result.Left[2], 1e-9, "burnt out");
            Assert.AreEqual(stages[1].DeltaV, Used(result, 0, 1) + result.Left[1], 1e-6, "used and left make the stage");
            Assert.AreEqual(stages[2].DeltaV, result.Left[0], 1e-6, "untouched");
        }

        [TestMethod]
        public void WithALanderThatNeverFliesAloneTheStagesGiveKspsDeltaV()
        {
            StageBudget.Result result = StageBudget.Allocate(Mothership(), new[] { 100000.0 }, 0.0, null, 0,
                new[] { LegMode.Whole });

            Assert.AreEqual(Mothership().Sum(stage => stage.DeltaV), result.Uses.Sum(u => u.DeltaV), 1e-6);
        }

        [TestMethod]
        public void ASavedStageOfTheRestHoldsBackTheLanderToo()
        {
            List<BudgetStage> stages = new List<BudgetStage> { Stage(3, 16, 12), Stage(2, 10, 8), Stage(1, 5, 4), Stage(0, 3, 2) };
            StageBudget.Result result = StageBudget.Allocate(stages, new[] { 100000.0, 100.0, 100.0 }, 0.0,
                Plans(3, new[] { new[] { 2 }, new[] { 2 } }), 1, new[] { LegMode.Whole, LegMode.Whole, LegMode.Whole });

            Assert.AreEqual(0.0, Used(result, 0, 1) + Used(result, 0, 0), "the lander waits behind the saved stage");
            Assert.AreEqual(100.0, Used(result, 2, 2), 1e-6);
        }

        // A part hanging under `parent` in the part tree.
        private static PartFacts Part(int parent, int decoupleStage, double mass, string title = "part", bool command = false,
                                      bool engine = false, bool legs = false, bool parachute = false,
                                      Separation separator = Separation.None)
        {
            return new PartFacts
            {
                Parent = parent, DecoupleStage = decoupleStage, Mass = mass, DryMass = mass, Title = title, Command = command,
                Engine = engine, Legs = legs, Parachute = parachute, Separator = separator, Id = (uint)(title.GetHashCode() & 0xffff),
            };
        }

        // The Kerbal X's shape: a pod with a parachute on top, a decoupler under it,
        // the upper stage, another decoupler, the booster.
        private static List<PartFacts> KerbalX()
        {
            return new List<PartFacts>
            {
                Part(-1, -1, 0.8, "Mk1 Command Pod", command: true), Part(0, -1, 0.1, "Mk16 Parachute", parachute: true),
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
                Part(-1, -1, 2.0, "Mk1-3 Command Pod", command: true), Part(0, -1, 0.1, "Mk16 Parachute", parachute: true),
                Part(0, 0, 0.05, "TD-12", separator: Separation.Decoupler), Part(2, 0, 4.0, "FL-T400"),
                Part(3, 0, 1.0, "LV-909", engine: true), Part(4, 0, 0.1, "Clamp-O-Tron SM", separator: Separation.DockingPort),
                Part(5, 0, 0.1, "Clamp-O-Tron LM", separator: Separation.DockingPort),
                Part(6, 0, 1.0, "Mk2 Lander Can", command: true), Part(7, 0, 0.5, "Spark", engine: true),
                Part(8, 2, 0.05, "TD-06", separator: Separation.Decoupler), Part(9, 2, 2.0, "FL-T200"),
                Part(10, 2, 0.2, "LT-2 Landing Strut", legs: true), Part(10, 2, 0.5, "Terrier", engine: true),
                Part(12, 4, 0.05, "TD-25", separator: Separation.Decoupler), Part(13, 4, 6.0, "Rockomax X200-16"),
                Part(14, 4, 1.5, "Poodle", engine: true),
            };
        }

        [TestMethod]
        public void TheCapsuleIsThePodThatStaysWithItsParachutes()
        {
            List<PartGroupChoice> groups = VesselShape.Candidates(KerbalX());
            PartGroupChoice capsule = VesselShape.AutoCapsule(groups);

            Assert.IsNotNull(capsule);
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, capsule.Members.ToArray(), "the decoupler goes with the stage under it");
            StringAssert.StartsWith(capsule.Name, "Mk1 Command Pod, 2 parts");
            Assert.IsNull(VesselShape.AutoLander(groups, capsule), "no landing legs");
        }

        [TestMethod]
        public void APodWithoutParachutesIsNoCapsule()
        {
            List<PartFacts> parts = KerbalX();
            parts[1].Parachute = false;

            Assert.IsNull(VesselShape.AutoCapsule(VesselShape.Candidates(parts)));
        }

        [TestMethod]
        public void TheLanderIsWhatItsDockingPortSeparatesWithoutTheStagesDroppedBeforeIt()
        {
            List<PartGroupChoice> groups = VesselShape.Candidates(Apollo());
            PartGroupChoice capsule = VesselShape.AutoCapsule(groups);
            PartGroupChoice lander = VesselShape.AutoLander(groups, capsule);

            Assert.IsNotNull(lander);
            CollectionAssert.AreEquivalent(new[] { 6, 7, 8, 9, 10, 11, 12 }, lander.Members.ToArray(),
                "the lander under its own port, without the transfer stage");
            StringAssert.StartsWith(lander.Name, "Mk2 Lander Can");
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, capsule.Members.ToArray());
            Assert.IsFalse(VesselShape.Inside(capsule, lander), "the capsule belongs to the rest");
            Assert.IsTrue(VesselShape.Fits(lander, capsule));
        }

        [TestMethod]
        public void ALanderWithoutADockingPortIsTheRestOfTheStackAndNotFoundByItself()
        {
            // The legs on the Kerbal X's upper stage: it would only land as a whole.
            List<PartFacts> parts = KerbalX();
            parts.Add(Part(3, 1, 0.2, "LT-1 Landing Struts", legs: true));

            Assert.IsNull(VesselShape.AutoLander(VesselShape.Candidates(parts), null));
        }

        [TestMethod]
        public void AnApolloLanderFliesBetweenTheRestsStages()
        {
            // S3 the service module (rest), S2 and S1 the lander's descent and ascent,
            // S0 the command module (capsule, part of the rest), without engines.
            List<BudgetStage> stages = new List<BudgetStage>
            {
                Stage(3, 18, 12), Stage(2, 12, 10), Stage(1, 9, 8), new BudgetStage { Number = 0, StartMass = 2, EndMass = 2 },
            };
            VesselSplit split = new VesselSplit { HasLander = true, HasCapsule = true, CapsuleInLander = false };
            split.Mass[(int)PartGroup.Rest] = 10;
            split.Mass[(int)PartGroup.Lander] = 6;
            split.Mass[(int)PartGroup.Capsule] = 2;
            PartGroup[] owners = { PartGroup.Capsule, PartGroup.Lander, PartGroup.Lander, PartGroup.Rest };
            for (int number = 0; number < 4; number++)
            {
                split.Owner[number] = owners[number];
                split.Fuel[number] = VesselSplit.Unit(owners[number]);
                split.Drop[number] = VesselSplit.Unit(owners[number]);
            }
            LegMode[] modes = { LegMode.Whole, LegMode.Lander, LegMode.WithoutLander, LegMode.Capsule };
            StageBudget.Result result = StageBudget.Allocate(stages, new[] { 300.0, 1500.0, 300.0, 0.0 }, 0.0, null, split, modes,
                null, null);

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

            Assert.IsTrue(braked.Lines.Single().Name.StartsWith("Aerobraking"));
            Assert.IsTrue(braked.DeltaV < burnt.DeltaV, braked.DeltaV.ToString("0") + " < " + burnt.DeltaV.ToString("0"));
        }

        [TestMethod]
        public void RefuellingFillsTheLanderFromTheRestAfterItDocks()
        {
            LegMode[] modes = { LegMode.Whole, LegMode.Lander, LegMode.Lander, LegMode.Whole };
            double[] legs = { 500.0, 600.0, 600.0, 100.0 };
            StageBudget.Result plain = StageBudget.Allocate(Mothership(), legs, 0.0, null, 0, modes);
            StageBudget.Result refilled = StageBudget.Allocate(Mothership(), legs, 0.0, null, 0, modes,
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

        private static RouteStop Choose(RouteStop stop, StepRole role, Vehicle vehicle)
        {
            stop.By[(int)role] = (int)vehicle;
            return stop;
        }

        private static string Shape(IEnumerable<RouteStep> steps)
        {
            return string.Join(" ", steps.Select(step => step.Goal + step.Role.ToString().Substring(0, 1) + ":"
                                                         + step.Vehicle.ToString().Substring(0, 1)
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
            List<RouteStep> steps = RouteSteps.Build(Stock(), goals, false, false);

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
            RouteStop home = Choose(Surface(Kerbin, true), StepRole.Land, Vehicle.Lander);
            List<RouteStop> goals = new List<RouteStop> { Surface(Kerbin), Orbit(Kerbin, 2863334), home };
            List<RouteStep> steps = RouteSteps.Build(Stock(), goals, true, false);

            Assert.AreEqual("1L:S 2L:L<", Shape(steps));
            Assert.AreEqual(2863334.0, steps[0].To.Altitude, "the launch goes straight up to the destination's orbit");
            Assert.AreEqual(LegMode.Lander, steps[1].Mode, "the capsule alone; the satellite stays");
            Assert.IsTrue(steps.All(step => step.Problem == null));
        }

        [TestMethod]
        public void TheDunaLanderFliesHomeByItselfAndLeavesTheTransferStage()
        {
            // A Kerbin surface, B Duna orbit, C Duna surface with Chutes, D Kerbin surface
            // with Chutes; the Duna landing clicked to Lander, and the way across home
            // too, where the planner would have it dock.
            RouteStop landing = Choose(Surface(Duna, true), StepRole.Land, Vehicle.Lander);
            RouteStop home = Choose(Surface(Kerbin, true), StepRole.Transfer, Vehicle.Lander);
            List<RouteStop> goals = new List<RouteStop> { Surface(Kerbin), Orbit(Duna, 60000), landing, home };
            List<RouteStep> steps = RouteSteps.Build(Stock(), goals, true, false);

            Assert.AreEqual("1L:S 1T:S 2L:L< 3L:L 3T:L 3L:L", Shape(steps));
            Assert.AreEqual(60000.0, steps[3].To.Altitude, "up to where the transfer stage waits");
            Assert.IsTrue(steps.All(step => step.Problem == null));

            // Without the click, the lander docks with the transfer stage, and the
            // whole vessel flies home.
            home.By[(int)StepRole.Transfer] = -1;
            Assert.AreEqual("1L:S 1T:S 2L:L< 3L:L> 3T:S 3L:S", Shape(RouteSteps.Build(Stock(), goals, true, false)));
        }

        [TestMethod]
        public void FromAMunBaseALanderHopsOnMinmusAndComesBack()
        {
            // A Mun surface, B Minmus orbit, C Minmus surface, D Minmus surface, E Mun
            // surface; the first Minmus landing clicked to Lander.
            RouteStop first = Choose(Surface(Minmus), StepRole.Land, Vehicle.Lander);
            List<RouteStop> goals = new List<RouteStop> { Surface(Mun), Orbit(Minmus, 10000), first, Surface(Minmus), Surface(Mun) };
            List<RouteStep> steps = RouteSteps.Build(Stock(), goals, true, false);

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
            RouteStop laythe = Choose(Surface(JLaythe, true), StepRole.Land, Vehicle.Lander);
            RouteStop vall = Orbit(Vall, 15000);
            vall.Refuel[(int)StepRole.Launch] = true;
            RouteStop vallSurface = Choose(Surface(Vall), StepRole.Land, Vehicle.Lander);
            RouteStop home = Choose(Surface(JKerbin, true), StepRole.Land, Vehicle.Lander);
            List<RouteStop> goals = new List<RouteStop>
            {
                Surface(JKerbin), Orbit(JKerbin, 80000), jool, Orbit(JLaythe, 60000), laythe, vall, vallSurface,
                Orbit(JTylo, 17000), home,
            };
            List<RouteStep> steps = RouteSteps.Build(JoolSystem(), goals, true, false);

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
            RouteStop onward = Choose(Orbit(Kerbin, 80000), StepRole.Transfer, Vehicle.Rest);
            RouteStop later = Choose(Orbit(Minmus, 10000), StepRole.Transfer, Vehicle.Lander);
            List<RouteStop> goals = new List<RouteStop> { Orbit(Mun, 14000), onward, later };
            List<RouteStep> steps = RouteSteps.Build(Stock(), goals, true, false);

            Assert.AreEqual(LegMode.WithoutLander, steps[0].Mode);
            Assert.IsTrue(steps[0].Separates);
            Assert.AreEqual("The lander stayed at Mun orbit", steps[1].Problem);
            Assert.AreEqual(0.0, steps[1].Leg.DeltaV, "never flown");
        }

        [TestMethod]
        public void TheWholeVesselCannotGoOnWhereTheLanderIsNotWithTheRest()
        {
            // The lander flies alone from Kerbin's orbit to the Mun's, and the next
            // step is clicked to Ship: the rest waits back at Kerbin.
            RouteStop mun = Choose(Orbit(Mun, 14000), StepRole.Transfer, Vehicle.Lander);
            RouteStop minmus = Choose(Orbit(Minmus, 10000), StepRole.Transfer, Vehicle.Ship);
            List<RouteStep> steps = RouteSteps.Build(Stock(), new List<RouteStop> { Orbit(Kerbin, 80000), mun, minmus }, true, false);

            Assert.AreEqual("The lander is not where the rest waits, at Kerbin orbit", steps[1].Problem);
        }

        [TestMethod]
        public void ALandingOnParachutesFromAnOrbitGoesDownIntoTheAir()
        {
            Leg high = RoutePlanner.LegOf(Stock(), Orbit(Kerbin, 2863334), Surface(Kerbin, true), 0, false);
            Leg low = RoutePlanner.LegOf(Stock(), Orbit(Kerbin, 80000), Surface(Kerbin, true), 0, false);

            Assert.AreEqual("Deorbit into the air", high.Lines[0].Name);
            Near(450, high.DeltaV, 40, "from a synchronous orbit");
            Near(40, low.DeltaV, 25, "from a low orbit");
        }

        [TestMethod]
        public void ASatelliteLeftInItsOrbitNoLongerWeighsOnTheUpperStage()
        {
            // S3 the booster; S2 the upper stage with the satellite (1 t) and the
            // capsule on it; S1 separates the satellite, no delta-v; S0 the capsule.
            List<BudgetStage> stages = new List<BudgetStage>
            {
                Stage(3, 20, 12), Stage(2, 6, 3.5), new BudgetStage { Number = 1, StartMass = 2.5, EndMass = 2.5 },
                new BudgetStage { Number = 0, StartMass = 1.2, EndMass = 1.2 },
            };
            double[] legs = { stages[0].DeltaV + 600.0, 400.0 };
            StageBudget.Result kept = StageBudget.Allocate(stages, legs, 0.0, null, -1, null);
            StageBudget.Result left = StageBudget.Allocate(stages, legs, 0.0, null, -1, null, null, new[] { true, false });

            Assert.AreEqual(1.0, left.Payload[0], 1e-9, "what S1 separates");
            Assert.IsTrue(left.LeftBehind.Contains(1));
            Assert.IsTrue(left.Left[2] > kept.Left[2], "the upper stage goes further without it");
            Assert.AreEqual(left.Uses.First(u => u.Leg == 1).Mass, kept.Uses.First(u => u.Leg == 1).Mass - 1.0, 1e-9);
        }

        [TestMethod]
        public void TheRestLeftBehindByTheLanderHasItsOwnMassLeft()
        {
            // Mothership: S1 16 t to 10 t, the lander S0 4 t to 2 t; the lander flies off.
            LegMode[] modes = { LegMode.Whole, LegMode.Lander };
            StageBudget.Result result = StageBudget.Allocate(Mothership(), new[] { 500.0, 300.0 }, 0.0, null, 0, modes);

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
                Choose(Surface(JLaythe, true), StepRole.Land, Vehicle.Lander), laytheBack, Orbit(Vall, 15000),
                Choose(Surface(Vall), StepRole.Land, Vehicle.Lander), Orbit(Vall, 15000), Orbit(JTylo, 17000),
                Choose(Surface(JKerbin, true), StepRole.Land, Vehicle.Lander),
            };
            List<RouteStep> steps = RouteSteps.Build(JoolSystem(), goals, true, false);

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
            List<RouteStep> steps = RouteSteps.Build(Stock(), goals, false, false, true);

            Assert.AreEqual("1L:S 1T:S 1L:S 2L:S 2T:S 2L:C<", Shape(steps));
            Assert.AreEqual(LegMode.Capsule, steps[5].Mode);
            Assert.AreEqual(StopKind.Entry, steps[5].From.Kind, "it leaves the vessel in Kerbin's air");
        }

        [TestMethod]
        public void NothingButTheCapsuleFliesOnceItHasLeft()
        {
            RouteStop home = Choose(Surface(Kerbin, true), StepRole.Land, Vehicle.Capsule);
            RouteStop onward = Choose(Orbit(Kerbin, 80000), StepRole.Launch, Vehicle.Ship);
            List<RouteStep> steps = RouteSteps.Build(Stock(), new List<RouteStop> { Orbit(Kerbin, 80000), home, onward }, false, false, true);

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
            StageBudget.Result result = StageBudget.Allocate(stages, new[] { 500.0, 0.0 }, 0.0, null, 1, modes, null, null, 0);

            Assert.IsTrue(result.Dropped.ContainsKey(2) && result.Dropped.ContainsKey(1), "left with what they hold");
            Assert.AreEqual(0.0, result.TotalShort, 1e-9, "the capsule lands on its parachutes");
        }

        [TestMethod]
        public void FromAnOrbitTheVesselBringsTheCapsuleDownRatherThanTheCapsuleItself()
        {
            // The satellite's mission with a capsule marked: from 2,863 km the whole
            // vessel lands; the capsule would have to deorbit on its own.
            List<RouteStop> goals = new List<RouteStop> { Surface(Kerbin), Orbit(Kerbin, 2863334), Surface(Kerbin, true) };
            Assert.AreEqual("1L:S 2L:S", Shape(RouteSteps.Build(Stock(), goals, false, false, true)));
        }

        [TestMethod]
        public void TheCapsuleBurnsItsOwnStagesPushingOnlyItself()
        {
            // S1 the rest, 20 t to 12 t; S0 the capsule with a small engine, 2 t to 1.5 t.
            List<BudgetStage> stages = new List<BudgetStage> { Stage(1, 20, 12), Stage(0, 2, 1.5) };
            StageBudget.Result result = StageBudget.Allocate(stages, new[] { 500.0, 100.0 }, 0.0, null, -1,
                new[] { LegMode.Whole, LegMode.Capsule }, null, null, 0);

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
            VesselSplit split = new VesselSplit { HasLander = true };
            split.Mass[(int)PartGroup.Rest] = 13;
            split.Mass[(int)PartGroup.Lander] = 3;
            PartGroup[] owners = { PartGroup.Rest, PartGroup.Lander, PartGroup.Rest };
            for (int number = 0; number < 3; number++)
            {
                split.Owner[number] = owners[number];
                split.Fuel[number] = VesselSplit.Unit(owners[number]);
                split.Drop[number] = VesselSplit.Unit(owners[number]);
            }
            LegMode[] modes = { LegMode.Whole, LegMode.Lander, LegMode.WithoutLander };
            StageBudget.Result result = StageBudget.Allocate(stages, new[] { stages[0].DeltaV + 50.0, 100.0, 100.0 }, 0.0, null,
                split, modes, null, null);

            Assert.AreEqual(4.0, result.Uses.Single(u => u.Leg == 2).Mass, 1e-6, "the service module without the transfer stage");
        }

        [TestMethod]
        public void ASplitTransferIsTheBurnThatLeavesAndTheOneThatArrives()
        {
            RouteStop high = Orbit(Kerbin, 2863334);
            List<RouteStop> goals = new List<RouteStop> { Surface(Kerbin), Orbit(Kerbin, 80000), high };
            double together = RouteSteps.Build(Stock(), goals, false, false).Sum(step => step.Leg.DeltaV);
            high.SplitBurns = true;
            List<RouteStep> steps = RouteSteps.Build(Stock(), goals, false, false);

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
            RouteStop high = Choose(Orbit(Kerbin, 2863334), StepRole.Arrive, Vehicle.Capsule);
            high.SplitBurns = true;
            List<RouteStop> goals = new List<RouteStop> { Surface(Kerbin), Orbit(Kerbin, 80000), high };
            List<RouteStep> steps = RouteSteps.Build(Stock(), goals, false, false, true);

            Assert.AreEqual("1L:S 2T:S 2A:C<", Shape(steps));
            Assert.AreEqual(LegMode.Capsule, steps[2].Mode);
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
            RouteStop home = Choose(Surface(Kerbin, true), StepRole.Transfer, Vehicle.Rest);
            List<RouteStop> goals = new List<RouteStop>
            {
                Surface(Kerbin), Orbit(Kerbin, 80000), Orbit(Mun, 14000), Surface(Mun), Orbit(Mun, 14000), home,
            };
            List<RouteStep> steps = RouteSteps.Build(Stock(), goals, true, false, true, false);

            Assert.AreEqual("1L:S 2T:S 3L:L< 4L:L 5T:R 5L:C<", Shape(steps));
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
            RouteStop home = Choose(Surface(JKerbin, true), StepRole.Transfer, Vehicle.Rest);
            List<RouteStop> goals = new List<RouteStop>
            {
                Surface(JKerbin), Orbit(JKerbin, 80000), jool, Orbit(JLaythe, 60000), Surface(JLaythe, true), laytheBack,
                Orbit(Vall, 15000), Surface(Vall), Orbit(Vall, 15000), home,
            };
            List<RouteStep> steps = RouteSteps.Build(JoolSystem(), goals, true, false, true, false);

            Assert.AreEqual("1L:S 2T:S 3T:S 4L:L< 5L:L> 6T:S 7L:L< 8L:L 9T:R 9L:C<", Shape(steps));
            Assert.IsTrue(steps[4].RefuelAfter, "refilled as it docks after Laythe");
            Assert.IsTrue(steps.All(step => step.Problem == null));
        }

        [TestMethod]
        public void ALandingShortOfTheEndIsTheLandersByItself()
        {
            // Kerbin surface, Mun surface, Kerbin surface with Chutes; a lander marked,
            // nothing clicked on the steps: the lander lands on the Mun, climbs back,
            // docks, and the whole vessel comes home.
            List<RouteStop> goals = new List<RouteStop> { Surface(Kerbin), Surface(Mun), Surface(Kerbin, true) };
            Assert.AreEqual("1L:S 1T:S 1L:L< 2L:L> 2T:S 2L:S", Shape(RouteSteps.Build(Stock(), goals, true, false)));
        }
    }
}
