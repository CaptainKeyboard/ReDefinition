using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Engineer;

namespace ReDefinition.Tests
{
    // The planner tests on the steps of a route (RouteSteps): five missions set up as the player sets them in the planner.
    public partial class PlannerTests
    {
        // ------------------------------------------------ the steps of a route
        //
        // Five missions, each set up as the player sets it in the planner: the
        // destinations, and the steps whose vehicle the player clicks (Choose).

        // The stop with the flyer the player clicked for one of its step roles.
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

        // The steps written short, one word each: the goal's number, the role's initial, the flyer's letter, and
        // < where the step separates, > where it docks.
        private static string Shape(IEnumerable<RouteStep> steps)
        {
            return string.Join(" ", steps.Select(step => step.Goal + step.Role.ToString().Substring(0, 1) + ":"
                                                         + Letter(step.Flyer)
                                                         + (step.Separates ? "<" : "") + (step.Docks ? ">" : "")));
        }

        // The bodies' indices in JoolSystem().
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
                    SemiMajorAxis = 68500000, Inclination = 0.025, LowOrbitAltitude = 17000,
                },
            };
        }

        // One vessel without a lander flies to Minmus and home, straight into Kerbin's air and down on its
        // parachutes.
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

        // A satellite stays in its orbit while the capsule, marked as the lander, comes home alone.
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

        // The Duna lander flies home by itself where clicked, leaving the transfer stage in orbit; without the
        // click it docks and the whole vessel flies home.
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

        // From a Mun base a lander hops on Minmus, docks again and the whole vessel comes back.
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

        // Around Jool the lander lands on Laythe and Vall, docks and is refilled after Laythe, and lands at home
        // with the capsule.
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

        // A lander left behind cannot fly a later step: the step is reported and not flown.
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

        // A step clicked to the whole vessel is reported where the lander is not with the rest.
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

        // A landing on parachutes from an orbit is a deorbit burn into the air: about 450 m/s from the synchronous
        // orbit, 40 from a low one.
        [TestMethod]
        public void ALandingOnParachutesFromAnOrbitGoesDownIntoTheAir()
        {
            Leg high = RoutePlanner.LegOf(Stock(), Orbit(Kerbin, 2863334), Surface(Kerbin, true), 0, false);
            Leg low = RoutePlanner.LegOf(Stock(), Orbit(Kerbin, 80000), Surface(Kerbin, true), 0, false);

            Assert.AreEqual("Deorbit into the atmosphere", high.Lines[0].Name);
            Near(450, high.DeltaV, 40, "from a synchronous orbit");
            Near(40, low.DeltaV, 25, "from a low orbit");
        }

        // The rest the lander left behind has the delta-v of its own mass and fuel left.
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

        // The Jool tour with every orbit between the landings listed as a destination gives the same steps.
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

        // With a capsule marked and nothing clicked, the capsule leaves the vessel in Kerbin's air and lands on
        // its parachutes.
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

        // Once the capsule has left, a later step clicked to the whole vessel is reported.
        [TestMethod]
        public void NothingButTheCapsuleFliesOnceItHasLeft()
        {
            RouteStop home = Choose(Surface(Kerbin, true), StepRole.Land, CapsuleFlies);
            RouteStop onward = Choose(Orbit(Kerbin, 80000), StepRole.Launch, Flyer.Ship);
            List<RouteStep> steps = Route(Stock(), new List<RouteStop> { Orbit(Kerbin, 80000), home, onward }, false, false, true);

            Assert.AreEqual("The capsule has left the vessel", steps[1].Problem);
        }

        // The capsule leaving everything else leaves the other stages with what they hold, landing on its
        // parachutes with no burn.
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

        // From a high orbit the whole vessel brings the capsule down rather than the capsule deorbiting on its own.
        [TestMethod]
        public void FromAnOrbitTheVesselBringsTheCapsuleDownRatherThanTheCapsuleItself()
        {
            // The satellite's mission with a capsule marked: from 2,863 km the whole
            // vessel lands; the capsule would have to deorbit on its own.
            List<RouteStop> goals = new List<RouteStop> { Surface(Kerbin), Orbit(Kerbin, 2863334), Surface(Kerbin, true) };
            Assert.AreEqual("1L:S 2L:S", Shape(Route(Stock(), goals, false, false, true)));
        }

        // The capsule burns its own stage pushing only itself, the rest left with what it holds.
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

        // The stages of the rest dropped before the lander are gone for the rest when it flies on without the
        // lander.
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

        // A split transfer is two steps, the burn that leaves and the one that arrives, with the same delta-v as
        // the transfer in one.
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

        // A satellite marked as the capsule flies the arrival of a split transfer alone, leaving the upper stage
        // on the way up.
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

        // A way straight into a planet's air cannot be split into two burns; one between orbits can.
        [TestMethod]
        public void AWayStraightIntoTheAirCannotBeSplit()
        {
            Assert.IsFalse(RouteSteps.Splits(Stock(), Orbit(Mun, 14000), Surface(Kerbin, true)));
            Assert.IsTrue(RouteSteps.Splits(Stock(), Orbit(Kerbin, 80000), Orbit(Mun, 14000)));
        }

        // Apollo: the lander docks after the Mun, the rest leaves it for home, and the capsule leaves the rest in
        // Kerbin's air.
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

        // The Jool tour with a capsule in the mothership: the lander used twice, refilled after Laythe, left
        // before home, and the capsule landing at home by itself.
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

        // A satellite is carried to its destination, deployed there and not carried on with the rest.
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

        // A satellite set to stay where only the lander goes, while the rest carrying it waits in orbit, is
        // reported as not reaching its destination.
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

        // A satellite that flies its own last step, the arrival of a split transfer, stays there and is not
        // carried home.
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

        // A lander carried by a satellite leaves it where the satellite stays and lands from there.
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

        // Two landers each land where the player picks them, the first waiting with the rest while the second flies.
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

        // After a lander's climb it docks where the whole vessel flies on, climbing to the rest; where it flies on
        // itself it climbs to its own low orbit.
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

        // Two landers sent down one after the other both climb back to the rest and dock, and both are carried home.
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

        // With a lander marked and nothing clicked, the Mun landing short of the end is the lander's, which docks
        // again before the whole vessel comes home.
        [TestMethod]
        public void ALandingShortOfTheEndIsTheLandersByItself()
        {
            // Kerbin surface, Mun surface, Kerbin surface with Chutes; a lander marked,
            // nothing clicked on the steps: the lander lands on the Mun, climbs back,
            // docks, and the whole vessel comes home.
            List<RouteStop> goals = new List<RouteStop> { Surface(Kerbin), Surface(Mun), Surface(Kerbin, true) };
            Assert.AreEqual("1L:S 1T:S 1L:L< 2L:L> 2T:S 2L:S", Shape(Route(Stock(), goals, true, false)));
        }

        // The planner finds the lander by itself: Apollo's under its port, able to dock, and the Kerbal X's upper
        // stage with legs, which cannot.
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

        // No lander without landing legs, nor on a radial booster that lands itself, nor with the launch stage's
        // engine.
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

        // A probe lander under a relay is found as the lander, the relay's core staying as the satellite.
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
    }
}
