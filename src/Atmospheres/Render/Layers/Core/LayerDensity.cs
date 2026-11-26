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
        private readonly CloudLayerDef layer;
        // The soft density's state the profile was baked for (the stamp of the rise by height and the density scale, and whether the soft density was on).
        private long bakedStamp = -1;
        private bool bakedSoft;

        internal LayerTypeTable(CloudLayerDef layer)
        {
            if (layer == null) throw new ArgumentNullException("layer");
            this.layer = layer;
            Count = layer.Types.Count;
            profile = new float[Count * ProfileSamples];
            baseShare = new float[Count];
            topShare = new float[Count];
            erosion = new float[Count];
            smoothing = new float[Count];
            for (int t = 0; t < Count; t++)
            {
                CloudTypeDef type = layer.Types[t];
                baseShare[t] = type.BaseShare;
                topShare[t] = type.TopShare;
                erosion[t] = type.Erosion;
                smoothing[t] = type.ShapeSmoothing >= 0f ? type.ShapeSmoothing : Math.Max(0f, layer.ShapeSmoothing);
            }
            lift = new float[Count * ProfileSamples];
            erosionGain = new float[Count * ProfileSamples];
            noiseAmount = new float[Count * ProfileSamples];
            Bake();
        }

        // Master plan 43, the form of each type: the erosion gain g (the shader's profile texture channel g; FORM EROSION, the pannus' EROSION while it is drawn; 0: none) and the
        // extra noise amount b (channel b; FORM AMOUNT; 0: none), sample after sample, and the largest b of the layer (the shader's _LayerE.w: the empty-space bounds take it).
        private readonly float[] erosionGain, noiseAmount;
        private float maxAmount;
        internal float[] ErosionGainData { get { Sync(); return erosionGain; } }
        internal float[] NoiseAmountData { get { Sync(); return noiseAmount; } }
        internal float MaxNoiseAmount { get { Sync(); return maxAmount; } }

        // The extra amount b at a type value and a type height h (the two neighbouring types' blended, as the shader reads channel b).
        internal double NoiseAmountOf(double typeValue, double h)
        {
            Sync();
            int a, c;
            double f;
            Neighbours(typeValue, out a, out c, out f);
            return Sample(noiseAmount, a, h) * (1.0 - f) + Sample(noiseAmount, c, h) * f;
        }

        // The erosion gain g at a type value and a type height h (blended as the shader reads channel g).
        internal double ErosionGainOf(double typeValue, double h)
        {
            Sync();
            int a, c;
            double f;
            Neighbours(typeValue, out a, out c, out f);
            return Sample(erosionGain, a, h) * (1.0 - f) + Sample(erosionGain, c, h) * f;
        }

        // The type height h (0 base, 1 top) of a layer share at a type value; NaN outside the type.
        internal double TypeHeightOf(double typeValue, double layerShare)
        {
            double b, t;
            InterpolatedHeights(typeValue, out b, out t);
            if (layerShare < b || layerShare > t || !(t > b)) return double.NaN;
            return (layerShare - b) / (t - b);
        }

        private static double Sample(float[] data, int type, double h)
        {
            double x = (h <= 0.0 ? 0.0 : h >= 1.0 ? 1.0 : h) * (ProfileSamples - 1);
            int i = Math.Min((int)x, ProfileSamples - 2);
            double f = x - i;
            return data[type * ProfileSamples + i] * (1.0 - f) + data[type * ProfileSamples + i + 1] * f;
        }

        private readonly float[] lift;
        // The width in noise scales the shape noise is smoothed over where each type is drawn (E40 L6 shape): the type's own key, the layer's where it has none.
        private readonly float[] smoothing;

        // CLOUDR1: the type's cumuliform preset (0: none), the exponent of its density rim (0: the layer's) and its cavity strength, with the tuning overrides of CloudLookTuning applied: what the shader's
        // second type info texture holds.
        internal int CumuliformOfType(int type)
        {
            int forced = CloudLookTuning.CumuliformOverrideFor(layer.Name);
            // CLOUD FIELD: a type with a cloud form reads the bulge cumuliform where the field is on (the owner's fluffy concept is part of the type).
            if (forced < 0 && layer.Types[type].Cumuliform == 0 && layer.Types[type].Form > 0 && CloudLookTuning.Field) return CumuliformModel.PresetOf("bulge");
            return forced >= 0 ? forced : layer.Types[type].Cumuliform;
        }

        internal float EdgeOfType(int type)
        {
            if (!layer.Soft) return 0f;
            float forced = CloudLookTuning.EdgePowerOverrideFor(layer.Name);
            if (forced <= 0f && layer.Types[type].EdgePower <= 0f && layer.Types[type].Form > 0 && CloudLookTuning.Field) return 0.5f;
            return forced > 0f ? forced : layer.Types[type].EdgePower;
        }

        internal float CavityOfType(int type)
        {
            if (CumuliformOfType(type) <= 0) return 0f;
            float forced = CloudLookTuning.CavityOverrideFor(layer.Name);
            if (forced < 0f && layer.Types[type].Cavity <= 0f && layer.Types[type].Form > 0 && CloudLookTuning.Field) return 0.35f;
            return forced >= 0f ? forced : layer.Types[type].Cavity;
        }

        // The cumuliform preset at a type value: the nearer of the two neighbouring types (a preset cannot be blended; the shader takes the same one).
        internal int CumuliformAtTypeValue(double typeValue)
        {
            int a, c;
            double f;
            Neighbours(typeValue, out a, out c, out f);
            return CumuliformOfType(f < 0.5 ? a : c);
        }

        // CLOUD FIELD: the cloud form of a type where the field is on (0: none or the field off); the shader reads the nearer of the two neighbouring types' like the cumuliform preset.
        internal int FormOfType(int type)
        {
            return CloudLookTuning.Field ? layer.Types[type].Form : 0;
        }

        // The smoothing of a type (noise scales).
        internal float SmoothingOfType(int type)
        {
            return smoothing[type];
        }

        // The smoothing at a type value: the two neighbouring types' blended by the weight of the second (the shader's read of the type info texture's alpha).
        internal double SmoothingOf(double typeValue)
        {
            int a, c;
            double f;
            Neighbours(typeValue, out a, out c, out f);
            return smoothing[a] + (smoothing[c] - smoothing[a]) * f;
        }

        // The layer's headroom in map units: how far the map value must be lowered so that no noise value passes the threshold even at the map's top (map 1): the share
        // saturate((map + (n - 0.5) A + C - 1) / e + 0.5) is 0 for every n in 0..1 once map + C - 1 + A / 2 + e / 2 <= 0. The shader's _LayerB.y + CoverageNoiseBound + 0.5 _LayerB.z.
        internal static double LiftHeadroom(CloudLayerDef layer)
        {
            return LiftHeadroom(layer, 0.0);
        }

        // The same for a type at a height whose extra noise amount is b (master plan 43, FORM AMOUNT: the shader's TypeNoiseBound), so a lift of 1 closes every column there too.
        internal static double LiftHeadroom(CloudLayerDef layer, double extraAmount)
        {
            return layer.EffectiveCoverage + 0.5 * layer.EffectiveNoiseAmount * (1.0 + extraAmount) + 0.5 * Math.Max(layer.EffectiveEdgeSoftness, 1e-4f);
        }

        // The lift of the coverage threshold of all types, sample after sample (the shader's profile texture channel a; 0 where a type has no LIFT).
        internal float[] LiftData { get { Sync(); return lift; } }

        // The lift of the layer's threshold at a type value and a height share of the layer, in map units: the two neighbouring types' lifts at the same height of the interpolated
        // type, blended (the shader's read of the profile texture's channel a), times the layer's headroom (the shader's coverage + CoverageNoiseBound + 0.5 edge); 0 outside the type.
        // The CPU readers of the density take the column shape and do not call it; the tests of the shape do.
        internal double LiftOf(double typeValue, double layerShare)
        {
            int a, c;
            double f, b, t;
            Neighbours(typeValue, out a, out c, out f);
            InterpolatedHeights(typeValue, out b, out t);
            if (layerShare < b || layerShare > t || !(t > b)) return 0.0;
            double h = (layerShare - b) / (t - b);
            return (LiftSample(a, h) * (1.0 - f) + LiftSample(c, h) * f) * LiftHeadroom(layer, NoiseAmountOf(typeValue, h));
        }

        private double LiftSample(int type, double h)
        {
            Sync();
            double x =(h <= 0.0 ? 0.0 : h >= 1.0 ? 1.0 : h) * (ProfileSamples - 1);
            int i = Math.Min((int)x, ProfileSamples - 2);
            double f = x - i;
            return lift[type * ProfileSamples + i] * (1.0 - f) + lift[type * ProfileSamples + i + 1] * f;
        }

        // The profile samples of every type: the type's curve times its density scale and, where the layer's density is the soft water content, the rise by height (E40 soft
        // density: the shader reads the rise and the scale through this texture, so it needs no code for them).
        // The lift (E40 L2 D1: the height-shaped threshold with the top's closure, in headroom units: the shader's profile texture channel a) is baked with it: a type's pannus
        // (CloudTypeDef.PannusProfile) changes both.
        private void Bake()
        {
            bool soft = layer.Soft;
            maxAmount = 0f;
            for (int t = 0; t < Count; t++)
            {
                CloudTypeDef type = layer.Types[t];
                Curve drawn = type.DrawnProfile();
                for (int i = 0; i < ProfileSamples; i++)
                {
                    double h = i / (double)(ProfileSamples - 1);
                    profile[t * ProfileSamples + i] = (float)(drawn.Evaluate((float)h) * type.ProfileFactor(h, soft));
                    lift[t * ProfileSamples + i] = (float)type.BakedLiftAt(h);
                    erosionGain[t * ProfileSamples + i] = (float)type.ErosionGainAt(h);
                    float extra = (float)type.NoiseAmountAt(h);
                    noiseAmount[t * ProfileSamples + i] = extra;
                    if (extra > maxAmount) maxAmount = extra;
                }
            }
            bakedSoft = soft;
            bakedStamp = CloudCoverShift.Stamp;
        }

        // Bakes the profile again where the soft density's state changed since (a setting, a tuning key, a layer's noise amount).
        private void Sync()
        {
            if (bakedStamp == CloudCoverShift.Stamp && bakedSoft == layer.Soft) return;
            Bake();
        }

        internal int Count { get; }

        // The profile samples of all types, type after type: the texture the shader reads (width ProfileSamples, height Count).
        internal float[] ProfileData { get { Sync(); return profile; } }

        // Base share, top share, erosion of a type.
        internal void Info(int type, out float b, out float t, out float e)
        {
            b = baseShare[type];
            t = topShare[type];
            e = erosion[type];
        }

        // The profile's column above each sample (master plan 43 step 4, PRUEFPROTOKOLL 273, 307): for each type and sample i the integral of the profile from the sample's height
        // share h_i = i / (n - 1) to the type's top, in units of the type's height (trapezoids: exact for the profile, which is linear between its samples). Times the layer's
        // extinction and the type's height in m it is the optical depth straight up through a plane-parallel deck of that type (the shader's _TypeColumn).
        internal static float[] Column(float[] profile, int count)
        {
            var column = new float[count * ProfileSamples];
            double dh = 1.0 / (ProfileSamples - 1);
            for (int t = 0; t < count; t++)
            {
                int o = t * ProfileSamples;
                double sum = 0.0;
                column[o + ProfileSamples - 1] = 0f;
                for (int i = ProfileSamples - 2; i >= 0; i--)
                {
                    sum += 0.5 * (profile[o + i] + profile[o + i + 1]) * dh;
                    column[o + i] = (float)sum;
                }
            }
            return column;
        }

        // The profile of one type at a height share 0 to 1, linear between the samples (what the texture's bilinear filter does).
        internal float ProfileAt(int type, double h)
        {
            Sync();
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
            double s = Share(layer, coverageMap, table, typeMap);
            if (s <= 0.0) return 0.0;
            return layer.EffectiveSigmaPerM * s * table.Profile(typeMap, share);
        }

        // The layer's share of a column for a map value, as the density takes it: the clear-sky rule on the map value, the soft edge's share, to the layer's exponent.
        // Where the layer keeps the old density this is Shape of the map value (the rule and the exponent are off).
        internal static double Share(CloudLayerDef layer, double coverageMap)
        {
            double s = Shape(layer.ClearSkyMap(coverageMap), layer.EffectiveCoverage, layer.EffectiveEdgeSoftness);
            return CloudWater.Pow(s, layer.EffectiveWaterPower);
        }

        // The same with the exponent of the type at the type value (CLOUDR1 P1: a type's edgePower replaces the layer's waterPower).
        internal static double Share(CloudLayerDef layer, double coverageMap, LayerTypeTable table, double typeValue)
        {
            double s = Shape(layer.ClearSkyMap(coverageMap), layer.EffectiveCoverage, layer.EffectiveEdgeSoftness);
            return CloudWater.Pow(s, layer.EffectiveWaterPowerAt(table, typeValue));
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
