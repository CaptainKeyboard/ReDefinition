using System;

namespace ReDefinition.Seasons
{
    // Angles and directions on a planet, in PQS's own convention. The latitude is measured from the equator toward +y and the
    // longitude from the +z axis toward +x. The direction of a place is (cos lat sin lon, sin lat, cos lat cos lon). Pure numbers.
    internal static class Geo
    {
        internal static double ToRad(double degrees)
        {
            return degrees * Math.PI / 180;
        }

        internal static double ToDeg(double radians)
        {
            return radians * 180 / Math.PI;
        }

        // The unit direction from the planet's centre to a place; latitude and longitude in radians.
        internal static void DirectionOf(double latitudeRadians, double longitudeRadians, out double x, out double y, out double z)
        {
            double c = Math.Cos(latitudeRadians);
            x = c * Math.Sin(longitudeRadians);
            y = Math.Sin(latitudeRadians);
            z = c * Math.Cos(longitudeRadians);
        }

        // The latitude in degrees of a direction (north positive); y is held between -1 and 1.
        internal static double LatitudeOf(double y)
        {
            return ToDeg(Math.Asin(Mathx.Clamp(y, -1, 1)));
        }

        // The longitude in degrees of a direction, -180 to 180, from +z toward +x.
        internal static double LongitudeOf(double x, double z)
        {
            return ToDeg(Math.Atan2(x, z));
        }
    }
}
