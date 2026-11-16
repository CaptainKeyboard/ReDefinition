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
        // The layer's numbers of the share as they stood when the bake began (the clear-sky K, the exponent), and per texel whether the share is above 0: the near
        // check of a node reads 25 texels, and the layer's getters (the clear-sky K walks the types) are too dear for that.
        private readonly float[] clearK;
        private readonly double[] shareAtOne, shareRatio, sigma;
        private readonly byte[] cloudy;
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
            shareAtOne = new double[layers];
            shareRatio = new double[layers];
            sigma = new double[layers];
            clearK = new float[layers];
            cloudy = new byte[perStage];
            layerDefs = new CloudLayerDef[layers];
            tuning = new HeightTuning[layers];
            byType = new float[layers][];
            ownWeight = new double[layers];
            ownPowerSum = new double[layers];
            totalWeight = new double[layers];
            for (int l = 0; l < layers; l++)
            {
                CloudLayerDef layer = source.LayerAt(l);
                shift[l] = layer.MapShiftDeg;
                layer.TightAltitudes(out tightInner[l], out tightOuter[l]);
                coverage[l] = layer.EffectiveCoverage;
                softness[l] = layer.EffectiveEdgeSoftness;
                clearK[l] = layer.ClearSkyK;
                // The shape at a map value of 1: a density with the shape in it is divided by it, so that tau_full does not depend on the coverage threshold
                // of the bake.
                // (E40 soft density: the share is the layer's, with the clear-sky rule and the exponent, as the ray side forms it.)
                layerDefs[l] = layer;
                shareAtOne[l] = LayerDensity.Share(layer, 1.0);
                shapeOfOne[l] = (float)Math.Max(1e-3, shareAtOne[l]);
                shareRatio[l] = shareAtOne[l] <= 0.0 ? 0.0 : shareAtOne[l] / shapeOfOne[l];
                sigma[l] = layer.EffectiveSigmaPerM;
                HeightTuning t = heightSource == null ? null : heightSource.HeightOf(l);
                tuning[l] = t != null && t.Any ? t : null;
            }
            SourceVersion = source.Version;
        }

        // The exponent of the density rim the ray side applies per layer (the pack has one number per layer, not one per texel): the layer's waterPower where a type has no
        // edgePower of its own, the types' own exponents where it has. ownShare is the share of the layer's depth (area-weighted over the nodes) that lies in types with an
        // exponent of their own, ownPower the depth-weighted mean of those exponents; the live exponent is ownShare * ownPower + (1 - ownShare) * the layer's now.
        private readonly double[] ownWeight, ownPowerSum, totalWeight;

        internal void OwnEdge(int layerIndex, out float ownShare, out float ownPower)
        {
            ownShare = totalWeight[layerIndex] > 0.0 ? (float)(ownWeight[layerIndex] / totalWeight[layerIndex]) : 0f;
            ownPower = ownWeight[layerIndex] > 0.0 ? (float)(ownPowerSum[layerIndex] / ownWeight[layerIndex]) : 1f;
        }

        // The depth of a node (area weight cos(latitude)) into the sums of the layer's exponents.
        private void AccumulateEdge(int l, double latDeg, double type, double tau)
        {
            double w = tau * Math.Cos(latDeg * Math.PI / 180.0);
            if (!(w > 0.0)) return;
            CloudLayerDef layer = layerDefs[l];
            int a, c;
            double f;
            tables[l].Neighbours(type, out a, out c, out f);
            totalWeight[l] += w;
            double oa = layer.HasOwnEdgePower(layer.Types[a]) ? 1.0 - f : 0.0, oc = layer.HasOwnEdgePower(layer.Types[c]) ? f : 0.0;
            if (oa + oc <= 0.0) return;
            ownWeight[l] += w * (oa + oc);
            ownPowerSum[l] += w * (oa * layer.EffectiveWaterPowerOf(layer.Types[a]) + oc * layer.EffectiveWaterPowerOf(layer.Types[c]));
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
            const int c = RtCloudPackLayout.Channels;
            data[index * c] = cover;
            data[index * c + 1] = 0f;
            data[index * c + 2] = 0f;
            data[index * c + 3] = 0f;
            cloudy[index] = LayerDensity.Shape(CloudLayerDef.ClearSky(cover, clearK[l]), coverage[l], softness[l]) > 0.0 ? (byte)1 : (byte)0;
        }

        private bool CloudyAt(int l, int column, int row)
        {
            int c = column < 0 ? column + width : column >= width ? column - width : column;
            int r = row < 0 ? 0 : row >= height ? height - 1 : row;
            return cloudy[(long)l * width * height + (long)r * width + c] != 0;
        }

        private void BakeNode(int l, int column, int row, long index)
        {
            if ((column % stride) != 0 || (row % stride) != 0) return;
            // Cloud in a texel the filter reaches from this node: the nodes around a cloudy texel all carry a depth.
            bool near = false;
            for (int dr = -stride; dr <= stride && !near; dr++)
                for (int dc = -stride; dc <= stride && !near; dc++)
                    if (CloudyAt(l, column + dc, row + dr)) near = true;
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
                double between = x - i;
                double tauType = byType[l][i] * (1.0 - between) + byType[l][i + 1] * between;
                double typeBase, typeTop;
                tables[l].InterpolatedHeights(type, out typeBase, out typeTop);
                AccumulateEdge(l, lat, type, Math.Min(tauType, RtCloudPackLayout.MaxTau));
                WriteNode(index, l, tauType, typeBase, typeTop);
                return;
            }
            CloudLayerDef layer = layerDefs[l];
            LayerTypeTable table = tables[l];
            HeightTuning t = tuning[l];
            LocalHeights heights = heightSource.SampleHeights(l, lat, lon);
            // The steps stand inside the cloud's own interval at this place (its local base and top), not the layer's tight interval: the height variation
            // widens the tight interval to several times the thickness of the cloud, and a midpoint rule over all of it would put one or two steps in the cloud.
            // This is HeightDensity.Extinction's column with what does not change along it taken out of the loop: the type's neighbours and heights, the local
            // base and top (all of one place) and the layer's numbers (the tau_full of a column is sigma x the profile's integral; the coverage share is out).
            double thickness = layer.ThicknessM;
            int a, c;
            double f, b, top;
            table.Neighbours(type, out a, out c, out f);
            table.InterpolatedHeights(type, out b, out top);
            float ba, ta, ea, bc, tc, ec;
            table.Info(a, out ba, out ta, out ea);
            table.Info(c, out bc, out tc, out ec);
            double erosion = ea + (ec - ea) * f;
            double lb, lt;
            LayerGeometry.LocalBaseTop(b, top, erosion, thickness, heights, 0.0, t.BaseRagged ? t.RaggedM : 0.0, t.MinThicknessM, out lb, out lt,
                                       -layer.InnerAltitudeM / thickness);
            if (!(lt > lb)) return;
            double profileSum = 0.0;
            for (int k = 0; k < steps; k++)
            {
                double h = (k + 0.5) / steps;
                profileSum += table.ProfileAt(a, h) * (1.0 - f) + table.ProfileAt(c, h) * f;
            }
            double tau = sigma[l] * profileSum * (lt - lb) * thickness / steps * shareRatio[l];
            AccumulateEdge(l, lat, type, Math.Min(tau, RtCloudPackLayout.MaxTau));
            WriteNode(index, l, tau, lb, lt);
        }

        // The node's tau_full and the cloud's base and top there (shares of the layer, as the table and the height variation have them) into the texel: G the depth,
        // B and A the depth weighted by the base and the top as fractions of the layer's tight shell (see RtCloudPack).
        private void WriteNode(long index, int l, double tau, double baseShare, double topShare)
        {
            const int c = RtCloudPackLayout.Channels;
            double depth = Math.Min(tau, RtCloudPackLayout.MaxTau);
            double shell = Math.Max(1.0, tightOuter[l] - tightInner[l]);
            double fromBase = layerDefs[l].InnerAltitudeM + baseShare * layerDefs[l].ThicknessM, fromTop = layerDefs[l].InnerAltitudeM + topShare * layerDefs[l].ThicknessM;
            double fb = Math.Max(0.0, Math.Min(1.0, (fromBase - tightInner[l]) / shell)), ft = Math.Max(fb, Math.Min(1.0, (fromTop - tightInner[l]) / shell));
            data[index * c + 1] = (float)depth;
            data[index * c + 2] = (float)(depth * fb);
            data[index * c + 3] = (float)(depth * ft);
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
            const int ch = RtCloudPackLayout.Channels;
            // The depth and the two weighted heights (channels 1 to 3) are weighted alike, so the same filter holds for all three.
            for (int k = 1; k < ch; k++)
            {
                double a = data[(slice + (long)r0 * width + c0) * ch + k], b = data[(slice + (long)r0 * width + c1) * ch + k];
                double c = data[(slice + (long)r1 * width + c0) * ch + k], d = data[(slice + (long)r1 * width + c1) * ch + k];
                data[index * ch + k] = (float)((a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy);
            }
        }
    }
}
