using System;

namespace ReDefinition.Atmospheres
{
    // Makes the data of the ray-side pack from a layer stack, a few texels at a time (package T18). Three stages over the same texels:
    //  1. the coverage map value of the layer at each texel centre (ICloudLayerSource.SampleMaps: one map read);
    //  2. the full-cloud column depth tau_full at the nodes of a lattice of every `stride`-th texel that have cloud near them (the shape of some texel within
    //     `stride` of the node above zero): the layer's density (maps' type value, type table, height variation; not the shader's noise) with the coverage
    //     shape taken out, integrated over the tight interval of the layer by the midpoint rule. A layer without height variation has tau_full as a function of
    //     the type value alone: a table of it is made once and read, so a node costs a lookup;
    //  3. the texels between the nodes get tau_full by bilinear interpolation of the nodes (stride 1: every texel is a node, nothing to do).
    // The ray side forms shape(filtered coverage) x filtered tau_full, which is the layer's vertical optical depth of the cloud shadow
    // (CloudShadowField.VerticalOpticalDepth) wherever the filter has not mixed different columns.
    //
    // The longitude of a texel is in the layer's map frame; the stack's readers take the world longitude, so the baker adds the layer's MapShiftDeg as it
    // stood when the bake began (the entry carries the live shift for the ray side).
    internal sealed class RtCloudTauBaker
    {
        // The entries of the table of tau_full over the type value 0 to 1.
        private const int TypeTable = 129;

        private readonly ICloudLayerSource source;
        private readonly ICloudHeightSource heightSource;
        private readonly LayerTypeTable[] tables;
        private readonly int layers, width, height, steps, stride;
        private readonly float[] data;
        private readonly float[] shift, tightInner, tightOuter, coverage, softness, shapeOfOne;
        private readonly CloudLayerDef[] layerDefs;
        private readonly HeightTuning[] tuning;
        private readonly float[][] byType;
        private readonly long perStage;
        private long cursor;

        internal RtCloudTauBaker(ICloudLayerSource source, LayerTypeTable[] tables, int layers, int width, int height, int steps, int stride = 1)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (layers < 0 || layers > source.LayerCount) throw new ArgumentOutOfRangeException("layers");
            if (tables == null || tables.Length < layers) throw new ArgumentException("One table per layer.", "tables");
            this.source = source;
            heightSource = source as ICloudHeightSource;
            this.tables = tables;
            this.layers = layers;
            this.width = width;
            this.height = height;
            this.steps = Math.Max(1, steps);
            // A stride that divides the width, so that the lattice closes at the seam of the longitude.
            this.stride = Math.Max(1, Math.Min(4, stride));
            while (this.stride > 1 && width % this.stride != 0) this.stride--;
            perStage = (long)layers * width * height;
            data = new float[perStage * RtCloudPackLayout.Channels];
            shift = new float[layers];
            tightInner = new float[layers];
            tightOuter = new float[layers];
            coverage = new float[layers];
            softness = new float[layers];
            shapeOfOne = new float[layers];
            layerDefs = new CloudLayerDef[layers];
            tuning = new HeightTuning[layers];
            byType = new float[layers][];
            for (int l = 0; l < layers; l++)
            {
                CloudLayerDef layer = source.LayerAt(l);
                shift[l] = layer.MapShiftDeg;
                layer.TightAltitudes(out tightInner[l], out tightOuter[l]);
                coverage[l] = layer.EffectiveCoverage;
                softness[l] = layer.EffectiveEdgeSoftness;
                // The shape at a map value of 1: a density with the shape in it is divided by it, so that tau_full does not depend on the coverage threshold
                // of the bake.
                // (E40 soft density: the share is the layer's, with the clear-sky rule and the exponent, as the ray side forms it.)
                layerDefs[l] = layer;
                shapeOfOne[l] = (float)Math.Max(1e-3, LayerDensity.Share(layer, 1.0));
                HeightTuning t = heightSource == null ? null : heightSource.HeightOf(l);
                tuning[l] = t != null && t.Any ? t : null;
            }
            SourceVersion = source.Version;
        }

