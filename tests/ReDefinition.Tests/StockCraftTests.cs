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
    }

    internal static partial class StockCrafts
    {
        private static StageRow Row(int number, double start, double end, double deltaV, double thrust, double seaLevel)
        {
            StageRow row = new StageRow
            {
                Number = number, StartMass = start, EndMass = end, DeltaV = deltaV, DeltaVVacuum = deltaV, ThrustVacuum = thrust,
                Thrust = seaLevel, HasEngines = thrust > 0.0,
            };
            row.ThrustAtSurface["Kerbin"] = seaLevel;
            return row;
        }

        private static PartFacts Facts(int parent, int decouple, int fires, double mass, double dry, string title, bool command,
                                       bool engine, bool legs, bool parachute, bool heatShield, bool clamp, int crew,
                                       Separation separator, Dictionary<int, double[]> fuel)
        {
            PartFacts facts = new PartFacts
            {
                Parent = parent, DecoupleStage = decouple, ActivatesAt = fires, Mass = mass, DryMass = dry, Title = title,
                Command = command, Engine = engine, Legs = legs, Parachute = parachute, HeatShield = heatShield, Clamp = clamp,
                Crew = crew, Separator = separator,
            };
            foreach (KeyValuePair<int, double[]> stage in fuel) facts.StageFuel[stage.Key] = stage.Value;
            return facts;
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
            public double Short;
            public string Report;
        }

        private static Flight Fly(StockCraft craft, List<RouteStop> goals, double margin = 0.10)
        {
            List<PlannerBody> bodies = PlannerTests.Stock();
            PlannerBody home = bodies[Kerbin];
            double gravity = home.Mu / (home.Radius * home.Radius);
            Dictionary<int, string> labels = new Dictionary<int, string>();
            List<StageRow> rows = craft.Rows.OrderByDescending(row => row.Number).ToList();
            rows = ClampedLaunch.Parallel(rows, craft.Parts, labels, gravity);

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
                Number = row.Number, StartMass = row.StartMass, EndMass = row.EndMass, DeltaV = row.DeltaVVacuum,
            }).ToList();
            List<StagePlan> plans = steps.Select(step => new StagePlan()).ToList();
            StageBudget.Result budget = StageBudget.Allocate(available, budgetSteps, margin, plans, VesselShape.Split(available, parts, chosen));

            Flight flight = new Flight { Craft = craft.Name, Steps = steps, Budget = budget };
            StageBudget.StageUse first = budget.Uses.FirstOrDefault(use => use.Leg == 0);
            StageRow lifting = first != null ? rows.Find(row => row.Number == first.Stage) : null;
            flight.LiftOff = lifting != null ? lifting.ThrustAtSurface["Kerbin"] / (first.Mass * gravity) : 0.0;
            flight.Short = budget.Short.Sum();

            StringBuilder text = new StringBuilder();
            text.AppendFormat(CultureInfo.InvariantCulture, "{0}: lift-off TWR {1:0.00}, sections {2}", craft.Name, flight.LiftOff,
                sections.Count == 0 ? "none" : string.Join(", ", sections.Select(section => section.Name)));
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

        [TestMethod]
        public void TheOtherLaunchersAsTheModelHasThem()
        {
            // No destination in their descriptions: logged for the record.
            foreach (string name in new[] { "Kerbal 1", "Science Jr", "Orbiter One", "Z-MAP Satellite Launch Kit" })
                Log(Fly(StockCrafts.Named(name), ToOrbit()));
        }
    }
}
