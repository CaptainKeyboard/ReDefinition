using System;
using System.Collections.Generic;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // The layer stack of one body as the CPU consumers read it (ICloudLayerSource): the layers, each with the two byte maps the GPU
    // reads, so that a shadow or a light-in-medium sample sees the same field as the picture. Pure (no Unity), so the tests and the
    // weather player link it.
    internal sealed class LayerStackSource : ICloudLayerSource
    {
        private readonly List<CloudLayerDef> layers = new List<CloudLayerDef>();
        private readonly List<WeatherMap> coverage = new List<WeatherMap>();
        private readonly List<WeatherMap> types = new List<WeatherMap>();
        private readonly List<LayerTypeTable> tables = new List<LayerTypeTable>();
        private long version;

        internal LayerStackSource(int bodyIndex, double bodyRadiusM, bool hasSurface)
        {
            BodyIndex = bodyIndex;
            BodyRadiusM = bodyRadiusM;
            HasSurface = hasSurface;
        }

        public int BodyIndex { get; }
        public double BodyRadiusM { get; }
        public bool HasSurface { get; }
        public int LayerCount { get { return layers.Count; } }
        public long Version { get { return version; } }

        public CloudLayerDef LayerAt(int index)
        {
            return layers[index];
        }

        internal LayerTypeTable TableAt(int index)
        {
            return tables[index];
        }

        internal LayerTypeTable[] Tables()
        {
            return tables.ToArray();
        }

        // Adds a layer above the ones already there (the stack is kept sorted by inner altitude) with its maps (either may be null).
        internal int Add(CloudLayerDef layer, WeatherMap coverageMap, WeatherMap typeMap)
        {
            if (layers.Count >= CloudLayerDef.MaxLayers) throw new InvalidOperationException("At most " + CloudLayerDef.MaxLayers + " layers.");
            int at = layers.Count;
            while (at > 0 && layers[at - 1].InnerAltitudeM > layer.InnerAltitudeM) at--;
            layers.Insert(at, layer);
            coverage.Insert(at, coverageMap);
            types.Insert(at, typeMap);
            tables.Insert(at, new LayerTypeTable(layer));
            version++;
            return at;
        }

        // Replaces the maps of a layer (a map update) and moves the version on.
        internal void SetMaps(int index, WeatherMap coverageMap, WeatherMap typeMap)
        {
            coverage[index] = coverageMap;
            types[index] = typeMap;
            version++;
        }

        public void SampleMaps(int layerIndex, double latDeg, double lonDeg, out float coverageValue, out float typeValue)
        {
            CloudLayerDef layer = layers[layerIndex];
            double lon = lonDeg - layer.MapShiftDeg;
            WeatherMap c = coverage[layerIndex], t = types[layerIndex];
            coverageValue = c == null ? 1f : c.Sample(latDeg, lon);
            typeValue = t == null ? 0f : t.Sample(latDeg, lon);
        }
    }
}
