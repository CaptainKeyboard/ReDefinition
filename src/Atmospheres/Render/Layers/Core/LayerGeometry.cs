using System;

namespace ReDefinition.Atmospheres
{
    // A stretch of a ray, in metres from its origin, through one layer.
    internal struct LayerSpan
    {
        public double T0, T1;
        public int Layer;

        public double Length { get { return T1 - T0; } }
    }

    // The geometry of a planet-wide shell, pure and in double precision (the shader mirrors it in float): where a ray meets the two
    // spheres of a layer, the stretches it spends inside the layer from below, inside and above (P01, P25), the gas-giant case without
    // a surface (P24), and the distance to the horizon of a deck. The planet's centre is the origin.
    internal static class LayerGeometry
    {
        // The distance along the unit ray o + t d to the sphere of radius r, nearest and farthest root; false where it misses.
        internal static bool HitSphere(double ox, double oy, double oz, double dx, double dy, double dz, double r, out double t0, out double t1)
        {
            // |o + t d|^2 = r^2 with the difference of the squares written as (|o| - r)(|o| + r): the small number stays exact.
            double b = ox * dx + oy * dy + oz * dz;
            double len = Math.Sqrt(ox * ox + oy * oy + oz * oz);
            double c = (len - r) * (len + r);
            double disc = b * b - c;
            if (disc <= 0.0)
            {
                t0 = t1 = 0.0;
                return false;
            }
            double s = Math.Sqrt(disc);
            t0 = -b - s;
            t1 = -b + s;
            return true;
        }

        // The stretches of the ray [0, tMax] inside the shell between two altitudes (radius + altitude). Up to two: the near stretch and,
        // for a ray that enters the hollow inner sphere, the far wall on its other side. A body with a surface passes tMax = the ground
        // hit, so the far wall drops out; a body without one passes the ray's full length (P24).
        internal static int ShellSpans(double ox, double oy, double oz, double dx, double dy, double dz, double radius, double innerAltitude,
                                       double outerAltitude, double tMax, out LayerSpan first, out LayerSpan second)
        {
            first = default(LayerSpan);
            second = default(LayerSpan);
            double o0, o1;
            if (!HitSphere(ox, oy, oz, dx, dy, dz, radius + outerAltitude, out o0, out o1)) return 0;
            double lo = Math.Max(o0, 0.0), hi = Math.Min(o1, tMax);
            if (!(hi > lo)) return 0;
            double i0, i1;
            if (innerAltitude <= 0.0 || !HitSphere(ox, oy, oz, dx, dy, dz, radius + innerAltitude, out i0, out i1))
            {
                first.T0 = lo;
                first.T1 = hi;
                return 1;
            }
            // The shell is the outer sphere's stretch minus the inner sphere's: the part before the inner sphere, and the part after it.
            int count = 0;
            double aEnd = Math.Min(i0, hi);
            if (aEnd > lo)
            {
                first.T0 = lo;
                first.T1 = aEnd;
                count = 1;
            }
            double bStart = Math.Max(i1, lo);
            if (hi > bStart)
            {
                if (count == 0)
                {
                    first.T0 = bStart;
                    first.T1 = hi;
                }
                else
                {
                    second.T0 = bStart;
                    second.T1 = hi;
                }
                count++;
            }
            return count;
        }

        // The height of a point above sea level.
        internal static double AltitudeOf(double x, double y, double z, double radius)
        {
            return Math.Sqrt(x * x + y * y + z * z) - radius;
        }

        // The distance at which a ray along the ground meets a deck of altitude h seen from a camera at altitude 0: sqrt(2 R h + h^2).
        internal static double HorizonDistance(double radius, double deckAltitude)
        {
            return Math.Sqrt(2.0 * radius * deckAltitude + deckAltitude * deckAltitude);
        }

        // The angle below the local horizontal at which the horizon of a camera at altitude h lies, radians: acos(R / (R + h)).
        internal static double DipAngle(double radius, double cameraAltitude)
        {
            return Math.Acos(radius / (radius + cameraAltitude));
        }

        // The latitude and longitude (degrees) of a point of the map frame (x towards longitude 0, y towards 90 east, z north).
        internal static void LatLon(double x, double y, double z, out double latDeg, out double lonDeg)
        {
            double len = Math.Sqrt(x * x + y * y + z * z);
            latDeg = len > 0.0 ? Math.Asin(Math.Max(-1.0, Math.Min(1.0, z / len))) * 180.0 / Math.PI : 0.0;
            lonDeg = Math.Atan2(y, x) * 180.0 / Math.PI;
        }
    }
}
