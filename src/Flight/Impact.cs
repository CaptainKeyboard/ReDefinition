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
        private static readonly HeldVessel cachedFor = new HeldVessel();
        private static float cachedAt = -1f;
        private static double cached = double.NaN;

        // A search that met no ground up to the periapsis holds while the vessel
        // keeps to that path, until the periapsis: the stretch it looked at is
        // fixed in time, and so is the ground under it as the body turns. Orbits
        // under the highest ground's radius that never meet the ground (the Mun's
        // low orbits over its plains) would otherwise look at 201 heights every
        // 0.2 s and find none. The path counts as the same while the orbit puts the
        // vessel where it did halfway to the periapsis and at it, to within
        // ClearTolerance metres; a burn moves it, and the next search looks again.
        private const double ClearTolerance = 10.0;
        private static double clearUntil = double.NaN;
        private static double clearMiddle;
        private static Vector3d clearAtMiddle, clearAtEnd;

        internal static double Seconds(Vessel vessel)
        {
            if (cachedFor.Is(vessel) && Time.unscaledTime - cachedAt < 0.2f) return cached;
            if (!cachedFor.Is(vessel)) clearUntil = double.NaN;
            cachedFor.Set(vessel);
            cachedAt = Time.unscaledTime;
            cached = StillClear(vessel) ? double.NaN : Search(vessel);
            return cached;
        }

        private static bool StillClear(Vessel vessel)
        {
            if (double.IsNaN(clearUntil) || vessel == null || vessel.orbit == null || vessel.LandedOrSplashed) return false;
            if (Planetarium.GetUniversalTime() >= clearUntil) return false;
            Orbit orbit = vessel.orbit;
            return (orbit.getRelativePositionAtUT(clearMiddle) - clearAtMiddle).magnitude < ClearTolerance
                   && (orbit.getRelativePositionAtUT(clearUntil) - clearAtEnd).magnitude < ClearTolerance;
        }

        private static double Search(Vessel vessel)
        {
            clearUntil = double.NaN;
            if (vessel == null || vessel.LandedOrSplashed || vessel.orbit == null || vessel.mainBody == null) return double.NaN;
            Orbit orbit = vessel.orbit;
            CelestialBody body = vessel.mainBody;
            double highest = body.pqsController != null ? body.pqsController.radiusMax - body.Radius : 0.0;
            if (orbit.PeA > highest || double.IsNaN(orbit.timeToPe) || orbit.timeToPe <= 0.0) return double.NaN;
            double now = Planetarium.GetUniversalTime();
            double end = orbit.timeToPe;
            double before = 0.0;
            if (Above(orbit, body, now, 0.0, highest) <= 0.0) return double.NaN;
            for (int i = 1; i <= Steps; i++)
            {
                double at = end * i / Steps;
                if (Above(orbit, body, now, at, highest) > 0.0)
                {
                    before = at;
                    continue;
                }
                double low = before, high = at;
                for (int h = 0; h < Halvings; h++)
                {
                    double middle = (low + high) / 2.0;
                    if (Above(orbit, body, now, middle, highest) > 0.0) low = middle;
                    else high = middle;
                }
                return high;
            }
            clearUntil = now + end;
            clearMiddle = now + end / 2.0;
            clearAtMiddle = orbit.getRelativePositionAtUT(clearMiddle);
            clearAtEnd = orbit.getRelativePositionAtUT(clearUntil);
            return double.NaN;
        }

        // How high the vessel stands over the ground `after` seconds from `now`;
        // over the highest ground there is, its height over the datum, without
        // asking the terrain: on the way down from a high orbit most steps are.
        private static double Above(Orbit orbit, CelestialBody body, double now, double after, double highest)
        {
            Vector3d relative = orbit.getRelativePositionAtUT(now + after).xzy;
            double height = relative.magnitude - body.Radius;
            if (height > highest) return height;
            Vector3d world = body.position + relative;
            double latitude = body.GetLatitude(world);
            double longitude = body.GetLongitude(world);
            if (body.rotates && body.rotationPeriod > 0.0) longitude -= 360.0 * after / body.rotationPeriod;
            longitude = ((longitude % 360.0) + 540.0) % 360.0 - 180.0;
            // Below the datum only where the sea covers it: craters on airless bodies
            // lie lower than it.
            return height - body.TerrainAltitude(latitude, longitude, !body.ocean);
        }
    }
}
