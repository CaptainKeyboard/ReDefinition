using System;
using System.Collections.Generic;
using System.Globalization;

namespace ReDefinition.Atmospheres
{
    // The numbers of the height variation of the cloud layers (package T13): where the base of a cloud lies and how high its top reaches
    // vary over the sky instead of being one level per type. The four switches carry the settings cloudBaseVariation, cloudBasePhysics,
    // cloudTopFromUpdraft and baseRagged (OwnCloudOptions.Apply); the rest are the look and physics constants of a body, read from the
    // keys of its CLOUDS node (Parse), so that no body's value lives in code (a gas giant or a dusty moon sets its own).
    //
    // References (named in the check list of the package, MAINREPO\.local\plans\atmospheres\tvc-parity.md rows P06-P10, P20):
    //   lifting condensation level LCL ~ 125 m per K of dew-point spread (Lawrence 2005, BAMS 86, "The relationship between relative
    //   humidity and the dewpoint temperature in moist air"; the Espy relation), the Magnus formula for the dew point (a = 17.62,
    //   b = 243.12 C, Alduchov and Eskridge 1996); cumulus bases lie at one level locally with a spread of tens to a few hundred metres;
    //   the WMO cloud levels for the stacked layers.
    internal sealed class HeightTuning
    {
        // The switches. Each is default ON: the height variation has no quality cost (it is a handful of texture reads in the march).
        public bool BaseVariation = true, BasePhysics = true, TopFromUpdraft = true, BaseRagged = true;

        // BASE PHYSICS: metres of base per kelvin of dew-point spread, the spread at which the type table's base applies as it is (0: the
        // mean spread of the body's humidity map, so that the type table's base is the body's average and the physics makes it vary), and
        // the most the physics moves the base either way.
        public float LclMPerK = 125f, ReferenceSpreadK = 0f, PhysicsMaxM = 500f;

        // BASE VARIATION: the standard deviation of the low-frequency offset in m, and its wavelength on the ground in m. The size trend
        // is the change of the base in m for a neighbourhood fully covered against an uncovered one (negative: larger clouds sit lower)
        // within a radius in m.
        public float VariationAmplitudeM = 110f, VariationWavelengthM = 12000f;
        public float SizeTrendM = -220f, SizeRadiusM = 40000f;

        // RAGGED BASE: the displacement of the lower boundary in m at an erosion of 0.5, and the size of its detail in m.
        public float RaggedM = 110f, RaggedScaleM = 220f;

        // TOP FROM UPDRAFT: m of top per m/s of updraft above the reference, the reference updraft, the most the top moves, the random
        // share of it (a cell is not a formula) and the thinnest a cloud may get.
        public float TopGainMPerMs = 120f, ReferenceUpdraftMs = 1.2f, TopMaxM = 1200f, TopJitterM = 40f, MinThicknessM = 120f;

        // The seed of the noise fields (the body's seed).
        public uint Seed = 1u;

        // The altitude at which the field is asked for the updraft of the cells, as a share of a layer's thickness.
        public float UpdraftSampleShare = 0.35f;

        // The most the base can lie below the type table's, and the most the top can lie above it, m: the interval the march must widen
        // by (the layer's tight interval) and the range the byte maps encode.
        public float RootRangeM { get { return PhysicsMaxM + 0.5f * Math.Abs(SizeTrendM); } }
        public float VariationRangeM { get { return 4f * VariationAmplitudeM; } }
        public float TopRangeM { get { return TopMaxM; } }
        // The ragged boundary moves by up to twice RaggedM (erosion 1 against the reference 0.5); a raised base pushes the thinnest top up with it.
        public float DownM { get { return RootRangeM + VariationRangeM + 2f * RaggedM; } }
        public float UpM { get { return RootRangeM + VariationRangeM + TopRangeM + 2f * RaggedM + MinThicknessM; } }

        // Whether any part is active (a layer with none leaves the density as the type table gives it).
        public bool Any { get { return BaseVariation || BasePhysics || TopFromUpdraft || BaseRagged; } }

        // A copy with the switches of this one and the numbers of the layer kind: a thin high layer varies more in m than a low one, a
        // fog layer does not vary at all (it lies on the ground).
        public HeightTuning ForLayer(LayerKind kind)
        {
            var t = (HeightTuning)MemberwiseClone();
            switch (kind)
            {
                case LayerKind.Fog:
                case LayerKind.Emissive:
                    t.BaseVariation = t.BasePhysics = t.TopFromUpdraft = t.BaseRagged = false;
                    break;
                case LayerKind.Cheap:
                    t.VariationAmplitudeM *= 2f;
                    t.BasePhysics = false;
                    t.TopFromUpdraft = false;
                    break;
            }
            return t;
        }

        // The keys of a body's CLOUDS node (heightLclMPerK, heightReferenceSpreadK, heightPhysicsMaxM, heightVariationM, heightWavelengthM,
        // heightSizeTrendM, heightSizeRadiusM, heightRaggedM, heightRaggedScaleM, heightTopGainMPerMs, heightReferenceUpdraftMs,
        // heightTopMaxM, heightTopJitterM, heightMinThicknessM); a missing key keeps the compiled value.
        internal static HeightTuning Parse(IDictionary<string, string> keys)
        {
            var t = new HeightTuning();
            if (keys == null) return t;
            t.LclMPerK = Read(keys, "heightLclMPerK", t.LclMPerK);
            t.ReferenceSpreadK = Read(keys, "heightReferenceSpreadK", t.ReferenceSpreadK);
            t.PhysicsMaxM = Math.Max(0f, Read(keys, "heightPhysicsMaxM", t.PhysicsMaxM));
            t.VariationAmplitudeM = Math.Max(0f, Read(keys, "heightVariationM", t.VariationAmplitudeM));
            t.VariationWavelengthM = Math.Max(100f, Read(keys, "heightWavelengthM", t.VariationWavelengthM));
            t.SizeTrendM = Read(keys, "heightSizeTrendM", t.SizeTrendM);
            t.SizeRadiusM = Math.Max(100f, Read(keys, "heightSizeRadiusM", t.SizeRadiusM));
            t.RaggedM = Math.Max(0f, Read(keys, "heightRaggedM", t.RaggedM));
            t.RaggedScaleM = Math.Max(1f, Read(keys, "heightRaggedScaleM", t.RaggedScaleM));
            t.TopGainMPerMs = Read(keys, "heightTopGainMPerMs", t.TopGainMPerMs);
            t.ReferenceUpdraftMs = Read(keys, "heightReferenceUpdraftMs", t.ReferenceUpdraftMs);
            t.TopMaxM = Math.Max(0f, Read(keys, "heightTopMaxM", t.TopMaxM));
            t.TopJitterM = Math.Max(0f, Read(keys, "heightTopJitterM", t.TopJitterM));
            t.MinThicknessM = Math.Max(1f, Read(keys, "heightMinThicknessM", t.MinThicknessM));
            return t;
        }

        private static float Read(IDictionary<string, string> keys, string name, float fallback)
        {
            string text;
            float value;
            if (keys.TryGetValue(name, out text) && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                !float.IsNaN(value) && !float.IsInfinity(value)) return value;
            return fallback;
        }
    }
}
