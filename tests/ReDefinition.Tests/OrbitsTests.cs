using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Orbits;

namespace ReDefinition.Tests
{
    // The orbit maths (src/Orbits): motion on a conic, Lambert's problem, the burns within an orbit and the search for a transfer.
    [TestClass]
    public class OrbitsTests
    {
        // Kerbin's gravitational parameter, and the radius of a 100 km orbit.
        private const double Mu = 3.5316e12;
        private const double Low = 700000.0;

        private static State Circular(double radius, double phase = 0.0)
        {
            double speed = Math.Sqrt(Mu / radius);
            return new State(new V3(Math.Cos(phase), Math.Sin(phase), 0.0) * radius, new V3(-Math.Sin(phase), Math.Cos(phase), 0.0) * speed);
        }

        private static double Energy(State s)
        {
            return V3.Dot(s.V, s.V) / 2.0 - Mu / s.R.Length;
        }

        // A circular orbit comes back to where it was after a period, and is opposite after half of one.
        [TestMethod]
        public void ACircularOrbitReturnsAfterOnePeriod()
        {
            State start = Circular(Low);
            double period = 2.0 * Math.PI * Math.Sqrt(Low * Low * Low / Mu);
            State whole = Kepler.Propagate(start, period, Mu);
            Assert.AreEqual(0.0, (whole.R - start.R).Length, 1e-3);
            Assert.AreEqual(0.0, (whole.V - start.V).Length, 1e-6);
            State half = Kepler.Propagate(start, period / 2.0, Mu);
            Assert.AreEqual(0.0, (half.R + start.R).Length, 1e-3);
        }

        // On an ellipse and on a hyperbola the energy and the angular momentum hold, and going back returns to the start.
        [TestMethod]
        public void EnergyAndMomentumHoldOnEllipsesAndHyperbolas()
        {
            double vCircular = Math.Sqrt(Mu / Low);
            foreach (double factor in new[] { 1.2, 1.38, 1.5, 2.0 })
            {
                var start = new State(new V3(Low, 0.0, 0.0), new V3(0.0, vCircular * factor, 0.0));
                foreach (double time in new[] { 100.0, 3000.0, 40000.0 })
                {
                    State later = Kepler.Propagate(start, time, Mu);
                    Assert.AreEqual(Energy(start), Energy(later), Math.Abs(Energy(start)) * 1e-9, "energy, factor " + factor + ", " + time + " s");
                    Assert.AreEqual(V3.Cross(start.R, start.V).Length, V3.Cross(later.R, later.V).Length, 1e-3 * vCircular);
                    State back = Kepler.Propagate(later, -time, Mu);
                    Assert.AreEqual(0.0, (back.R - start.R).Length, 1e-2, "back, factor " + factor + ", " + time + " s");
                }
            }
        }

        // Curtis's worked example (Orbital Mechanics for Engineering Students, example 5.2): a Lambert transfer in 3600 s.
        [TestMethod]
        public void LambertGivesCurtissExample()
        {
            double mu = 398600.0;
            V3 v1, v2;
            bool found = Lambert.Solve(new V3(5000.0, 10000.0, 2100.0), new V3(-14600.0, 2500.0, 7000.0), 3600.0, mu, true,
                                       new V3(0.0, 0.0, 1.0), out v1, out v2);
            Assert.IsTrue(found);
            Assert.AreEqual(-5.9925, v1.X, 1e-3);
            Assert.AreEqual(1.9254, v1.Y, 1e-3);
            Assert.AreEqual(3.2456, v1.Z, 1e-3);
            Assert.AreEqual(-3.3125, v2.X, 1e-3);
            Assert.AreEqual(-4.1966, v2.Y, 1e-3);
            Assert.AreEqual(-0.38529, v2.Z, 1e-3);
        }

