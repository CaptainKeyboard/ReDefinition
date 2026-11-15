namespace ReDefinition.Atmospheres
{
    // The numbers of the height variation of the body's cloud layers (E40 HKEYS; keys of the node CLOUDS, all optional, each with the range the reader holds it to). A cloud's base and
    // top vary over the sky (HeightTuning, package T13); these are that variation's physical and look constants, so that no body's value lives in code: a gas giant, a dusty moon or a
    // world with another humidity law sets its own. The defaults are the numbers Kerbin's clouds always had; a body that sets none of the keys draws the same clouds as before.
    // The switches (cloudBaseVariation, cloudBasePhysics, cloudTopFromUpdraft, baseRagged) are the player's, not the body's.
    internal sealed class CloudHeightRules
    {
        internal static readonly CloudHeightRules Default = new CloudHeightRules();

        // BASE PHYSICS: metres of base per kelvin of dew-point spread (heightLclMPerK; 125 is the Espy relation, Lawrence 2005), the spread at which the type table's base applies as it
        // is (heightReferenceSpreadK; 0: the mean spread of the body's humidity map, so that the type table's base is the body's average and the physics makes it vary) and the most
        // the physics moves the base either way, in m (heightPhysicsMaxM).
        internal const float DefaultLclMPerK = 125f, DefaultReferenceSpreadK = 0f, DefaultPhysicsMaxM = 500f;
        internal float LclMPerK { get; set; } = DefaultLclMPerK;
        internal float ReferenceSpreadK { get; set; } = DefaultReferenceSpreadK;
        internal float PhysicsMaxM { get; set; } = DefaultPhysicsMaxM;

        // BASE VARIATION: the standard deviation of the low-frequency offset of the base in m (heightVariationM) and its wavelength on the ground in m (heightWavelengthM). The size trend
        // is the change of the base in m for a neighbourhood fully covered against an uncovered one, negative where larger clouds sit lower (heightSizeTrendM), within a radius in m
        // (heightSizeRadiusM).
        internal const float DefaultVariationM = 110f, DefaultWavelengthM = 12000f, DefaultSizeTrendM = -220f, DefaultSizeRadiusM = 40000f;
        internal float VariationM { get; set; } = DefaultVariationM;
        internal float WavelengthM { get; set; } = DefaultWavelengthM;
        internal float SizeTrendM { get; set; } = DefaultSizeTrendM;
        internal float SizeRadiusM { get; set; } = DefaultSizeRadiusM;

        // RAGGED BASE: the displacement of the lower boundary in m at an erosion of 0.5 (heightRaggedM) and the size of its detail in m (heightRaggedScaleM). The thinnest a cloud may get, in m
        // (heightMinThicknessM), holds where a raised base pushes the top up with it.
        internal const float DefaultRaggedM = 110f, DefaultRaggedScaleM = 220f, DefaultMinThicknessM = 120f;
        internal float RaggedM { get; set; } = DefaultRaggedM;
        internal float RaggedScaleM { get; set; } = DefaultRaggedScaleM;
        internal float MinThicknessM { get; set; } = DefaultMinThicknessM;

        // TOP FROM UPDRAFT: m of top per m/s of updraft above the reference (heightTopGainMPerMs), the reference updraft in m/s at which the top is the type table's (heightReferenceUpdraftMs), the
        // most the top moves either way in m (heightTopMaxM) and the random share of it in m (heightTopJitterM; a cell is not a formula). A LAYER that names topReach (above 0) takes its own
        // gain, reference and maximum, relative to its thickness (HeightTuning.ForLayer(CloudLayerDef)); these are the numbers of every layer that does not, and the jitter is the body's for all.
        internal const float DefaultTopGainMPerMs = 120f, DefaultReferenceUpdraftMs = 1.2f, DefaultTopMaxM = 1200f, DefaultTopJitterM = 40f;
        internal float TopGainMPerMs { get; set; } = DefaultTopGainMPerMs;
        internal float ReferenceUpdraftMs { get; set; } = DefaultReferenceUpdraftMs;
        internal float TopMaxM { get; set; } = DefaultTopMaxM;
        internal float TopJitterM { get; set; } = DefaultTopJitterM;
    }
}
