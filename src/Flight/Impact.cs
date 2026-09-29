using UnityEngine;

namespace ReDefinition.Flight
{
    // When a vessel's path meets the ground: its orbit, followed from now to its
    // next periapsis, against the height of the ground under each point of it at
    // the time the vessel gets there (CelestialBody.TerrainAltitude, the sea's
    // surface over water, below the datum where there is no sea; the body turning
    // beneath: the longitude moves west by its turn since now). Drag is not counted: in air the vessel falls later than
    // this says. First in steps along the way, then by halving the step where the
    // height over the ground turns negative. NaN where it does not meet the ground:
    // on the ground already, or a periapsis above the highest ground there is.
    internal static class Impact
    {
        private const int Steps = 200;
        private const int Halvings = 16;

        // One search per vessel and quarter second, for the gauge and the panel
        // alike.
        private static Vessel cachedFor;
        private static float cachedAt = -1f;
        private static double cached = double.NaN;

        internal static double Seconds(Vessel vessel)
        {
            if (vessel == cachedFor && Time.unscaledTime - cachedAt < 0.2f) return cached;
            cachedFor = vessel;
            cachedAt = Time.unscaledTime;
            cached = Search(vessel);
            return cached;
        }

        private static double Search(Vessel vessel)
        {
            if (vessel == null || vessel.LandedOrSplashed || vessel.orbit == null || vessel.mainBody == null) return double.NaN;
            Orbit orbit = vessel.orbit;
            CelestialBody body = vessel.mainBody;
            double highest = body.pqsController != null ? body.pqsController.radiusMax - body.Radius : 0.0;
            if (orbit.PeA > highest || double.IsNaN(orbit.timeToPe) || orbit.timeToPe <= 0.0) return double.NaN;
            double now = Planetarium.GetUniversalTime();
            double end = orbit.timeToPe;
            double before = 0.0;
            if (Above(orbit, body, now, 0.0) <= 0.0) return double.NaN;
            for (int i = 1; i <= Steps; i++)
            {
                double at = end * i / Steps;
                if (Above(orbit, body, now, at) > 0.0)
                {
                    before = at;
                    continue;
                }
                double low = before, high = at;
                for (int h = 0; h < Halvings; h++)
                {
                    double middle = (low + high) / 2.0;
                    if (Above(orbit, body, now, middle) > 0.0) low = middle;
                    else high = middle;
                }
                return high;
            }
            return double.NaN;
        }

        // How high the vessel stands over the ground `after` seconds from `now`.
        private static double Above(Orbit orbit, CelestialBody body, double now, double after)
        {
            Vector3d relative = orbit.getRelativePositionAtUT(now + after).xzy;
            Vector3d world = body.position + relative;
            double latitude = body.GetLatitude(world);
            double longitude = body.GetLongitude(world);
            if (body.rotates && body.rotationPeriod > 0.0) longitude -= 360.0 * after / body.rotationPeriod;
            longitude = ((longitude % 360.0) + 540.0) % 360.0 - 180.0;
            // Below the datum only where the sea covers it: craters on airless bodies
            // lie lower than it.
            return relative.magnitude - body.Radius - body.TerrainAltitude(latitude, longitude, !body.ocean);
        }
    }
}
