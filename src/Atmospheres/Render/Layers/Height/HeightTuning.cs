using System;

namespace ReDefinition.Atmospheres
{
    // The numbers of the height variation of the cloud layers (package T13): where the base of a cloud lies and how high its top reaches
    // vary over the sky instead of being one level per type. The four switches carry the settings cloudBaseVariation, cloudBasePhysics,
    // cloudTopFromUpdraft and baseRagged (OwnCloudOptions.Apply); the rest are the look and physics constants of a body, read from the
    // keys of its CLOUDS node (CloudHeightRules, TakeNumbers), so that no body's value lives in code (a gas giant or a dusty moon sets its own).
    // The four switches default ON only conditionally: the K-T1 rows T13.height_cost_ms (<= 0.1 ms) and T13.ragged_base_shimmer (<= 0.0015) are
    // unmeasured; flip the defaults to OFF at T09's first start if either is red (tests/series/rows/T13.json, .local T13-status.md).
    //
    // References (named in the check list of the package, MAINREPO\.local\plans\atmospheres\tvc-parity.md rows P06-P10, P20):
    //   lifting condensation level LCL ~ 125 m per K of dew-point spread (Lawrence 2005, BAMS 86, "The relationship between relative
    //   humidity and the dewpoint temperature in moist air"; the Espy relation), the Magnus formula for the dew point (a = 17.62,
    //   b = 243.12 C, WMO CIMO Guide 2008; Alduchov and Eskridge 1996 give 17.625 and 243.04); cumulus bases lie at one level locally with a spread of tens to a few hundred metres;
    //   the WMO cloud levels for the stacked layers.
    internal sealed class HeightTuning
    {
        // The switches. Each is default ON: the height variation has no quality cost (it is a handful of texture reads in the march).
        public bool BaseVariation = true, BasePhysics = true, TopFromUpdraft = true, BaseRagged = true;

        // BASE PHYSICS: metres of base per kelvin of dew-point spread, the spread at which the type table's base applies as it is (0: the
        // mean spread of the body's humidity map, so that the type table's base is the body's average and the physics makes it vary), and
        // the most the physics moves the base either way.
        public float LclMPerK = CloudHeightRules.DefaultLclMPerK, ReferenceSpreadK = CloudHeightRules.DefaultReferenceSpreadK, PhysicsMaxM = CloudHeightRules.DefaultPhysicsMaxM;

        // BASE VARIATION: the standard deviation of the low-frequency offset in m, and its wavelength on the ground in m. The size trend
        // is the change of the base in m for a neighbourhood fully covered against an uncovered one (negative: larger clouds sit lower)
        // within a radius in m.
        public float VariationAmplitudeM = CloudHeightRules.DefaultVariationM, VariationWavelengthM = CloudHeightRules.DefaultWavelengthM;
        public float SizeTrendM = CloudHeightRules.DefaultSizeTrendM, SizeRadiusM = CloudHeightRules.DefaultSizeRadiusM;

        // RAGGED BASE: the displacement of the lower boundary in m at an erosion of 0.5, and the size of its detail in m.
        public float RaggedM = CloudHeightRules.DefaultRaggedM, RaggedScaleM = CloudHeightRules.DefaultRaggedScaleM;

        // TOP FROM UPDRAFT: m of top per m/s of updraft above the reference, the reference updraft, the most the top moves, the random
        // share of it (a cell is not a formula) and the thinnest a cloud may get.
        public float TopGainMPerMs = 120f, ReferenceUpdraftMs = 1.2f, TopMaxM = 1200f, TopJitterM = 40f, MinThicknessM = CloudHeightRules.DefaultMinThicknessM;

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

        // The same for a layer of the stack: where the layer's cfg names its top-from-updraft numbers (topReach above 0, E40 L6 shape), they replace the body's. They are relative to the layer, so
        // that they follow the body's troposphere through the altitudes the layer is authored at: the most a top moves is TopReach of the layer's thickness, it moves by the whole reach at the
        // updraft TopFullMs and stands at the type table's top at TopRefMs (a Kerbin-fitted gain in m per m/s fits one layer of one body).
        public HeightTuning ForLayer(CloudLayerDef layer)
        {
            HeightTuning t = ForLayer(layer.Kind);
            if (!(layer.TopReach > 0f)) return t;
            t.TopMaxM = layer.TopReach * layer.ThicknessM;
            t.ReferenceUpdraftMs = layer.TopRefMs;
            t.TopGainMPerMs = t.TopMaxM / Math.Max(layer.TopFullMs - layer.TopRefMs, 1e-3f);
            return t;
        }

        // The numbers of a body's CLOUDS node (CloudHeightRules, the keys height*; BodyRuleReaders.Clouds reads and bounds them) take the place of the compiled ones; the switches stay.
        internal void TakeNumbers(CloudHeightRules r)
        {
            LclMPerK = r.LclMPerK;
            ReferenceSpreadK = r.ReferenceSpreadK;
            PhysicsMaxM = r.PhysicsMaxM;
            VariationAmplitudeM = r.VariationM;
            VariationWavelengthM = r.WavelengthM;
            SizeTrendM = r.SizeTrendM;
            SizeRadiusM = r.SizeRadiusM;
            RaggedM = r.RaggedM;
            RaggedScaleM = r.RaggedScaleM;
            MinThicknessM = r.MinThicknessM;
        }
    }
}
