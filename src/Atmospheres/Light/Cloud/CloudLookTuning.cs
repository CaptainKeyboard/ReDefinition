using System;
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The runtime knobs of the cloud look (E40 plan 2026-11, Phase O step 3): every constant the look sweeps want to change inside ONE game start, as Tuning keys that
    // default to today's behaviour (a default changes no output, tests/ReDefinition.Tests/Atmospheres/T09/CloudLookTuningTests.cs). The pattern of CloudCoverShift:
    // CloudLayerDef's Effective* getters and the module read these, never the configured numbers, so the layer pass, the cloud shadows and the CPU readers see one value.
    //   cloud.layerSigmaScale             factor on every layer's sigma per m (1)
    //   cloud.layerSoftnessScale          factor on every layer's edge softness (1)
    //   cloud.layerNoiseSizeScale         factor on every layer's noise scale in m (1)
    //   cloud.mapOverride.<name>          a constant coverage-map value for the layer everywhere, 0 to 1 (-1: the field's map); tells a map that varies over the view from the pass
    //   cloud.layerSigmaScale.<name>, cloud.layerSoftnessScale.<name>, cloud.layerNoiseSizeScale.<name>
    //                                     the same for the layer of that name only, multiplied with the general key (1)
    //   cloud.layerCoverageShift.<name>   added to the coverage of the layer of that name, on top of cloud.coverageShift (0)
    //   cloud.typeFair.<name>             the type value of the layer at no precipitation, rising linearly to 1 at full precipitation (-1: the precipitation level itself)
    //   cloud.typeOverride.<name>         a constant type value for the layer (-1: none; wins over typeFair)
    //   cloud.lowStratiformRule.<name>    1 forces the layer's low stratiform rule on (the cfg's numbers, else WeatherLowStratiformRule.Defaults), 0 gives the old rule back (the type is the
    //                                     precipitation level and the layer draws wherever its map passes), -1 (default): the layer's cfg (lowStratiformRule = 1 on the low stratiform weather layers)
    //   cloud.lowStratiformPrecip.<name>  the probe's: a precipitation level the layer's low stratiform rule sees everywhere, 0 to 1 (-1: the field's)
    //   cloud.lowStratiformFog.<name>     the probe's: a fog share the layer's low stratiform rule sees everywhere, 0 to 1 (-1: the field's dew-point spread and wind)
    //   cloud.coverCombine                0 to 1: how far the layer maps are scaled so that their random-overlap union equals the field's cover (0: maps as they are; default 1, E40 F1: each
    //                                     layer map the generator makes carries about the field's whole cover, the four closed the sky twice over)
    //   cloud.fadeFarM                    m: a distance fade for the layers that have none (0: none), starting at cloud.fadeNearShare of it
    //   cloud.fadeNearShare               share of cloud.fadeFarM where the fade starts (0.5)
    //   cloud.aerialPerspective           -1: the cloudAerialPerspective setting decides, 0 / 1 forces the aerial perspective off / on, 2 automatic (on where the pass is behind Scatterer's scattering; AerialPerspectivePlan)
    //   cloud.aerialPerspectiveStrength   factor on the aerial perspective's strength (1)
    //   cloud.soloLayer                   solo mode (E40 SOLO): the 1-based index of the only layer the pass draws (0: every layer); the layers' maps and types are the per-layer keys above
    //   cloud.cellKm, cloud.cellLat, cloud.cellLon
    //                                     a forced map (cloud.mapOverride.<name>) as ONE cell instead of a constant: a Gaussian of this e-folding radius in km round the point (degrees),
    //                                     the override's value at its centre, 0 outside (0: the constant map); the one cumulonimbus cell whose tower spreads an anvil (AnvilMap)
    //   cloud.weatherMapSky.stratiformRule / .depthRule   the rules of the sky from the weather map (WeatherMapSky, MapSkyRegions): the layer maps are built from the package B column
    //                                     quantities; read by the game's module (OwnCloudModule.MapSky.cs) and by the map dump that feeds the weather player.
    internal static class CloudLookTuning
    {
        // The default of cloud.coverCombine (E40 F1): the layer maps meet the field's cover.
        internal const float DefaultCoverCombine = 1f;

        internal const string TuneSigma = "cloud.layerSigmaScale", TuneSoftness = "cloud.layerSoftnessScale",
            TuneNoiseSize = "cloud.layerNoiseSizeScale", TuneLayerShift = "cloud.layerCoverageShift.", TuneTypeFair = "cloud.typeFair.", TuneTypeOverride = "cloud.typeOverride.",
            TuneCoverCombine = "cloud.coverCombine", TuneFadeFar = "cloud.fadeFarM",
            TuneFadeNearShare = "cloud.fadeNearShare", TuneAerialPerspective = "cloud.aerialPerspective", TuneAerialPerspectiveStrength = "cloud.aerialPerspectiveStrength", TuneMapOverride = "cloud.mapOverride.",
            TuneLowStratiformRule = "cloud.lowStratiformRule.",
            TuneLowStratiformPrecip = "cloud.lowStratiformPrecip.", TuneLowStratiformFog = "cloud.lowStratiformFog.",
            TuneSoloLayer = "cloud.soloLayer", TuneCellKm = "cloud.cellKm", TuneCellLat = "cloud.cellLat", TuneCellLon = "cloud.cellLon";

        private static long seenVersion = -1;
        private static float sigma = 1f, softness = 1f, noiseSize = 1f, combine = DefaultCoverCombine, fadeFar, fadeNearShare = 0.5f, aerialPerspective = -1f, aerialPerspectiveStrength = 1f;
        private static float cellKm, cellLat, cellLon;
        private static float cumuliformForce = -1f, edgeForce, cavityForce = -1f, cumuliformFoot = 1f, cumuliformIso = 1f;
        // cloud.field as last read (the type table's cumuliform, edge and cavity follow it, LayerDensity: a change must re-upload the table).
        private static float fieldState = -1f;
        private static float pannusState = -1f;
        private static long stamp;
        private static readonly Dictionary<string, float> LayerShift = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> TypeFairByName = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> TypeOverrideByName = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> MapOverrideByName = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> LowStratiformRuleByName = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> LowStratiformPrecipByName = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> LowStratiformFogByName = new Dictionary<string, float>(StringComparer.Ordinal);
        // The per-layer factors (cloud.layerSigmaScale.<name> and the two like it), multiplied with the general ones.
        private static readonly Dictionary<string, float> SigmaByName = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> SoftnessByName = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> NoiseSizeByName = new Dictionary<string, float>(StringComparer.Ordinal);

        // Rises each time any of the keys that reach the layers' uploaded numbers changed.
        internal static long Stamp
        {
            get
            {
                Refresh();
                return stamp;
            }
        }

        // The readers run on the main thread and on the height worker; the caches are guarded.
        private static readonly object Gate = new object();

        private static void Refresh()
        {
            long version = Tuning.Version;
            if (version == System.Threading.Volatile.Read(ref seenVersion)) return;
            lock (Gate)
            {
                if (version == seenVersion) return;
                Load();
                // Published after the load: a reader that sees the new version sees the loaded values.
                System.Threading.Volatile.Write(ref seenVersion, version);
            }
        }

        private static void Load()
        {
            float s = Scale(Tuning.Get(TuneSigma, 1f)), e = Scale(Tuning.Get(TuneSoftness, 1f)), z = Scale(Tuning.Get(TuneNoiseSize, 1f));
            float c = Unit(Tuning.Get(TuneCoverCombine, DefaultCoverCombine));
            float f = Clean(Tuning.Get(TuneFadeFar, 0f), 0f, 0f, 1e7f), n = Clean(Tuning.Get(TuneFadeNearShare, 0.5f), 0.5f, 0f, 1f);
            float h = Tuning.Get(TuneAerialPerspective, -1f);
            h = float.IsNaN(h) || float.IsInfinity(h) ? -1f : h < 0f ? -1f : h > 1.5f ? 2f : h > 0f ? 1f : 0f;
            float hs = Clean(Tuning.Get(TuneAerialPerspectiveStrength, 1f), 1f, 0f, 100f);
            float ck = Clean(Tuning.Get(TuneCellKm, 0f), 0f, 0f, 1e5f), cla = Clean(Tuning.Get(TuneCellLat, 0f), 0f, -90f, 90f), clo = Clean(Tuning.Get(TuneCellLon, 0f), 0f, -360f, 360f);
            float hf = Clean(Tuning.Get(TuneCumuliform, -1f), -1f, -1f, CumuliformModel.PresetCount), ef = Clean(Tuning.Get(TuneEdgePower, 0f), 0f, 0f, 6f);
            float cf = Clean(Tuning.Get(TuneCavity, -1f), -1f, -1f, 1f), ft = Clean(Tuning.Get(TuneCumuliformFootLod, 1f), 1f, 0f, 1f), hi = Clean(Tuning.Get(TuneCumuliformIso, 1f), 1f, 0f, 1f);
            hf = hf < 0f ? -1f : (float)Math.Round(hf);
            float fd = Tuning.Get(TuneField, FieldDefault) > 0.5f ? 1f : 0f;
            float pn = Tuning.Get(TunePannus, PannusDefault) > 0.5f ? 1f : 0f;
            bool changed = hf != cumuliformForce || ef != edgeForce || cf != cavityForce || ft != cumuliformFoot || hi != cumuliformIso || fd != fieldState || pn != pannusState;
            fieldState = fd;
            pannusState = pn;
            cumuliformIso = hi;
            cumuliformForce = hf;
            edgeForce = ef;
            cavityForce = cf;
            cumuliformFoot = ft;
            changed |= s != sigma || e != softness || z != noiseSize || c != combine || f != fadeFar || n != fadeNearShare
                           || h != aerialPerspective || hs != aerialPerspectiveStrength || ck != cellKm || cla != cellLat || clo != cellLon;
            cellKm = ck;
            cellLat = cla;
            cellLon = clo;
            sigma = s;
            softness = e;
            noiseSize = z;
            combine = c;
            fadeFar = f;
            fadeNearShare = n;
            aerialPerspective = h;
            aerialPerspectiveStrength = hs;
            // The per-layer keys of the names asked for so far are read again in place; a value that moved counts as a change.
            changed |= Reread(LayerShift, TuneLayerShift, 0f, -1f, 1f);
            changed |= Reread(TypeFairByName, TuneTypeFair, -1f, -1f, 1f);
            changed |= Reread(TypeOverrideByName, TuneTypeOverride, -1f, -1f, 1f);
            changed |= Reread(MapOverrideByName, TuneMapOverride, -1f, -1f, 1f);
            changed |= Reread(LowStratiformRuleByName, TuneLowStratiformRule, -1f, -1f, 1f);
            changed |= Reread(LowStratiformPrecipByName, TuneLowStratiformPrecip, -1f, -1f, 1f);
            changed |= Reread(LowStratiformFogByName, TuneLowStratiformFog, -1f, -1f, 1f);
            changed |= Reread(CumuliformByName, TuneCumuliformLayer, -1f, -1f, CumuliformModel.PresetCount);
            changed |= Reread(EdgeByName, TuneEdgePowerLayer, 0f, 0f, 6f);
            changed |= Reread(CavityByName, TuneCavityLayer, -1f, -1f, 1f);
            changed |= Reread(SigmaByName, TuneSigma + ".", 1f, 0.01f, 100f);
            changed |= Reread(SoftnessByName, TuneSoftness + ".", 1f, 0.01f, 100f);
            changed |= Reread(NoiseSizeByName, TuneNoiseSize + ".", 1f, 0.01f, 100f);
            if (changed) stamp++;
        }

        // A per-layer key through a cache (the caller holds no lock; this takes it).
        private static float Named(Dictionary<string, float> cache, string prefix, string layerName, float fallback, float lo, float hi)
        {
            Refresh();
            if (string.IsNullOrEmpty(layerName)) return fallback;
            lock (Gate)
            {
                float v;
                if (cache.TryGetValue(layerName, out v)) return v;
                v = ReadKey(prefix + layerName, fallback, lo, hi);
                cache[layerName] = v;
                return v;
            }
        }

        private static bool Reread(Dictionary<string, float> cache, string prefix, float fallback, float lo, float hi)
        {
            bool changed = false;
            foreach (string name in new List<string>(cache.Keys))
            {
                float v = ReadKey(prefix + name, fallback, lo, hi);
                if (v != cache[name]) changed = true;
                cache[name] = v;
            }
            return changed;
        }

        // A per-layer key: NaN and infinity give the fallback, a negative value below the lower bound the fallback too where the key means "off" (fallback = lo).
        private static float ReadKey(string key, float fallback, float lo, float hi)
        {
            float v = Tuning.Get(key, fallback);
            if (float.IsNaN(v) || float.IsInfinity(v)) return fallback;
            if (fallback == lo && lo < 0f && v < 0f) return lo;
            return Math.Max(lo, Math.Min(hi, v));
        }

        private static float Scale(float v)
        {
            return Clean(v, 1f, 0.01f, 100f);
        }

        private static float Unit(float v)
        {
            return Clean(v, 0f, 0f, 1f);
        }

        private static float Clean(float v, float fallback, float lo, float hi)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return fallback;
            return Math.Max(lo, Math.Min(hi, v));
        }

        // --- The per-layer numbers (CloudLayerDef reads these) ---------------------------------------------------------------------------

        internal static float SigmaScale { get { Refresh(); return sigma; } }
        internal static float SoftnessScale { get { Refresh(); return softness; } }
        internal static float NoiseSizeScale { get { Refresh(); return noiseSize; } }
        internal static float CoverCombine { get { Refresh(); return combine; } }
        internal static float AerialPerspectiveForce { get { Refresh(); return aerialPerspective; } }
        internal static float AerialPerspectiveStrengthScale { get { Refresh(); return aerialPerspectiveStrength; } }
        // The forced map as one cell (cloud.cellKm above 0): its e-folding radius in km and its centre in degrees.
        internal static float CellKm { get { Refresh(); return cellKm; } }
        internal static float CellLat { get { Refresh(); return cellLat; } }
        internal static float CellLon { get { Refresh(); return cellLon; } }

        // --- Bulge detail (package PUFF): the small-scale detail of the cloud's surface. Every key is neutral at its default, so the pass is as it was. ---------------------------------
        //   cloud.bulge.light        above 0: bulge shading, a sample's direct light is scaled by 1 + 2 x this x (d - mean), d the erosion detail's value at the sample (the bumps catch the light,
        //                             the crevices are shaded; no extra density read, which would put a second copy of the erosion into the shader and crash its compiler) (0: off)
        //   cloud.bulge.contrast     the erosion detail's contrast about its mean (1)
        //   cloud.bulge.top          the erosion's gain toward the cloud's top: erosion x (1 + gain x height in the cloud) (0)
        //   cloud.bulge.fine         the weight of the fine octave of the detail (-1: the compiled NoiseModel.FineDetailWeight)
        //   cloud.bulge.fineFreq     the fine octave's frequency over the first read (-1: the compiled NoiseModel.FineDetailFrequency)
        internal const string TuneBulgeLight = "cloud.bulge.light", TuneBulgeContrast = "cloud.bulge.contrast", TuneBulgeTop = "cloud.bulge.top",
            TuneBulgeFine = "cloud.bulge.fine", TuneBulgeFineFreq = "cloud.bulge.fineFreq";

        internal static float BulgeLight { get { return Clean(Tuning.Get(TuneBulgeLight, 0f), 0f, 0f, 8f); } }
        internal static float BulgeContrast { get { return Clean(Tuning.Get(TuneBulgeContrast, 1f), 1f, 0f, 8f); } }
        internal static float BulgeTop { get { return Clean(Tuning.Get(TuneBulgeTop, 0f), 0f, 0f, 8f); } }

        // The weight of the fine octave: the key, or `compiled` where it is below 0.
        internal static float BulgeFine(float compiled)
        {
            float v = Clean(Tuning.Get(TuneBulgeFine, -1f), -1f, -1f, 1f);
            return v < 0f ? compiled : v;
        }

        // The frequency of the fine octave: the key, or `compiled` where it is not above 0.
        internal static float BulgeFineFreq(float compiled)
        {
            float v = Clean(Tuning.Get(TuneBulgeFineFreq, -1f), -1f, -1f, 16f);
            return v > 0f ? v : compiled;
        }

        // --- The cumuliform volume (package CLOUDR1): global overrides of the per-type cfg keys cumuliform, edgePower and cavity, for the A/B of the player and the viewer. Every key is neutral at its default. ---
        //   cloud.cumuliform            -1: each type's cfg key cumuliform; 0: no cumuliform anywhere (the shape volume of today); 1, 2: that preset for every type
        //   cloud.edgePower       0: each type's cfg key edgePower (else the layer's waterPower); above 0: this exponent of the density rim for every type of a soft layer
        //   cloud.cavity          -1: each type's cfg key cavity; 0 to 1: this strength for every type that reads a cumuliform
        //   cloud.cumuliform.iso        1: the cumuliform is read at the lattice's true vertical scale, so a layer's verticalScale does not stretch the rounded mounds into columns; 0: as the shape volume is read
        //   cloud.cumuliform.footLod    1: the march's sample spacing raises the cumuliform volume's mip level (an octave is dropped only where it is below the footprint); 0: only the smoothing does
        internal const string TuneCumuliform = "cloud.cumuliform", TuneEdgePower = "cloud.edgePower", TuneCavity = "cloud.cavity", TuneCumuliformFootLod = "cloud.cumuliform.footLod", TuneCumuliformIso = "cloud.cumuliform.iso", TuneCumuliformDetail = "cloud.cumuliform.detail", TuneNoiseBlend = "cloud.noise.blend", TuneNoiseBlendExp = "cloud.noise.blendExp", TuneNoiseBlendCell = "cloud.noise.blendCell";

        // The same per layer (the layer's name after the prefix): the layer's own value wins over the global key, so a look can be tried on one layer (the cumulus layer) and the low stratiform layers stay as they were.
        internal const string TuneCumuliformLayer = "cloud.cumuliform.layer.", TuneEdgePowerLayer = "cloud.edgePower.layer.", TuneCavityLayer = "cloud.cavity.layer.";
        private static readonly Dictionary<string, float> CumuliformByName = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> EdgeByName = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> CavityByName = new Dictionary<string, float>(StringComparer.Ordinal);

        internal static int CumuliformOverrideFor(string layerName)
        {
            float v = Named(CumuliformByName, TuneCumuliformLayer, layerName, -1f, -1f, CumuliformModel.PresetCount);
            return v >= 0f ? (int)Math.Round(v) : CumuliformOverride;
        }

        internal static float EdgePowerOverrideFor(string layerName)
        {
            float v = Named(EdgeByName, TuneEdgePowerLayer, layerName, 0f, 0f, 6f);
            return v > 0f ? v : EdgePowerOverride;
        }

        internal static float CavityOverrideFor(string layerName)
        {
            float v = Named(CavityByName, TuneCavityLayer, layerName, -1f, -1f, 1f);
            return v >= 0f ? v : CavityOverride;
        }

        internal static int CumuliformOverride { get { Refresh(); return (int)cumuliformForce; } }
        internal static float EdgePowerOverride { get { Refresh(); return edgeForce; } }
        internal static float CavityOverride { get { Refresh(); return cavityForce; } }
        internal static bool CumuliformFootLod { get { Refresh(); return cumuliformFoot > 0.5f; } }
        // cloud.cumuliform.detail: the Hurst exponent of the erosion detail volume's octaves (2^(-H k): the cumuliform's cascade continued; 0: the compiled weights, whose finest octave is the heaviest). Read when the
        // volumes are generated (the first frame of a scene), not live.
        // cloud.noise.blend (the layer pass reads it since the compile budget package CLOUDR2 P4: the earlier builds with it crashed the Unity 2019.4 shader compiler at 1190 s and 833 s): 1 hides the tiling of the shape and cumuliform volumes by blending (random offset per lattice cell, variance-preserving, back to a rank: NoiseBlend, the local strain is exactly 1)
        // instead of the untiling displacement, which pulls the pattern (1, the default; 0: the displacement). cloud.noise.blendExp is the exponent of the weights (8), cloud.noise.blendCell the lattice
        // cell in periods (1). The erosion detail keeps its displacement.
        // cloud.light.erosion (CLOUDR2 P3): how many of the first taps of the sun's light march read the density with its erosion detail (2, the default; 0: the shape only). The eroded taps are what
        // makes the lobes shade each other; the tap count is whole.
        internal const string TuneLightErosion = "cloud.light.erosion";
        internal const float LightErosionDefault = 2f;
        internal const float NoiseBlendDefault = 1f;
        internal static float LightErosionTaps { get { float v = Tuning.Get(TuneLightErosion, LightErosionDefault); return float.IsNaN(v) || float.IsInfinity(v) ? LightErosionDefault : (float)Math.Floor(Math.Max(0f, Math.Min(v, 8f))); } }
        internal static bool NoiseBlend { get { return Tuning.Get(TuneNoiseBlend, NoiseBlendDefault) > 0.5f; } }
        internal static float NoiseBlendExp { get { return Clean(Tuning.Get(TuneNoiseBlendExp, 8f), 8f, 1f, 32f); } }
        internal static float NoiseBlendCell { get { return Clean(Tuning.Get(TuneNoiseBlendCell, 1f), 1f, 0.25f, 16f); } }
        // cloud.cells (CELLS, 2026-11-17): 1 puts the map's cloud share down as single clouds on the layers with a cellPitch (CloudLayerDef.CellPitchM, CloudCells.cginc); 0 reads the map
        // as it is (the clouds are the map's smoothed texels moved by the noise). On by default: the owner's clouds are the single-cloud bodies (CellBody); the map's texels without
        // them were rejected (owner 2026-11-19: "ICH WILL DIE RAUS HABEN"). The lattice's even arrangement is to be replaced by a realistic one from the weather map (plan: CLOUD FIELD).
        internal const string TuneCells = "cloud.cells";
        internal const float CellsDefault = 1f;
        internal static bool Cells { get { return Tuning.Get(TuneCells, CellsDefault) > 0.5f; } }
        // cloud.field (CLOUD FIELD, 2026-11-19): 1 draws the types with a cloud form (cfg key form: the cumulus family, altocumulus, stratocumulus, cumulonimbus calvus and the incus tower) as single
        // clouds arranged by the weather map's share and one multi-scale field (CloudField.cs), without the lattice (cloud.cells is then not read on those types); 1 is the default
        // (owner 2026-11-22: the lattice is only a tool to test single cloud types); 0 the lattice.
        // cloud.field.wind: the direction of the streets in degrees from east, counter-clockwise, in the map's frame (the class's stretch lengthens the field along it).
        internal const string TuneField = "cloud.field", TuneFieldWind = "cloud.field.wind";
        internal const float FieldDefault = 1f;
        internal static bool Field { get { return Tuning.Get(TuneField, FieldDefault) > 0.5f; } }
        internal static float FieldWindDeg { get { return Clean(Tuning.Get(TuneFieldWind, 0f), 0f, -360f, 360f); } }
        // cloud.pannus (M2c 4a(1), 2026-11-24): 1 draws the pannus of the types that have one (cfg node PANNUS: the ragged shreds below a nimbostratus deck, CloudTypeDef.PannusProfile);
        // 0 (the default until the owner's yes on the pictures) the types as before. A change re-bakes the type tables (the stamp below).
        internal const string TunePannus = "cloud.pannus";
        internal const float PannusDefault = 0f;
        internal static bool Pannus { get { return Tuning.Get(TunePannus, PannusDefault) > 0.5f; } }
        // The body of a single cloud in the viewer (CloudLayerDef.CellBulge, CellBandM, CellFine): cloud.cellBulge, cloud.cellBand and cloud.cellFine set it on every layer with cells where
        // they are set (below 0, the default: each layer's own). Read every frame (LayerRenderer.Apply and CloudShadowRenderer, _CellE).
        internal const string TuneCellBulge = "cloud.cellBulge", TuneCellBand = "cloud.cellBand", TuneCellFine = "cloud.cellFine";
        internal static float CellBulgeFor(CloudLayerDef layer) { return Layered(TuneCellBulge, layer.CellBulge, 2f); }
        internal static float CellBandFor(CloudLayerDef layer) { return Math.Max(Layered(TuneCellBand, layer.CellBandM, 1000f), 1f); }
        internal static float CellFineFor(CloudLayerDef layer) { return Layered(TuneCellFine, layer.CellFine, 4f); }

        private static float Layered(string key, float own, float max)
        {
            float v = Tuning.Get(key, -1f);
            return float.IsNaN(v) || float.IsInfinity(v) || v < 0f ? own : Math.Min(v, max);
        }

        // cloud.cellTrace (A4, owner audit 2026-11-17): 1 lets the march leap the grid points beyond the reach of a single cloud's bulges, where the body's depth bounds the distance to its
        // surface (CloudCells.cginc CellShellGap); the grid's phase is kept, so the picture is the grid march's. The first form, a sphere trace on the bulges' slope bound, ran out of its loop
        // (player 2026-11-18, ceiling march: cover 0.065 against 0.318). On: the player's cover and spectra are equal on and off (gclock, sclock; ceiling and product marches), and K-T2
        // perfN2560dlssqleap (2026-11-18, 2560x1440, DLSS Quality) gained 2 to 11 % fps per view (ground 37.6 to 38.4, inside 29.8 to 31.1, above 25.8 to 26.5, sky3 43.0 to 47.9).
        internal const string TuneCellTrace = "cloud.cellTrace";
        internal const float CellTraceDefault = 1f;
        internal static bool CellTrace { get { return Tuning.Get(TuneCellTrace, CellTraceDefault) > 0.5f; } }

        // cloud.cellShell (A4): 1 lets the march step finely inside a body's shell, where its surface can lie (the pixel's size there, at least the chosen look's 6 m, at most the grid step;
        // CloudCells.cginc CELL_SHELL_STEP_MIN), so heads smaller than the grid step are not missed between two grid points. Off: the cover and the outline came back (player, sclock 0.296
        // against the ceiling's 0.292), but the owner saw visible losses against the ceiling's picture, and it cost up to a fifth of the fps (K-T2 perfN2560dlssqleap: ground 37.6 to 29.9).
        internal const string TuneCellShell = "cloud.cellShell";
        internal const float CellShellDefault = 0f;
        internal static bool CellShell { get { return Tuning.Get(TuneCellShell, CellShellDefault) > 0.5f; } }

        // cloud.lightVolume (LV1 prototype, 2026-11-20; wolken-m2a-technik.md section 11.2): 1 lights the march's samples beyond the hand-over distance from the light volume (LightVolume.cs,
        // keyword CLOUD_LIGHTVOL of the march pass) and leaves the light cache (cloudLightCache) unread; 0 (default) today's march. Its levers, each a first value set on pictures later:
        //   cloud.lightVolume.handOverKm   the distance from the camera beyond which a lit sample takes the far path (near taps and one volume read); below 0: infinity (the full march everywhere)
        //   cloud.lightVolume.band         the blend band beyond the hand-over distance, as a share of it (both paths are computed only inside it)
        //   cloud.lightVolume.farTaps      the far path's near taps (3 .. 0; fewer where the shortest is shorter than the pixel's footprint)
        //   cloud.lightVolume.slices       slices refreshed per frame over both cascades, round-robin (0: all, the reference)
        //   cloud.lightVolume.warp         factor on the slices per frame (the time warp's rate; the game's hookup comes later), up to all
        //   cloud.lightVolume.voxelM, .grid, .gridZ, .farScale, .cascades   the near cascade's voxel in m, the voxels across a window and up, the far cascade's coarsening, 1 or 2 cascades
        //   cloud.lightVolume.fade         the fade band at a window's edge in voxels
        //   cloud.lightVolume.relayKm      the travel from the anchor after which the volume is laid anew (refilled once)
        //   cloud.lightVolume.fillTaps, .fillRatio   the direct fill's sun taps over the whole sun march and their growth; fillTaps 0 (default): the march's own taps beyond the far
        //                                  near taps, the full march's estimate of the same path
        internal const string TuneLightVolume = "cloud.lightVolume", TuneLvHandOver = "cloud.lightVolume.handOverKm", TuneLvBand = "cloud.lightVolume.band",
            TuneLvFarTaps = "cloud.lightVolume.farTaps", TuneLvSlices = "cloud.lightVolume.slices", TuneLvWarp = "cloud.lightVolume.warp", TuneLvVoxel = "cloud.lightVolume.voxelM",
            TuneLvGrid = "cloud.lightVolume.grid", TuneLvGridZ = "cloud.lightVolume.gridZ", TuneLvFarScale = "cloud.lightVolume.farScale", TuneLvCascades = "cloud.lightVolume.cascades",
            TuneLvFade = "cloud.lightVolume.fade", TuneLvRelay = "cloud.lightVolume.relayKm", TuneLvFillTaps = "cloud.lightVolume.fillTaps", TuneLvFillRatio = "cloud.lightVolume.fillRatio";
        // The defaults in force of the three light volume keys the fast cloud path changes: the readers here and the viewer's registry (CloudModelKeys) take the same property.
        internal static float LightVolumeDefaultNow { get { return CloudFastPath.Pick(CloudFastPath.LightVolume, 0f); } }
        internal static float LvHandOverDefaultKm { get { return CloudFastPath.Pick(CloudFastPath.LvHandOverKm, 8f); } }
        internal static float LvFarTapsDefaultNow { get { return CloudFastPath.Pick(CloudFastPath.LvFarTaps, 3f); } }
        internal static bool LightVolume { get { return Tuning.Get(TuneLightVolume, LightVolumeDefaultNow) > 0.5f; } }
        // The hand-over distance in m; below 0 infinity.
        internal static float LvHandOverM
        {
            get
            {
                float km = Tuning.Get(TuneLvHandOver, LvHandOverDefaultKm);
                if (float.IsNaN(km)) return 8000f;
                return km < 0f || float.IsInfinity(km) ? -1f : Math.Min(km, 1e5f) * 1000f;
            }
        }
        internal static float LvBand { get { return Clean(Tuning.Get(TuneLvBand, 0.15f), 0.15f, 0.01f, 10f); } }
        internal static int LvFarTaps { get { return (int)Math.Round(Clean(Tuning.Get(TuneLvFarTaps, LvFarTapsDefaultNow), 3f, 0f, 3f)); } }
        internal static int LvSlices { get { return (int)Math.Round(Clean(Tuning.Get(TuneLvSlices, 8f), 8f, 0f, 512f)); } }
        internal static float LvWarp { get { return Clean(Tuning.Get(TuneLvWarp, 1f), 1f, 1f, 1e5f); } }
        internal static float LvVoxelM { get { return Clean(Tuning.Get(TuneLvVoxel, 312.5f), 312.5f, 25f, 10000f); } }
        internal static int LvGrid { get { return (int)Math.Round(Clean(Tuning.Get(TuneLvGrid, 128f), 128f, 16f, 512f)); } }
        internal static int LvGridZ { get { return (int)Math.Round(Clean(Tuning.Get(TuneLvGridZ, 32f), 32f, 4f, 64f)); } }
        internal static float LvFarScale { get { return Clean(Tuning.Get(TuneLvFarScale, 4f), 4f, 1f, 64f); } }
        internal static int LvCascades { get { return (int)Math.Round(Clean(Tuning.Get(TuneLvCascades, 2f), 2f, 1f, 2f)); } }
        internal static float LvFade { get { return Clean(Tuning.Get(TuneLvFade, 4f), 4f, 0.25f, 64f); } }
        internal static float LvRelayM { get { return Clean(Tuning.Get(TuneLvRelay, 100f), 100f, 1f, 5000f) * 1000f; } }
        internal static int LvFillTaps { get { return (int)Math.Round(Clean(Tuning.Get(TuneLvFillTaps, 0f), 0f, 0f, 64f)); } }
        internal static float LvFillRatio { get { return Clean(Tuning.Get(TuneLvFillRatio, 1.15f), 1.15f, 1f, 4f); } }

        // LV2 (wolken-m2a-technik.md 11.5):
        //   cloud.lightVolume.fill         0 (default) a direct march per voxel with the march's own far taps, 1 propagation: a slice takes the slice above where the sun ray crosses it and adds the segment
        //                                  between them, refreshed from the top down
        //   cloud.lightVolume.propSteps, .propCap   the least and most samples of a segment (scaled between them so that they stay about a voxel apart at a low sun)
        //   cloud.lightVolume.propLimit    the longest horizontal run of a segment in voxels; beyond it (a low sun) the voxel takes the direct march
        internal const string TuneLvFill = "cloud.lightVolume.fill", TuneLvPropSteps = "cloud.lightVolume.propSteps", TuneLvPropCap = "cloud.lightVolume.propCap",
            TuneLvPropLimit = "cloud.lightVolume.propLimit";
        internal static int LvFill { get { return (int)Math.Round(Clean(Tuning.Get(TuneLvFill, 0f), 0f, 0f, 1f)); } }
        internal static int LvPropSteps { get { return (int)Math.Round(Clean(Tuning.Get(TuneLvPropSteps, 2f), 2f, 1f, 64f)); } }
        internal static int LvPropCap { get { return (int)Math.Round(Clean(Tuning.Get(TuneLvPropCap, 16f), 16f, 1f, 64f)); } }
        internal static float LvPropLimit { get { return Clean(Tuning.Get(TuneLvPropLimit, 8f), 8f, 0.5f, 1000f); } }
        //   cloud.lightVolume.storeK       0: the volume stores the optical depth; above 0 (default 4): exp(-tau / k), so that the trilinear read interpolates transmittance (read back as -k log)
        //   cloud.lightVolume.warpLimit    the time warp's rate above which the far path is off and the full sun march lights everything (LightVolume.TimeWarpRate, which the host sets)
        internal const string TuneLvStoreK = "cloud.lightVolume.storeK", TuneLvWarpLimit = "cloud.lightVolume.warpLimit";
        // LV3, the lateral spread of the direct light (works at every distance; with it on the volume is filled even at a hand-over distance of infinity):
        //   cloud.lightVolume.spread        0 off; the source of the spread optical depth: 1 the volume blurred over cloud.lightVolume.spreadRadiusM, 2 the far cascade,
        //                                   3 the volume as filled (with the propagation fill its bilinear upstream reads already spread)
        //   cloud.lightVolume.spreadRadiusM the blur's standard deviation in m
        //   cloud.lightVolume.spreadDepth   the optical depth over which the spread's weight grows (1 - exp(-tau / depth)): none at the sunlit surface
        //   cloud.lightVolume.spreadMax     the largest weight
        internal const string TuneLvSpread = "cloud.lightVolume.spread", TuneLvSpreadRadius = "cloud.lightVolume.spreadRadiusM", TuneLvSpreadDepth = "cloud.lightVolume.spreadDepth",
            TuneLvSpreadMax = "cloud.lightVolume.spreadMax";
        internal static int LvSpread { get { return (int)Math.Round(Clean(Tuning.Get(TuneLvSpread, 0f), 0f, 0f, 3f)); } }
        internal static float LvSpreadRadiusM { get { return Clean(Tuning.Get(TuneLvSpreadRadius, 400f), 400f, 10f, 5000f); } }
        internal static float LvSpreadDepth { get { return Clean(Tuning.Get(TuneLvSpreadDepth, 4f), 4f, 0.01f, 100f); } }
        internal static float LvSpreadMax { get { return Clean(Tuning.Get(TuneLvSpreadMax, 1f), 1f, 0f, 1f); } }
        internal static float LvStoreK { get { return Clean(Tuning.Get(TuneLvStoreK, 4f), 4f, 0f, 64f); } }
        internal static float LvWarpLimit { get { return Clean(Tuning.Get(TuneLvWarpLimit, 1000f), 1000f, 1f, 1e7f); } }

        // _CellE of a layer (CloudCells.cginc): x the bulges' reach, y the band in m, z the finer octaves' gain, w the flags (1 cloud.cellTrace, 4 cloud.cellShell).
        internal static UnityEngine.Vector4 CellBodyVector(CloudLayerDef layer)
        {
            float bulge = layer == null ? CloudLayerDef.DefaultCellBulge : CellBulgeFor(layer);
            float band = layer == null ? CloudLayerDef.DefaultCellBandM : CellBandFor(layer);
            float fine = layer == null ? CloudLayerDef.DefaultCellFine : CellFineFor(layer);
            float flags = (CellTrace ? 1f : 0f) + (CellShell ? 4f : 0f);
            return new UnityEngine.Vector4(bulge, band, fine, flags);
        }
        internal static float CumuliformDetailHurst { get { return Clean(Tuning.Get(TuneCumuliformDetail, 0f), 0f, 0f, 2f); } }
        internal static bool CumuliformIso { get { Refresh(); return cumuliformIso > 0.5f; } }

        // The layer mask of the solo key (cloud.soloLayer): bit n - 1 for a value n from 1 to the largest stack; 0 (every layer) otherwise.
        internal static int SoloMask()
        {
            return SoloMaskOf(Tuning.Get(TuneSoloLayer, 0f));
        }

        internal static int SoloMaskOf(float key)
        {
            if (float.IsNaN(key) || float.IsInfinity(key)) return 0;
            int n = (int)Math.Round(key);
            return n >= 1 && n <= CloudLayerDef.MaxLayers ? 1 << (n - 1) : 0;
        }

        // The extra coverage shift of the layer of that name (0: none).
        internal static float ShiftOf(string layerName)
        {
            return Named(LayerShift, TuneLayerShift, layerName, 0f, -1f, 1f);
        }

        // The type value of the layer at no precipitation (-1: the precipitation itself is the type value).
        internal static float TypeFairOf(string layerName)
        {
            return Named(TypeFairByName, TuneTypeFair, layerName, -1f, -1f, 1f);
        }

        // A constant type value for the layer (-1: none).
        internal static float TypeOverrideOf(string layerName)
        {
            return Named(TypeOverrideByName, TuneTypeOverride, layerName, -1f, -1f, 1f);
        }

        // The low stratiform rule of a layer (WeatherLowStratiformRule): the tuning key where it is set (0 off, 1 on with the cfg's numbers or the defaults), else the layer's own (null: none).
        internal static WeatherLowStratiformRule LowStratiformRuleOf(string layerName, WeatherLowStratiformRule own)
        {
            float k = Named(LowStratiformRuleByName, TuneLowStratiformRule, layerName, -1f, -1f, 1f);
            if (k < 0f) return own;
            if (k < 0.5f) return null;
            return own ?? WeatherLowStratiformRule.Defaults;
        }

        // The probe's forced inputs of the low stratiform rule: a precipitation level and a fog share, 0 to 1, the same everywhere (-1: the field's).
        internal static float LowStratiformPrecipOf(string layerName)
        {
            return Named(LowStratiformPrecipByName, TuneLowStratiformPrecip, layerName, -1f, -1f, 1f);
        }

        internal static float LowStratiformFogOf(string layerName)
        {
            return Named(LowStratiformFogByName, TuneLowStratiformFog, layerName, -1f, -1f, 1f);
        }

        // A constant coverage-map value for the layer everywhere (-1: the field's map).
        internal static float MapOverrideOf(string layerName)
        {
            return Named(MapOverrideByName, TuneMapOverride, layerName, -1f, -1f, 1f);
        }

        // The factors of one layer: the general key times the key of that name (cloud.layerSigmaScale.<name> and the like), 1 by default.
        internal static float SigmaScaleOf(string layerName)
        {
            return SigmaScale * Named(SigmaByName, TuneSigma + ".", layerName, 1f, 0.01f, 100f);
        }

        internal static float SoftnessScaleOf(string layerName)
        {
            return SoftnessScale * Named(SoftnessByName, TuneSoftness + ".", layerName, 1f, 0.01f, 100f);
        }

        internal static float NoiseSizeScaleOf(string layerName)
        {
            return NoiseSizeScale * Named(NoiseSizeByName, TuneNoiseSize + ".", layerName, 1f, 0.01f, 100f);
        }

        // The distance fade for a layer that has none of its own: (near, far) in m from cloud.fadeFarM and cloud.fadeNearShare; false where the key is 0.
        internal static bool FadeFor(out float nearM, out float farM)
        {
            Refresh();
            farM = fadeFar;
            nearM = fadeFar * fadeNearShare;
            return fadeFar > 0f;
        }

        // --- Pure functions (the module and the tests call these) ------------------------------------------------------------------------

        // The fair type a layer's type bytes are made with: the tuning key's (cloud.typeFair.<name>) where it is set (0 to 1), else the layer's own (cfg key fairType, -1: none).
        internal static float FairType(float tuningFair, float layerFair)
        {
            return tuningFair >= 0f ? tuningFair : layerFair;
        }

        // The type value of a layer at a place from the precipitation level there (0 to 1): the override, else the fair-weather curve fair + (1 - fair) p, else p itself.
        // With rain below 1 (E40 B3, the layer's cfg key rainType) the curve ends at that type value at full precipitation instead of the last type: fair + (rain - fair) p, and p itself
        // times rain where there is no fair value.
        internal static float TypeValue(float precipitation, float fair, float overrideValue, float rain = 1f)
        {
            if (overrideValue >= 0f) return overrideValue;
            if (fair >= 0f) return fair + (rain - fair) * precipitation;
            return precipitation * rain;
        }

        // The type bytes of a layer from the precipitation bytes. With no override, no fair value and no rain type the source array itself is returned (no copy, no change).
        internal static byte[] TypeBytes(byte[] precipitation, float fair, float overrideValue, ref byte[] scratch, float rain = 1f)
        {
            if (fair < 0f && overrideValue < 0f && rain >= 1f) return precipitation;
            if (scratch == null || scratch.Length != precipitation.Length) scratch = new byte[precipitation.Length];
            for (int i = 0; i < precipitation.Length; i++)
            {
                float v = TypeValue(precipitation[i] / 255f, fair, overrideValue, rain);
                scratch[i] = (byte)Math.Min(255.0, Math.Max(0.0, Math.Round(v * 255.0, MidpointRounding.ToEven)));
            }
            return scratch;
        }

        // The share (0 to 1) of the mid-level sheet in a texel (E40 L2 D1): how much more cloud the generator makes at the sheet's height than at the fair-weather type's height (map values
        // 0 to 1), smoothly from `from` (none) to `full` (all). A sheet shows where the air aloft is moist and the low air is not: the low cumulus is what the low map says.
        internal static float AltoShare(float mid, float low, float from, float full)
        {
            float d = mid - low;
            if (!(full > from)) return d >= from ? 1f : 0f;
            float x = (d - from) / (full - from);
            x = x <= 0f ? 0f : x >= 1f ? 1f : x;
            return x * x * (3f - 2f * x);
        }

        // The type bytes of a layer with the mid-level sheet's rule applied: where the generator makes the sheet (AltoShare of the maps at the sheet's and at the low type's height) and
        // it does not rain (the share goes out by the precipitation from 0 to 0.4: a rain cloud hides the sheet), the type value runs from the type bytes' value to `alto`. mid, low:
        // the layer maps at those heights as bytes. alto < 0, or a map that is missing or of another size: the type bytes themselves (no copy). The source arrays are not changed.
        internal static byte[] ApplyAlto(byte[] type, byte[] precipitation, byte[] mid, byte[] low, float alto, float from, float full, ref byte[] scratch)
        {
            if (alto < 0f || type == null || precipitation == null || mid == null || low == null
                || mid.Length != type.Length || low.Length != type.Length || precipitation.Length != type.Length) return type;
            if (scratch == null || scratch.Length != type.Length) scratch = new byte[type.Length];
            for (int i = 0; i < type.Length; i++)
            {
                float rain = precipitation[i] / 255f / 0.4f;
                rain = rain >= 1f ? 1f : rain;
                float share = AltoShare(mid[i] / 255f, low[i] / 255f, from, full) * (1f - rain * rain * (3f - 2f * rain));
                float v = type[i] / 255f + (alto - type[i] / 255f) * share;
                scratch[i] = (byte)Math.Min(255.0, Math.Max(0.0, Math.Round(v * 255.0, MidpointRounding.ToEven)));
            }
            return scratch;
        }

        // The factor k with 1 - prod(1 - k m_l) = cover for the layer values m (0 to 1) of one texel, by bisection; 1 where the union is already at or under the cover, 0 where
        // the cover is 0.
        internal static float CombineFactor(float[] m, int count, float cover)
        {
            if (cover <= 0f) return 0f;
            if (Union(m, count, 1f) <= cover) return 1f;
            float lo = 0f, hi = 1f;
            // 12 halvings: the factor to 0.0003, far under the byte of the map it scales (E40 F1: the combination runs on every map round)
            for (int i = 0; i < 12; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (Union(m, count, mid) < cover) lo = mid;
                else hi = mid;
            }
            return 0.5f * (lo + hi);
        }

        private static float Union(float[] m, int count, float k)
        {
            double keep = 1.0;
            for (int i = 0; i < count; i++) keep *= 1.0 - Math.Min(1.0, k * m[i]);
            return (float)(1.0 - keep);
        }

        // The layers' maps of one round brought to the field (E40 F1): every layer map the generator makes carries about the whole cover of the field, so the four together close the sky
        // twice over (the union of the 24 maps of the type-mix audit: 0.75 visible against the field's 0.40). CombineCover scales them so that their union is the field's cover, and a
        // low stratiform weather layer that has a rain floor is lifted to it again where it rains (rain falls from a cloud, whatever share of the cover the layer took). rules: the rule of each layer or
        // null; level: the precipitation level map (null: no floors). weight 0 changes nothing.
        internal static void MeetTheField(byte[][] layers, int count, byte[] shared, float weight, WeatherLowStratiformRule[] rules, float[] level)
        {
            MeetTheFieldRange(layers, count, shared, weight, rules, level, 0, shared.Length);
        }

        // The same for the texels from to to - 1 (the module does a round's combination in slices over several frames: the whole map costs 100 ms).
        internal static void MeetTheFieldRange(byte[][] layers, int count, byte[] shared, float weight, WeatherLowStratiformRule[] rules, float[] level, int from, int to)
        {
            if (weight <= 0f) return;
            CombineCoverRange(layers, count, shared, weight, from, to);
            if (rules == null || level == null) return;
            for (int l = 0; l < count; l++)
                if (rules[l] != null) rules[l].RaiseToFloor(layers[l], level, from, to);
        }

        // Scales the layers' coverage bytes in place so that the union of their maps (independent layers overlap at random) meets the shared coverage byte of each texel,
        // by weight (0: unchanged, 1: the full factor). Layers is [layer][texel].
        internal static void CombineCover(byte[][] layers, int count, byte[] shared, float weight)
        {
            if (shared == null) return;
            CombineCoverRange(layers, count, shared, weight, 0, shared.Length);
        }

        internal static void CombineCoverRange(byte[][] layers, int count, byte[] shared, float weight, int from, int to)
        {
            if (weight <= 0f || layers == null || count <= 0) return;
            var m = new float[count];
            for (int t = Math.Max(0, from); t < to; t++)
            {
                bool any = false;
                for (int l = 0; l < count; l++)
                {
                    m[l] = layers[l][t] / 255f;
                    any |= m[l] > 0f;
                }
                if (!any) continue;
                float k = CombineFactor(m, count, shared[t] / 255f);
                float scale = 1f + weight * (k - 1f);
                if (scale == 1f) continue;
                for (int l = 0; l < count; l++)
                    layers[l][t] = (byte)Math.Min(255.0, Math.Max(0.0, Math.Round(m[l] * scale * 255.0, MidpointRounding.ToEven)));
            }
        }
    }
}
