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
    //   cloud.layerNoiseAmountScale       factor on every layer's noise amount (1). It only acts with cloud.coverMeaning 0: with the cover meaning (default) the amount is 1 by definition (the
    //                                     outline stands at noise rank 1 - map value, so a map value is the share of the sky), and a factor above 0 changes nothing but the on / off of the noise
    //   cloud.layerNoiseSizeScale         factor on every layer's noise scale in m (1)
    //   cloud.mapOverride.<name>          a constant coverage-map value for the layer everywhere, 0 to 1 (-1: the field's map); tells a map that varies over the view from the pass
    //   cloud.layerSigmaScale.<name>, cloud.layerSoftnessScale.<name>, cloud.layerNoiseAmountScale.<name>, cloud.layerNoiseSizeScale.<name>
    //                                     the same for the layer of that name only, multiplied with the general key (1)
    //   cloud.layerCoverageShift.<name>   added to the coverage of the layer of that name, on top of cloud.coverageShift (0)
    //   cloud.typeFair.<name>             the type value of the layer at no precipitation, rising linearly to 1 at full precipitation (-1: the precipitation level itself)
    //   cloud.typeOverride.<name>         a constant type value for the layer (-1: none; wins over typeFair)
    //   cloud.deckRule.<name>             1 forces the layer's weather deck rule on (the cfg's numbers, else WeatherDeckRule.Defaults), 0 gives the old rule back (the type is the
    //                                     precipitation level and the deck draws wherever its map passes), -1 (default): the layer's cfg (deckRule = 1 on the weather decks)
    //   cloud.deckPrecip.<name>           the probe's: a precipitation level the layer's deck rule sees everywhere, 0 to 1 (-1: the field's)
    //   cloud.deckFog.<name>              the probe's: a fog share the layer's deck rule sees everywhere, 0 to 1 (-1: the field's dew-point spread and wind)
    //   layers.densityRemap               the share of the erosion that also thins the interior of a closed deck (0: erosion only in the soft edge)
    //   cloud.coverCombine                0 to 1: how far the layer maps are scaled so that their random-overlap union equals the field's cover (0: maps as they are; default 1, E40 F1: each
    //                                     layer map the generator makes carries about the field's whole cover, the four closed the sky twice over)
    //   cloud.fadeFarM                    m: a distance fade for the layers that have none (0: none), starting at cloud.fadeNearShare of it
    //   cloud.fadeNearShare               share of cloud.fadeFarM where the fade starts (0.5)
    //   cloud.farHaze                     -1: the cloudFarHaze setting decides, 0 / 1 forces the aerial perspective off / on, 2 automatic (on where the pass is behind Scatterer's scattering; CloudHazePlan)
    //   cloud.hazeStrength                factor on the aerial perspective's strength (1)
    //   cloud.soloLayer                   solo mode (E40 SOLO): the 1-based index of the only layer the pass draws (0: every layer); the layers' maps and types are the per-layer keys above
    //   cloud.cellKm, cloud.cellLat, cloud.cellLon
    //                                     a forced map (cloud.mapOverride.<name>) as ONE cell instead of a constant: a Gaussian of this e-folding radius in km round the point (degrees),
    //                                     the override's value at its centre, 0 outside (0: the constant map); the one cumulonimbus cell whose tower spreads an anvil (AnvilMap)
    internal static class CloudLookTuning
    {
        // The default of cloud.coverCombine (E40 F1): the layer maps meet the field's cover.
        internal const float DefaultCoverCombine = 1f;

        internal const string TuneSigma = "cloud.layerSigmaScale", TuneSoftness = "cloud.layerSoftnessScale", TuneNoiseAmount = "cloud.layerNoiseAmountScale",
            TuneNoiseSize = "cloud.layerNoiseSizeScale", TuneLayerShift = "cloud.layerCoverageShift.", TuneTypeFair = "cloud.typeFair.", TuneTypeOverride = "cloud.typeOverride.",
            TuneDensityRemap = "layers.densityRemap", TuneCoverCombine = "cloud.coverCombine", TuneFadeFar = "cloud.fadeFarM",
            TuneFadeNearShare = "cloud.fadeNearShare", TuneFarHaze = "cloud.farHaze", TuneHazeStrength = "cloud.hazeStrength", TuneMapOverride = "cloud.mapOverride.",
            TuneDeckRule = "cloud.deckRule.",
            TuneDeckPrecip = "cloud.deckPrecip.", TuneDeckFog = "cloud.deckFog.",
            TuneSoloLayer = "cloud.soloLayer", TuneCellKm = "cloud.cellKm", TuneCellLat = "cloud.cellLat", TuneCellLon = "cloud.cellLon";

        private static long seenVersion = -1;
        private static float sigma = 1f, softness = 1f, noiseAmount = 1f, noiseSize = 1f, remap, combine = DefaultCoverCombine, fadeFar, fadeNearShare = 0.5f, farHaze = -1f, hazeStrength = 1f;
        private static float cellKm, cellLat, cellLon;
        private static float heapForce = -1f, edgeForce, cavityForce = -1f, heapFoot = 1f, heapIso = 1f;
        private static long stamp;
        private static readonly Dictionary<string, float> LayerShift = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> TypeFairByName = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> TypeOverrideByName = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> MapOverrideByName = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> DeckRuleByName = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> DeckPrecipByName = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> DeckFogByName = new Dictionary<string, float>(StringComparer.Ordinal);
        // The per-layer factors (cloud.layerSigmaScale.<name> and the three like it), multiplied with the general ones.
        private static readonly Dictionary<string, float> SigmaByName = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> SoftnessByName = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> NoiseAmountByName = new Dictionary<string, float>(StringComparer.Ordinal);
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
            float s = Scale(Tuning.Get(TuneSigma, 1f)), e = Scale(Tuning.Get(TuneSoftness, 1f)), a = Scale(Tuning.Get(TuneNoiseAmount, 1f)), z = Scale(Tuning.Get(TuneNoiseSize, 1f));
            float r = Unit(Tuning.Get(TuneDensityRemap, 0f)), c = Unit(Tuning.Get(TuneCoverCombine, DefaultCoverCombine));
            float f = Clean(Tuning.Get(TuneFadeFar, 0f), 0f, 0f, 1e7f), n = Clean(Tuning.Get(TuneFadeNearShare, 0.5f), 0.5f, 0f, 1f);
            float h = Tuning.Get(TuneFarHaze, -1f);
            h = float.IsNaN(h) || float.IsInfinity(h) ? -1f : h < 0f ? -1f : h > 1.5f ? 2f : h > 0f ? 1f : 0f;
            float hs = Clean(Tuning.Get(TuneHazeStrength, 1f), 1f, 0f, 100f);
            float ck = Clean(Tuning.Get(TuneCellKm, 0f), 0f, 0f, 1e5f), cla = Clean(Tuning.Get(TuneCellLat, 0f), 0f, -90f, 90f), clo = Clean(Tuning.Get(TuneCellLon, 0f), 0f, -360f, 360f);
            float hf = Clean(Tuning.Get(TuneHeap, -1f), -1f, -1f, HeapModel.PresetCount), ef = Clean(Tuning.Get(TuneEdgePower, 0f), 0f, 0f, 6f);
            float cf = Clean(Tuning.Get(TuneCavity, -1f), -1f, -1f, 1f), ft = Clean(Tuning.Get(TuneHeapFootLod, 1f), 1f, 0f, 1f), hi = Clean(Tuning.Get(TuneHeapIso, 1f), 1f, 0f, 1f);
            hf = hf < 0f ? -1f : (float)Math.Round(hf);
            bool changed = hf != heapForce || ef != edgeForce || cf != cavityForce || ft != heapFoot || hi != heapIso;
            heapIso = hi;
            heapForce = hf;
            edgeForce = ef;
            cavityForce = cf;
            heapFoot = ft;
            changed |= s != sigma || e != softness || a != noiseAmount || z != noiseSize || r != remap || c != combine || f != fadeFar || n != fadeNearShare
                           || h != farHaze || hs != hazeStrength || ck != cellKm || cla != cellLat || clo != cellLon;
            cellKm = ck;
            cellLat = cla;
            cellLon = clo;
            sigma = s;
            softness = e;
            noiseAmount = a;
            noiseSize = z;
            remap = r;
            combine = c;
            fadeFar = f;
            fadeNearShare = n;
            farHaze = h;
            hazeStrength = hs;
            // The per-layer keys of the names asked for so far are read again in place; a value that moved counts as a change.
            changed |= Reread(LayerShift, TuneLayerShift, 0f, -1f, 1f);
            changed |= Reread(TypeFairByName, TuneTypeFair, -1f, -1f, 1f);
            changed |= Reread(TypeOverrideByName, TuneTypeOverride, -1f, -1f, 1f);
            changed |= Reread(MapOverrideByName, TuneMapOverride, -1f, -1f, 1f);
            changed |= Reread(DeckRuleByName, TuneDeckRule, -1f, -1f, 1f);
            changed |= Reread(DeckPrecipByName, TuneDeckPrecip, -1f, -1f, 1f);
            changed |= Reread(DeckFogByName, TuneDeckFog, -1f, -1f, 1f);
            changed |= Reread(HeapByName, TuneHeapLayer, -1f, -1f, HeapModel.PresetCount);
            changed |= Reread(EdgeByName, TuneEdgePowerLayer, 0f, 0f, 6f);
            changed |= Reread(CavityByName, TuneCavityLayer, -1f, -1f, 1f);
            changed |= Reread(SigmaByName, TuneSigma + ".", 1f, 0.01f, 100f);
            changed |= Reread(SoftnessByName, TuneSoftness + ".", 1f, 0.01f, 100f);
            changed |= Reread(NoiseAmountByName, TuneNoiseAmount + ".", 1f, 0.01f, 100f);
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
        internal static float NoiseAmountScale { get { Refresh(); return noiseAmount; } }
        internal static float NoiseSizeScale { get { Refresh(); return noiseSize; } }
        internal static float DensityRemap { get { Refresh(); return remap; } }
        internal static float CoverCombine { get { Refresh(); return combine; } }
        internal static float FarHazeForce { get { Refresh(); return farHaze; } }
        internal static float HazeStrengthScale { get { Refresh(); return hazeStrength; } }
        // The forced map as one cell (cloud.cellKm above 0): its e-folding radius in km and its centre in degrees.
        internal static float CellKm { get { Refresh(); return cellKm; } }
        internal static float CellLat { get { Refresh(); return cellLat; } }
        internal static float CellLon { get { Refresh(); return cellLon; } }

        // --- Billow detail (package PUFF): the small-scale detail of the cloud's surface. Every key is neutral at its default, so the pass is as it was. ---------------------------------
        //   cloud.billow.light        above 0: billow shading, a sample's direct light is scaled by 1 + 2 x this x (d - mean), d the erosion detail's value at the sample (the bumps catch the light,
        //                             the crevices are shaded; no extra density read, which would put a second copy of the erosion into the shader and crash its compiler) (0: off)
        //   cloud.billow.contrast     the erosion detail's contrast about its mean (1)
        //   cloud.billow.top          the erosion's gain toward the cloud's top: erosion x (1 + gain x height in the cloud) (0)
        //   cloud.billow.fine         the weight of the fine octave of the detail (-1: the compiled NoiseModel.FineDetailWeight)
        //   cloud.billow.fineFreq     the fine octave's frequency over the first read (-1: the compiled NoiseModel.FineDetailFrequency)
        //   cloud.billow.sharpen      0 to 1: the composite upsamples the march target with a Catmull-Rom spline, clamped to the four nearest texels (0: bilinear)
        internal const string TuneBillowLight = "cloud.billow.light", TuneBillowContrast = "cloud.billow.contrast", TuneBillowTop = "cloud.billow.top",
            TuneBillowFine = "cloud.billow.fine", TuneBillowFineFreq = "cloud.billow.fineFreq", TuneBillowSharpen = "cloud.billow.sharpen";

        internal static float BillowLight { get { return Clean(Tuning.Get(TuneBillowLight, 0f), 0f, 0f, 8f); } }
        internal static float BillowContrast { get { return Clean(Tuning.Get(TuneBillowContrast, 1f), 1f, 0f, 8f); } }
        internal static float BillowTop { get { return Clean(Tuning.Get(TuneBillowTop, 0f), 0f, 0f, 8f); } }
        internal static float BillowSharpen { get { return Clean(Tuning.Get(TuneBillowSharpen, 0f), 0f, 0f, 1f); } }

        // The weight of the fine octave: the key, or `compiled` where it is below 0.
        internal static float BillowFine(float compiled)
        {
            float v = Clean(Tuning.Get(TuneBillowFine, -1f), -1f, -1f, 1f);
            return v < 0f ? compiled : v;
        }

        // The frequency of the fine octave: the key, or `compiled` where it is not above 0.
        internal static float BillowFineFreq(float compiled)
        {
            float v = Clean(Tuning.Get(TuneBillowFineFreq, -1f), -1f, -1f, 16f);
            return v > 0f ? v : compiled;
        }

        // --- The heap volume (package CLOUDR1): global overrides of the per-type cfg keys heap, edgePower and cavity, for the A/B of the player and the viewer. Every key is neutral at its default. ---
        //   cloud.heap            -1: each type's cfg key heap; 0: no heap anywhere (the shape volume of today); 1, 2: that preset for every type
        //   cloud.edgePower       0: each type's cfg key edgePower (else the layer's waterPower); above 0: this exponent of the density rim for every type of a soft layer
        //   cloud.cavity          -1: each type's cfg key cavity; 0 to 1: this strength for every type that reads a heap
        //   cloud.heap.iso        1: the heap is read at the lattice's true vertical scale, so a layer's verticalScale does not stretch the rounded heaps into columns; 0: as the shape volume is read
        //   cloud.heap.footLod    1: the march's sample spacing raises the heap volume's mip level (an octave is dropped only where it is below the footprint); 0: only the smoothing does
        internal const string TuneHeap = "cloud.heap", TuneEdgePower = "cloud.edgePower", TuneCavity = "cloud.cavity", TuneHeapFootLod = "cloud.heap.footLod", TuneHeapIso = "cloud.heap.iso", TuneHeapDetail = "cloud.heap.detail", TuneHeapWarp = "cloud.heap.warp";

        // --- CLOUDR1 P6: the first hit of the march by bisection (default off). ---
        //   cloud.firstHit        evaluations of the bisection on the span a ray enters (0: off, the march is as it was; 3 is the default of the experiments)
        //   cloud.firstHit.thin   the steps right inside the cloud as a share of the grid step (0.25)
        //   cloud.firstHit.shell  how many thin steps follow the entry, the outer shell of the cloud (32), then the grid step again
        //   cloud.firstHit.near   the distance in m up to which the thin steps are taken (0: everywhere)
        internal const string TuneFirstHit = "cloud.firstHit", TuneFirstHitThin = "cloud.firstHit.thin", TuneFirstHitShell = "cloud.firstHit.shell", TuneFirstHitNear = "cloud.firstHit.near";
        internal static float FirstHitIterations { get { float v = Clean(Tuning.Get(TuneFirstHit, 0f), 0f, 0f, 8f); return (float)Math.Round(v); } }
        internal static float FirstHitThin { get { return Clean(Tuning.Get(TuneFirstHitThin, 0.25f), 0.25f, 0.02f, 1f); } }
        internal static float FirstHitShell { get { return (float)Math.Round(Clean(Tuning.Get(TuneFirstHitShell, 32f), 32f, 0f, 512f)); } }
        internal static float FirstHitNear { get { return Clean(Tuning.Get(TuneFirstHitNear, 0f), 0f, 0f, 1e7f); } }

        // The same per layer (the layer's name after the prefix): the layer's own value wins over the global key, so a look can be tried on one layer (the cumulus layer) and the decks stay as they were.
        internal const string TuneHeapLayer = "cloud.heap.layer.", TuneEdgePowerLayer = "cloud.edgePower.layer.", TuneCavityLayer = "cloud.cavity.layer.";
        private static readonly Dictionary<string, float> HeapByName = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> EdgeByName = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> CavityByName = new Dictionary<string, float>(StringComparer.Ordinal);

        internal static int HeapOverrideFor(string layerName)
        {
            float v = Named(HeapByName, TuneHeapLayer, layerName, -1f, -1f, HeapModel.PresetCount);
            return v >= 0f ? (int)Math.Round(v) : HeapOverride;
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

        internal static int HeapOverride { get { Refresh(); return (int)heapForce; } }
        internal static float EdgePowerOverride { get { Refresh(); return edgeForce; } }
        internal static float CavityOverride { get { Refresh(); return cavityForce; } }
        internal static bool HeapFootLod { get { Refresh(); return heapFoot > 0.5f; } }
        // cloud.heap.detail: the Hurst exponent of the erosion detail volume's octaves (2^(-H k): the heap's cascade continued; 0: the compiled weights, whose finest octave is the heaviest). Read when the
        // volumes are generated (the first frame of a scene), not live.
        // cloud.heap.warp: the share of the untiling displacement's horizontal part the heap lookup keeps (its radial part is always dropped); the default is HeapModel.WarpShare, -1 reads the heap at the
        // fully displaced point like the shape volume (the lookup that strains it: the diagnosis of the pulled look).
        internal static float HeapWarpShare { get { float v = Tuning.Get(TuneHeapWarp, (float)HeapModel.WarpShare); return float.IsNaN(v) || float.IsInfinity(v) ? (float)HeapModel.WarpShare : v < 0f ? -1f : Math.Min(v, 1f); } }
        internal static float HeapDetailHurst { get { return Clean(Tuning.Get(TuneHeapDetail, 0f), 0f, 0f, 2f); } }
        internal static bool HeapIso { get { Refresh(); return heapIso > 0.5f; } }

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

        // The deck rule of a layer (WeatherDeckRule): the tuning key where it is set (0 off, 1 on with the cfg's numbers or the defaults), else the layer's own (null: none).
        internal static WeatherDeckRule DeckRuleOf(string layerName, WeatherDeckRule own)
        {
            float k = Named(DeckRuleByName, TuneDeckRule, layerName, -1f, -1f, 1f);
            if (k < 0f) return own;
            if (k < 0.5f) return null;
            return own ?? WeatherDeckRule.Defaults;
        }

        // The probe's forced inputs of the deck rule: a precipitation level and a fog share, 0 to 1, the same everywhere (-1: the field's).
        internal static float DeckPrecipOf(string layerName)
        {
            return Named(DeckPrecipByName, TuneDeckPrecip, layerName, -1f, -1f, 1f);
        }

        internal static float DeckFogOf(string layerName)
        {
            return Named(DeckFogByName, TuneDeckFog, layerName, -1f, -1f, 1f);
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

        internal static float NoiseAmountScaleOf(string layerName)
        {
            return NoiseAmountScale * Named(NoiseAmountByName, TuneNoiseAmount + ".", layerName, 1f, 0.01f, 100f);
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
        // weather deck that has a rain floor is lifted to it again where it rains (rain falls from a cloud, whatever share of the cover the deck took). decks: the rule of each layer or
        // null; level: the precipitation level map (null: no floors). weight 0 changes nothing.
        internal static void MeetTheField(byte[][] layers, int count, byte[] shared, float weight, WeatherDeckRule[] decks, float[] level)
        {
            MeetTheFieldRange(layers, count, shared, weight, decks, level, 0, shared.Length);
        }

        // The same for the texels from to to - 1 (the module does a round's combination in slices over several frames: the whole map costs 100 ms).
        internal static void MeetTheFieldRange(byte[][] layers, int count, byte[] shared, float weight, WeatherDeckRule[] decks, float[] level, int from, int to)
        {
            if (weight <= 0f) return;
            CombineCoverRange(layers, count, shared, weight, from, to);
            if (decks == null || level == null) return;
            for (int l = 0; l < count; l++)
                if (decks[l] != null) decks[l].RaiseToFloor(layers[l], level, from, to);
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
