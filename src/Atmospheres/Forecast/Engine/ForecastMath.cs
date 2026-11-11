using System;

namespace ReDefinition.Atmospheres.Forecast
{
    // The small pure functions of the forecast: longitude wrap, great-circle distance, a flat offset on a body, and the hash that keys
    // tracks and the display-only uncertainty. No UnityEngine, any thread.
    internal static class ForecastMath
    {
        internal const double DegToRad = Math.PI / 180.0;
        internal const double RadToDeg = 180.0 / Math.PI;

        // A longitude in [-180, 180).
        internal static double WrapLon(double lonDeg)
        {
            double l = lonDeg % 360.0;
            if (l >= 180.0) l -= 360.0;
            if (l < -180.0) l += 360.0;
            return l;
        }

        // A latitude kept off the poles, where a longitude step has no length.
        internal static double ClampLat(double latDeg)
        {
            return latDeg < -89.9 ? -89.9 : latDeg > 89.9 ? 89.9 : latDeg;
        }

        // The great-circle distance in metres between two places on a body of that radius.
        internal static double DistanceM(double lat1Deg, double lon1Deg, double lat2Deg, double lon2Deg, double radiusM)
        {
            double p1 = lat1Deg * DegToRad, p2 = lat2Deg * DegToRad;
            double dp = p2 - p1;
            double dl = (lon2Deg - lon1Deg) * DegToRad;
            double s1 = Math.Sin(dp * 0.5), s2 = Math.Sin(dl * 0.5);
            double a = s1 * s1 + Math.Cos(p1) * Math.Cos(p2) * s2 * s2;
            if (a > 1.0) a = 1.0;
            return 2.0 * radiusM * Math.Asin(Math.Sqrt(a));
        }

        // The place a flat offset (east, north, metres) from a start lies at; good for offsets small against the radius.
        internal static void Offset(double latDeg, double lonDeg, double eastM, double northM, double radiusM, out double lat2Deg, out double lon2Deg)
        {
            double lat = ClampLat(latDeg + northM / radiusM * RadToDeg);
            double cos = Math.Cos(lat * DegToRad);
            if (cos < 0.01) cos = 0.01;
            lat2Deg = lat;
            lon2Deg = WrapLon(lonDeg + eastM / (radiusM * cos) * RadToDeg);
        }

        // Folds a value into a running 64-bit hash.
        internal static ulong Mix(ulong h, ulong v)
        {
            unchecked
            {
                h ^= v + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2);
                h *= 0xBF58476D1CE4E5B9UL;
                h ^= h >> 31;
                return h;
            }
        }

        internal static ulong Mix(ulong h, long v)
        {
            return Mix(h, unchecked((ulong)v));
        }

        internal static ulong Mix(ulong h, double v)
        {
            return Mix(h, unchecked((ulong)BitConverter.DoubleToInt64Bits(v)));
        }

        // A number from -1 to 1 from a hash.
        internal static double Unit(ulong h)
        {
            unchecked
            {
                ulong x = h;
                x ^= x >> 33;
                x *= 0xFF51AFD7ED558CCDUL;
                x ^= x >> 33;
                return (x >> 11) * (2.0 / 9007199254740992.0) - 1.0;
            }
        }

        // The index of the step a time falls in (floor of ut / step).
        internal static long StepIndex(double ut, double stepSeconds)
        {
            return (long)Math.Floor(ut / stepSeconds);
        }
    }
}
