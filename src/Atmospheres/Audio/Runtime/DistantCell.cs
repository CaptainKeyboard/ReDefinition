using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres.Audio
{
    // The nearest weather system as the distant storm sound hears it.
    internal struct CellPlace
    {
        internal bool Found;
        // Distance from the listener to the system's centre and to its edge, in metres, along the ground.
        internal double CentreDistanceM;
        internal double EdgeDistanceM;
        // The centre of the system.
        internal double LatDeg;
        internal double LonDeg;
        internal float Strength01;
    }

    // The distance curve and the direction of the distant storm (key audioDistantStorm, default off). The rumble comes from the nearest
    // system the forecast engine tracks (WeatherHooks.BodyTracks): silent at 30 km from its edge, full at 3 km, in between a straight line
    // in the logarithm of the distance, so that a halving of the distance is the same step everywhere. Pure.
    internal static class DistantCellModel
    {
        internal const double FarM = 30000.0;
        internal const double NearM = 3000.0;
        // A system weaker than this is not heard.
        internal const float MinStrength = 0.15f;

        // 0 at FarM and beyond, 1 at NearM and closer, strictly rising between.
        internal static float Closeness(double edgeDistanceM)
        {
            if (!(edgeDistanceM < FarM)) return 0f;
            if (edgeDistanceM <= NearM) return 1f;
            double t = (Math.Log(FarM) - Math.Log(edgeDistanceM)) / (Math.Log(FarM) - Math.Log(NearM));
            return (float)t;
        }

        // The left-right position from -1 (left) to 1 (right) of a source in the direction toCell, for a listener whose right-hand side is right
        // and whose up is up: the part of the direction along the ground, projected on the right-hand axis.
        internal static float Pan(Vector3 toCell, Vector3 right, Vector3 up)
        {
            Vector3 u = up.normalized;
            Vector3 flat = toCell - u * Vector3.Dot(toCell, u);
            Vector3 r = right - u * Vector3.Dot(right, u);
            if (flat.sqrMagnitude < 1e-12f || r.sqrMagnitude < 1e-12f) return 0f;
            float pan = Vector3.Dot(flat.normalized, r.normalized);
            return pan < -1f ? -1f : pan > 1f ? 1f : pan;
        }

        // The great-circle distance in metres on a sphere of the body's radius.
        internal static double DistanceM(double lat1Deg, double lon1Deg, double lat2Deg, double lon2Deg, double radiusM)
        {
            double p1 = lat1Deg * Math.PI / 180.0, p2 = lat2Deg * Math.PI / 180.0;
            double dp = p2 - p1;
            double dl = (lon2Deg - lon1Deg) * Math.PI / 180.0;
            double a = Math.Sin(dp * 0.5) * Math.Sin(dp * 0.5) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl * 0.5) * Math.Sin(dl * 0.5);
            a = a < 0.0 ? 0.0 : a > 1.0 ? 1.0 : a;
            return 2.0 * radiusM * Math.Asin(Math.Sqrt(a));
        }

        // The place of a track at a time: linear between the samples, the nearest sample beyond the ends; false for a track without samples.
        internal static bool TrackAt(ForecastTrack track, double ut, out double latDeg, out double lonDeg, out double radiusM, out float strength)
        {
            latDeg = lonDeg = radiusM = 0.0;
            strength = 0f;
            if (track == null || track.Ut == null || track.Ut.Length == 0 || track.Lat.Length != track.Ut.Length || track.Lon.Length != track.Ut.Length) return false;
            int n = track.Ut.Length;
            int hi = 0;
            while (hi < n && track.Ut[hi] < ut) hi++;
            int lo = hi - 1;
            if (hi >= n) lo = hi = n - 1;
            else if (lo < 0) lo = hi = 0;
            double t = lo == hi ? 0.0 : (ut - track.Ut[lo]) / (track.Ut[hi] - track.Ut[lo]);
            latDeg = track.Lat[lo] + (track.Lat[hi] - track.Lat[lo]) * t;
            double dl = track.Lon[hi] - track.Lon[lo];
            dl = (dl + 540.0) % 360.0 - 180.0;
            lonDeg = track.Lon[lo] + dl * t;
            radiusM = track.RadiusM != null && track.RadiusM.Length == n ? track.RadiusM[lo] + (track.RadiusM[hi] - track.RadiusM[lo]) * t : 0.0;
            strength = track.Strength01 != null && track.Strength01.Length == n ? (float)(track.Strength01[lo] + (track.Strength01[hi] - track.Strength01[lo]) * t) : 1f;
            return true;
        }

        // The nearest audible system among the tracks, by the distance to its edge.
        internal static CellPlace Nearest(ForecastTrack[] tracks, double ut, double listenerLatDeg, double listenerLonDeg, double bodyRadiusM)
        {
            var best = new CellPlace();
            if (tracks == null) return best;
            double bestEdge = double.MaxValue;
            for (int i = 0; i < tracks.Length; i++)
            {
                double lat, lon, radius;
                float strength;
                if (!TrackAt(tracks[i], ut, out lat, out lon, out radius, out strength) || strength < MinStrength) continue;
                double centre = DistanceM(listenerLatDeg, listenerLonDeg, lat, lon, bodyRadiusM);
                double edge = Math.Max(0.0, centre - radius);
                if (edge >= bestEdge) continue;
                bestEdge = edge;
                best.Found = true;
                best.CentreDistanceM = centre;
                best.EdgeDistanceM = edge;
                best.LatDeg = lat;
                best.LonDeg = lon;
                best.Strength01 = strength;
            }
            return best;
        }
    }
}
