using System;
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Autopilot.Background
{
    // Where a vessel is set down and how it stands there.
    internal struct TouchdownSite
    {
        public double Latitude, Longitude;
        // The altitude of the vessel's reference point, in m over the datum.
        public double Height;
        // The ground's altitude at the place the descent met it, in m.
        public double Ground;
        // The slope of the ground under the vessel, in degrees, and how uneven it is: the larger of that and the angle its roughness makes.
        public double Tilt;
        public double Score;
        // How far the site lies from the point the descent met the ground, in m.
        public double Shift;
        public bool Water;
        // False where no place was found.
        public bool Found;
        // The vessel's rotation relative to the body's surface.
        public Quaternion Rotation;
    }

    // The ground a vessel is set down on without a landing flown. It is the flattest ground near the point where the descent meets it.
    // The vessel stands on the slope of the ground there, its lowest part over the highest ground under it.
    // The game turns a vessel saved on the ground to the slope as it loads it, and one it has to tilt far is pushed out of the ground.
    internal static class Touchdown
    {
        // How far from the point of the descent the flattest ground is looked for, and the spacing of the places looked at, in m.
        private const double SearchRadius = 1000.0;
        private const double SearchStep = 50.0;
        // How far from a place the ground is looked at to tell its slope and its roughness, in m at least.
        // The game's ground is a coarser mesh than the height the terrain is given by. Over rough ground the two differ by metres.
        private const double SlopeBaseline = 10.0;
        // A slope the game does not tilt a vessel for (Vessel.CheckGroundCollision turns it only past one degree): ground this flat is taken at once.
        private const double FlatDegrees = 1.0;

        // `lowest`: how far the vessel reaches below its reference point, `footprint`: how far it reaches to the side, both in m.
        internal static TouchdownSite Find(CelestialBody body, double latitude, double longitude, double lowest, double footprint, double clearance)
        {
            double centre = Ground(body, latitude, longitude);
            if (body.ocean && centre < 0.0)
            {
                // A tall vessel does not stand up on the water: it falls over, and what hits the water with its top breaks. It lies on the water,
                // its axis to the east, with its widest side down. A low one, a capsule, floats upright.
                double dLon = 1.0 / (body.Radius * Math.Max(0.01, Math.Cos(latitude * Math.PI / 180.0))) * 180.0 / Math.PI;
                Vector3 axis = lowest > 3.0 * Math.Max(1.0, footprint)
                    ? (Vector3)(body.GetWorldSurfacePosition(latitude, longitude + dLon, 0.0) - body.GetWorldSurfacePosition(latitude, longitude - dLon, 0.0)).normalized
                    : (Vector3)body.GetSurfaceNVector(latitude, longitude);
                bool flat = lowest > 3.0 * Math.Max(1.0, footprint);
                return new TouchdownSite
                {
                    Latitude = latitude, Longitude = longitude, Height = (flat ? Math.Max(1.0, footprint) : lowest) + clearance, Ground = centre, Water = true, Found = true,
                    Rotation = Quaternion.Inverse(body.bodyTransform.rotation) * Quaternion.FromToRotation(Vector3.up, axis),
                };
            }
            var offsets = new List<Vector2>();
            for (double east = -SearchRadius; east <= SearchRadius; east += SearchStep)
                for (double north = -SearchRadius; north <= SearchRadius; north += SearchStep)
                    if (east * east + north * north <= SearchRadius * SearchRadius) offsets.Add(new Vector2((float)east, (float)north));
            offsets.Sort((a, b) => a.sqrMagnitude.CompareTo(b.sqrMagnitude));
            bool found = false;
            TouchdownSite best = default(TouchdownSite);
            foreach (Vector2 offset in offsets)
            {
                double la = latitude + offset.y / body.Radius * 180.0 / Math.PI;
                double lo = longitude + offset.x / (body.Radius * Math.Max(0.01, Math.Cos(la * Math.PI / 180.0))) * 180.0 / Math.PI;
                if (body.ocean && Ground(body, la, lo) < 0.0) continue;
                TouchdownSite site = At(body, la, lo, lowest, Math.Max(1.0, footprint), clearance);
                site.Shift = offset.magnitude;
                site.Found = true;
                if (!found || site.Score < best.Score - 0.01)
                {
                    best = site;
                    found = true;
                }
                if (best.Score <= FlatDegrees) break;
            }
            best.Ground = centre;
            best.Found = found;
            return best;
        }

        // The vessel standing at the place. The slope comes from the ground at four points `footprint` away, and the vessel's up lies along the slope's normal.
        // Its reference point is over the ground by what the vessel reaches below it and by what the highest corner of its footprint stands over the slope.
        private static TouchdownSite At(CelestialBody body, double latitude, double longitude, double lowest, double footprint, double clearance)
        {
            double baseline = Math.Max(footprint, SlopeBaseline);
            double g0 = Ground(body, latitude, longitude);
            double gx, gy, roughness;
            Plane(body, latitude, longitude, g0, baseline, out gx, out gy, out roughness);
            // The highest ground under the vessel's footprint over the plane: the vessel stands on that.
            double dLat = footprint / body.Radius * 180.0 / Math.PI;
            double dLon = footprint / (body.Radius * Math.Max(0.01, Math.Cos(latitude * Math.PI / 180.0))) * 180.0 / Math.PI;
            double residual = 0.0;
            for (int corner = 0; corner < 4; corner++)
            {
                double sx = (corner & 1) == 0 ? -1.0 : 1.0, sy = (corner & 2) == 0 ? -1.0 : 1.0;
                double height = Ground(body, latitude + sy * dLat, longitude + sx * dLon);
                residual = Math.Max(residual, height - (g0 + gx * sx * footprint + gy * sy * footprint));
            }
            // The directions of east and north in the world at the place, from the positions a little to either side.
            double eLat = baseline / body.Radius * 180.0 / Math.PI;
            double eLon = baseline / (body.Radius * Math.Max(0.01, Math.Cos(latitude * Math.PI / 180.0))) * 180.0 / Math.PI;
            Vector3d at = body.GetWorldSurfacePosition(latitude, longitude, g0);
            Vector3d eastward = (body.GetWorldSurfacePosition(latitude, longitude + eLon, g0) - body.GetWorldSurfacePosition(latitude, longitude - eLon, g0)).normalized;
            Vector3d northward = (body.GetWorldSurfacePosition(latitude + eLat, longitude, g0) - body.GetWorldSurfacePosition(latitude - eLat, longitude, g0)).normalized;
            Vector3d normal = (body.GetSurfaceNVector(latitude, longitude) - gx * eastward - gy * northward).normalized;
            Vector3d reference = at + normal * (lowest + clearance + Math.Max(0.0, residual));
            double la, lo, height2;
            body.GetLatLonAlt(reference, out la, out lo, out height2);
            double tilt = Math.Atan(Math.Sqrt(gx * gx + gy * gy)) * 180.0 / Math.PI;
            return new TouchdownSite
            {
                Latitude = la, Longitude = lo, Height = height2, Ground = g0, Tilt = tilt, Score = Math.Max(tilt, Math.Atan(roughness / baseline) * 180.0 / Math.PI),
                Rotation = Quaternion.Inverse(body.bodyTransform.rotation) * Quaternion.FromToRotation(Vector3.up, (Vector3)normal),
            };
        }

        // The plane through the ground at the place, from the ground `baseline` away to each side: its slope to the east and to the north, and how far
        // the ground at the four corners lies from it.
        private static void Plane(CelestialBody body, double latitude, double longitude, double g0, double baseline, out double gx, out double gy, out double roughness)
        {
            double dLat = baseline / body.Radius * 180.0 / Math.PI;
            double dLon = baseline / (body.Radius * Math.Max(0.01, Math.Cos(latitude * Math.PI / 180.0))) * 180.0 / Math.PI;
            gx = (Ground(body, latitude, longitude + dLon) - Ground(body, latitude, longitude - dLon)) / (2.0 * baseline);
            gy = (Ground(body, latitude + dLat, longitude) - Ground(body, latitude - dLat, longitude)) / (2.0 * baseline);
            roughness = 0.0;
            for (int corner = 0; corner < 4; corner++)
            {
                double sx = (corner & 1) == 0 ? -1.0 : 1.0, sy = (corner & 2) == 0 ? -1.0 : 1.0;
                roughness = Math.Max(roughness, Math.Abs(Ground(body, latitude + sy * dLat, longitude + sx * dLon) - (g0 + gx * sx * baseline + gy * sy * baseline)));
            }
        }

        private static double Ground(CelestialBody body, double latitude, double longitude)
        {
            return body.TerrainAltitude(latitude, longitude, true);
        }
    }
}
