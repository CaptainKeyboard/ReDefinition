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

        [TestMethod]
        public void AKeptStageWaitsForItsLegAndBlocksTheStagesAfterIt()
        {
            // Stage 1 is kept for the landing (leg 2).
            List<BudgetStage> stages = new List<BudgetStage> { Stage(2, 20, 14), Stage(1, 10, 6), Stage(0, 4, 3) };
            StageBudget.Result result = StageBudget.Allocate(stages, new[] { 500.0, 100000.0, 600.0 }, 0.0,
                new Dictionary<int, int> { { 1, 2 } }, -1, null);

            Assert.AreEqual(0.0, Used(result, 1, 1), "leg 1 may not reach into the kept stage");
            Assert.AreEqual(0.0, Used(result, 1, 0), "nor into the stages after it");
            Assert.AreEqual(600.0, Used(result, 2, 1), 1e-6);
        }

        [TestMethod]
        public void WhatAStageHoldsWhenAKeptStageTakesOverIsDropped()
        {
            List<BudgetStage> stages = new List<BudgetStage> { Stage(1, 20, 12), Stage(0, 8, 4) };
            StageBudget.Result result = StageBudget.Allocate(stages, new[] { 400.0, 600.0 }, 0.0,
                new Dictionary<int, int> { { 0, 1 } }, -1, null);

            Assert.IsTrue(result.Dropped[1] > 0.0);
            Assert.AreEqual(600.0, Used(result, 1, 0), 1e-6);
            Assert.AreEqual(0.0, result.TotalShort);
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
        public void TheRestOfTheVesselHasToBeWhereTheLanderReturns()
        {
            List<RouteStop> stops = new List<RouteStop>
            {
                Orbit(Kerbin, 80000), Orbit(Mun, 14000), Surface(Mun), Orbit(Mun, 30000), Orbit(Kerbin, 80000),
            };
            LegMode[] modes = { LegMode.Whole, LegMode.Lander, LegMode.Lander, LegMode.Whole };

            int[] meetings = RoutePlanner.Meetings(stops, modes);

            CollectionAssert.AreEqual(new[] { -1, -1, -1, 1 }, meetings, "the rest waits at 14 km, the lander returns to 30 km");
            stops[3] = Orbit(Mun, 14000);
            CollectionAssert.AreEqual(new[] { -1, -1, -1, -1 }, RoutePlanner.Meetings(stops, modes));
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
        public void AKeptStageOfTheRestHoldsBackTheLanderToo()
        {
            List<BudgetStage> stages = new List<BudgetStage> { Stage(3, 16, 12), Stage(2, 10, 8), Stage(1, 5, 4), Stage(0, 3, 2) };
            StageBudget.Result result = StageBudget.Allocate(stages, new[] { 100000.0, 100.0, 100.0 }, 0.0,
                new Dictionary<int, int> { { 2, 2 } }, 1, new[] { LegMode.Whole, LegMode.Whole, LegMode.Whole });

            Assert.AreEqual(0.0, Used(result, 0, 1) + Used(result, 0, 0), "the lander waits behind the kept stage");
            Assert.AreEqual(100.0, Used(result, 2, 2), 1e-6);
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
    }
}
