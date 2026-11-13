using System;
using System.Collections.Generic;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T18
{
    // A stack of layers on a body of real size with a coverage function of (latitude, longitude) per layer, the longitude shift of the real stack applied the way
    // LayerStackSource applies it. The layers are slabs (constant density between two altitudes); a layer's coverage 0.5 with a map of 1 is full cloud.
    internal sealed class LatLonSource : ICloudLayerSource
    {
        private readonly List<CloudLayerDef> layers = new List<CloudLayerDef>();
        private readonly List<Func<double, double, double>> covers = new List<Func<double, double, double>>();

        internal LatLonSource(double radiusM, bool hasSurface = true)
        {
            BodyRadiusM = radiusM;
            HasSurface = hasSurface;
        }

        public int BodyIndex { get { return 1; } }
        public double BodyRadiusM { get; }
        public bool HasSurface { get; }
        public int LayerCount { get { return layers.Count; } }
        public long Version { get; set; } = 1;
        public CloudLayerDef LayerAt(int index) { return layers[index]; }

        internal LatLonSource Add(string name, double baseM, double topM, double tau, Func<double, double, double> cover, double softness = 0.15, CloudTypeDef type = null)
        {
            var layer = new CloudLayerDef(name, 1, (float)baseM, (float)topM, new[] { type ?? CloudTypeDef.Slab("s", 0f, 1f) }, (float)(tau / (topM - baseM)));
            layer.Coverage = 0.5f;
            layer.EdgeSoftness = (float)softness;
            layers.Add(layer);
            covers.Add(cover);
            return this;
        }

        public void SampleMaps(int layerIndex, double latDeg, double lonDeg, out float coverage, out float type)
        {
            coverage = (float)covers[layerIndex](latDeg, lonDeg - layers[layerIndex].MapShiftDeg);
            type = 0f;
        }

        internal LayerTypeTable[] Tables()
        {
            var tables = new LayerTypeTable[layers.Count];
            for (int i = 0; i < tables.Length; i++) tables[i] = new LayerTypeTable(layers[i]);
            return tables;
        }

        internal CloudShadowField Field(int steps = 16, int disc = 1)
        {
            return new CloudShadowField(this, Tables()) { StepsPerSpan = steps, DiscSamples = disc };
        }

        // The map-frame position of a place at an altitude above sea level.
        internal void Position(double latDeg, double lonDeg, double altitudeM, out double x, out double y, out double z)
        {
            double la = latDeg * Math.PI / 180.0, lo = lonDeg * Math.PI / 180.0, r = BodyRadiusM + altitudeM;
            x = Math.Cos(la) * Math.Cos(lo) * r;
            y = Math.Cos(la) * Math.Sin(lo) * r;
            z = Math.Sin(la) * r;
        }

        // The direction of a sun at an elevation (degrees) and azimuth (degrees from north towards east) seen from a place, as a map-frame vector.
        internal static void SunTowards(double latDeg, double lonDeg, double elevationDeg, double azimuthDeg, out double sx, out double sy, out double sz)
        {
            double la = latDeg * Math.PI / 180.0, lo = lonDeg * Math.PI / 180.0;
            double ux = Math.Cos(la) * Math.Cos(lo), uy = Math.Cos(la) * Math.Sin(lo), uz = Math.Sin(la);
            double ex = -Math.Sin(lo), ey = Math.Cos(lo), ez = 0.0;
            double nx = -Math.Sin(la) * Math.Cos(lo), ny = -Math.Sin(la) * Math.Sin(lo), nz = Math.Cos(la);
            double e = elevationDeg * Math.PI / 180.0, a = azimuthDeg * Math.PI / 180.0;
            double h = Math.Cos(e), v = Math.Sin(e);
            sx = h * (Math.Cos(a) * nx + Math.Sin(a) * ex) + v * ux;
            sy = h * (Math.Cos(a) * ny + Math.Sin(a) * ey) + v * uy;
            sz = h * (Math.Cos(a) * nz + Math.Sin(a) * ez) + v * uz;
        }
    }

    internal static class PackScenes
    {
        // Kerbin's size: the texel of a 256 x 128 slice is 14.7 km on it.
        internal const double Kerbin = 600000.0;

        // A bake to the end in one call.
        internal static RtCloudPack Bake(ICloudLayerSource source, LayerTypeTable[] tables, int width = 512, int steps = 8, int layers = -1)
        {
            int n = layers < 0 ? source.LayerCount : layers;
            var baker = new RtCloudTauBaker(source, tables, n, width, width / 2, steps);
            baker.Step(int.MaxValue);
            var entries = new RtCloudLayerEntry[n];
            for (int i = 0; i < n; i++) entries[i] = RtCloudPackDriver.EntryFor(source, i);
            return new RtCloudPack(source.BodyIndex, source.BodyRadiusM, source.HasSurface, width, width / 2, entries, baker.Data, baker.SourceVersion);
        }

        // A smooth cloud field: a product of two cosines, 1 on the crests and 0 in the troughs, with the given number of crests round the body.
        internal static Func<double, double, double> Broken(int crestsLon, int crestsLat, double phaseLon = 0.0)
        {
            return (lat, lon) =>
            {
                double a = 0.5 + 0.5 * Math.Cos((lon - phaseLon) * Math.PI / 180.0 * crestsLon);
                double b = 0.5 + 0.5 * Math.Cos(lat * Math.PI / 180.0 * crestsLat);
                return a * b;
            };
        }

        // A deck over a lat/lon box with a linear ramp of the given width (degrees) at its edges: 1 inside, 0 outside.
        internal static Func<double, double, double> Box(double latMin, double latMax, double lonMin, double lonMax, double rampDeg)
        {
            return (lat, lon) =>
            {
                double wl = Ramp(lon - lonMin, lonMax - lon, rampDeg), wt = Ramp(lat - latMin, latMax - lat, rampDeg);
                return Math.Min(wl, wt);
            };
        }

        private static double Ramp(double fromLow, double fromHigh, double ramp)
        {
            double d = Math.Min(fromLow, fromHigh);
            return ramp <= 0.0 ? (d > 0.0 ? 1.0 : 0.0) : Math.Max(0.0, Math.Min(1.0, d / ramp + 0.5));
        }

        // The field as the real stack reads it: a 512 x 256 weather map filled at its texel centres from a function, read back bilinear (WeatherMap.Sample).
        internal static Func<double, double, double> OnMap(Func<double, double, double> atCentres)
        {
            var map = new ReDefinition.Core.WeatherMap();
            map.Resize(512, 256);
            for (int j = 0; j < 256; j++)
                for (int i = 0; i < 512; i++)
                {
                    double lat, lon;
                    RtCloudPackLayout.TexelLatLon(i, j, 512, 256, out lat, out lon);
                    map.Values[j * 512 + i] = (float)Math.Max(0.0, Math.Min(1.0, atCentres(lat, lon)));
                }
            return (lat, lon) => map.Sample(lat, lon);
        }

        // Value noise on the map's lattice: cells of `cell` texels, two octaves, contrast about 0.5, deterministic by seed.
        internal static Func<double, double, double> Noise(uint seed, int cell)
        {
            Func<int, int, double> lattice = (i, j) =>
            {
                uint h = (uint)(i * 73856093) ^ (uint)(j * 19349663) ^ (seed * 83492791u);
                h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
                return (h & 0xFFFFu) / 65535.0;
            };
            Func<double, double, int, double> octave = (u, v, c) =>
            {
                double x = u / c, y = v / c;
                int i = (int)Math.Floor(x), j = (int)Math.Floor(y);
                double fx = x - i, fy = y - j;
                fx = fx * fx * (3 - 2 * fx);
                fy = fy * fy * (3 - 2 * fy);
                double a = lattice(i, j) * (1 - fx) + lattice(i + 1, j) * fx, b = lattice(i, j + 1) * (1 - fx) + lattice(i + 1, j + 1) * fx;
                return a * (1 - fy) + b * fy;
            };
            return OnMap((lat, lon) =>
            {
                double u = (lon + 180.0) / 360.0 * 512, v = (90.0 - lat) / 180.0 * 256;
                double n = (octave(u, v, cell) + 0.35 * octave(u, v, Math.Max(1, cell / 2))) / 1.35;
                return 0.5 + (n - 0.5) * 1.6;
            });
        }

        internal static double Relative(double value, double reference)
        {
            return Math.Abs(value - reference) / Math.Max(1e-9, Math.Abs(reference));
        }
    }
}
