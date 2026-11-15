using System;

namespace ReDefinition.Atmospheres
{
    // The forced map as ONE cell (E40 SOLO): the solo mode of the cloud pass puts a single cumulonimbus cell into an otherwise empty layer so that the anvil rule (AnvilMap) has a tower to
    // spread from. A constant map (cloud.mapOverride.<name>) is one source the size of the planet; a cell is a bump of the map. The shape is the one of the player's cumulonimbus rows
    // (tools/weather_metrics/e40ty.py cb_cell_files): a Gaussian of the great-circle distance to the cell's centre with an e-folding radius, lifted by CoreGain and clipped, so that its
    // core is flat (the texels of the map are 7 km at Kerbin's equator) and falls to nothing outside. The value at the centre is the cell's strength (the override's value).
    internal static class CloudSoloCell
    {
        // The Gaussian is multiplied by this and clipped at 1: the map reaches the cell's strength where the Gaussian is above 1 / CoreGain (the player's CB_CORE_GAIN).
        internal const double CoreGain = 1.4;

        // The map value (0 to 1) at a texel's centre: peak x min(1, CoreGain x exp(-(d / cellKm)^2)), d the great-circle distance in km to the centre.
        internal static double ValueAt(double peak, double distanceKm, double cellKm)
        {
            if (!(cellKm > 0.0)) return peak;
            double x = distanceKm / cellKm;
            return Math.Max(0.0, Math.Min(1.0, peak)) * Math.Min(1.0, CoreGain * Math.Exp(-x * x));
        }

        // The distance in km along the sphere of a radius (m) between two places (degrees).
        internal static double DistanceKm(double lat1Deg, double lon1Deg, double lat2Deg, double lon2Deg, double radiusM)
        {
            double p1 = lat1Deg * Math.PI / 180.0, p2 = lat2Deg * Math.PI / 180.0, dp = p2 - p1, dl = (lon2Deg - lon1Deg) * Math.PI / 180.0;
            double a = Math.Sin(dp / 2.0) * Math.Sin(dp / 2.0) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2.0) * Math.Sin(dl / 2.0);
            return 2.0 * radiusM * Math.Asin(Math.Min(1.0, Math.Sqrt(a))) / 1000.0;
        }

        // Fills a width x height map (row 0 at the north pole, column 0 at longitude -180, texel centres, as LayerMapStore holds them) with the cell: bytes and the same values as floats
        // (either may be null). A cellKm of 0 or less fills the constant peak (the forced map as it was).
        internal static void Fill(byte[] bytes, float[] values, int width, int height, double radiusM, double centreLatDeg, double centreLonDeg, double cellKm, double peak)
        {
            if (width < 1 || height < 1) return;
            for (int y = 0; y < height; y++)
            {
                double lat = (0.5 - (y + 0.5) / height) * 180.0;
                for (int x = 0; x < width; x++)
                {
                    double v = peak;
                    if (cellKm > 0.0)
                    {
                        double lon = ((x + 0.5) / width - 0.5) * 360.0;
                        v = ValueAt(peak, DistanceKm(lat, lon, centreLatDeg, centreLonDeg, radiusM), cellKm);
                    }
                    byte b = (byte)Math.Min(255.0, Math.Max(0.0, Math.Round(v * 255.0, MidpointRounding.ToEven)));
                    int i = y * width + x;
                    if (bytes != null) bytes[i] = b;
                    if (values != null) values[i] = b / 255f;
                }
            }
        }
    }
}
