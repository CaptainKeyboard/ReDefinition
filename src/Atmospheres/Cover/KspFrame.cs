using System;

namespace ReDefinition.Atmospheres
{
    // The way from a direction in the PQS sphere's frame to the latitude and longitude the game and the weather field use. The sphere's
    // longitude (Geo.LongitudeOf, from +z toward +x) is not the game's, and the map of the field is indexed by the game's. The frame is
    // measured once per body from three directions the game itself gives (CelestialBody.GetRelSurfaceNVector at latitude 0 longitude 0,
    // latitude 0 longitude 90 and latitude 90), so that it holds whatever the sphere's turn is, also for bodies of other mods. Pure
    // numbers.
    internal sealed class KspFrame
    {
        // The unit directions of (0, 0), (0, 90 east) and the north pole, in the sphere's frame.
        private readonly double ax, ay, az;
        private readonly double bx, by, bz;
        private readonly double px, py, pz;

        // The sphere's own convention (the identity of the calibration): used where the game's directions cannot be read.
        internal static readonly KspFrame Sphere = new KspFrame(0, 0, 1, 1, 0, 0, 0, 1, 0);

        internal KspFrame(double ax, double ay, double az, double bx, double by, double bz, double px, double py, double pz)
        {
            Normalise(ref ax, ref ay, ref az);
            Normalise(ref bx, ref by, ref bz);
            Normalise(ref px, ref py, ref pz);
            this.ax = ax; this.ay = ay; this.az = az;
            this.bx = bx; this.by = by; this.bz = bz;
            this.px = px; this.py = py; this.pz = pz;
        }

        private static void Normalise(ref double x, ref double y, ref double z)
        {
            double l = Math.Sqrt(x * x + y * y + z * z);
            if (l > 1e-9)
            {
                x /= l; y /= l; z /= l;
            }
        }

        // Whether the three directions are three different, nearly orthogonal directions (a frame that can be trusted).
        internal bool Valid
        {
            get
            {
                double ab = ax * bx + ay * by + az * bz, ap = ax * px + ay * py + az * pz, bp = bx * px + by * py + bz * pz;
                return Math.Abs(ab) < 0.05 && Math.Abs(ap) < 0.05 && Math.Abs(bp) < 0.05;
            }
        }

        // The latitude and longitude in degrees (longitude -180 to 180) of a direction in the sphere's frame.
        internal void LatLon(double x, double y, double z, out double latDeg, out double lonDeg)
        {
            double l = Math.Sqrt(x * x + y * y + z * z);
            if (l <= 0)
            {
                latDeg = 0;
                lonDeg = 0;
                return;
            }
            double up = (x * px + y * py + z * pz) / l;
            latDeg = Math.Asin(up > 1 ? 1 : up < -1 ? -1 : up) * 180.0 / Math.PI;
            double east = (x * bx + y * by + z * bz) / l;
            double zero = (x * ax + y * ay + z * az) / l;
            lonDeg = Math.Atan2(east, zero) * 180.0 / Math.PI;
        }

        // The direction in the sphere's frame of a latitude and longitude in degrees.
        internal void Direction(double latDeg, double lonDeg, out double x, out double y, out double z)
        {
            double la = latDeg * Math.PI / 180.0, lo = lonDeg * Math.PI / 180.0;
            double c = Math.Cos(la), s = Math.Sin(la);
            double e = c * Math.Sin(lo), n = c * Math.Cos(lo);
            x = n * ax + e * bx + s * px;
            y = n * ay + e * by + s * py;
            z = n * az + e * bz + s * pz;
        }
    }
}
