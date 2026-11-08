using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Engineer;

namespace ReDefinition.Tests
{
    // The plan's route replaced from outside the rows (the map's menu, the Live button), brought back, emptied and added to, and the landing site a
    // stop keeps in the file.
    [TestClass]
    public class PlannerRouteReplaceTests
    {
        private const int Kerbin = PlannerModelFixture.Kerbin, Mun = PlannerModelFixture.Mun;

        private static PlannerModel Strong()
        {
            return PlannerModelFixture.Model(PlannerModelFixture.OneStage(6000.0, 200.0, 180.0), PlannerModelFixture.ToKerbinOrbit());
        }

        private static List<RouteStop> ToMunOrbit()
        {
            return new List<RouteStop>
            {
                new RouteStop { Body = Kerbin, Kind = StopKind.Orbit, Altitude = 80000.0 },
                new RouteStop { Body = Mun, Kind = StopKind.Orbit, Altitude = 30000.0 },
            };
        }

        // Replacing the route sets the new stops, tells the view the stops changed, and keeps the old route for Undo.
        [TestMethod]
        public void ReplacingTheRouteKeepsTheOldOneForUndo()
        {
            PlannerModel model = Strong();
            int reshaped = 0;
            model.StopsReshaped = () => reshaped++;
            Assert.IsFalse(model.CanUndo);

            model.SetRoute(ToMunOrbit());

            Assert.AreEqual(Mun, model.State.Stops[1].Body);
            Assert.AreEqual(StopKind.Orbit, model.State.Stops[0].Kind);
            Assert.AreEqual(1, reshaped);
            Assert.IsTrue(model.CanUndo);

            model.Undo();

            Assert.AreEqual(StopKind.Surface, model.State.Stops[0].Kind);
            Assert.AreEqual(Kerbin, model.State.Stops[1].Body);
            Assert.IsFalse(model.CanUndo, "the route is brought back once");
        }

        // A route needs a start and a destination, and no more stops than the planner has room for.
        [TestMethod]
        public void ARouteOfOneStopOrTooManyIsNotTaken()
        {
            PlannerModel model = Strong();

            model.SetRoute(new List<RouteStop> { new RouteStop { Body = Mun } });
            Assert.AreEqual(2, model.State.Stops.Count);
            Assert.IsFalse(model.CanUndo);

            var many = new List<RouteStop>();
            for (int i = 0; i <= PlannerModel.HardStops; i++) many.Add(new RouteStop { Body = Kerbin });
            model.SetRoute(many);
            Assert.AreEqual(2, model.State.Stops.Count);
        }

        // An emptied route is the start and a destination that is not picked, which the autopilot does not fly.
        [TestMethod]
        public void AnEmptiedRouteHasNoDestination()
        {
            PlannerModel model = Strong();
            Assert.IsFalse(model.RouteEmpty);

            model.ClearRoute();

            Assert.IsTrue(model.RouteEmpty);
            Assert.AreEqual(RouteStop.NoBody, model.State.Stops[1].Body);
            Assert.IsTrue(model.CanUndo);
        }

        // A stop added to an empty route fills its destination; added to a route that has one it goes at the end.
        [TestMethod]
        public void AStopFillsTheEmptyDestinationOrGoesAtTheEnd()
        {
            PlannerModel model = Strong();
            model.ClearRoute();

            Assert.IsTrue(model.PutStop(new RouteStop { Body = Mun, Kind = StopKind.Orbit, Altitude = 30000.0 }));
            Assert.AreEqual(2, model.State.Stops.Count);
            Assert.AreEqual(Mun, model.State.Stops[1].Body);

            Assert.IsTrue(model.PutStop(new RouteStop { Body = Mun, Kind = StopKind.Surface }));
            Assert.AreEqual(3, model.State.Stops.Count);
            Assert.AreEqual(StopKind.Surface, model.State.Stops[2].Kind);
        }

        // A landing site on the ground is kept in the file with its stop.
        [TestMethod]
        public void ALandingSiteIsReadAsItWasWritten()
        {
            PlannerBodies bodies = PlannerModelFixture.Bodies();
            PlannerState state = PlannerState.Default(bodies);
            state.Stops[1].Body = Mun;
            state.Stops[1].Kind = StopKind.Surface;
            state.Stops[1].HasSite = true;
            state.Stops[1].SiteLatitude = -12.5;
            state.Stops[1].SiteLongitude = 74.25;

            PlannerState read = PlannerState.FromNode(state.ToNode("Kerbal X", bodies), bodies);

            Assert.IsFalse(read.Stops[0].HasSite);
            Assert.IsTrue(read.Stops[1].HasSite);
            Assert.AreEqual(-12.5, read.Stops[1].SiteLatitude, 1e-9);
            Assert.AreEqual(74.25, read.Stops[1].SiteLongitude, 1e-9);
        }

