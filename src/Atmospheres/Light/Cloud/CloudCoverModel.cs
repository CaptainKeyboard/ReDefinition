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

        // The same with the runtime shift of the coverage threshold (cloud.coverageShift, cloud.coverageShiftRain: CloudCoverShift.Apply, the rule the readers of the layer use).
        // Shifts of 0 give ClosedShare of the configured coverage exactly.
        internal static double ClosedShareShifted(double map, double[] noiseSamples, double noiseAmount, float coverage, string group, float shiftAll, float shiftRain,
                                                  double edge, double sigmaPerM, double equivalentThicknessM, double slant)
        {
            return ClosedShare(map, noiseSamples, noiseAmount, CloudCoverShift.Apply(coverage, group, shiftAll, shiftRain), edge, sigmaPerM, equivalentThicknessM, slant);
        }

        // The rain deck's Stratus as the Kerbin file has it (K-T2d, CloudOutputStageTests): sigma 0.045 per m, the type's 0.4 of the 2100 m deck times its profile's mean 0.74,
        // the rain deck's coverage 0.5, edge 0.2, noise amount 0.3, the slant of a ray 18 degrees up (3.24), 25 degrees down (2.37) and straight up (1).
        internal const double StratusSigma = 0.045, StratusThicknessM = 0.4 * 2100.0 * 0.74, RainEdge = 0.2, RainNoiseAmount = 0.3;
        internal const float RainCoverage = 0.5f;

        // The expected closed share of the sky under the rain deck's Stratus at a map value, for a shift of the threshold: the number the K-T2d rows T09d.<view>.<cover>.<tag>.model_closed
        // are written beside, from uniform shape noise (samples).
        internal static double RainStratusClosed(double map, double shift, double slant, int samples = 401)
        {
            var noise = new double[samples];
            for (int i = 0; i < samples; i++) noise[i] = (i + 0.5) / samples;
            return ClosedShareShifted(map, noise, RainNoiseAmount, RainCoverage, CloudCoverShift.RainGroup, (float)shift, 0f, RainEdge, StratusSigma, StratusThicknessM, slant);
        }

        // K-T2d start 49: the same for any layer of a stack, from its own numbers (sigma, edge, noise amount, type table) at a map value and a type value. The equivalent
        // thickness is the layer's height integral of the interpolated type's profile (LayerTypeTable.Profile), in m.
        internal static double EquivalentThicknessM(CloudLayerDef layer, LayerTypeTable table, double typeValue, int steps = 200)
        {
            double sum = 0.0;
            for (int i = 0; i < steps; i++) sum += table.Profile(typeValue, (i + 0.5) / steps);
            return sum / steps * layer.ThicknessM;
        }

        // The closed share of the sky under one layer at a map value and a type value, for a coverage threshold (the caller applies any shift), uniform shape noise.
        internal static double LayerClosed(CloudLayerDef layer, LayerTypeTable table, double map, double typeValue, double coverage, double slant, int samples = 401)
        {
            var noise = new double[samples];
            for (int i = 0; i < samples; i++) noise[i] = (i + 0.5) / samples;
            return ClosedShare(map, noise, layer.NoiseAmount, coverage, layer.EdgeSoftness, layer.SigmaPerM, EquivalentThicknessM(layer, table, typeValue), slant);
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
