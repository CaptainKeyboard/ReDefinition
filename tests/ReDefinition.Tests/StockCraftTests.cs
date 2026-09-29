using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Engineer;

namespace ReDefinition.Tests
{
    // A stock craft as KSP's delta-v simulation gives it, worked out from its file by
    // tools/craft_stages.py (StockCrafts.cs).
    internal sealed class StockCraft
    {
        public string Name;
        public List<StageRow> Rows;
        public List<PartFacts> Parts;
        // As the planner's own simulation takes them (StageSim).
        public List<SimPart> Sim;
    }

    internal static partial class StockCrafts
    {
        private static StageRow Row(int number, double start, double end, double deltaV, double thrust, double seaLevel, bool solid)
        {
            StageRow row = new StageRow
            {
                Number = number, StartMass = start, EndMass = end, DeltaV = deltaV, DeltaVVacuum = deltaV, ThrustVacuum = thrust,
                Thrust = seaLevel, Solid = solid,
            };
            row.ThrustAtSurface["Kerbin"] = seaLevel;
            return row;
        }

        private static PartFacts Facts(int parent, int decouple, int fires, double mass, double dry, string title, bool command,
                                       bool engine, bool legs, bool parachute, bool heatShield, bool lift, bool wheels, bool clamp, int crew,
                                       Separation separator, Dictionary<int, double[]> fuel)
        {
            PartFacts facts = new PartFacts
            {
                Parent = parent, DecoupleStage = decouple, ActivatesAt = fires, Mass = mass, DryMass = dry, Title = title,
                Command = command, Engine = engine, Legs = legs, Parachute = parachute, HeatShield = heatShield, Lift = lift, Wheels = wheels,
                Clamp = clamp, Crew = crew, Separator = separator,
            };
            foreach (KeyValuePair<int, double[]> stage in fuel) facts.StageFuel[stage.Key] = stage.Value;
            return facts;
        }

        private static SimPart SimPartOf(int parent, int decouple, int fires, bool clamp, bool crossfeed, double dry,
                                         Dictionary<string, double> amount, int[] fuelLineSources, SimEngine rocket, SimEngine air)
        {
            SimPart part = new SimPart
            {
                Parent = parent, DecoupleStage = decouple, ActivatesAt = fires, Clamp = clamp, Crossfeed = crossfeed, Fixed = dry,
                Rocket = rocket, Air = air,
            };
            foreach (KeyValuePair<string, double> held in amount) part.Amount[held.Key] = held.Value;
            part.FuelLineSources.AddRange(fuelLineSources);
            return part;
        }

        private static SimEngine Rocket(double thrust, double isp, double seaLevel, bool solid, params SimPropellant[] propellants)
        {
            SimEngine engine = new SimEngine { Thrust = thrust, Isp = isp, Solid = solid };
            engine.ThrustAtSurface["Kerbin"] = seaLevel;
            engine.Propellants.AddRange(propellants);
            return engine;
        }

        // Standing at Kerbin's sea level.
        private static SimEngine AirEngine(double thrust, double isp, double mach, params SimPropellant[] propellants)
        {
            SimEngine engine = new SimEngine { Thrust = thrust, Isp = isp, Mach = mach };
            engine.ThrustAtSurface["Kerbin"] = thrust;
            engine.Propellants.AddRange(propellants);
            return engine;
        }

        private static SimPropellant Prop(string name, double ratio, SimFlow flow)
        {
            return new SimPropellant { Name = name, Ratio = ratio, Flow = flow };
        }

        // The parts with their children, as the game's part tree has them.
        internal static List<SimPart> Linked(List<SimPart> parts)
        {
            foreach (SimPart part in parts) part.Children.Clear();
            for (int i = 0; i < parts.Count; i++)
                if (parts[i].Parent >= 0) parts[parts[i].Parent].Children.Add(i);
            return parts;
        }

        internal static StockCraft Named(string name)
        {
            return All.Single(craft => craft.Name == name);
        }
    }

    // The stock crafts flown through the planner's model as the planner does in the
    // editor (PlannerView.Evaluate): the stages lit on the pad together, the sections
    // it finds by itself, the route, the stage budget with a 10 % margin, and the
    // lift-off TWR; against what each craft's description promises.
    [TestClass]
    public class StockCraftTests
    {
        public TestContext TestContext { get; set; }

        private const int Kerbin = 1, Mun = 2, Minmus = 3;

        private sealed class Flight
        {
            public string Craft;
            public List<RouteStep> Steps;
            public StageBudget.Result Budget;
            public double LiftOff;
            // Air-breathing engines and wings: it takes off along the runway.
            public bool Runway;
            public double Short;
            public string Report;
        }

