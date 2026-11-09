using System;
using System.Collections.Generic;
using ReDefinition.Planner.Autopilot.Planning;
using ReDefinition.Planner.Engineer;
using ReDefinition.Planner.Planning;

namespace ReDefinition.Tests
{
    // A planner host with a vessel readout the test sets and no flight plan, in place of the editor's or the map's window.
    internal sealed class FakePlannerHost : IPlannerHost
    {
        public EditorReadout Readout { get; set; }

        public RoutePlan Plan
        {
            get { return null; }
        }

        public string VesselName()
        {
            return "Test craft";
        }

        // Where the vessel is, which a test sets; null is no flight.
        public RouteStop Position { get; set; }

        public RouteStop Here(PlannerBodies bodies)
        {
            return Position;
        }

        public void WatchVessel(Action changed, Action fresh)
        {
        }

        public void StopWatching()
        {
        }
    }

    // The plan's model (PlannerModel) set up without the game: the stock system's first four bodies, a vessel readout, a route.
    internal static class PlannerModelFixture
    {
        // The bodies' indices in Bodies().
        internal const int Sun = 0, Kerbin = 1, Mun = 2, Minmus = 3;

        // The Sun, Kerbin, the Mun and Minmus as PlannerTests.Stock() has them, each with the name KSP knows it by, listed with the moons under
        // their planet.
        internal static PlannerBodies Bodies()
        {
            PlannerBodies bodies = new PlannerBodies { Home = Kerbin };
            List<PlannerBody> stock = PlannerTests.Stock();
            for (int i = Sun; i <= Minmus; i++)
            {
                stock[i].Id = stock[i].Name;
                bodies.Bodies.Add(stock[i]);
                bodies.Order.Add(i);
            }
            return bodies;
        }

        // A readout of a vessel with the stages and the parts given.
        internal static EditorReadout Readout(StageRow[] stages, params PartFacts[] parts)
        {
            EditorReadout readout = new EditorReadout { HasShip = true };
            readout.Stages.AddRange(stages);
            readout.PartFacts.AddRange(parts);
            return readout;
        }

        // The readout of a vessel that is one stage: `Stage` and the tank and engine that fire it, 10 t with 6 t of fuel.
        internal static EditorReadout OneStage(double deltaV, double thrustVacuum, double thrustKerbin)
        {
            return Readout(new[] { Stage(1, deltaV, thrustVacuum, thrustKerbin) }, Tank(10.0, 4.0, 1, -1, -1));
        }

        // A tank with an engine: `mass` t with `dry` t of it empty. It fires at stage `number`, burning the fuel in it, and is dropped as stage
        // `decoupleStage` fires (-1: never). It hangs under the part at index `parent`.
        internal static PartFacts Tank(double mass, double dry, int number, int decoupleStage, int parent)
        {
            PartFacts part = new PartFacts
            {
                Parent = parent, Mass = mass, DryMass = dry, Engine = true, ActivatesAt = number, DecoupleStage = decoupleStage,
                Title = "Tank " + number,
            };
            part.StageFuel[number] = new[] { mass - dry, 0.0 };
            return part;
        }

        // A stage that is the whole vessel: 10 t as it starts and 4 t as it ends, with `thrustVacuum` kN in vacuum and `thrustKerbin` kN at
        // Kerbin's sea level.
        internal static StageRow Stage(int number, double deltaV, double thrustVacuum, double thrustKerbin)
        {
            StageRow row = new StageRow
            {
                Number = number, StartMass = 10.0, EndMass = 4.0, DeltaV = deltaV, DeltaVVacuum = deltaV, ThrustVacuum = thrustVacuum,
                Thrust = thrustVacuum,
            };
            row.ThrustAtSurface["Kerbin"] = thrustKerbin;
            return row;
        }

        // A model for the vessel read, with the route given worked out at once. The time stands still, and nothing is written to disk or log.
        internal static PlannerModel Model(EditorReadout readout, params RouteStop[] route)
        {
            FakePlannerHost host = new FakePlannerHost { Readout = readout };
            PlannerModel model = new PlannerModel(host, PlannerPool.Full) { Clock = () => 0f };
            PlannerBodies bodies = Bodies();
            PlannerState state = PlannerState.Default(bodies);
            state.Stops.Clear();
            state.Stops.AddRange(route);
            model.Install(bodies, state, "Test craft");
            model.Evaluate();
            return model;
        }

        // From Kerbin's surface to its 80 km orbit.
        internal static RouteStop[] ToKerbinOrbit()
        {
            return new[] { PlannerTests.Surface(Kerbin), PlannerTests.Orbit(Kerbin, 80000) };
        }
    }
}
