using System;
using ReDefinition.Autopilot.Guidance;
using ReDefinition.Core;
using ReDefinition.Landing;
using ReDefinition.Orbits;
using UnityEngine;

namespace ReDefinition.Autopilot.Planning
{
    // The deorbit burn that brings the vessel down on a chosen point of the ground. The burn is the one of the untargeted landing, retrograde, to a
    // periapsis under the ground. The time of it is what is chosen. The descent is simulated from the orbit a burn leaves, the way MechJeb predicts it,
    // with the air and the braking against the ground in it. The time is searched where the simulated descent comes down nearest the point: a burn
    // a little later moves that place by the way the vessel flies, so the search is along one line. What the orbit's plane does not reach is left as
    // a miss across the track, which the plan says.
    internal static class DeorbitAim
    {
        // How many times the time is tried over the first stretch, and how often it is bettered, until the descent comes down within the tolerance.
        private const int Samples = 36;
        private const int Refinements = 9;
        // How far from the vessel the burn may be: over this many orbits, and not sooner than this many seconds.
        private const double Orbits = 1.5;
        private const double Soon = 60.0;
        // The periapsis the burn leaves, as a share of the body's radius, as the untargeted deorbit has it.
        private const double Periapsis = 0.9;

        // The burns tried in a search, and why one could not be simulated: told once, with what the search found.
        private static int tries;
        private static string failure;

        // The best burn found so far in a search.
        private struct Best
        {
            // The delay of the burn from the state it was searched from, how far from the place it comes down, when it lands, and the burn.
            public double Delay, Miss, Impact;
            public V3 Burn;
            // The UT of the state the delay counts from, and the plane turn the state follows, if any.
            public double Now;
            public bool Turned;
            public Burn Turn;
        }

        internal static NodePlanner.Result Plan(Frame frame, double latitude, double longitude)
        {
            Vessel vessel = frame.Vessel;
            CelestialBody body = frame.Body;
            Orbit orbit = frame.Orbit;
            if (orbit == null || orbit.eccentricity >= 1.0) return NodePlanner.Result.Failed("The orbit is not closed.");
            // Without air the descent is braked by the engines, and weak ones cannot do that; with air the chutes may.
            if (!body.atmosphere && Engines.Acceleration(vessel) <= body.GeeASL * 9.81) return NodePlanner.Result.Failed("The engines are too weak to land on " + body.bodyName + ".");
            double mu = body.gravParameter, start = frame.Now;
            double tolerance = Tolerance(body);
            State origin = KspOrbits.StateAt(orbit, start);
            double terrain = Math.Max(0.0, body.TerrainAltitude(latitude, longitude, true));
            failure = null;
            tries = 0;
            // A place off the orbit's plane is brought under it first, and the deorbit is searched from the orbit that burn leaves. The turn depends on when
            // the vessel is over the place, which the search finds; so the turn is worked out again for that time, once.
            double arrival = double.NaN;
            Best best = new Best { Delay = double.NaN, Miss = double.PositiveInfinity };
            for (int pass = 0; pass < 2; pass++)
            {
                Burn turn;
                bool turned = PlaneChange.Plan(body, origin, latitude, longitude, tolerance, arrival, out turn) == PlaneTurn.Needed;
                // Without a turn the second search would be the first again.
                if (pass == 1 && !turned && !best.Turned) break;
                State here = origin;
                double now = start;
                if (turned)
                {
                    State at = Kepler.Propagate(origin, turn.Delay, mu);
                    here = new State(at.R, at.V + turn.DeltaV);
                    now = start + turn.Delay;
                }
                Best found = Search(vessel, body, here, mu, now, latitude, longitude, terrain, tolerance);
                if (double.IsNaN(found.Delay)) break;
                found.Now = now;
                found.Turned = turned;
                found.Turn = turn;
                if (pass == 0 || found.Miss < best.Miss) best = found;
                if (best.Miss <= tolerance) break;
                arrival = found.Impact - start;
            }
            if (double.IsNaN(best.Delay))
                return NodePlanner.Result.Failed("No burn was found whose descent lands. " + (failure ?? "None of the " + tries + " burns tried comes down."));
            Debug.Log(Log.Tag + " Navigation: the deorbit searched " + tries + " burns; the best is in " + best.Delay.ToString("0") + " s, " + best.Burn.Length.ToString("0")
                      + " m/s, and comes down " + best.Miss.ToString("0") + " m from the marker.");
            return Made(frame, best.Now + best.Delay, best.Burn, best.Miss, best.Turned ? (Burn?)best.Turn : null, best.Now);
        }