        private static Flight Fly(StockCraft craft, List<RouteStop> goals, double margin = 0.10)
        {
            List<PlannerBody> bodies = PlannerTests.Stock();
            PlannerBody home = bodies[Kerbin];
            double gravity = home.Mu / (home.Radius * home.Radius);
            Dictionary<int, string> labels = new Dictionary<int, string>();
            // As PlannerView.Evaluate: the planner's own stages where an engine breathes
            // air, then the stages lit on the pad together.
            int airRow;
            List<StageRow> rows = AirLaunch.Plan(craft.Rows, craft.Parts, StockCrafts.Linked(craft.Sim), StockCrafts.Density,
                goals[0].Kind == StopKind.Surface ? home : null, "Kerbin", out airRow);
            if (airRow >= 0) labels[airRow] = "Air";
            rows = rows.OrderByDescending(row => row.Number).ToList();
            rows = ClampedLaunch.Parallel(rows, craft.Parts, labels, gravity, "Kerbin");

            // The sections the planner finds by itself: capsules first, then the others.
            List<PartFacts> parts = craft.Parts;
            List<PartGroupChoice> groups = VesselShape.Candidates(parts);
            bool crewed = VesselShape.Crewed(parts);
            PartGroupChoice capsule = crewed ? VesselShape.AutoCapsule(groups) : null;
            PartGroupChoice satellite = crewed ? null : VesselShape.AutoSatellite(parts, groups);
            PartGroupChoice lander = VesselShape.AutoLander(parts, groups, capsule, satellite);
            List<KeyValuePair<SectionKind, PartGroupChoice>> found = new List<KeyValuePair<SectionKind, PartGroupChoice>>();
            if (capsule != null) found.Add(new KeyValuePair<SectionKind, PartGroupChoice>(SectionKind.Capsule, capsule));
            if (lander != null && VesselShape.Fits(lander, capsule))
                found.Add(new KeyValuePair<SectionKind, PartGroupChoice>(SectionKind.Lander, lander));
            if (satellite != null) found.Add(new KeyValuePair<SectionKind, PartGroupChoice>(SectionKind.Satellite, satellite));
            List<PartGroupChoice> chosen = found.Select(item => item.Value).ToList();
            List<RouteSection> sections = new List<RouteSection>();
            for (int i = 0; i < found.Count; i++)
            {
                int parent = VesselShape.Parent(chosen, i);
                bool canDock = VesselShape.CanDock(parts, found[i].Value);
                sections.Add(new RouteSection
                {
                    Id = i + 1, Kind = found[i].Key, Name = found[i].Key.ToString(), Parent = parent >= 0 ? parent + 1 : 0,
                    Docks = found[i].Key == SectionKind.Lander && canDock, CanDock = canDock,
                    DeployAt = found[i].Key == SectionKind.Satellite ? FirstOrbit(goals) : -1,
                });
            }

            List<RouteStep> steps = RouteSteps.Build(bodies, goals, sections, false);
            List<BudgetStep> budgetSteps = new List<BudgetStep>();
            foreach (RouteStep step in steps)
            {
                BudgetStep budgetStep = new BudgetStep { Need = step.Leg.DeltaV, Side = new bool[sections.Count + 1] };
                foreach (int id in step.Flying) budgetStep.Side[id] = true;
                foreach (GoneSection gone in step.Gone)
                    budgetStep.Gone.Add(new GoneGroup { Group = gone.Section, Stays = gone.Stays, Waiting = gone.Waiting });
                foreach (int id in step.Deploys) budgetStep.Deploy.Add(id);
                budgetSteps.Add(budgetStep);
            }
            List<BudgetStage> available = rows.Select(row => new BudgetStage
            {
                Number = row.Number, StartMass = row.StartMass, EndMass = row.EndMass, DeltaV = row.DeltaVVacuum, Solid = row.Solid,
                FirstStepOnly = row.Air,
            }).ToList();
            List<StagePlan> plans = steps.Select(step => new StagePlan()).ToList();
            StageBudget.Result budget = StageBudget.Allocate(available, budgetSteps, margin, plans, VesselShape.Split(available, parts, chosen));

            Flight flight = new Flight { Craft = craft.Name, Steps = steps, Budget = budget };
            StageBudget.StageUse first = budget.Uses.FirstOrDefault(use => use.Leg == 0);
            StageRow lifting = first != null ? rows.Find(row => row.Number == first.Stage) : null;
            flight.LiftOff = lifting != null ? lifting.ThrustAtSurface["Kerbin"] / (first.Mass * gravity) : 0.0;
            flight.Runway = lifting != null && lifting.Air && VesselShape.Runway(parts);
            flight.Short = budget.Short.Sum();

            StringBuilder text = new StringBuilder();
            text.AppendFormat(CultureInfo.InvariantCulture, "{0}: lift-off TWR {1:0.00}{2}, sections {3}", craft.Name, flight.LiftOff,
                flight.Runway ? " (runway)" : "", sections.Count == 0 ? "none" : string.Join(", ", sections.Select(section => section.Name)));
            foreach (StageRow row in rows.Where(row => row.DeltaVVacuum > 0.5))
                text.AppendFormat(CultureInfo.InvariantCulture, "\n  {0}: {1:0.00} t -> {2:0.00} t, {3:0} m/s, {4:0} kN{5}",
                    labels.ContainsKey(row.Number) ? labels[row.Number] : "S" + row.Number, row.StartMass, row.EndMass, row.DeltaVVacuum,
                    row.ThrustVacuum, row.Solid ? ", solid" : "");
            for (int i = 0; i < steps.Count; i++)
            {
                string by = string.Join("+", budget.Uses.Where(use => use.Leg == i)
                    .Select(use => (labels.ContainsKey(use.Stage) ? labels[use.Stage] : "S" + use.Stage)).ToArray());
                text.AppendFormat(CultureInfo.InvariantCulture, "\n  {0} to {1} {2}: needs {3:0} m/s, flown by {4} [{5}]{6}",
                    step(i, steps), bodies[steps[i].To.Body].Name, steps[i].To.Kind, budget.Needed[i], steps[i].Flyer.Kind, by,
                    budget.Short[i] > 0.5 ? string.Format(CultureInfo.InvariantCulture, ", SHORT {0:0} m/s", budget.Short[i]) : "");
            }
            text.AppendFormat(CultureInfo.InvariantCulture, "\n  left {0:0} m/s, short {1:0} m/s", budget.TotalLeft, flight.Short);
            flight.Report = text.ToString();
            return flight;
        }

