using System;
using ReDefinition.Planner.Autopilot.Guidance;
using ReDefinition.Core;
using ReDefinition.Planner.Landing;
using ReDefinition.Planner.Orbits;
using UnityEngine;

namespace ReDefinition.Planner.Autopilot.Planning
{
    // The deorbit burn that brings the vessel down on a chosen point of the ground. The burn is the one of the untargeted landing, retrograde, to a
    // periapsis under the ground. The time of it is what is chosen. The descent is simulated from the orbit a burn leaves, the way MechJeb predicts it,
    // with the air and the braking against the ground in it. The time is searched where the simulated descent comes down nearest the point: a burn
    // a little later moves that place by the way the vessel flies, so the search is along one line. What the orbit's plane does not reach is left as
    // a miss across the track, which the plan says.
    internal static class DeorbitAim
    {
        // The search that is under way, and what it was started for. It runs over the frames, so a plan asked again meets it where it was.
        private static DeorbitSearch search;
        private static string searchKey;
        private static Vessel searchFor;

        // The search is given up: nobody asks for it any more.
        internal static void Abandon()
        {
            if (search != null && !search.Done) search.Cancel();
            search = null;
            searchKey = null;
            searchFor = null;
        }

        // The plan of the deorbit burn. Where `responsive`, the search runs in the background and the plan says it is still searching until it is done, and
        // is asked again; else the plan waits for the search.
        internal static NodePlanner.Result Plan(Frame frame, double latitude, double longitude, bool responsive)
        {
            Vessel vessel = frame.Vessel;
            CelestialBody body = frame.Body;
            Orbit orbit = frame.Orbit;
            if (orbit == null || orbit.eccentricity >= 1.0) return NodePlanner.Result.Failed("The orbit is not closed.");
            // Without air the descent is braked by the engines, and weak ones cannot do that; with air the chutes may.
            if (!body.atmosphere && Engines.Acceleration(vessel) <= body.GeeASL * 9.81) return NodePlanner.Result.Failed("The engines are too weak to land on " + body.bodyName + ".");
            DeorbitSearch running;
            if (!responsive)
            {
                // A plan that waits has its own search: the one running in the background is for the vessel's own orbit, which this one may not be.
                running = new DeorbitSearch(frame, latitude, longitude);
                running.Complete();
                return Answer(frame, running, false, latitude, longitude);
            }
            string key = vessel.id + "|" + latitude + "|" + longitude + "|" + Math.Round(orbit.PeA / 50.0) + "|" + Math.Round(orbit.ApA / 50.0) + "|" + Math.Round(orbit.inclination, 1)
                         + "|" + vessel.parts.Count;
            if (search == null || searchKey != key || !ReferenceEquals(searchFor, vessel))
            {
                if (search != null) search.Cancel();
                search = new DeorbitSearch(frame, latitude, longitude);
                searchKey = key;
                searchFor = vessel;
            }
            running = search;
            running.Advance();
            if (running.Done) Abandon();
            return Answer(frame, running, true, latitude, longitude);
        }

        // The plan from a search: still searching, the reason there is no burn, or the burn as a node.
        private static NodePlanner.Result Answer(Frame frame, DeorbitSearch finished, bool responsive, double latitude, double longitude)
        {
            if (!finished.Done) return NodePlanner.Result.Searching(finished.Progress);
            if (!finished.Found) return NodePlanner.Result.Failed(finished.Failure);
            DeorbitSearch.Best best = finished.Choice;
            // The search ran over some time. A burn that is not ahead any more is searched anew.
            double first = best.Turned ? best.Now : best.Now + best.Delay;
            if (responsive && first < frame.Now + 1.0) return Plan(frame, latitude, longitude, true);
            return Made(frame, best.Now + best.Delay, best.Burn, best.Miss, best.Turned ? (Burn?)best.Turn : null, best.Now);
        }

        // How near the place counts as reached, in m: a share of the body's size, so that it is the same fraction of the way round on every body, and not
        // less than the width of a landing site.
        internal static double Tolerance(CelestialBody body)
        {
            return Math.Max(200.0, 0.0025 * body.Radius);
        }

        // The retrograde burn that leaves a periapsis at `radius`: the change of velocity, against the velocity. False where there is none.
        internal static bool Retrograde(State at, double mu, double radius, out V3 burn)
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
        internal static double PeriapsisAfter(State at, double mu, double dv)
        {
            var conic = new Conic(new State(at.R, at.V - at.V.Unit * dv), mu);
            return conic.P / (1.0 + conic.E);
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
                Ok = true, UT = turn.HasValue ? turnUT : ut, DeltaV = turn.HasValue ? turn.Value.DeltaV : burn, Error = miss, Purpose = NodePlanner.BurnPurpose.Deorbit,
                Why = (turn.HasValue ? "Turn the plane: " + turn.Value.Magnitude.ToString("0") + " m/s, then deorbit" : "Deorbit to the marker") + ": " + burn.Length.ToString("0")
                      + " m/s, the descent comes down " + near + " from the marker.",
            };
        }

        internal static double Distance(double radius, double latitudeA, double longitudeA, double latitudeB, double longitudeB)
        {
            double a = latitudeA * Math.PI / 180.0, b = latitudeB * Math.PI / 180.0;
            double dLat = b - a, dLon = Wrap(longitudeB - longitudeA) * Math.PI / 180.0;
            double h = Math.Sin(dLat / 2.0) * Math.Sin(dLat / 2.0) + Math.Cos(a) * Math.Cos(b) * Math.Sin(dLon / 2.0) * Math.Sin(dLon / 2.0);
            return 2.0 * radius * Math.Asin(Math.Min(1.0, Math.Sqrt(h)));
        }

        // An angle in degrees brought to between -180 and 180.
        internal static double Wrap(double degrees)
        {
            degrees %= 360.0;
            if (degrees > 180.0) degrees -= 360.0;
            if (degrees < -180.0) degrees += 360.0;
            return degrees;
        }
    }
}
