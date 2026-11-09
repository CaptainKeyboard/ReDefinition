using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Planner.Orbits;

namespace ReDefinition.Tests
{
    // The plane asked for by an inclination (Maneuvers.NormalForInclination), in axes of either handedness, and the burn that turns the orbit into it.
    [TestClass]
    public class InclinationTests
    {
        private const double Mu = 3.5316e12;
        private static readonly V3 Pole = new V3(0.0, 0.0, 1.0);

        private static V3 NormalAt(double degrees)
        {
            double a = degrees * Math.PI / 180.0;
            return new V3(Math.Sin(a), 0.0, Math.Cos(a));
        }

        private static double AngleToPole(V3 normal)
        {
            return V3.Angle(normal, Pole) * 180.0 / Math.PI;
        }

        // An orbit of 30 degrees asked for 60 has a normal 60 degrees off the pole.
        [TestMethod]
        public void TheNormalOfTheAskedInclinationIsThatFarFromThePole()
        {
            V3 wanted = Maneuvers.NormalForInclination(NormalAt(30.0), Pole, 60.0, 30.0);

            Assert.AreEqual(60.0, AngleToPole(wanted), 1e-6);
        }

        // Where the axes are the other way round, the normal lies at the supplement of the inclination the game reports, and so does the answer.
        [TestMethod]
        public void AMirroredFrameGivesTheSupplement()
        {
            V3 wanted = Maneuvers.NormalForInclination(NormalAt(150.0), Pole, 60.0, 30.0);

            Assert.AreEqual(120.0, AngleToPole(wanted), 1e-6);
        }

        // An orbit in the equator has no line of nodes of its own, and still turns to the inclination asked for.
        [TestMethod]
        public void AnEquatorialOrbitTurnsToTheInclinationAsked()
        {
            V3 wanted = Maneuvers.NormalForInclination(Pole, Pole, 20.0, 0.0);

            Assert.AreEqual(20.0, AngleToPole(wanted), 1e-6);
        }

        // The burn at a node turns a circular orbit into the plane asked for, and keeps its speed.
        [TestMethod]
        public void TheBurnTurnsTheOrbitIntoTheAskedPlane()
        {
            double radius = 700000.0;
            double speed = Math.Sqrt(Mu / radius);
            var start = new State(new V3(radius, 0.0, 0.0), new V3(0.0, speed, 0.0));
            V3 wanted = Maneuvers.NormalForInclination(V3.Cross(start.R, start.V).Unit, Pole, 25.0, 0.0);
            Burn burn;

            Assert.IsTrue(Maneuvers.MatchPlane(start, Mu, wanted, out burn));
            State there = Kepler.Propagate(start, burn.Delay, Mu);
            var after = new State(there.R, there.V + burn.DeltaV);
            V3 normal = V3.Cross(after.R, after.V).Unit;

            Assert.AreEqual(25.0, AngleToPole(normal), 1e-3);
            Assert.AreEqual(there.V.Length, after.V.Length, 1e-3 * speed);
        }
    }
}
