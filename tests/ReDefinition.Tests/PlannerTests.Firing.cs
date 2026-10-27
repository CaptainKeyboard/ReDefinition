using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Engineer;

namespace ReDefinition.Tests
{
    // The planner tests on firing: the burns of a route with their orbits and durations, and the pad's rows with the
    // clamps (ClampedLaunch).
    public partial class PlannerTests
    {
        // The parts with the engine at `launch` firing first, the others after it.
        private static List<PartFacts> Firing(List<PartFacts> parts, int launch)
        {
            for (int i = 0; i < parts.Count; i++)
                if (parts[i].Engine) parts[i].ActivatesAt = i == launch ? 4 : 1;
            return parts;
        }

        // The burns of a way to the Mun know the orbit they burn in: the leaving burn at low Kerbin orbit, the
        // capture at low Mun orbit.
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

        // A burn takes as long as its propellant flows at the stage's thrust; a plane change on the way is no burn
        // in orbit, and a burn the propellant does not last for is short.
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

        // A long burn loses what points away from the burn's direction over its arc. That is nothing for none, 1 - 2/pi
        // over half an orbit, and all past a whole one. Up to a tenth of an orbit stays under the warning.
        [TestMethod]
        public void ALongBurnLosesWhatPointsAwayOverItsArc()
        {
            Assert.AreEqual(0.0, new Burn { Seconds = 0, Period = 1800 }.Loss);
            Near(1.0 - 2.0 / System.Math.PI, new Burn { Seconds = 900, Period = 1800 }.Loss, 1e-9, "half an orbit");
            Assert.AreEqual(1.0, new Burn { Seconds = 2000, Period = 1800 }.Loss, "more than an orbit");
            Assert.IsTrue(new Burn { Seconds = 90, Period = 1800 }.Loss < BurnCheck.WarnLoss, "a twentieth of the orbit loses little");
            Assert.IsTrue(new Burn { Seconds = 180, Period = 1800 }.Loss < BurnCheck.WarnLoss, "a tenth still under the warning");
        }
        // A lander set to dock with the rest but without a docking port is reported.
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
        // The stock Learstar A1 as KSP's delta-v simulation gives it (Ships/VAB/Learstar
        // A1.craft). S4 lights the Rhino core and two Thuds on the pad (2,240 kN, Isp
        // 336 s, 180 t of fuel). S3 fires four Kickbacks (2,680 kN, Isp 220 s, 75 t of
        // solid fuel) and releases the clamps. S2 drops the boosters (18.3 t).
        private static List<StageRow> PadRows()
        {
            return new List<StageRow>
            {
                new StageRow { Number = 4, StartMass = 321.9, EndMass = 141.9, DeltaVVacuum = 336 * 9.80665 * System.Math.Log(321.9 / 141.9), ThrustVacuum = 2240, Thrust = 2240 },
                new StageRow { Number = 3, StartMass = 141.9, EndMass = 66.9, DeltaVVacuum = 220 * 9.80665 * System.Math.Log(141.9 / 66.9), ThrustVacuum = 2680, Thrust = 2680 },
                new StageRow { Number = 2, StartMass = 48.6, EndMass = 48.6 },
                new StageRow { Number = 0, StartMass = 14.28, EndMass = 12.0, DeltaVVacuum = 570, ThrustVacuum = 158.4 },
            };
        }

        // The Learstar's pad as parts: the core engine lit at S4, a booster fired at S3, and a launch clamp
        // released at S3.
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

        // The engines lit on the pad burn together with the boosters whose stage releases the clamps: one lift-off
        // row at both thrusts until the boosters are empty, then the core alone, labelled by the stages they join.
        [TestMethod]
        public void EnginesLitOnThePadBurnWithTheBoostersThatReleaseTheClamps()
        {
            Dictionary<int, string> labels = new Dictionary<int, string>();
            List<StageRow> rows = ClampedLaunch.Parallel(PadRows(), PadParts(), labels, 9.81);

            Assert.AreEqual(4, rows[0].Number, "the lift-off");
            Near(321.9, rows[0].StartMass, 0.01, "lifts off full");
            Near(4920, rows[0].ThrustVacuum, 0.1, "at the core's and the boosters' thrust");
            Near(60.4, rows[0].BurnTime, 0.1, "until the boosters are empty");
            Near(205.85, rows[0].EndMass, 0.1, "both burning");
            Assert.AreEqual(3, rows[1].Number, "the core burns on");
            Near(187.55, rows[1].StartMass, 0.1, "after the boosters are dropped");
            Near(48.6, rows[1].EndMass, 0.01, "down to the mass KSP has after both");
            Near(2240, rows[1].ThrustVacuum, 0.1, "the core alone");
            Assert.AreEqual("S4+3", labels[4]);
            Assert.AreEqual("S4", labels[3]);
            Assert.AreEqual(2, rows[2].Number);
            Assert.AreEqual(0, rows[3].Number);
            Assert.IsTrue(rows[0].ThrustVacuum / (rows[0].StartMass * 9.81) > 1.5, "the vessel lifts off");
        }