        // A transfer found by Lambert, flown by the propagator, ends where it was meant to.
        [TestMethod]
        public void ALambertTransferEndsWhereItWasMeantTo()
        {
            var start = new State(new V3(Low, 0.0, 0.0), new V3(0.0, 1.3 * Math.Sqrt(Mu / Low), 300.0));
            foreach (double time in new[] { 1500.0, 4000.0, 8000.0 })
            {
                State end = Kepler.Propagate(start, time, Mu);
                V3 v1, v2;
                Assert.IsTrue(Lambert.Solve(start.R, end.R, time, Mu, true, V3.Cross(start.R, start.V), out v1, out v2), "found, " + time + " s");
                Assert.AreEqual(0.0, (v1 - start.V).Length, 1e-4, "start velocity, " + time + " s");
                Assert.AreEqual(0.0, (v2 - end.V).Length, 1e-4, "end velocity, " + time + " s");
            }
        }

        // A transfer of half a turn, the Hohmann transfer's angle, reaches its target as well, in a plane given only by the hint.
        [TestMethod]
        public void ALambertTransferOfHalfATurnReachesItsTarget()
        {
            double far = 12000000.0;
            double hohmann = Math.PI * Math.Sqrt(Math.Pow((Low + far) / 2.0, 3.0) / Mu);
            foreach (double slip in new[] { 0.0, 1e-9, -1e-9, 0.05 })
            {
                V3 r1 = new V3(Low, 0.0, 0.0), r2 = new V3(-far * Math.Cos(slip), -far * Math.Sin(slip), 0.0);
                V3 v1, v2;
                Assert.IsTrue(Lambert.Solve(r1, r2, hohmann, Mu, true, new V3(0.0, 0.0, 1.0), out v1, out v2), "found, slip " + slip);
                State flown = Kepler.Propagate(new State(r1, v1), hohmann, Mu);
                Assert.AreEqual(0.0, (flown.R - r2).Length, 1.0, "miss, slip " + slip);
                Assert.IsTrue(V3.Cross(r1, v1).Z > 0.0, "prograde, slip " + slip);
            }
        }

        // From a low circular orbit to a circular one far out, in the same plane, the cheapest leaving burn is the Hohmann transfer's.
        [TestMethod]
        public void TheSearchFindsTheHohmannTransfer()
        {
            double far = 12000000.0;
            State ship = Circular(Low);
            State target = Circular(far, 1.0);
            double expected = Math.Sqrt(Mu / Low) * (Math.Sqrt(2.0 * far / (Low + far)) - 1.0);
            double hohmann = Math.PI * Math.Sqrt(Math.Pow((Low + far) / 2.0, 3.0) / Mu);
            double near = 2.0 * Math.PI * Math.Sqrt(Low * Low * Low / Mu), distant = 2.0 * Math.PI * Math.Sqrt(far * far * far / Mu);
            double synodic = 1.0 / Math.Abs(1.0 / near - 1.0 / distant);
            Transfer best;
            Assert.IsTrue(Transfers.Cheapest(ship, target, Mu, synodic, 0.6 * hohmann, 1.6 * hohmann, false, out best));
            Assert.AreEqual(expected, best.Departure.Length, expected * 0.02);
            Assert.AreEqual(hohmann, best.FlightTime, hohmann * 0.06);
        }

        // Circularising at the apoapsis takes the speed there to the circular one, and the orbit that follows is round.
        [TestMethod]
        public void CircularisingAtTheApoapsisRoundsTheOrbit()
        {
            var start = new State(new V3(Low, 0.0, 0.0), new V3(0.0, Math.Sqrt(Mu / Low) * 1.15, 0.0));
            var orbit = new Conic(start, Mu);
            double apoapsis = orbit.RadiusAt(Math.PI);
            Burn burn;
            Assert.IsTrue(Maneuvers.Circularize(start, Mu, true, out burn));
            Assert.AreEqual(orbit.TimeToApoapsis, burn.Delay, 1e-6);
            State there = Kepler.Propagate(start, burn.Delay, Mu);
            Assert.AreEqual(Math.Sqrt(Mu / apoapsis) - there.V.Length, burn.Magnitude, 1e-3);
            var after = new Conic(new State(there.R, there.V + burn.DeltaV), Mu);
            Assert.AreEqual(0.0, after.E, 1e-6);
        }

