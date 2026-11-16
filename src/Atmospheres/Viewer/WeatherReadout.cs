using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ReDefinition.Atmospheres.Viewer
{
    // What the viewer reads out of the weather the pass is given: the number of convective (Cb) cells and anvil sources in the field, their density per million square kilometres against
    // a stated real range, and the cover and type shares of each layer. Pure arithmetic and text; the game side (WeatherLab) feeds it.
    internal static class WeatherReadout
    {
        // The real range the density is shown against: about 1800 thunderstorms are active on Earth at any time (5.1e8 km2), 3.5 per million km2 (the plan's note
        // atmospheres/E40-cb-generator-result.md, an estimate not yet checked against a primary source); the band shown around it is 2 to 5.
        internal const double RealLow = 2.0, RealHigh = 5.0, RealMid = 3.5;
        internal const string RealSource = "Earth: about 1800 thunderstorms at a time, 3.5 per million km2 (plan note E40-cb-generator-result, estimate, not verified against a primary source)";

        // The area of a sphere of that radius in million km2.
        internal static double AreaMillionKm2(double radiusM)
        {
            double km = radiusM / 1000.0;
            return 4.0 * Math.PI * km * km / 1.0e6;
        }

        // A count per million km2 of a body of that radius.
        internal static double PerMillionKm2(int count, double radiusM)
        {
            double area = AreaMillionKm2(radiusM);
            return area > 0.0 ? count / area : double.NaN;
        }

        // 'below', 'within' or 'above' the real band; 'unknown' for a number that is not finite.
        internal static string AgainstReal(double perMillionKm2)
        {
            if (double.IsNaN(perMillionKm2) || double.IsInfinity(perMillionKm2)) return "unknown";
            if (perMillionKm2 < RealLow) return "below";
            if (perMillionKm2 > RealHigh) return "above";
            return "within";
        }

        // How many times the real middle value a density is (for example 100 for a sky with a hundred times Earth's density).
        internal static double TimesReal(double perMillionKm2)
        {
            return perMillionKm2 / RealMid;
        }

        internal static string DensityLine(string what, int count, double radiusM)
        {
            double density = PerMillionKm2(count, radiusM);
            if (double.IsNaN(density)) return what + " " + count + ", density unknown";
            return what + " " + count + ", " + density.ToString("0.0", CultureInfo.InvariantCulture) + " per million km2, " + AgainstReal(density) + " the real band "
                   + RealLow.ToString("0", CultureInfo.InvariantCulture) + " to " + RealHigh.ToString("0", CultureInfo.InvariantCulture)
                   + " (" + TimesReal(density).ToString("0.0", CultureInfo.InvariantCulture) + " x " + RealMid.ToString("0.0", CultureInfo.InvariantCulture) + ")";
        }

        // The area-weighted cover and type shares of a map sampled on a latitude/longitude grid. sample(latDeg, lonDeg, out cover, out type) gives the layer's map values (cover 0 to 1,
        // type 0 to 1 along its types); a texel counts as covered where its cover is at least coveredAt. typeCount is the number of the layer's types (the type value picks the nearest).
        internal sealed class LayerShares
        {
            internal double MeanCover, CoveredShare;
            // The share of the covered area per type, in the order of the layer's types.
            internal double[] TypeShare;
        }

        internal delegate void MapSampler(double latDeg, double lonDeg, out float cover, out float type);

        internal static LayerShares Shares(MapSampler sample, int typeCount, int columns = 144, int rows = 72, float coveredAt = 0.5f)
        {
            var result = new LayerShares { TypeShare = new double[Math.Max(1, typeCount)] };
            double weight = 0.0, cover = 0.0, covered = 0.0;
            for (int j = 0; j < rows; j++)
            {
                double lat = 90.0 - (j + 0.5) * 180.0 / rows;
                double w = Math.Cos(lat * Math.PI / 180.0);
                for (int i = 0; i < columns; i++)
                {
                    double lon = -180.0 + (i + 0.5) * 360.0 / columns;
                    float c, t;
                    sample(lat, lon, out c, out t);
                    weight += w;
                    cover += w * c;
                    if (c < coveredAt) continue;
                    covered += w;
                    int type = (int)Math.Round(Math.Max(0f, Math.Min(1f, t)) * (result.TypeShare.Length - 1));
                    result.TypeShare[type] += w;
                }
            }
            if (weight > 0.0)
            {
                result.MeanCover = cover / weight;
                result.CoveredShare = covered / weight;
            }
            if (covered > 0.0) for (int k = 0; k < result.TypeShare.Length; k++) result.TypeShare[k] /= covered;
            return result;
        }

        internal static string SharesLine(string layer, IList<string> typeNames, LayerShares s)
        {
            var text = new StringBuilder();
            text.Append(layer).Append(": cover ").Append((100.0 * s.MeanCover).ToString("0", CultureInfo.InvariantCulture)).Append(" %, covered ")
                .Append((100.0 * s.CoveredShare).ToString("0", CultureInfo.InvariantCulture)).Append(" %");
            if (s.CoveredShare > 0.0)
            {
                text.Append("; types");
                for (int k = 0; k < s.TypeShare.Length; k++)
                    if (s.TypeShare[k] >= 0.005)
                        text.Append(' ').Append(typeNames != null && k < typeNames.Count ? typeNames[k] : "type" + k).Append(' ').Append((100.0 * s.TypeShare[k]).ToString("0", CultureInfo.InvariantCulture)).Append(" %");
            }
            return text.ToString();
        }
    }
}
