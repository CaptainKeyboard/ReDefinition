using System;

namespace ReDefinition.Atmospheres
{
    // The read model of the height variation, beside ICloudLayerSource (which other packages implement and read, and which stays as it is):
    // the offsets of a layer at a place and the numbers of its ragged boundary. The CPU consumers (cloud shadows, the light in the medium,
    // the optical depth of the integration) test a source for this interface and fall back to the type table's heights where it is absent.
    internal interface ICloudHeightSource
    {
        // The tuning of a layer, or null where the layer has no height variation.
        HeightTuning HeightOf(int layerIndex);
        // The offsets in m at a place (zero where the layer has no maps).
        LocalHeights SampleHeights(int layerIndex, double latDeg, double lonDeg);
    }

    // The height maps and tuning of the layers of one stack. Layers without an entry keep the type table's heights.
    internal sealed class LayerHeightField
    {
        private readonly HeightMaps[] maps = new HeightMaps[CloudLayerDef.MaxLayers];
        private readonly HeightTuning[] tuning = new HeightTuning[CloudLayerDef.MaxLayers];
        private readonly uint[] seeds = new uint[CloudLayerDef.MaxLayers];

        // Changes whenever a layer's maps or tuning change; the renderer uploads then.
        internal long Version { get; private set; }

        internal HeightMaps MapsOf(int layer)
        {
            return maps[layer];
        }

        internal HeightTuning TuningOf(int layer)
        {
            return tuning[layer];
        }

        // The seed of the ragged boundary of a layer (the noise lattice of the shader and of RaggedAt).
        internal uint SeedOf(int layer)
        {
            return seeds[layer];
        }

        internal void Set(int layer, HeightTuning layerTuning, HeightMaps layerMaps, uint raggedSeed)
        {
            if (layer < 0 || layer >= CloudLayerDef.MaxLayers) throw new ArgumentOutOfRangeException("layer");
            tuning[layer] = layerTuning;
            maps[layer] = layerMaps;
            seeds[layer] = raggedSeed;
            Version++;
        }

        internal void Clear(int layer)
        {
            tuning[layer] = null;
            maps[layer] = null;
            Version++;
        }

        internal LocalHeights Sample(int layer, double latDeg, double lonDeg)
        {
            HeightMaps m = maps[layer];
            return m == null ? default(LocalHeights) : m.Sample(latDeg, lonDeg);
        }

        // How far below the layer's inner altitude and above its outer altitude a cloud of the layer can reach, in m: the widening of the
        // march's tight interval (0 for a layer without height variation).
        internal void Reach(int layer, out float downM, out float upM)
        {
            HeightTuning t = tuning[layer];
            if (t == null || !t.Any)
            {
                downM = upM = 0f;
                return;
            }
            downM = t.DownM;
            upM = t.UpM;
        }
    }
}