        private static string step(int index, List<RouteStep> steps)
        {
            return (index + 1).ToString(CultureInfo.InvariantCulture) + ".";
        }

        // Where a satellite stays by itself: the first orbit before a landing or the end.
        private static int FirstOrbit(List<RouteStop> goals)
        {
            for (int i = 1; i < goals.Count; i++)
                if (goals[i].Kind == StopKind.Orbit && (i == goals.Count - 1 || goals[i + 1].Kind == StopKind.Surface)) return i;
            return -1;
        }

        private static List<RouteStop> ToOrbit()
        {
            return new List<RouteStop> { PlannerTests.Surface(Kerbin), PlannerTests.Orbit(Kerbin, 80000) };
        }

        private static List<RouteStop> ToMunOrbit()
        {
            return new List<RouteStop> { PlannerTests.Surface(Kerbin), PlannerTests.Orbit(Kerbin, 80000), PlannerTests.Orbit(Mun, 14000) };
        }

        private static List<RouteStop> MunAndBack()
        {
            return new List<RouteStop>
            {
                PlannerTests.Surface(Kerbin), PlannerTests.Orbit(Kerbin, 80000), PlannerTests.Surface(Mun), PlannerTests.Surface(Kerbin, true),
            };
        }

        private static List<RouteStop> MinmusAndBack()
        {
            return new List<RouteStop>
            {
                PlannerTests.Surface(Kerbin), PlannerTests.Orbit(Kerbin, 80000), PlannerTests.Surface(Minmus), PlannerTests.Surface(Kerbin, true),
            };
        }

        private Flight Log(Flight flight)
        {
            TestContext.WriteLine(flight.Report);
            return flight;
        }

        [TestMethod]
        public void TheOrbitalCraftsReachALowKerbinOrbit()
        {
            string[] orbital =
            {
                "Kerbal X", "Kerbal 1-5", "Learstar A1", "Dynawing", "Slim Shuttle", "Ariane 5", "GDLV3", "ComSat Lx", "AeroEquus",
                "PT Series Munsplorer",
            };
            foreach (string name in orbital)
            {
                Flight flight = Log(Fly(StockCrafts.Named(name), ToOrbit()));
                Assert.IsTrue(flight.LiftOff >= 1.2, name + " lifts off: TWR " + flight.LiftOff.ToString("0.00", CultureInfo.InvariantCulture));
                Assert.IsTrue(flight.Short < 0.5, name + " reaches orbit:\n" + flight.Report);
            }
        }