        // How near the place counts as reached, in m: a share of the body's size, so that it is the same fraction of the way round on every body, and not
        // less than the width of a landing site.
        internal static double Tolerance(CelestialBody body)
        {
            return Math.Max(200.0, 0.0025 * body.Radius);
        }

        // The burn from a state that brings the descent down nearest the place: the best of the times tried, with its delay, its miss and when it lands. The
        // delay is NaN where no burn lands.
        private static Best Search(Vessel vessel, CelestialBody body, State here, double mu, double now, double latitude, double longitude, double terrain, double tolerance)
        {
            double period = new Conic(here, mu).Period;
            // The time of the burn over the stretch ahead, tried at even steps.
            double step = Orbits * period / Samples;
            var best = new Best { Delay = double.NaN, Miss = double.PositiveInfinity };
            double low = Soon, high = Soon;
            for (int i = 0; i <= Samples; i++)
            {
                double delay = Soon + i * step;
                double before = best.Miss;
                Try(vessel, body, here, mu, now, delay, latitude, longitude, terrain, ref best);
                if (best.Miss < before)
                {
                    low = Math.Max(Soon, delay - step);
                    high = delay + step;
                }
            }
            if (double.IsNaN(best.Delay)) return best;
            // The miss falls and rises about the best, so the interval round it is narrowed by golden section.
            const double golden = 0.6180339887;
            double a = low, b = high;
            double c = b - golden * (b - a), d = a + golden * (b - a);
            double missC = Try(vessel, body, here, mu, now, c, latitude, longitude, terrain, ref best);
            double missD = Try(vessel, body, here, mu, now, d, latitude, longitude, terrain, ref best);
            for (int i = 0; i < Refinements && best.Miss > tolerance; i++)
            {
                if (missC < missD)
                {
                    b = d;
                    d = c;
                    missD = missC;
                    c = b - golden * (b - a);
                    missC = Try(vessel, body, here, mu, now, c, latitude, longitude, terrain, ref best);
                }
                else
                {
                    a = c;
                    c = d;
                    missC = missD;
                    d = a + golden * (b - a);
                    missD = Try(vessel, body, here, mu, now, d, latitude, longitude, terrain, ref best);
                }
            }
            return best;
        }

        // How far from the point the descent comes down after the burn at `delay`; infinity where there is no burn or no landing. The best so far is kept.
        private static double Try(Vessel vessel, CelestialBody body, State here, double mu, double now, double delay, double latitude, double longitude, double terrain, ref Best best)
        {
            State leaving = Kepler.Propagate(here, delay, mu);
            V3 burn;
            if (!Retrograde(leaving, mu, body.Radius * Periapsis, out burn)) return double.PositiveInfinity;
            double lat, lon, lands;
            if (!Impact(vessel, body, leaving, burn, now + delay, terrain, out lat, out lon, out lands)) return double.PositiveInfinity;
            double miss = Distance(body, latitude, longitude, lat, lon);
            tries++;
            if (miss < best.Miss)
            {
                best.Miss = miss;
                best.Delay = delay;
                best.Burn = burn;
                best.Impact = lands;
            }
            return miss;
        }

        // The retrograde burn that leaves a periapsis at `radius`: the change of velocity, against the velocity. False where there is none.
        private static bool Retrograde(State at, double mu, double radius, out V3 burn)
        {
            burn = new V3(0.0, 0.0, 0.0);
            V3 direction = at.V.Unit;
            double speed = at.V.Length;
            // An orbit that is down there already needs no burn to be kept; the descent is simulated from it.
            if (PeriapsisAfter(at, mu, 0.0) <= radius) return true;
            double low = 0.0, high = 0.98 * speed;
            if (PeriapsisAfter(at, mu, high) > radius) return false;
            for (int i = 0; i < 60; i++)
            {
                double mid = 0.5 * (low + high);
                if (PeriapsisAfter(at, mu, mid) > radius) low = mid;
                else high = mid;
            }
            burn = direction * -(0.5 * (low + high));
            return true;
        }