        // A stage on the pad between the core's and the boosters' that burns nothing drops out of the rows.
        [TestMethod]
        public void AStageOnThePadThatBurnsNothingDropsNothing()
        {
            // S5 lights the core, S4 does nothing, S3 fires the boosters.
            List<StageRow> rows = PadRows();
            rows[0].Number = 5;
            rows.Insert(1, new StageRow { Number = 4, StartMass = 141.9, EndMass = 141.9 });
            List<PartFacts> parts = PadParts();
            parts[0].ActivatesAt = 5;
            List<StageRow> result = ClampedLaunch.Parallel(rows, parts, new Dictionary<int, string>(), 9.81);

            CollectionAssert.AreEqual(new[] { 5, 3, 2, 0 }, result.Select(row => row.Number).ToArray());
        }

        // Where something is dropped between the core's stage and the clamps' release it is no lift-off, and the
        // stages stay as KSP gives them.
        [TestMethod]
        public void ClampsStagedAfterADropLeaveTheStagesAlone()
        {
            // Something dropped between the core and the release: not a lift-off.
            List<StageRow> rows = PadRows();
            rows[1].StartMass = 120.0;
            rows[1].DeltaVVacuum = 220 * 9.80665 * System.Math.Log(120.0 / 85.5);

            Assert.AreSame(rows, ClampedLaunch.Parallel(rows, PadParts(), new Dictionary<int, string>(), 9.81));
        }

        // Without clamps a core too weak to lift off alone burns with the next stage, as the player would fire it
        // at once.
        [TestMethod]
        public void WithoutClampsACoreTooWeakToLiftOffBurnsWithTheNextStage()
        {
            // The core alone: TWR 0.71 in vacuum. The player fires the boosters at once.
            List<PartFacts> parts = PadParts();
            parts[2].Clamp = false;
            List<StageRow> rows = ClampedLaunch.Parallel(PadRows(), parts, new Dictionary<int, string>(), 9.81);

            Near(4920, rows[0].ThrustVacuum, 0.1, "lifts off with both");
        }

        // Without clamps a core strong enough to lift off alone burns alone: the stages stay as KSP gives them.
        [TestMethod]
        public void WithoutClampsACoreThatLiftsOffBurnsAlone()
        {
            List<PartFacts> parts = PadParts();
            parts[2].Clamp = false;
            List<StageRow> rows = PadRows();
            rows[0].ThrustVacuum = 4000;

            Assert.AreSame(rows, ClampedLaunch.Parallel(rows, parts, new Dictionary<int, string>(), 9.81));
        }

        // A stage's drop is what goes as the next stage fires: a booster decoupled at
        // stage 1 that burnt dry in stage 2 drops with stage 2, at its dry mass.
        [TestMethod]
        public void SplitDropsWithTheStageBeforeTheDecoupling()
        {
            PartFacts pod = Part(-1, -1, 1.0, "pod", command: true);
            PartFacts booster = Part(0, 1, 5.0, "booster");
            booster.DryMass = 1.0;
            booster.StageFuel[2] = new[] { 4.0, 0.0 };
            List<PartFacts> parts = new List<PartFacts> { pod, booster };
            PartGroupChoice capsule = new PartGroupChoice { Name = "Capsule" };
            capsule.Members.Add(0);
            List<BudgetStage> stages = new List<BudgetStage>
            {
                new BudgetStage { Number = 2, StartMass = 6.0, EndMass = 2.0 },
                new BudgetStage { Number = 1, StartMass = 1.0, EndMass = 1.0 },
            };
            VesselSplit split = VesselShape.Split(stages, parts, new List<PartGroupChoice> { capsule });
            Assert.AreEqual(1.0, split.Drop[2][0], 1e-9);
            Assert.AreEqual(0.0, split.Drop[2][1], 1e-9);
            Assert.AreEqual(0.0, split.Drop[1].Sum(), 1e-9);
        }

        // KSP's 6.27 t hold three kerbals' 0.27 t over the parts' 6 t: in the pod they
        // sit in, which comes to 1.27 t; the booster keeps its 5 t. Scaled over both,
        // the pod came to 1.045 t and the booster to 5.225 t.
        [TestMethod]
        public void TheCrewsMassStaysInTheirPod()
        {
            PartFacts pod = Part(-1, -1, 1.0, "pod", command: true, crew: 3);
            pod.Aboard = 3;
            PartFacts booster = Part(0, 1, 5.0, "booster");
            List<PartFacts> parts = new List<PartFacts> { pod, booster };
            PartGroupChoice capsule = new PartGroupChoice { Name = "Capsule" };
            capsule.Members.Add(0);
            List<BudgetStage> stages = new List<BudgetStage> { new BudgetStage { Number = 1, StartMass = 6.27, EndMass = 6.27 } };
            VesselSplit split = VesselShape.Split(stages, parts, new List<PartGroupChoice> { capsule });
            Assert.AreEqual(1.27, split.Mass[1], 1e-9);
            Assert.AreEqual(5.0, split.Mass[0], 1e-9);

            // Without anyone aboard, the scale as before.
            pod.Aboard = 0;
            split = VesselShape.Split(stages, parts, new List<PartGroupChoice> { capsule });
            Assert.AreEqual(1.045, split.Mass[1], 1e-9);
        }
    }
}