        // A meeting or a docking with a vessel, and the choice to put the folded parts out on arrival, are kept in the file.
        [TestMethod]
        public void AMeetingAndTheDeployChoiceAreReadAsTheyWereWritten()
        {
            PlannerBodies bodies = PlannerModelFixture.Bodies();
            PlannerState state = PlannerState.Default(bodies);
            System.Guid target = System.Guid.NewGuid();
            state.Stops[1].Body = Kerbin;
            state.Stops[1].Kind = StopKind.Orbit;
            state.Stops[1].MeetWith = target;
            state.Stops[1].MeetName = "Station";
            state.Stops[1].Dock = true;
            state.Stops[1].DockWith = 77u;
            state.DeployOnArrival = true;

            PlannerState read = PlannerState.FromNode(state.ToNode("Kerbal X", bodies), bodies);

            Assert.AreEqual(target, read.Stops[1].MeetWith);
            Assert.AreEqual("Station", read.Stops[1].MeetName);
            Assert.IsTrue(read.Stops[1].Dock);
            Assert.AreEqual(77u, read.Stops[1].DockWith);
            Assert.IsTrue(read.DeployOnArrival);
            Assert.AreEqual(System.Guid.Empty, read.Stops[0].MeetWith);
        }

        // A route flown to its end is empty with nothing to bring back, and keeps the choice to put the parts out.
        [TestMethod]
        public void AFlownRouteIsEmptyWithNothingToBringBack()
        {
            PlannerModel model = Strong();
            model.ToggleDeployOnArrival();
            model.SetRoute(ToMunOrbit());
            Assert.IsTrue(model.CanUndo);

            model.EndRoute();

            Assert.IsTrue(model.RouteEmpty);
            Assert.IsFalse(model.CanUndo);
            Assert.IsTrue(model.DeployOnArrival);
        }

        // The apoapsis and the inclination typed into a stop's fields are kept: an apoapsis not above the altitude means a round orbit, and an empty
        // inclination leaves the plane as it is.
        [TestMethod]
        public void TheShapeOfAnOrbitIsSetByItsFields()
        {
            PlannerModel model = Strong();
            model.SetRoute(ToMunOrbit());
            Assert.AreEqual("", model.ApoapsisKm(1));
            Assert.AreEqual("", model.InclinationText(1));

            model.SetApoapsis(1, "90");
            model.SetInclination(1, "12.5");

            Assert.AreEqual(90000.0, model.State.Stops[1].Apoapsis, 1e-6);
            Assert.AreEqual("90", model.ApoapsisKm(1));
            Assert.AreEqual(12.5, model.State.Stops[1].Inclination, 1e-9);
            StringAssert.Contains(model.StopText(1), "90 km");
            StringAssert.Contains(model.StopText(1), "12.5 deg");

            model.SetApoapsis(1, "20");
            model.SetInclination(1, "");

            Assert.AreEqual(0.0, model.State.Stops[1].Apoapsis, "an apoapsis under the periapsis is a round orbit");
            Assert.IsFalse(model.State.Stops[1].HasInclination);
        }

        // The shape of an orbit and the plane of a vessel are kept in the file.
        [TestMethod]
        public void TheShapeOfAnOrbitIsReadAsItWasWritten()
        {
            PlannerBodies bodies = PlannerModelFixture.Bodies();
            PlannerState state = PlannerState.Default(bodies);
            System.Guid other = System.Guid.NewGuid();
            state.Stops[1].Body = Kerbin;
            state.Stops[1].Kind = StopKind.Orbit;
            state.Stops[1].Altitude = 100000.0;
            state.Stops[1].Apoapsis = 250000.0;
            state.Stops[1].HasInclination = true;
            state.Stops[1].Inclination = 45.0;
            state.Stops[1].MatchPlaneWith = other;
            state.Stops[1].MatchPlaneName = "Station";

            PlannerState read = PlannerState.FromNode(state.ToNode("Kerbal X", bodies), bodies);

            Assert.AreEqual(250000.0, read.Stops[1].Apoapsis, 1e-6);
            Assert.IsTrue(read.Stops[1].HasInclination);
            Assert.AreEqual(45.0, read.Stops[1].Inclination, 1e-9);
            Assert.AreEqual(other, read.Stops[1].MatchPlaneWith);
            Assert.AreEqual("Station", read.Stops[1].MatchPlaneName);
            Assert.AreEqual(0.0, read.Stops[0].Apoapsis);
        }