        internal long SourceVersion { get; }
        internal float[] Data { get { return data; } }
        internal bool Done { get { return cursor >= 3 * perStage; } }
        internal double Progress { get { return perStage == 0 ? 1.0 : (double)cursor / (3 * perStage); } }
        // The texels of the pack.
        internal long Texels { get { return perStage; } }
        // The units of work: three stages over the texels.
        internal long WorkUnits { get { return 3 * perStage; } }

        // tau_full over the type value for a layer without height variation: the table type -> integral of the profile, with the shape out.
        private float[] TypeDepthTable(int layerIndex, CloudLayerDef layer, LayerTypeTable table)
        {
            var result = new float[TypeTable];
            // The steps stand inside the interpolated type's own base and top; a table is made once per layer and bake, so it can afford twice the steps.
            int n = Math.Max(steps, 16);
            for (int i = 0; i < TypeTable; i++)
            {
                double type = i / (double)(TypeTable - 1), tau = 0.0, b, t;
                table.InterpolatedHeights(type, out b, out t);
                double inner = layer.InnerAltitudeM + b * layer.ThicknessM, dz = (t - b) * layer.ThicknessM / n;
                for (int k = 0; k < n; k++)
                    tau += LayerDensity.Extinction(layer, table, inner + dz * (k + 0.5), 1.0, type) * dz;
                result[i] = (float)Math.Min(tau / shapeOfOne[layerIndex], RtCloudPackLayout.MaxTau);
            }
            return result;
        }

        // Bakes up to `maxTexels` more visits; true when the bake is complete.
        internal bool Step(int maxTexels)
        {
            return Step(maxTexels, null);
        }

        // The same, stopping early when `stop` answers true (a time budget); the check runs every 64 visits.
        internal bool Step(int maxTexels, Func<bool> stop)
        {
            int done = 0;
            long total = 3 * perStage;
            while (cursor < total && done < maxTexels)
            {
                long visit = cursor;
                int stage = (int)(visit / perStage);
                long index = visit - stage * perStage;
                int l = (int)(index / ((long)width * height));
                int inSlice = (int)(index - (long)l * width * height);
                int row = inSlice / width, column = inSlice - row * width;
                if (stage == 0) BakeCoverage(l, column, row, index);
                else if (stage == 1) BakeNode(l, column, row, index);
                else if (stride > 1) FillBetween(l, column, row, index);
                cursor++;
                done++;
                if (stop != null && (done & 63) == 0 && stop()) break;
            }
            return cursor >= total;
        }

        private void BakeCoverage(int l, int column, int row, long index)
        {
            double lat, lonMap;
            RtCloudPackLayout.TexelLatLon(column, row, width, height, out lat, out lonMap);
            float cover, type;
            source.SampleMaps(l, lat, lonMap + shift[l], out cover, out type);
            data[index * 2] = cover;
            data[index * 2 + 1] = 0f;
        }

        private double ShapeAt(int l, int column, int row)
        {
            int c = column < 0 ? column + width : column >= width ? column - width : column;
            int r = row < 0 ? 0 : row >= height ? height - 1 : row;
            return LayerDensity.Share(layerDefs[l], data[((long)l * width * height + (long)r * width + c) * 2]);
        }

