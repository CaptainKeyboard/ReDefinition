using System;

namespace ReDefinition.Atmospheres
{
    // What a field's cloud-cover value does in the own cloud pass (K-T2d: a cover of 0.47 shows a closed deck over the whole sky). The CPU twin of the layer's
    // coverage shape (NoiseShareAt in ReDefinitionAtmospheresNoise.cginc) and of the pass's opacity rule (Layers.shader: alpha is 1 where the transmittance
    // falls under 0.01, i.e. tau > 4.6), so that a test can ask "which share of the sky is closed at a map value m" for a layer's numbers.
    //
    // The shape of a layer: share = saturate((m + (n - 0.5) * noiseAmount + coverage - 1) / edge + 0.5), n the shape noise (0..1, median 0.5), m the map value.
    // A column's optical depth is sigma * share * (the type's profile integrated over its height range), seen along a ray of slant factor 1 / sin(elevation).
    // The sky is "closed" where that exceeds ClosedTau. A cover value is a share of the sky, so the closed share should follow it (m = 0.5 closing half of the
    // sky); the closed share depends on the layer's coverage threshold, the edge, the noise amount and the extinction: that is a calibration of the layer set,
    // not of the pass.
    internal static class CloudCoverModel
    {
        // The optical depth at which the pass writes alpha 1 (transmittance 0.01).
        internal const double ClosedTau = 4.605;

        internal static double Share(double map, double noise, double noiseAmount, double coverage, double edge)
        {
            double s = (map + (noise - 0.5) * noiseAmount + coverage - 1.0) / Math.Max(edge, 1e-4) + 0.5;
            return Math.Min(1.0, Math.Max(0.0, s));
        }

        // The optical depth of a column for a share (1 = full density): sigma per m, the equivalent thickness in m (the type's height range times the mean
        // of its profile), the ray's slant factor.
        internal static double Tau(double share, double sigmaPerM, double equivalentThicknessM, double slant)
        {
            return share * sigmaPerM * equivalentThicknessM * slant;
        }

        // The share of the sky that is closed at a map value, over the samples of the shape noise (sorted or not; 0..1 each).
        internal static double ClosedShare(double map, double[] noiseSamples, double noiseAmount, double coverage, double edge, double sigmaPerM,
                                           double equivalentThicknessM, double slant)
        {
            if (noiseSamples == null || noiseSamples.Length == 0) return 0.0;
            int closed = 0;
            for (int i = 0; i < noiseSamples.Length; i++)
            {
                double share = Share(map, noiseSamples[i], noiseAmount, coverage, edge);
                if (Tau(share, sigmaPerM, equivalentThicknessM, slant) > ClosedTau) closed++;
            }
            return closed / (double)noiseSamples.Length;
        }

        // The coverage threshold at which a map value m closes the share m of the sky (a cover value read as an area share): bisection on the closed share, which
        // rises with the threshold. Returns NaN where even a threshold of 0 and 1 cannot reach it.
        internal static double CentredCoverage(double map, double[] noiseSamples, double noiseAmount, double edge, double sigmaPerM, double equivalentThicknessM, double slant)
        {
            double lo = 0.0, hi = 1.5;
            if (ClosedShare(map, noiseSamples, noiseAmount, hi, edge, sigmaPerM, equivalentThicknessM, slant) < map) return double.NaN;
            for (int i = 0; i < 40; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (ClosedShare(map, noiseSamples, noiseAmount, mid, edge, sigmaPerM, equivalentThicknessM, slant) < map) lo = mid;
                else hi = mid;
            }
            return 0.5 * (lo + hi);
        }
    }
}
