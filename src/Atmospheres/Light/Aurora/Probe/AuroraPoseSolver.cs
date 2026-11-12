using System;

namespace ReDefinition.Atmospheres
{
    // The sun elevation a night pose of the series probe can ask for at a latitude, apart from the engine so that the unit tests run it.
    // At a latitude phi on a body whose rotation axis makes the angle declination with the sun's direction, the sun's elevation over a
    // full turn of the body runs from asin(sin phi sin dec - cos phi cos dec) to asin(sin phi sin dec + cos phi cos dec). A pose asks
    // for the elevation AskedDeg, or for the lowest the latitude reaches plus MarginDeg when that is higher; a latitude whose lowest
    // elevation is above NightMaxDeg has no night at the date and is skipped with its name instead of ending the series. The longitude
    // (the time of day on that body) that gives the elevation is solved by the probe's placement from the same formula.
    internal static class AuroraPoseSolver
    {
        // The elevation a night pose asks for (the sky is dark enough for the shell).
        internal const double AskedDeg = -12.0;
        // The distance kept from the lowest elevation a latitude reaches, so that the solved longitude is not at the edge.
        internal const double MarginDeg = 1.0;
        // A latitude whose sun stays above this all day has no night pose.
        internal const double NightMaxDeg = -6.0;

        internal struct Plan
        {
            internal double LatDeg, DeclinationDeg, LowestDeg, HighestDeg, WantedDeg;
            internal bool Skip;
        }

        // The lowest and the highest sun elevation over a turn of the body at the latitude (degrees).
        internal static void Range(double latDeg, double declinationDeg, out double lowestDeg, out double highestDeg)
        {
            double lat = latDeg * Math.PI / 180.0, dec = declinationDeg * Math.PI / 180.0;
            double s = Math.Sin(lat) * Math.Sin(dec), c = Math.Cos(lat) * Math.Cos(dec);
            lowestDeg = Math.Asin(Clamp(s - c)) * 180.0 / Math.PI;
            highestDeg = Math.Asin(Clamp(s + c)) * 180.0 / Math.PI;
        }

        // The elevation to ask for at the latitude, or the pose's skip.
        internal static Plan Solve(double latDeg, double declinationDeg)
        {
            double low, high;
            Range(latDeg, declinationDeg, out low, out high);
            double wanted = Math.Max(AskedDeg, low + MarginDeg);
            wanted = Math.Min(wanted, high);
            return new Plan
            {
                LatDeg = latDeg,
                DeclinationDeg = declinationDeg,
                LowestDeg = low,
                HighestDeg = high,
                WantedDeg = wanted,
                Skip = low + MarginDeg > NightMaxDeg
            };
        }

        // The declination of the sun at the body: the angle of the direction to the sun above the equator plane of the body.
        internal static double DeclinationDeg(double sunAlongAxis)
        {
            return Math.Asin(Clamp(sunAlongAxis)) * 180.0 / Math.PI;
        }

        // The cosine of the hour angle (the longitude from the sub-solar point) at which the sun stands at that elevation; outside -1..1
        // the elevation is not reached at the latitude.
        internal static double CosHourAngle(double latDeg, double declinationDeg, double elevationDeg)
        {
            double lat = latDeg * Math.PI / 180.0, dec = declinationDeg * Math.PI / 180.0;
            double c = Math.Cos(lat) * Math.Cos(dec);
            return c > 1e-9 ? (Math.Sin(elevationDeg * Math.PI / 180.0) - Math.Sin(lat) * Math.Sin(dec)) / c : 2.0;
        }

        private static double Clamp(double v)
        {
            return Math.Max(-1.0, Math.Min(1.0, v));
        }
    }
}
