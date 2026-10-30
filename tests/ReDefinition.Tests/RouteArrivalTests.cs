using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Autopilot;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Engineer;

namespace ReDefinition.Tests
{
    // How the autopilot's route reads the planner's way of arriving at a stop (RouteSource.Arrival).
    [TestClass]
    public class RouteArrivalTests
    {
        private static PlannerBodies Bodies()
        {
            var bodies = new PlannerBodies();
            bodies.Bodies.Add(new PlannerBody { Name = "Duna", Id = "Duna", AtmosphereDepth = 50000.0 });
            bodies.Bodies.Add(new PlannerBody { Name = "The Mun", Id = "Mun", AtmosphereDepth = 0.0 });
            return bodies;
        }

        private static Destination Arrive(int body, StopKind kind, bool atmosphere, bool orbitFirst)
        {
            var stop = new RouteStop { Body = body, Kind = kind, UseAtmosphere = atmosphere, OrbitFirst = orbitFirst };
            return RouteSource.Arrival(new Destination { Surface = kind == StopKind.Surface }, stop, Bodies());
        }

        // A landing that uses the air and does not go into an orbit first goes straight in, and dips as far as the planner's pass.
        [TestMethod]
        public void ALandingInTheAirWithoutAnOrbitGoesStraightIn()
        {
            Destination arrival = Arrive(0, StopKind.Surface, true, false);
            Assert.IsTrue(arrival.Direct);
            Assert.IsFalse(arrival.Aerobrake);
            Assert.AreEqual(50000.0 * RoutePlanner.AerobrakeDepth, arrival.PassAltitude, 1e-6);
        }

        // With an orbit chosen first, the pass through the air is what slows the vessel into that orbit.
        [TestMethod]
        public void ALandingThatGoesIntoAnOrbitFirstBrakesInTheAir()
        {
            Destination arrival = Arrive(0, StopKind.Surface, true, true);
            Assert.IsFalse(arrival.Direct);
            Assert.IsTrue(arrival.Aerobrake);
        }

        // An orbit that uses the air is reached by aerobraking.
        [TestMethod]
        public void AnOrbitThatUsesTheAirIsReachedByAerobraking()
        {
            Destination arrival = Arrive(0, StopKind.Orbit, true, false);
            Assert.IsFalse(arrival.Direct);
            Assert.IsTrue(arrival.Aerobrake);
        }

        // Without air, or without the choice, the stop is reached as before.
        [TestMethod]
        public void WithoutAirOrChoiceTheArrivalIsAsBefore()
        {
            foreach (Destination arrival in new[] { Arrive(1, StopKind.Surface, true, false), Arrive(0, StopKind.Surface, false, false), Arrive(0, StopKind.Orbit, false, false) })
            {
                Assert.IsFalse(arrival.Direct);
                Assert.IsFalse(arrival.Aerobrake);
                Assert.AreEqual(0.0, arrival.PassAltitude);
            }
        }
    }
}
