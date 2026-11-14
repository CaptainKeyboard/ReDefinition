using System;

namespace ReDefinition.Atmospheres
{
    // The soft water content of a cloud (E40 soft density): ONE mapping from the layer's map value, the shape noise's rank, the softness and the height in the cloud
    // to the density. The layer shader needs no code for it: its share saturate((map + (n - 0.5) amount + coverage - 1) / edge + 0.5), eroded, to the layer's
    // water power, times the type's profile IS this mapping once the layer's numbers are the ones of Coverage below (noise amount 1, edge = Width), nothing
    // hardens the edge and the type's profile carries the rise. CloudLayerDef's Effective* numbers are those numbers; CloudWater itself is called by them, by the
    // weather player's experiment (SoftDensityScene) and by the tests.
    //
    //   cc      = clamp(c + shift)                         the map value: the share of the sky that is closed looking up (the cover meaning)
    //   t       = 1 - min(cc, 2 cc - closed width)         the outline in noise rank: the cloud is closed where n >= t (the noise volume is ranked, so
    //                                                      P(n >= t) = cc); the second term makes a map value of 0 a clear sky (Threshold)
    //   u       = clamp((n - t) / width + closed)          the water content: it rises over `width` of noise rank; at the outline it is `closed`, the content at
    //                                                      which a vertical column of the cloud type has the optical depth 0.92; below the outline the cloud thins out
    //   e       = erosion (1 - detail)                     the detail eats into the water that is not full
    //   u'      = u >= 1 ? 1 : clamp((u - e) / (1 - e))
    //   density = sigma u'^power profile(h) rise(h)        rise(h) = baseGain + (1 - baseGain) clamp(h / riseShare): the water content grows with the height above
    //                                                      the base (h: 0 at the cloud's base, 1 at its top)
    internal struct CloudWaterNumbers
    {
        // The width of the rise in noise rank, the exponent, the gain at the base and the share of the cloud's height over which the gain rises to 1 (0: no rise).
        internal double Width, Power, BaseGain, RiseShare;

        internal static CloudWaterNumbers Default
        {
            get { return new CloudWaterNumbers { Width = 0.5, Power = 2.0, BaseGain = 0.3, RiseShare = 0.5 }; }
        }
    }

    internal static class CloudWater
    {
        // The optical depth of a vertical column from which it counts as closed (CloudCoverOracle's and the cover meaning's).
        internal const double ClosedTau = 0.92;

        private static double Sat(double x)
        {
            return x <= 0.0 ? 0.0 : x >= 1.0 ? 1.0 : x;
        }

        // The outline in noise rank for a map value and the user's cover shift: 1 - cc where the thin water below the outline has room (cc >= closed x Width).
        // Below that the outline moves up so that the water begins at the rank 1 - 2 cc: a map value of 0 is a clear sky (without this the thin water below the
        // outline would cover closed x Width of the sky at a map value of 0), and a small map value gives thin clouds over twice its share of the sky, of which
        // 2 cc - closed x Width is closed.
        internal static double Threshold(double mapValue, double shift, double closed, CloudWaterNumbers k)
        {
            double cc = Sat(mapValue + shift);
            return 1.0 - Math.Min(cc, 2.0 * cc - closed * Math.Max(k.Width, 1e-3));
        }

        // The water content (0 to 1) before the erosion.
        internal static double Content(double noiseRank, double threshold, double closed, CloudWaterNumbers k)
        {
            return Sat((noiseRank - threshold) / Math.Max(k.Width, 1e-3) + closed);
        }

        // True where no noise value can make water.
        internal static bool Empty(double threshold, double closed, CloudWaterNumbers k)
        {
            return threshold - closed * Math.Max(k.Width, 1e-3) >= 1.0 - 1e-9;
        }

        // The water content after the detail has eaten into it (the erosion remap of package T02 without its hardening; full water content stays full).
        internal static double Erode(double content, double detail, double erosion)
        {
            if (content >= 1.0) return 1.0;
            double e = erosion * (1.0 - detail);
            return Sat((content - e) / Math.Max(1.0 - e, 1e-3));
        }

        // The share as the density takes it: to the layer's exponent. The one helper every reader (the shader's pow, the CPU twins, the shadow) calls.
        internal static double Pow(double share, double power)
        {
            if (share <= 0.0) return 0.0;
            return power == 1.0 ? share : Math.Pow(share, power);
        }

        // The gain of the density by the height in the cloud (0 at its base, 1 at its top).
        internal static double Rise(double height, CloudWaterNumbers k)
        {
            return k.RiseShare > 0.0 ? k.BaseGain + (1.0 - k.BaseGain) * Sat(height / k.RiseShare) : 1.0;
        }

        // The density per m of an eroded water content.
        internal static double Density(double sigmaPerM, double eroded, double profile, double height, CloudWaterNumbers k)
        {
            if (eroded <= 0.0) return 0.0;
            return sigmaPerM * (k.Power == 1.0 ? eroded : Math.Pow(eroded, k.Power)) * profile * Rise(height, k);
        }

        // The water content at the outline for a cloud type: columnTau is sigma x the type's thickness x the mean over its height of profile x rise (the optical
        // depth of a column of full water content); the content c with columnTau c^power = ClosedTau is taken back through the erosion at the detail's mean.
        internal static double Closed(double columnTau, double erosion, double detailMean, CloudWaterNumbers k)
        {
            double c = Math.Pow(Math.Min(1.0, ClosedTau / Math.Max(columnTau, 1e-9)), 1.0 / Math.Max(k.Power, 0.1));
            double e = erosion * (1.0 - detailMean);
            return c * (1.0 - e) + e;
        }

        // The layer's coverage constant with which the old share's expression saturate((map + (n - 0.5) amount + coverage - 1) / edge + 0.5), at the noise amount 1
        // and the edge `Width`, IS the water content saturate((n - t) / Width + closed). coverMeaning: the outline is t = 1 - map value; otherwise it is where the
        // old share began with the layer's old coverage constant and edge softness (only for a layer whose noise amount was 1). It holds for map values of at
        // least closed x Width; the clear sky at small map values (Threshold) is not in the old expression and needs the map value moved before it.
        internal static double Coverage(bool coverMeaning, double oldCoverage, double oldEdge, double closed, CloudWaterNumbers k)
        {
            double w = Math.Max(k.Width, 1e-3);
            return (coverMeaning ? 0.5 : oldCoverage + 0.5 * oldEdge) + (closed - 0.5) * w;
        }

        // The mean over a cloud's height of profile x rise, from the profile sampled evenly from the base to the top.
        internal static double ColumnMean(Func<double, double> profile, CloudWaterNumbers k, int samples = 256)
        {
            double sum = 0.0;
            for (int i = 0; i < samples; i++)
            {
                double h = (i + 0.5) / samples;
                sum += profile(h) * Rise(h, k);
            }
            return sum / samples;
        }
    }
}
