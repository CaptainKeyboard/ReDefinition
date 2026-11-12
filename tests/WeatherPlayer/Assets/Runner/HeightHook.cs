using System;
using System.IO;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.WeatherPlayer
{
    // The height variation in the layer scene (package T13): <input>/heights.json names, per job id, the switches, the tuning overrides and the
    // input maps (raw R8, the job's map size) the layers' height maps are built from with the mod's own HeightMapBuilder. A job without an entry
    // has no height variation (the BEFORE pictures). The hook runs on the finished stack before the renderer uploads it.
    [Serializable]
    public sealed class HeightSpecFile
    {
        public HeightSpec[] specs;
    }

    [Serializable]
    public sealed class HeightSpec
    {
        public string id;
        // The four switches (1 on, 0 off).
        public int variation = 1, physics = 1, topUpdraft = 1, ragged = 1;
        // Overrides of the tuning numbers; a value of -999 keeps the compiled one.
        public float amplitude = -999f, wavelength = -999f, sizeTrend = -999f, sizeRadius = -999f, raggedM = -999f, raggedScale = -999f,
                     topGain = -999f, referenceUpdraft = -999f, topJitter = -999f, physicsMax = -999f;
        // Raw R8 maps of the job's map size: relative humidity 0 to 1, updraft 0 to 12 m/s. Empty: none.
        public string humidityMap, updraftMap;
        // The resolution of the height maps, and the body's seed.
        public int width = 1024, height = 512;
        public uint seed = 1u;
    }

    internal static class HeightHook
    {
        private static HeightSpecFile cached;
        private static string cachedFolder;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            LayersSetup.BeforeUpload = Apply;
        }

        private static float Pick(float value, float fallback)
        {
            return value > -998f ? value : fallback;
        }

        private static void Apply(LayersJob job, string input, LayerStackSource source)
        {
            string path = Path.Combine(input, "heights.json");
            if (!File.Exists(path)) return;
            if (cached == null || cachedFolder != input)
            {
                cached = JsonUtility.FromJson<HeightSpecFile>(File.ReadAllText(path));
                cachedFolder = input;
            }
            HeightSpec spec = null;
            foreach (HeightSpec s in cached.specs)
                if (s.id == job.id) { spec = s; break; }
            if (spec == null) return;

            var body = new HeightTuning
            {
                BaseVariation = spec.variation != 0,
                BasePhysics = spec.physics != 0,
                TopFromUpdraft = spec.topUpdraft != 0,
                BaseRagged = spec.ragged != 0
            };
            var d = new HeightTuning();
            body.VariationAmplitudeM = Pick(spec.amplitude, d.VariationAmplitudeM);
            body.VariationWavelengthM = Pick(spec.wavelength, d.VariationWavelengthM);
            body.SizeTrendM = Pick(spec.sizeTrend, d.SizeTrendM);
            body.SizeRadiusM = Pick(spec.sizeRadius, d.SizeRadiusM);
            body.RaggedM = Pick(spec.raggedM, d.RaggedM);
            body.RaggedScaleM = Pick(spec.raggedScale, d.RaggedScaleM);
            body.TopGainMPerMs = Pick(spec.topGain, d.TopGainMPerMs);
            body.ReferenceUpdraftMs = Pick(spec.referenceUpdraft, d.ReferenceUpdraftMs);
            body.TopJitterM = Pick(spec.topJitter, d.TopJitterM);
            body.PhysicsMaxM = Pick(spec.physicsMax, d.PhysicsMaxM);

            float[] humidity = Load(job, input, spec.humidityMap, spec.width, spec.height, 1f / 255f);
            float[] updraft = Load(job, input, spec.updraftMap, spec.width, spec.height, 12f / 255f);
            for (int l = 0; l < source.LayerCount; l++)
            {
                // The maps of a layer depend on the switches and the layer only: the 700-odd jobs of the pop sweep share them.
                string key = spec.variation + "" + spec.physics + spec.topUpdraft + spec.ragged + "|" + source.LayerAt(l).Name + "|" + spec.seed + "|" + job.planetRadius;
                HeightMaps cachedMaps;
                if (MapCache.TryGetValue(key, out cachedMaps))
                {
                    HeightTuning ct = body.ForLayer(source.LayerAt(l).Kind);
                    uint cs = (spec.seed * 0x9E3779B1u + (uint)(l + 1) * 0x85EBCA6Bu) & 0xFFFFFFu;
                    ct.Seed = cs;
                    source.SetHeights(l, ct, cachedMaps, cs);
                    continue;
                }
                CloudLayerDef layer = source.LayerAt(l);
                string coverageFile = null;
                foreach (LayersJobLayer jl in job.layers)
                    if (jl.name == layer.Name) coverageFile = jl.coverageMap;
                var inputs = new HeightInputs
                {
                    Width = spec.width,
                    Height = spec.height,
                    Humidity = humidity,
                    Updraft = updraft,
                    Coverage = Load(job, input, coverageFile, spec.width, spec.height, 1f / 255f)
                };
                // The coverage map holds ranks; the share of the sky the layer clouds is where the rank is above 1 - Coverage.
                if (inputs.Coverage != null)
                    for (int i = 0; i < inputs.Coverage.Length; i++) inputs.Coverage[i] = inputs.Coverage[i] > 1f - layer.Coverage ? 1f : 0f;
                HeightTuning t = body.ForLayer(layer.Kind);
                uint seed = (spec.seed * 0x9E3779B1u + (uint)(l + 1) * 0x85EBCA6Bu) & 0xFFFFFFu;
                t.Seed = seed;
                HeightMaps built = HeightMapBuilder.Build(inputs, t, job.planetRadius, seed);
                MapCache[key] = built;
                source.SetHeights(l, t, built, seed);
            }
        }

        private static readonly System.Collections.Generic.Dictionary<string, HeightMaps> MapCache = new System.Collections.Generic.Dictionary<string, HeightMaps>();

        // A raw R8 map of the job's map size, averaged down to w x h and scaled; null where the file is not named.
        private static float[] Load(LayersJob job, string input, string file, int w, int h, float scale)
        {
            if (string.IsNullOrEmpty(file)) return null;
            string cacheKey = file + "|" + w + "|" + h + "|" + scale;
            float[] hit;
            if (LoadCache.TryGetValue(cacheKey, out hit)) return (float[])hit.Clone();
            byte[] raw = File.ReadAllBytes(Path.Combine(input, file));
            int mw = job.mapWidth, mh = job.mapHeight;
            var result = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                int y0 = y * mh / h, y1 = Math.Max(y0 + 1, (y + 1) * mh / h);
                for (int x = 0; x < w; x++)
                {
                    int x0 = x * mw / w, x1 = Math.Max(x0 + 1, (x + 1) * mw / w);
                    double sum = 0.0;
                    int n = 0;
                    for (int yy = y0; yy < y1; yy++)
                        for (int xx = x0; xx < x1; xx++) { sum += raw[yy * mw + xx]; n++; }
                    result[y * w + x] = (float)(sum / n) * scale;
                }
            }
            LoadCache[cacheKey] = result;
            return (float[])result.Clone();
        }

        private static readonly System.Collections.Generic.Dictionary<string, float[]> LoadCache = new System.Collections.Generic.Dictionary<string, float[]>();
    }
}