        private void BakeNode(int l, int column, int row, long index)
        {
            if ((column % stride) != 0 || (row % stride) != 0) return;
            // Cloud in a texel the filter reaches from this node: the nodes around a cloudy texel all carry a depth.
            bool near = false;
            for (int dr = -stride; dr <= stride && !near; dr++)
                for (int dc = -stride; dc <= stride && !near; dc++)
                    if (ShapeAt(l, column + dc, row + dr) > 0.0) near = true;
            if (!near) return;
            double lat, lonMap;
            RtCloudPackLayout.TexelLatLon(column, row, width, height, out lat, out lonMap);
            double lon = lonMap + shift[l];
            float cover, type;
            source.SampleMaps(l, lat, lon, out cover, out type);
            if (tuning[l] == null)
            {
                // Made on the first node of the layer, so that the construction of the baker stays cheap.
                if (byType[l] == null) byType[l] = TypeDepthTable(l, source.LayerAt(l), tables[l]);
                double x = Math.Max(0.0, Math.Min(1.0, type)) * (TypeTable - 1);
                int i = Math.Min(TypeTable - 2, (int)x);
                double f = x - i;
                data[index * 2 + 1] = (float)(byType[l][i] * (1.0 - f) + byType[l][i + 1] * f);
                return;
            }
            CloudLayerDef layer = source.LayerAt(l);
            LayerTypeTable table = tables[l];
            LocalHeights heights = heightSource.SampleHeights(l, lat, lon);
            // The steps stand inside the cloud's own interval at this place (its local base and top), not the layer's tight interval: the height variation
            // widens the tight interval to several times the thickness of the cloud, and a midpoint rule over all of it would put one or two steps in the cloud.
            double bottom, top;
            LocalInterval(layer, table, tuning[l], type, heights, out bottom, out top);
            if (!(top > bottom)) return;
            double dz = (top - bottom) / steps;
            double tau = 0.0;
            for (int k = 0; k < steps; k++)
                tau += HeightDensity.Extinction(layer, table, tuning[l], bottom + dz * (k + 0.5), 1.0, type, heights, 0.0) * dz;
            data[index * 2 + 1] = (float)Math.Min(tau / shapeOfOne[l], RtCloudPackLayout.MaxTau);
        }

        // The altitudes (m above sea level) between which HeightDensity.Extinction is not zero at a place: the interpolated type's base and top with the local
        // offsets, the way HeightDensity.Extinction forms them.
        private static void LocalInterval(CloudLayerDef layer, LayerTypeTable table, HeightTuning t, double typeMap, LocalHeights heights, out double lo, out double hi)
        {
            double thickness = layer.ThicknessM;
            int a, c;
            double f, b, top;
            table.Neighbours(typeMap, out a, out c, out f);
            table.InterpolatedHeights(typeMap, out b, out top);
            float ba, ta, ea, bc, tc, ec;
            table.Info(a, out ba, out ta, out ea);
            table.Info(c, out bc, out tc, out ec);
            double erosion = ea + (ec - ea) * f;
            double lb, lt;
            LayerGeometry.LocalBaseTop(b, top, erosion, thickness, heights, 0.0, t.BaseRagged ? t.RaggedM : 0.0, t.MinThicknessM, out lb, out lt,
                                       -layer.InnerAltitudeM / thickness);
            lo = layer.InnerAltitudeM + lb * thickness;
            hi = layer.InnerAltitudeM + lt * thickness;
        }

        // A texel between the nodes: the bilinear mean of the four nodes round it (longitude wraps; the last rows use the last node row).
        private void FillBetween(int l, int column, int row, long index)
        {
            if ((column % stride) == 0 && (row % stride) == 0) return;
            int c0 = column - column % stride, r0 = row - row % stride;
            int c1 = c0 + stride, r1 = Math.Min(r0 + stride, ((height - 1) / stride) * stride);
            if (c1 >= width) c1 -= width;
            double fx = (column - c0) / (double)stride, fy = r1 == r0 ? 0.0 : (row - r0) / (double)stride;
            long slice = (long)l * width * height;
            double a = data[(slice + (long)r0 * width + c0) * 2 + 1], b = data[(slice + (long)r0 * width + c1) * 2 + 1];
            double c = data[(slice + (long)r1 * width + c0) * 2 + 1], d = data[(slice + (long)r1 * width + c1) * 2 + 1];
            data[index * 2 + 1] = (float)((a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy);
        }
    }
}
