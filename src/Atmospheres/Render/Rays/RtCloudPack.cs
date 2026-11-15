using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // The ray-side cloud pack of the own layers (package T18, RtOwnClouds): what the traced rays need to see the same clouds the own pass draws, ALL layers.
    // Pure (no Unity), so the tests and the weather player link it.
    //
    // The pack is a table of layer entries and, per layer, one map of four numbers per texel, equirectangular in the layer's own map frame:
    //   R  the layer's coverage map value (0 to 1) at the texel, as the stack's readers get it (ICloudLayerSource.SampleMaps);
    //   G  the column depth at full cloud, tau_full: the integral over the layer of sigma x profile(type, local heights), the extinction without the coverage
    //      shape (0 where neither the texel nor a neighbour holds cloud);
    //   B  tau_full x the cloud's base at the texel, A tau_full x its top, both as a fraction (0 to 1) of the entry's shell (the type table's interpolated
    //      heights, or the local heights of the height variation: the field's own lb and lt). Weighted by tau_full so that the bilinear filter of the sampler
    //      gives the tau-weighted mean base and top (B / G, A / G), which is what a column mixed from two texels has.
    // The ray side filters all four bilinearly and forms the layer's vertical optical depth at a tap as shape(R) x G with the entry's coverage and softness
    // (LayerDensity.Shape), which is how the raster side and the cloud shadow of T04 read the same field (interpolate the map, then threshold). The edge of a
    // cloud then keeps the sharpness of the map, not of the pack's texel; the coverage shift and the soft edge of the look knobs act on the entry's numbers
    // without a new bake. It is not a second copy of a map: the maps stay in the layer stack (LayerStackSource); the pack holds what they and the type
    // table integrate to, and is made again when the stack's version moves.
    //
    // The cloud sits between its base and top, not across the whole shell: the shell is the interval of every type and every local height of the layer (the
    // cumulonimbus shell is 12 km, a cloud in it 1 to 3 km), and a ray at 45 degrees crosses a shell's thickness in as many km sideways. A ray that took the
    // column at every tap of the shell, spread evenly over its height, would average the clouds of a 12 km wide band into every shadow; a tap therefore counts
    // only the part of its path that lies between the column's base and top (RtCloudRayMath.LayerTau), which is where the cloud shadow's march finds the cloud.
    //
    // Layout, binding for the consumer (RtCloudPackLayout): a texture of Width x (Height x LayerCount), four floats (R32G32B32A32F) per texel, layer i in the rows
    // [i x Height, (i + 1) x Height), row j of a slice at latitude 90 - (j + 0.5) x 180 / Height, column i at longitude -180 + (i + 0.5) x 360 / Width in the
    // layer's map frame (the world longitude is that plus the entry's MapShiftDeg). The consumer clamps a read to its slice's rows.

    // The binding layout of the pack.
    internal static class RtCloudPackLayout
    {
        // The most layers a pack holds: the own pass's own limit.
        internal const int MaxLayers = CloudLayerDef.MaxLayers;
        // The floats a texel holds: R coverage, G tau_full, B and A the cloud's base and top, each weighted by tau_full (see the top of this file). The texture is
        // R32G32B32A32F, so the pack's data is the texture's data (no expansion on the main thread, no staging arrays).
        internal const int Channels = 4;
        // The floats a texel of the header row holds in the pack's own layout (two numbers a texel; the texture expands them to four).
        internal const int HeaderChannels = 2;
        // The largest column depth stored; a column thicker than this is as good as opaque for any light.
        internal const float MaxTau = 65536f;

        // The latitude and longitude (degrees, map frame) of the centre of texel (column, row) of a slice of width x height.
        internal static void TexelLatLon(int column, int row, int width, int height, out double latDeg, out double lonDeg)
        {
            latDeg = 90.0 - (row + 0.5) * 180.0 / height;
            lonDeg = -180.0 + (column + 0.5) * 360.0 / width;
        }

        // The continuous texel coordinates of a latitude and longitude (degrees, map frame): the texel centre (c + 0.5, r + 0.5) is at integer (c, r).
        internal static void TexelCoordinates(double latDeg, double lonDeg, int width, int height, out double x, out double y)
        {
            double lon = lonDeg;
            lon -= 360.0 * Math.Floor((lon + 180.0) / 360.0);
            x = (lon + 180.0) * width / 360.0 - 0.5;
            y = (90.0 - latDeg) * height / 180.0 - 0.5;
        }

        // The size in bytes of the texture for a layer count.
        internal static long Bytes(int width, int height, int layers)
        {
            return (long)width * height * layers * Channels * sizeof(float);
        }

        // The texels of the header row a pack needs (ReDefinition.Core.RayCloudLayout): the body's numbers, then each layer's.
        internal static int HeaderTexels(int layers)
        {
            return RayCloudLayout.GlobalTexels + layers * RayCloudLayout.LayerTexels;
        }

        // The header row the traced rays read below the slices (RayCloudLayout), two floats a texel, `header` of 2 x Width floats: the body's radius
        // and whether it has a surface, then per layer the shell's radii, the shadow strength and coverage, the edge softness and clear-sky K, the water
        // power and map shift, the distance fade (far 0 for none). The live fields are in it, so it is written again whenever the pack's LiveVersion moves.
        internal static void WriteHeader(RtCloudPack pack, float[] header)
        {
            if (header.Length < HeaderTexels(pack.LayerCount) * HeaderChannels)
                throw new ArgumentException("The header row is narrower than the pack's numbers.", "header");
            Array.Clear(header, 0, header.Length);
            header[0] = (float)pack.BodyRadiusM;
            header[1] = pack.HasSurface ? 1f : 0f;
            for (int i = 0; i < pack.LayerCount; i++)
            {
                RtCloudLayerEntry e = pack.Entries[i];
                int k = (RayCloudLayout.GlobalTexels + i * RayCloudLayout.LayerTexels) * HeaderChannels;
                header[k] = (float)e.InnerRadiusM;
                header[k + 1] = (float)e.OuterRadiusM;
                header[k + 2] = e.ShadowStrength;
                header[k + 3] = e.Coverage;
                header[k + 4] = e.EdgeSoftness;
                header[k + 5] = e.ClearSkyK;
                header[k + 6] = e.WaterPower;
                header[k + 7] = e.MapShiftDeg;
                bool fades = !float.IsInfinity(e.FadeFarM) && !float.IsNaN(e.FadeFarM) && e.FadeFarM > e.FadeNearM;
                header[k + 8] = fades ? e.FadeNearM : 0f;
                header[k + 9] = fades ? e.FadeFarM : 0f;
            }
        }
    }

    // One layer of the pack. The geometry and the strength move with the stack and the wind, not with a bake, so a refresh writes them without a new bake.
    internal sealed class RtCloudLayerEntry
    {
        // The layer's index in the stack the pack was made from, and its name.
        public int StackIndex;
        public string Name = "";
        // The tight shell (the interval the march needs, with the height variation's reach) as radii from the body's centre, m, and the shell's middle: the
        // radius of the shadow sphere a consumer may use.
        public double InnerRadiusM, OuterRadiusM, MiddleRadiusM;
        // How far the height variation reaches below and above the type table's shell, m (already in the radii).
        public float HeightDownM, HeightUpM;
        // How strongly the layer shades the ground and the other layers, 0 to 1 (the same number the cloud shadow of T04 reads).
        public float ShadowStrength = 1f;
        // The coverage threshold and the edge softness the shape is formed with now (the layer's EffectiveCoverage and EffectiveEdgeSoftness: the runtime
        // coverage shift and the look knobs are in them).
        public float Coverage = 1f, EdgeSoftness = 0.15f;
        // The soft density's two more numbers (E40 soft density): the clear-sky rule's K of the map value (0: off) and the exponent of the share (1: none).
        public float ClearSkyK, WaterPower = 1f;
        // The longitude the layer's maps are turned by against the body's frame now, degrees.
        public float MapShiftDeg;
        // The distance fade of the layer's picture (P64), m from the eye; Infinity: none. A reflection ray applies it, a light ray does not.
        public float FadeNearM, FadeFarM = float.PositiveInfinity;

        public double ThicknessM { get { return OuterRadiusM - InnerRadiusM; } }
    }

    // The pack: the entries and the data of one body's stack at one version.
    internal sealed class RtCloudPack
    {
        internal RtCloudPack(int bodyIndex, double bodyRadiusM, bool hasSurface, int width, int height, RtCloudLayerEntry[] entries, float[] data, long sourceVersion)
        {
            if (entries == null) throw new ArgumentNullException("entries");
            if (data == null || data.Length != (long)width * height * entries.Length * RtCloudPackLayout.Channels)
                throw new ArgumentException("One width x height slice of four floats per entry.", "data");
            BodyIndex = bodyIndex;
            BodyRadiusM = bodyRadiusM;
            HasSurface = hasSurface;
            Width = width;
            Height = height;
            Entries = entries;
            Data = data;
            SourceVersion = sourceVersion;
        }

        internal int BodyIndex { get; }
        internal double BodyRadiusM { get; }
        internal bool HasSurface { get; }
        internal int Width { get; }
        internal int Height { get; }
        internal RtCloudLayerEntry[] Entries { get; }
        internal int LayerCount { get { return Entries.Length; } }
        // R32G32B32A32F, layer-major, texel-interleaved (see RtCloudPackLayout).
        internal float[] Data { get; }
        // The version of the layer stack the data was baked from.
        internal long SourceVersion { get; }
        // Set by the owner of the pack: a number that rises with every new data set (the consumer re-uploads its texture when it changes) and with every
        // change of the live entry fields.
        internal long Version { get; set; }
        internal long LiveVersion { get; set; }

        // The coverage value and the full-cloud column depth of a layer at a place (degrees, the layer's map frame), bilinear with the longitude wrapped and
        // the latitude clamped: the filter of the consumer's sampler.
        internal void Sample(int layer, double latDeg, double mapLonDeg, out double coverage, out double tauFull)
        {
            double baseWeighted, topWeighted;
            Sample(layer, latDeg, mapLonDeg, out coverage, out tauFull, out baseWeighted, out topWeighted);
        }

        // The same with the two weighted heights (B and A of the texel: tau_full x the base and the top as a fraction of the shell), bilinear like the others.
        internal void Sample(int layer, double latDeg, double mapLonDeg, out double coverage, out double tauFull, out double baseWeighted, out double topWeighted)
        {
            double x, y;
            RtCloudPackLayout.TexelCoordinates(latDeg, mapLonDeg, Width, Height, out x, out y);
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            double fx = x - x0, fy = y - y0;
            int xa = Wrap(x0, Width), xb = Wrap(x0 + 1, Width);
            int ya = Clamp(y0, Height), yb = Clamp(y0 + 1, Height);
            int baseIndex = layer * Width * Height;
            const int c = RtCloudPackLayout.Channels;
            int ta = (baseIndex + ya * Width + xa) * c, tb = (baseIndex + ya * Width + xb) * c, tc = (baseIndex + yb * Width + xa) * c, td = (baseIndex + yb * Width + xb) * c;
            double wa = (1.0 - fx) * (1.0 - fy), wb = fx * (1.0 - fy), wc = (1.0 - fx) * fy, wd = fx * fy;
            coverage = Data[ta] * wa + Data[tb] * wb + Data[tc] * wc + Data[td] * wd;
            tauFull = Data[ta + 1] * wa + Data[tb + 1] * wb + Data[tc + 1] * wc + Data[td + 1] * wd;
            baseWeighted = Data[ta + 2] * wa + Data[tb + 2] * wb + Data[tc + 2] * wc + Data[td + 2] * wd;
            topWeighted = Data[ta + 3] * wa + Data[tb + 3] * wb + Data[tc + 3] * wc + Data[td + 3] * wd;
        }

        // The layer's vertical optical depth at a place: the shape of the filtered coverage times the filtered full-cloud depth.
        internal double ColumnTau(int layer, double latDeg, double mapLonDeg)
        {
            double tau, baseFraction, topFraction;
            Column(layer, latDeg, mapLonDeg, out tau, out baseFraction, out topFraction);
            return tau;
        }

        // The same with the column's base and top as fractions of the shell (the tau-weighted mean of the texels the filter mixes; 0 and 1 where the column holds no cloud).
        internal void Column(int layer, double latDeg, double mapLonDeg, out double tau, out double baseFraction, out double topFraction)
        {
            double coverage, tauFull, baseWeighted, topWeighted;
            Sample(layer, latDeg, mapLonDeg, out coverage, out tauFull, out baseWeighted, out topWeighted);
            RtCloudLayerEntry e = Entries[layer];
            double share = LayerDensity.Shape(CloudLayerDef.ClearSky((float)coverage, e.ClearSkyK), e.Coverage, e.EdgeSoftness);
            tau = CloudWater.Pow(share, e.WaterPower) * tauFull;
            if (tauFull > 1e-6)
            {
                baseFraction = Math.Max(0.0, Math.Min(1.0, baseWeighted / tauFull));
                topFraction = Math.Max(baseFraction, Math.Min(1.0, topWeighted / tauFull));
            }
            else
            {
                baseFraction = 0.0;
                topFraction = 1.0;
            }
        }

        private static int Wrap(int i, int n)
        {
            int m = i % n;
            return m < 0 ? m + n : m;
        }

        private static int Clamp(int i, int n)
        {
            return i < 0 ? 0 : i >= n ? n - 1 : i;
        }
    }
}
