using System;

namespace ReDefinition.Atmospheres
{
    // The type table of a layer baked for the GPU and for the CPU consumers: for each type, its profile sampled at ProfileSamples
    // heights, and its base, top and erosion. The shader reads the same two arrays, so the CPU functions below are the shader's
    // density function in double precision (P06 type table, P07 interpolation, P08 curves, P09 interpolated heights).
    internal sealed class LayerTypeTable
    {
        // The samples of a profile (a curve over the type's height, linear between them).
        internal const int ProfileSamples = 128;

        private readonly float[] profile;
        private readonly float[] baseShare, topShare, erosion;

        internal LayerTypeTable(CloudLayerDef layer)
        {
            if (layer == null) throw new ArgumentNullException("layer");
            Count = layer.Types.Count;
            profile = new float[Count * ProfileSamples];
            baseShare = new float[Count];
            topShare = new float[Count];
            erosion = new float[Count];
            for (int t = 0; t < Count; t++)
            {
                CloudTypeDef type = layer.Types[t];
                for (int i = 0; i < ProfileSamples; i++)
                    profile[t * ProfileSamples + i] = type.Profile.Evaluate(i / (float)(ProfileSamples - 1));
                baseShare[t] = type.BaseShare;
                topShare[t] = type.TopShare;
                erosion[t] = type.Erosion;
            }
        }

        internal int Count { get; }

        // The profile samples of all types, type after type: the texture the shader reads (width ProfileSamples, height Count).
        internal float[] ProfileData { get { return profile; } }

        // Base share, top share, erosion of a type.
        internal void Info(int type, out float b, out float t, out float e)
        {
            b = baseShare[type];
            t = topShare[type];
            e = erosion[type];
        }

        // The profile of one type at a height share 0 to 1, linear between the samples (what the texture's bilinear filter does).
        internal float ProfileAt(int type, double h)
        {
            if (h <= 0.0) return profile[type * ProfileSamples];
            if (h >= 1.0) return profile[type * ProfileSamples + ProfileSamples - 1];
            double x = h * (ProfileSamples - 1);
            int i = (int)x;
            double f = x - i;
            return (float)(profile[type * ProfileSamples + i] * (1.0 - f) + profile[type * ProfileSamples + i + 1] * f);
        }

        // The type value of a map (0 to 1) as two neighbouring types and the weight of the second: the value spans the table.
        internal void Neighbours(double typeValue, out int a, out int b, out double f)
        {
            double x = Math.Max(0.0, Math.Min(1.0, typeValue)) * (Count - 1);
            a = (int)Math.Floor(x);
            if (a > Count - 1) a = Count - 1;
            b = Math.Min(a + 1, Count - 1);
            f = x - a;
        }

        // The base and top (shares of the layer) at a type value: the heights of the two neighbouring types interpolated (P09).
        internal void InterpolatedHeights(double typeValue, out double b, out double t)
        {
            int a, c;
            double f;
            Neighbours(typeValue, out a, out c, out f);
            b = baseShare[a] + (baseShare[c] - baseShare[a]) * f;
            t = topShare[a] + (topShare[c] - topShare[a]) * f;
        }

        // The density at a height share of the layer (0 to 1) for a type value: 0 outside the interpolated base and top, otherwise the
        // two neighbouring profiles read at the same height of the interpolated type and blended (P07).
        internal double Profile(double typeValue, double layerShare)
        {
            int a, c;
            double f;
            Neighbours(typeValue, out a, out c, out f);
            double b, t;
            InterpolatedHeights(typeValue, out b, out t);
            if (layerShare < b || layerShare > t || !(t > b)) return 0.0;
            double h = (layerShare - b) / (t - b);
            return ProfileAt(a, h) * (1.0 - f) + ProfileAt(c, h) * f;
        }
    }

    // The density of a layer at a place: the shader's function in double precision, for the consumers that read the layers on the CPU
    // (cloud shadows, the light in the medium, the raytracing pack) and for the tests. Noise and erosion are the shader's and are not
    // mirrored: this is the field the maps and the table give.
    internal static class LayerDensity
    {
        // The share of a column that is cloud for a coverage map value m, the layer's coverage C and the edge softness s: 0 below the
        // threshold 1 - C, 1 above it, the transition s wide and centred on the threshold (P12, P13).
        internal static double Shape(double map, double coverage, double softness)
        {
            double edge = Math.Max(softness, 1e-4);
            double x = (map + coverage - 1.0) / edge + 0.5;
            return x <= 0.0 ? 0.0 : x >= 1.0 ? 1.0 : x;
        }

        // The extinction per m at a place of the layer: altitude above sea level, the two map values there.
        internal static double Extinction(CloudLayerDef layer, LayerTypeTable table, double altitudeM, double coverageMap, double typeMap)
        {
            double share = (altitudeM - layer.InnerAltitudeM) / layer.ThicknessM;
            if (share < 0.0 || share > 1.0) return 0.0;
            double s = Shape(coverageMap, layer.EffectiveCoverage, layer.EffectiveEdgeSoftness);
            if (s <= 0.0) return 0.0;
            return layer.EffectiveSigmaPerM * s * table.Profile(typeMap, share);
        }

        // The same with the coverage share given (the shape noise's share, CloudShadowShapeNoise) instead of the plain threshold; a share below 0 is not given.
        internal static double ExtinctionWithShare(CloudLayerDef layer, LayerTypeTable table, double altitudeM, double coverageMap, double typeMap, double coverageShare)
        {
            if (coverageShare < 0.0) return Extinction(layer, table, altitudeM, coverageMap, typeMap);
            double share = (altitudeM - layer.InnerAltitudeM) / layer.ThicknessM;
            if (share < 0.0 || share > 1.0 || coverageShare <= 0.0) return 0.0;
            return layer.EffectiveSigmaPerM * coverageShare * table.Profile(typeMap, share);
        }

        // The weight of the layer's distance fade at a distance (P64): 1 up to FadeNear, 0 from FadeFar, smooth between.
        internal static double DistanceFade(CloudLayerDef layer, double distanceM)
        {
            float fadeNear = layer.EffectiveFadeNearM, fadeFar = layer.EffectiveFadeFarM;
            if (double.IsPositiveInfinity(fadeFar) || !(fadeFar > fadeNear)) return 1.0;
            double x = (fadeFar - distanceM) / (fadeFar - fadeNear);
            x = x <= 0.0 ? 0.0 : x >= 1.0 ? 1.0 : x;
            return x * x * (3.0 - 2.0 * x);
        }
    }
}