        // Raising the periapsis from the apoapsis puts the other apsis where it was asked to be.
        [TestMethod]
        public void ABurnAtTheApoapsisPutsThePeriapsisWhereAsked()
        {
            var start = new State(new V3(Low, 0.0, 0.0), new V3(0.0, Math.Sqrt(Mu / Low) * 1.2, 0.0));
            double apoapsis = new Conic(start, Mu).RadiusAt(Math.PI);
            Burn burn;
            Assert.IsTrue(Maneuvers.ChangeOpposite(start, Mu, true, 900000.0, out burn));
            State there = Kepler.Propagate(start, burn.Delay, Mu);
            var after = new Conic(new State(there.R, there.V + burn.DeltaV), Mu);
            Assert.AreEqual(900000.0, after.RadiusAt(0.0), 1.0);
            Assert.AreEqual(apoapsis, after.RadiusAt(Math.PI), 1.0);
        }

        // A change of plane turns the orbit into the other plane for 2 v sin(i / 2) at a node.
        [TestMethod]
        public void APlaneChangeMatchesThePlaneForTwoVSinHalfTheTilt()
        {
            State start = Circular(Low, 0.3);
            double tilt = 10.0 * Math.PI / 180.0;
            V3 target = new V3(0.0, 0.0, 1.0).Rotated(new V3(1.0, 0.0, 0.0), tilt);
            Burn burn;
            Assert.IsTrue(Maneuvers.MatchPlane(start, Mu, target, out burn));
            Assert.AreEqual(2.0 * Math.Sqrt(Mu / Low) * Math.Sin(tilt / 2.0), burn.Magnitude, 1e-3);
            State there = Kepler.Propagate(start, burn.Delay, Mu);
            V3 normal = V3.Cross(there.R, there.V + burn.DeltaV).Unit;
            Assert.AreEqual(0.0, V3.Angle(normal, target.Unit), 1e-9);
        }

        // In Unity's left-handed world the node's normal points the other way from the cross product of position and velocity.
        // The radial and prograde axes do not change, and a burn goes there and back in that frame too.
        [TestMethod]
        public void TheNodeFrameInALeftHandedWorldFlipsTheNormal()
        {
            State at = Circular(Low, 0.7);
            V3 normal = V3.Cross(at.R, at.V).Unit;
            V3 normalBurn = Maneuvers.FromNodeFrame(at, new V3(0.0, 1.0, 0.0), true);
            Assert.AreEqual(-1.0, V3.Dot(normalBurn, normal), 1e-9);
            Assert.AreEqual(1.0, V3.Dot(Maneuvers.FromNodeFrame(at, new V3(1.0, 0.0, 0.0), true), at.R.Unit), 1e-9);
            Assert.AreEqual(1.0, V3.Dot(Maneuvers.FromNodeFrame(at, new V3(0.0, 0.0, 1.0), true), at.V.Unit), 1e-9);
            var burn = new V3(5.0, -6.0, 7.0);
            Assert.AreEqual(0.0, (Maneuvers.ToNodeFrame(at, Maneuvers.FromNodeFrame(at, burn, true), true) - burn).Length, 1e-9);
        }

        // A burn in the node frame, radial out, normal and prograde, goes there and back.
        [TestMethod]
        public void TheNodeFrameGoesThereAndBack()
        {
            State at = Circular(Low, 0.7);
            var burn = new V3(11.0, -22.0, 33.0);
            V3 inertial = Maneuvers.FromNodeFrame(at, burn);
            V3 back = Maneuvers.ToNodeFrame(at, inertial);
            Assert.AreEqual(0.0, (back - burn).Length, 1e-9);
            Assert.AreEqual(33.0, V3.Dot(inertial, at.V.Unit), 1e-9);
            Assert.AreEqual(11.0, V3.Dot(inertial, at.R.Unit), 1e-9);
        }
    }
}
