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

        // Stage and leg pairs: the stage pinned to the leg.
        private static Dictionary<int, HashSet<int>> Pins(params int[] stageThenLeg)
        {
            Dictionary<int, HashSet<int>> pins = new Dictionary<int, HashSet<int>>();
            for (int i = 0; i + 1 < stageThenLeg.Length; i += 2)
            {
                if (!pins.ContainsKey(stageThenLeg[i])) pins[stageThenLeg[i]] = new HashSet<int>();
                pins[stageThenLeg[i]].Add(stageThenLeg[i + 1]);
            }
            return pins;
        }

        [TestMethod]
        public void AStagePinnedToALaterLegStopsTheLegsBeforeIt()
        {
            // Stage 1 is pinned to the landing (leg 2).
            List<BudgetStage> stages = new List<BudgetStage> { Stage(2, 20, 14), Stage(1, 10, 6), Stage(0, 4, 3) };
            StageBudget.Result result = StageBudget.Allocate(stages, new[] { 500.0, 100000.0, 600.0 }, 0.0, Pins(1, 2), -1, null);

            Assert.AreEqual(0.0, Used(result, 1, 1), "leg 1 may not reach into the pinned stage");
            Assert.AreEqual(0.0, Used(result, 1, 0), "nor into the stages after it");
            Assert.AreEqual(600.0, Used(result, 2, 1), 1e-6);
        }

        [TestMethod]
        public void WhatTheStagesBeforeAPinnedOneHoldIsDropped()
        {
            List<BudgetStage> stages = new List<BudgetStage> { Stage(1, 20, 12), Stage(0, 8, 4) };
            StageBudget.Result result = StageBudget.Allocate(stages, new[] { 400.0, 600.0 }, 0.0, Pins(0, 1), -1, null);

            Assert.IsTrue(result.Dropped[1] > 0.0);
            Assert.AreEqual(600.0, Used(result, 1, 0), 1e-6);
            Assert.AreEqual(0.0, result.TotalShort);
        }

        [TestMethod]
        public void ALegWithPinsBurnsOnlyItsPinnedStages()
        {
            // Leg 0 pinned to stage 2 alone, which cannot pay all of it.
            List<BudgetStage> stages = new List<BudgetStage> { Stage(2, 20, 16), Stage(1, 10, 6), Stage(0, 4, 3) };
            StageBudget.Result result = StageBudget.Allocate(stages, new[] { 5000.0 }, 0.0, Pins(2, 0), -1, null);

            Assert.AreEqual(stages[0].DeltaV, Used(result, 0, 2), 1e-6);
            Assert.AreEqual(0.0, Used(result, 0, 1) + Used(result, 0, 0), "no stage it is not pinned to");
            Assert.AreEqual(5000.0 - stages[0].DeltaV, result.Short[0], 1e-6);
        }

        [TestMethod]
        public void OneStagePinnedToTheLandingAndTheClimbServesBoth()
        {
            List<BudgetStage> stages = new List<BudgetStage> { Stage(1, 20, 12), Stage(0, 8, 4) };
            StageBudget.Result result = StageBudget.Allocate(stages, new[] { 500.0, 600.0, 600.0 }, 0.0, Pins(0, 1, 0, 2), -1,
                null);

            Assert.AreEqual(600.0, Used(result, 1, 0), 1e-6);
            Assert.AreEqual(600.0, Used(result, 2, 0), 1e-6);
            Assert.AreEqual(0.0, result.TotalShort);
        }

        [TestMethod]
        public void ALegPaidForByItsFirstPinLeavesTheNextPinnedStageUntouched()
        {
            List<BudgetStage> stages = new List<BudgetStage> { Stage(2, 20, 12), Stage(1, 8, 6), Stage(0, 4, 3) };
            StageBudget.Result result = StageBudget.Allocate(stages, new[] { 500.0, 100000.0 }, 0.0, Pins(2, 0, 1, 0), -1, null);

            Assert.AreEqual(500.0, Used(result, 0, 2), 1e-6);
            Assert.IsFalse(result.Dropped.ContainsKey(2), "stage 2 keeps its fuel for the next leg");
            Assert.IsTrue(Used(result, 1, 2) > 0.0, "and burns it there");
        }

        [TestMethod]
        public void APinnedLanderStageOnALegOfTheWholeVesselStagesTheRestAwayFirst()
        {
            StageBudget.Result result = StageBudget.Allocate(Mothership(), new[] { 500.0 }, 0.0, Pins(0, 0), 0,
                new[] { LegMode.Whole });

            Assert.IsTrue(result.Dropped.ContainsKey(1), "the rest goes with what it holds");
            Assert.AreEqual(4.0, result.Uses.Single(u => u.Leg == 0).Mass, 1e-9, "the lander pushes only itself");
        }

        [TestMethod]
        public void APinToAStageAlreadyStagedAwayIsUnreachable()
        {
            // Leg 0 is pinned to stage 0, which drops stage 1; leg 1 is pinned to
            // stage 1, gone by then.
            List<BudgetStage> stages = new List<BudgetStage> { Stage(1, 20, 12), Stage(0, 8, 4) };
            StageBudget.Result result = StageBudget.Allocate(stages, new[] { 300.0, 300.0 }, 0.0, Pins(0, 0, 1, 1), -1, null);

            Assert.IsTrue(result.Unreachable.Any(pin => pin.Key == 1 && pin.Value == 1));
            Assert.AreEqual(300.0, result.Short[1], 1e-6);
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
        public void APinnedStageOfTheRestHoldsBackTheLanderToo()
        {
            List<BudgetStage> stages = new List<BudgetStage> { Stage(3, 16, 12), Stage(2, 10, 8), Stage(1, 5, 4), Stage(0, 3, 2) };
            StageBudget.Result result = StageBudget.Allocate(stages, new[] { 100000.0, 100.0, 100.0 }, 0.0,
                Pins(2, 2), 1, new[] { LegMode.Whole, LegMode.Whole, LegMode.Whole });

            Assert.AreEqual(0.0, Used(result, 0, 1) + Used(result, 0, 0), "the lander waits behind the pinned stage");
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
    }
}