        // An orbit with an apoapsis and an inclination costs what raising the apoapsis and turning the plane cost, on top of the round orbit.
        [TestMethod]
        public void TheShapeOfAnOrbitIsInTheLegsDeltaV()
        {
            PlannerBodies bodies = PlannerModelFixture.Bodies();
            PlannerBody kerbin = bodies.Bodies[Kerbin];
            var from = new RouteStop { Body = Kerbin, Kind = StopKind.Orbit, Altitude = 80000.0 };
            var round = new RouteStop { Body = Kerbin, Kind = StopKind.Orbit, Altitude = 80000.0 };
            var shaped = new RouteStop { Body = Kerbin, Kind = StopKind.Orbit, Altitude = 80000.0, Apoapsis = 250000.0, HasInclination = true, Inclination = 30.0 };

            Leg flat = RoutePlanner.LegOf(bodies.Bodies, from, round, 0, false);
            Leg leg = RoutePlanner.LegOf(bodies.Bodies, from, shaped, 0, false);

            double rp = kerbin.Radius + 80000.0, ra = kerbin.Radius + 250000.0;
            double raise = System.Math.Sqrt(kerbin.Mu * (2.0 / rp - 2.0 / (rp + ra))) - System.Math.Sqrt(kerbin.Mu / rp);
            double turn = 2.0 * System.Math.Sqrt(kerbin.Mu / rp) * System.Math.Sin(15.0 * System.Math.PI / 180.0);
            Assert.AreEqual(flat.DeltaV + raise + turn, leg.DeltaV, 1.0);
            Assert.IsTrue(leg.Lines.Exists(line => line.Name.StartsWith("Raise the apoapsis")));
            Assert.IsTrue(leg.Lines.Exists(line => line.Name.StartsWith("Turn the plane")));
        }

        // The plane of another vessel costs the turn to the inclination it had when it was chosen.
        [TestMethod]
        public void ThePlaneOfAnotherVesselIsInTheLegsDeltaV()
        {
            PlannerBodies bodies = PlannerModelFixture.Bodies();
            PlannerBody kerbin = bodies.Bodies[Kerbin];
            var from = new RouteStop { Body = Kerbin, Kind = StopKind.Orbit, Altitude = 80000.0 };
            var round = new RouteStop { Body = Kerbin, Kind = StopKind.Orbit, Altitude = 80000.0 };
            var match = new RouteStop { Body = Kerbin, Kind = StopKind.Orbit, Altitude = 80000.0, MatchPlaneWith = System.Guid.NewGuid(), MatchPlaneName = "Station", MatchPlaneInclination = 20.0 };

            double flat = RoutePlanner.LegOf(bodies.Bodies, from, round, 0, false).DeltaV;
            Leg leg = RoutePlanner.LegOf(bodies.Bodies, from, match, 0, false);

            double turn = 2.0 * System.Math.Sqrt(kerbin.Mu / (kerbin.Radius + 80000.0)) * System.Math.Sin(10.0 * System.Math.PI / 180.0);
            Assert.AreEqual(flat + turn, leg.DeltaV, 1.0);
            Assert.IsTrue(leg.Lines.Exists(line => line.Name == "Match the plane of Station"));
        }

        // A drive over the ground between two surface stops takes no delta-v; a landing does.
        [TestMethod]
        public void ADriveBetweenSurfaceStopsCostsNothing()
        {
            PlannerBodies bodies = PlannerModelFixture.Bodies();
            var from = new RouteStop { Body = Kerbin, Kind = StopKind.Surface };
            var landing = new RouteStop { Body = Kerbin, Kind = StopKind.Surface };
            var drive = new RouteStop { Body = Kerbin, Kind = StopKind.Surface, Drives = true };

            Assert.IsTrue(RoutePlanner.LegOf(bodies.Bodies, from, landing, 0, false).DeltaV > 0.0);
            Assert.AreEqual(0.0, RoutePlanner.LegOf(bodies.Bodies, from, drive, 0, false).DeltaV, 0.001);
            Assert.AreEqual(0, RouteSteps.RolesBetween(bodies.Bodies, from, drive).Count);
            Assert.AreEqual(2, RouteSteps.RolesBetween(bodies.Bodies, from, landing).Count);
        }
    }
}
