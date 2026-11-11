using System.Collections.Generic;
using ReDefinition.Planner.Autopilot.Planning;

namespace ReDefinition.Planner.Planning
{
    // The weather rows of the flight plan: the legs of a plan that pass through a body's air, as the storm check wants them (RouteLeg), and
    // the lines StormOnRoute.FindWithSeam gives for them. Without the atmospheres the seam is empty and there are no lines, so the plan
    // shows nothing of weather.
    internal static class StormOnPlan
    {
        // The lines for the plan, or an empty array.
        internal static string[] LinesOf(RoutePlan plan)
        {
            if (plan == null || plan.Legs.Count == 0) return new string[0];
            IList<RouteLeg> legs = LegsOf(plan);
            if (legs.Count == 0) return new string[0];
            StormOnRouteInfo info = StormOnRoute.FindWithSeam(legs, RadiusOf);
            if (info == null || info.Entries.Count == 0) return new string[0];
            return info.Lines(DateText);
        }

        // The legs that touch the air of a body with an atmosphere: the ones that pass into it, or begin where a pass through the air ended.
        // A leg in vacuum feels no weather, so it has no row.
        internal static IList<RouteLeg> LegsOf(RoutePlan plan)
        {
            var legs = new List<RouteLeg>();
            for (int i = 0; i < plan.Legs.Count; i++)
            {
                PlannedLeg planned = plan.Legs[i];
                Orbit orbit = planned.Orbit;
                if (orbit == null || orbit.referenceBody == null || planned.EndUT <= planned.StartUT) continue;
                CelestialBody body = orbit.referenceBody;
                if (!body.atmosphere) continue;
                if (!planned.Air && orbit.PeA >= body.atmosphereDepth) continue;
                double startLat, startLon, endLat, endLon;
                if (!GroundPoint(orbit, planned.StartUT, out startLat, out startLon) || !GroundPoint(orbit, planned.EndUT, out endLat, out endLon)) continue;
                legs.Add(new RouteLeg
                {
                    Number = i + 1, BodyIndex = body.flightGlobalsIndex, StartUt = planned.StartUT, EndUt = planned.EndUT,
                    StartLatDeg = startLat, StartLonDeg = startLon, EndLatDeg = endLat, EndLonDeg = endLon
                });
            }
            return legs;
        }

        // The point of the body's surface under the orbit at a time, in degrees. The direction is read with the body as it turns now and the
        // turn of the body between now and then is taken off the longitude. [VERIFY in the game: the sign of the turn against a landing site.]
        private static bool GroundPoint(Orbit orbit, double ut, out double latDeg, out double lonDeg)
        {
            latDeg = lonDeg = 0.0;
            CelestialBody body = orbit.referenceBody;
            Vector3d at = orbit.getPositionAtUT(ut);
            if (double.IsNaN(at.x) || double.IsNaN(at.y) || double.IsNaN(at.z)) return false;
            latDeg = body.GetLatitude(at);
            lonDeg = body.GetLongitude(at);
            if (body.rotates && body.rotationPeriod > 0.0)
                lonDeg -= 360.0 * (ut - Planetarium.GetUniversalTime()) / body.rotationPeriod;
            lonDeg = Geo.WrapLon(lonDeg);
            return !double.IsNaN(latDeg) && !double.IsNaN(lonDeg);
        }

        private static double RadiusOf(int bodyIndex)
        {
            List<CelestialBody> bodies = FlightGlobals.Bodies;
            return bodies != null && bodyIndex >= 0 && bodyIndex < bodies.Count ? bodies[bodyIndex].Radius : 0.0;
        }

        private static string DateText(double ut)
        {
            return KSPUtil.PrintDateCompact(ut, true);
        }
    }
}
