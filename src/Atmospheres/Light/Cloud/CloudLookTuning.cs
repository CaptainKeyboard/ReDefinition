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
    //   cloud.layerNoiseAmountScale       factor on every layer's noise amount (1)
    //   cloud.layerNoiseSizeScale         factor on every layer's noise scale in m (1)
    //   cloud.mapOverride.<name>          a constant coverage-map value for the layer everywhere, 0 to 1 (-1: the field's map); tells a map that varies over the view from the pass
    //   cloud.layerSigmaScale.<name>, cloud.layerSoftnessScale.<name>, cloud.layerNoiseAmountScale.<name>, cloud.layerNoiseSizeScale.<name>
    //                                     the same for the layer of that name only, multiplied with the general key (1)
    //   cloud.layerCoverageShift.<name>   added to the coverage of the layer of that name, on top of cloud.coverageShift (0)
    //   cloud.typeFair.<name>             the type value of the layer at no precipitation, rising linearly to 1 at full precipitation (-1: the precipitation level itself)
    //   cloud.typeOverride.<name>         a constant type value for the layer (-1: none; wins over typeFair)
    //   layers.densityRemap               the share of the erosion that also thins the interior of a closed deck (0: erosion only in the soft edge)
    //   layers.profileShape               how far the type's height profile lifts the coverage threshold where the profile is below 1, in map units (0: the profile scales the
    //                                     density only and every cloud is an upright column with a flat top; above 0 a cloud narrows where its profile falls, so tops are domed).
    //                                     Shader only (_NoiseT.w): the CPU readers and the cloud shadow keep the column shape.
    //   cloud.coverCombine                0 to 1: how far the layer maps are scaled so that their random-overlap union equals the field's cover (0: maps as they are)
    //   cloud.sunTint                     0 to 1: how far the sun colour of the pass is pulled to grey of the same luminance (0: the scene's colour)
    //   cloud.fadeFarM                    m: a distance fade for the layers that have none (0: none), starting at cloud.fadeNearShare of it
    //   cloud.fadeNearShare               share of cloud.fadeFarM where the fade starts (0.5)
    //   cloud.farHaze                     -1: the cloudFarHaze setting decides, 0 / 1 forces the aerial perspective off / on
    //   cloud.hazeStrength                factor on the aerial perspective's strength (1)
    internal static class CloudLookTuning
    {
        internal const string TuneSigma = "cloud.layerSigmaScale", TuneSoftness = "cloud.layerSoftnessScale", TuneNoiseAmount = "cloud.layerNoiseAmountScale",
            TuneNoiseSize = "cloud.layerNoiseSizeScale", TuneLayerShift = "cloud.layerCoverageShift.", TuneTypeFair = "cloud.typeFair.", TuneTypeOverride = "cloud.typeOverride.",
            TuneDensityRemap = "layers.densityRemap", TuneCoverCombine = "cloud.coverCombine", TuneSunTint = "cloud.sunTint", TuneFadeFar = "cloud.fadeFarM",
            TuneFadeNearShare = "cloud.fadeNearShare", TuneFarHaze = "cloud.farHaze", TuneHazeStrength = "cloud.hazeStrength", TuneMapOverride = "cloud.mapOverride.",
            TuneProfileShape = "layers.profileShape";

        private static long seenVersion = -1;
        private static float sigma = 1f, softness = 1f, noiseAmount = 1f, noiseSize = 1f, remap, combine, tint, fadeFar, fadeNearShare = 0.5f, farHaze = -1f, hazeStrength = 1f, profileShape;
        private static long stamp;
        private static readonly Dictionary<string, float> LayerShift = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> TypeFairByName = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> TypeOverrideByName = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> MapOverrideByName = new Dictionary<string, float>(StringComparer.Ordinal);
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
            float r = Unit(Tuning.Get(TuneDensityRemap, 0f)), c = Unit(Tuning.Get(TuneCoverCombine, 0f)), t = Unit(Tuning.Get(TuneSunTint, 0f));
            float f = Clean(Tuning.Get(TuneFadeFar, 0f), 0f, 0f, 1e7f), n = Clean(Tuning.Get(TuneFadeNearShare, 0.5f), 0.5f, 0f, 1f);
            float h = Tuning.Get(TuneFarHaze, -1f);
            h = float.IsNaN(h) || float.IsInfinity(h) ? -1f : h < 0f ? -1f : h > 0f ? 1f : 0f;
            float hs = Clean(Tuning.Get(TuneHazeStrength, 1f), 1f, 0f, 100f);
            bool changed = s != sigma || e != softness || a != noiseAmount || z != noiseSize || r != remap || c != combine || t != tint || f != fadeFar || n != fadeNearShare
                           || h != farHaze || hs != hazeStrength;
            sigma = s;
            softness = e;
            noiseAmount = a;
            noiseSize = z;
            remap = r;
            combine = c;
            tint = t;
            fadeFar = f;
            fadeNearShare = n;
            farHaze = h;
            hazeStrength = hs;
            // Read by the noise binding every frame; it does not change the layers' uploaded numbers, so it does not raise the stamp.
            profileShape = Clean(Tuning.Get(TuneProfileShape, 0f), 0f, 0f, 2f);
            // The per-layer keys of the names asked for so far are read again in place; a value that moved counts as a change.
            changed |= Reread(LayerShift, TuneLayerShift, 0f, -1f, 1f);
            changed |= Reread(TypeFairByName, TuneTypeFair, -1f, -1f, 1f);
            changed |= Reread(TypeOverrideByName, TuneTypeOverride, -1f, -1f, 1f);
            changed |= Reread(MapOverrideByName, TuneMapOverride, -1f, -1f, 1f);
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
        internal static float SunTint { get { Refresh(); return tint; } }
        internal static float FarHazeForce { get { Refresh(); return farHaze; } }
        internal static float HazeStrengthScale { get { Refresh(); return hazeStrength; } }
        // layers.profileShape (0: off): the lift of the coverage threshold where the type's profile is below 1 (the layer shader's _NoiseT.w).
        internal static float ProfileShape { get { Refresh(); return profileShape; } }

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

        // The type value of a layer at a place from the precipitation level there (0 to 1): the override, else the fair-weather curve fair + (1 - fair) p, else p itself.
        // The fair type a layer's type bytes are made with: the tuning key's (cloud.typeFair.<name>) where it is set (0 to 1), else the layer's own (cfg key fairType, -1: none).
        internal static float FairType(float tuningFair, float layerFair)
        {
            return tuningFair >= 0f ? tuningFair : layerFair;
        }

        internal static float TypeValue(float precipitation, float fair, float overrideValue)
        {
            if (overrideValue >= 0f) return overrideValue;
            if (fair >= 0f) return fair + (1f - fair) * precipitation;
            return precipitation;
        }

        // The type bytes of a layer from the precipitation bytes. With no override and no fair value the source array itself is returned (no copy, no change).
        internal static byte[] TypeBytes(byte[] precipitation, float fair, float overrideValue, ref byte[] scratch)
        {
            if (fair < 0f && overrideValue < 0f) return precipitation;
            if (scratch == null || scratch.Length != precipitation.Length) scratch = new byte[precipitation.Length];
            for (int i = 0; i < precipitation.Length; i++)
            {
                float v = TypeValue(precipitation[i] / 255f, fair, overrideValue);
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
            for (int i = 0; i < 24; i++)
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

        // Scales the layers' coverage bytes in place so that the union of their maps (independent layers overlap at random) meets the shared coverage byte of each texel,
        // by weight (0: unchanged, 1: the full factor). Layers is [layer][texel].
        internal static void CombineCover(byte[][] layers, int count, byte[] shared, float weight)
        {
            if (weight <= 0f || layers == null || count <= 0) return;
            var m = new float[count];
            int n = shared.Length;
            for (int t = 0; t < n; t++)
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

        // The sun colour of the pass pulled toward grey of the same luminance by the tint (0: the colour itself).
        internal static Color TintSun(Color sun, float tintAmount)
        {
            if (tintAmount <= 0f) return sun;
            float g = sun.grayscale;
            return new Color(sun.r + (g - sun.r) * tintAmount, sun.g + (g - sun.g) * tintAmount, sun.b + (g - sun.b) * tintAmount, sun.a);
        }
    }
}
