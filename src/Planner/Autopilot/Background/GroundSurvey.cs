using System;
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Autopilot.Background
{
    // The ground under a vessel that is about to be unpacked, measured with rays against the game's own ground, which is what the vessel will stand on.
    // The terrain's height function is not that ground: boulders and the coarser mesh lie metres off it. The vessel is loaded but still packed, held
    // from being unpacked while the ground is built under it; the rays are those of the game's own height from the ground (Vessel.GetHeightFromTerrain).
    internal static class GroundSurvey
    {
        // The layer of the ground and of the boulders on it, which KSP's rays from a vessel to the ground hit.
        private const int Ground = 1 << 15;
        // The rays start this far over the terrain's height and reach this far down, in m.
        private const double Above = 30.0, Reach = 80.0;
        // The places looked at round the one asked for, as multiples of the vessel's footprint, and the slope at which ground counts as flat, in degrees.
        private static readonly double[] Rings = { 0.5, 1.0, 2.0 };
        private const double FlatDegrees = 1.0;

        // The altitude of the ground at a place, where a ray hits it.
        internal static bool Hit(CelestialBody body, double latitude, double longitude, out double altitude)
        {
            altitude = 0.0;
            double terrain = body.TerrainAltitude(latitude, longitude, true);
            Vector3 from = (Vector3)body.GetWorldSurfacePosition(latitude, longitude, terrain + Above);
            Vector3 up = (Vector3)body.GetSurfaceNVector(latitude, longitude);
            RaycastHit hit;
            if (!Physics.Raycast(from, -up, out hit, (float)Reach, Ground, QueryTriggerInteraction.Ignore)) return false;
            altitude = body.GetAltitude(hit.point);
            return true;
        }

        // The vessel standing at the place, from a grid of rays over its footprint: the plane through the ground, its normal for the vessel's up, and its
        // reference point over the highest ground above the plane. False where a ray finds no ground yet.
        internal static bool Stand(CelestialBody body, double latitude, double longitude, double lowest, double footprint, double clearance, out TouchdownSite site)
        {
            site = default(TouchdownSite);
            double step = footprint / 2.0;
            double dLat = step / body.Radius * 180.0 / Math.PI;
            double dLon = step / (body.Radius * Math.Max(0.01, Math.Cos(latitude * Math.PI / 180.0))) * 180.0 / Math.PI;
            var heights = new double[5, 5];
            double sum = 0.0;
            for (int i = -2; i <= 2; i++)
                for (int j = -2; j <= 2; j++)
                {
                    double altitude;
                    if (!Hit(body, latitude + j * dLat, longitude + i * dLon, out altitude)) return false;
                    heights[i + 2, j + 2] = altitude;
                    sum += altitude;
                }
            double centre = sum / 25.0, gx = 0.0, gy = 0.0, norm = 0.0;
            for (int i = -2; i <= 2; i++)
                for (int j = -2; j <= 2; j++)
                {
                    gx += i * step * (heights[i + 2, j + 2] - centre);
                    gy += j * step * (heights[i + 2, j + 2] - centre);
                    norm += i * step * i * step;
                }
            gx /= norm;
            gy /= norm;
            double residual = 0.0, spread = 0.0;
            for (int i = -2; i <= 2; i++)
                for (int j = -2; j <= 2; j++)
                {
                    double off = heights[i + 2, j + 2] - (centre + gx * i * step + gy * j * step);
                    residual = Math.Max(residual, off);
                    spread = Math.Max(spread, Math.Abs(off));
                }
            Vector3d at = body.GetWorldSurfacePosition(latitude, longitude, centre);
            Vector3d east = (body.GetWorldSurfacePosition(latitude, longitude + dLon, centre) - body.GetWorldSurfacePosition(latitude, longitude - dLon, centre)).normalized;
            Vector3d north = (body.GetWorldSurfacePosition(latitude + dLat, longitude, centre) - body.GetWorldSurfacePosition(latitude - dLat, longitude, centre)).normalized;
            Vector3d normal = (body.GetSurfaceNVector(latitude, longitude) - gx * east - gy * north).normalized;
            double la, lo, height;
            body.GetLatLonAlt(at + normal * (lowest + clearance + residual), out la, out lo, out height);
            double tilt = Math.Atan(Math.Sqrt(gx * gx + gy * gy)) * 180.0 / Math.PI;
            site = new TouchdownSite
            {
                Latitude = la, Longitude = lo, Height = height, Ground = centre, Tilt = tilt, Score = Math.Max(tilt, Math.Atan(spread / footprint) * 180.0 / Math.PI), Found = true,
                Rotation = Quaternion.Inverse(body.bodyTransform.rotation) * Quaternion.FromToRotation(Vector3.up, (Vector3)normal),
            };
            return true;
        }

        // The flattest place at and near the one asked for, within a few lengths of the vessel: the places round it are looked at only where it is not flat.
        // False where the ground is not built under the places yet.
        internal static bool Choose(CelestialBody body, double latitude, double longitude, double lowest, double footprint, double clearance, out TouchdownSite site)
        {
            footprint = Math.Max(1.0, footprint);
            if (!Stand(body, latitude, longitude, lowest, footprint, clearance, out site)) return false;
            site.Shift = 0.0;
            TouchdownSite best = site;
            if (best.Score > FlatDegrees)
                foreach (double ring in Rings)
                    for (int direction = 0; direction < 8; direction++)
                    {
                        double angle = direction * Math.PI / 4.0, distance = ring * footprint * 2.0;
                        double la = latitude + distance * Math.Cos(angle) / body.Radius * 180.0 / Math.PI;
                        double lo = longitude + distance * Math.Sin(angle) / (body.Radius * Math.Max(0.01, Math.Cos(la * Math.PI / 180.0))) * 180.0 / Math.PI;
                        TouchdownSite other;
                        if (!Stand(body, la, lo, lowest, footprint, clearance, out other)) return false;
                        other.Shift = distance;
                        if (other.Score < best.Score - 0.01) best = other;
                    }
            site = best;
            return true;
        }
    }
}