        [TestMethod]
        public void TheTrainersDoNotReachOrbit()
        {
            foreach (string name in new[] { "Kerbal 2", "Jumping Flea", "Orbiter 1A" })
            {
                Flight flight = Log(Fly(StockCrafts.Named(name), ToOrbit()));
                Assert.IsTrue(flight.Short > 0.5 || flight.LiftOff < 1.0, name + " does not reach orbit:\n" + flight.Report);
            }
        }

        [TestMethod]
        public void TheMunCraftsAsTheirDescriptionsHaveThem()
        {
            // "past the Mun, or if really foolhardy, past Minmus".
            Assert.IsTrue(Log(Fly(StockCrafts.Named("AeroEquus"), ToMunOrbit())).Short < 0.5, "AeroEquus to the Mun");
            // "launcher/lander for Mun/Minmus".
            Assert.IsTrue(Log(Fly(StockCrafts.Named("PT Series Munsplorer"), MunAndBack())).Short < 0.5, "Munsplorer to the Mun and home");
            Assert.IsTrue(Log(Fly(StockCrafts.Named("PT Series Munsplorer"), MinmusAndBack())).Short < 0.5, "Munsplorer to Minmus and home");
            // "a very optimistic set of landing legs": the Mun and home without a margin,
            // just short with one.
            Flight kerbalX = Log(Fly(StockCrafts.Named("Kerbal X"), MunAndBack()));
            Assert.IsTrue(kerbalX.Short > 0.5 && kerbalX.Short < 150.0, "Kerbal X short by little with a margin");
            Assert.IsTrue(Log(Fly(StockCrafts.Named("Kerbal X"), MunAndBack(), 0.0)).Short < 0.5, "Kerbal X to the Mun and home without one");
        }

        // The planner's own simulation against the offline model for every craft whose
        // engines breathe no air, where both burn the same engines.
        [TestMethod]
        public void TheOwnSimulationMatchesTheOfflineModel()
        {
            foreach (StockCraft craft in StockCrafts.All.Where(craft => craft.Sim.All(part => part.Air == null)))
            {
                StageSim.Result result = StageSim.Run(StockCrafts.Linked(craft.Sim), StockCrafts.Density, 0.0, "Kerbin");
                foreach (StageRow expected in craft.Rows)
                {
                    StageSim.Row row = result.Rows.Single(r => r.Number == expected.Number);
                    string what = craft.Name + " S" + expected.Number;
                    Assert.AreEqual(expected.StartMass, row.StartMass, Math.Max(0.01, expected.StartMass * 0.002), what + " start");
                    Assert.AreEqual(expected.EndMass, row.EndMass, Math.Max(0.01, expected.EndMass * 0.002), what + " end");
                    Assert.AreEqual(expected.DeltaVVacuum, row.DeltaV, Math.Max(2.0, expected.DeltaVVacuum * 0.01), what + " delta-v");
                    Assert.AreEqual(expected.Solid, row.Solid, what + " solid");
                }
            }
        }

        // "A Prototype Spaceplane capable of reaching Orbit" (Dove, a RAPIER); "a
        // spaceplane capable of reaching orbit, if flown correctly" (Aeris 4A, two
        // Whiplashes and an aerospike); "capable of sub-orbital flights" (Ravenspear
        // Mk3, jets only).
        [TestMethod]
        public void TheSpaceplanesAsTheirDescriptionsHaveThem()
        {
            foreach (string name in new[] { "Dove", "Aeris 4A" })
            {
                Flight flight = Log(Fly(StockCrafts.Named(name), ToOrbit()));
                Assert.IsTrue(flight.Runway, name + " takes off along the runway");
                Assert.IsTrue(flight.Short < 0.5, name + " reaches orbit:\n" + flight.Report);
            }
            Flight ravenspear = Log(Fly(StockCrafts.Named("Ravenspear Mk3"), ToOrbit()));
            Assert.IsTrue(ravenspear.Short > 0.5, "Ravenspear Mk3 does not reach orbit:\n" + ravenspear.Report);
            foreach (string name in new[] { "Stratolauncher", "Satellite Launcher" }) Log(Fly(StockCrafts.Named(name), ToOrbit()));
        }

        [TestMethod]
        public void TheOtherLaunchersAsTheModelHasThem()
        {
            // No destination in their descriptions: logged for the record.
            foreach (string name in new[] { "Kerbal 1", "Science Jr", "Orbiter One", "Z-MAP Satellite Launch Kit" })
                Log(Fly(StockCrafts.Named(name), ToOrbit()));
        }
    }
}