        // The periapsis radius of the orbit after a retrograde burn of `dv`.
        private static double PeriapsisAfter(State at, double mu, double dv)
        {
            var conic = new Conic(new State(at.R, at.V - at.V.Unit * dv), mu);
            return conic.P / (1.0 + conic.E);
        }

        // Where the simulated descent from the orbit the burn leaves comes down; false where it does not land.
        private static bool Impact(Vessel vessel, CelestialBody body, State leaving, V3 burn, double ut, double terrain, out double latitude, out double longitude, out double landsAt)
        {
            latitude = longitude = landsAt = 0.0;
            V3 velocity = leaving.V + burn;
            // The orbit maths works in the world's axes; the game's orbit in its own, which has the second and third swapped.
            var start = new Orbit();
            start.UpdateFromStateVectors(new Vector3d(leaving.R.X, leaving.R.Z, leaving.R.Y), new Vector3d(velocity.X, velocity.Z, velocity.Y), body, ut);
            double thrust = Engines.Acceleration(vessel);
            double gravity = body.GeeASL * 9.81;
            double decelEnd = LandingPrediction.BrakeEnd(vessel, terrain);
            IDescentSpeedPolicy policy = LandingPrediction.Policy(vessel, terrain, decelEnd, gravity, thrust);
            ReentrySimulation.SimCurves curves = ReentrySimulation.SimCurves.Borrow(body);
            SimulatedVessel simulated = SimulatedVessel.Borrow(vessel, curves, ut, -1);
            ReentrySimulation simulation = ReentrySimulation.Borrow(start, ut, simulated, curves, policy, decelEnd, thrust, 3.0, terrain, false, 0.2, Time.fixedDeltaTime, 1.0, false);
            ReentrySimulation.Result result = null;
            try
            {
                result = simulation.RunSimulation();
                if (result.Outcome != ReentrySimulation.Outcome.LANDED) return false;
                latitude = result.EndPosition.Latitude;
                longitude = result.EndPosition.Longitude;
                landsAt = result.EndUT;
                return true;
            }
            catch (Exception e)
            {
                failure = "The descent could not be simulated: " + e.Message;
                return false;
            }
            finally
            {
                simulation.Release();
                if (result != null) result.Release();
            }
        }

        // The burn as a node of the vessel, and what the plan says of it.
        private static NodePlanner.Result Made(Frame frame, double ut, V3 burn, double miss, Burn? turn, double turnUT)
        {
            KspOrbits.ClearNodes(frame);
            if (turn.HasValue && KspOrbits.AddNode(frame, turnUT, turn.Value.DeltaV) == null) return NodePlanner.Result.Failed("The vessel has no flight plan.");
            if (KspOrbits.AddNode(frame, ut, burn) == null) return NodePlanner.Result.Failed("The vessel has no flight plan.");
            string near = miss < 1000.0 ? miss.ToString("0") + " m" : (miss / 1000.0).ToString("0.0") + " km";
            return new NodePlanner.Result
            {
                Ok = true, UT = turn.HasValue ? turnUT : ut, DeltaV = turn.HasValue ? turn.Value.DeltaV : burn, Error = miss,
                Why = (turn.HasValue ? "Turn the plane: " + turn.Value.Magnitude.ToString("0") + " m/s, then deorbit" : "Deorbit to the marker") + ": " + burn.Length.ToString("0")
                      + " m/s, the descent comes down " + near + " from the marker.",
            };
        }

        // The distance over the ground between two places, in m.
        private static double Distance(CelestialBody body, double latitudeA, double longitudeA, double latitudeB, double longitudeB)
        {
            double a = latitudeA * Math.PI / 180.0, b = latitudeB * Math.PI / 180.0;
            double dLat = b - a, dLon = Wrap(longitudeB - longitudeA) * Math.PI / 180.0;
            double h = Math.Sin(dLat / 2.0) * Math.Sin(dLat / 2.0) + Math.Cos(a) * Math.Cos(b) * Math.Sin(dLon / 2.0) * Math.Sin(dLon / 2.0);
            return 2.0 * body.Radius * Math.Asin(Math.Min(1.0, Math.Sqrt(h)));
        }

        // An angle in degrees brought to between -180 and 180.
        private static double Wrap(double degrees)
        {
            degrees %= 360.0;
            if (degrees > 180.0) degrees -= 360.0;
            if (degrees < -180.0) degrees += 360.0;
            return degrees;
        }
    }
}
