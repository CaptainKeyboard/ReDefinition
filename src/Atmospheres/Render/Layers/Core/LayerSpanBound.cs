using System;

namespace ReDefinition.Atmospheres
{
    // E40 grain against TVC (layers.spanTight): the altitude interval a layer's march has to search this frame.
    //
    // The march spreads its grid points over the layer's tight interval, which LayerRenderer.Upload takes from the whole type table (CloudLayerDef.TightAltitudes):
    // Kerbin's cumulus layer is searched from 1.7 to 7.4 km although the stratocumulus of a fair day ends at 2.3 km, so nine tenths of the grid points fall into air
    // that cannot hold a cloud and the step inside the cloud is nine times the one the same count gives over the cloud alone. The bound here reads the bytes of the
    // layer's two maps as they are uploaded (LayerMapStore) over the part of the map the camera can see and returns
    //   - the interval of the types that occur there (the type value picks two neighbouring types and interpolates their base and top, so the bound is taken over
    //     the whole range of values between the smallest and the largest byte), widened by the height variation's reach like the table's interval, and
    //   - "no cloud": the largest coverage value there cannot reach the threshold even with the noise at its highest (the shader's own coverage bound).
    // Both are conservative: a bilinear read of the map lies between the texels around it, and the region is the cap of the sphere up to where a ray from the camera
    // leaves the layer's shell behind the horizon, plus the reach of the light march and a texel of margin.
    // Pure (no Unity), so the tests run it.
    internal static class LayerSpanBound
    {
        // Above this many texels the region is not scanned (a camera in orbit sees half the map; the clouds are a few pixels thick there): the table's interval stays.
        internal const int MaxTexels = 12000;

        internal struct Result
        {
            // False: the table's interval stays (no map, the region too large).
            public bool Tightened;
            // True: no cloud of the layer can be seen from the camera (InnerM and OuterM are not set).
            public bool Empty;
            public float InnerM, OuterM;
            // The centre texel and half sizes the scan covered (the cache key of the caller) and the texels read.
            public int Texels;
        }

        // The half angle (degrees) of the cap around the camera's place in which a ray from the camera can meet a shell of the outer altitude outerM, with marginM of
        // horizontal reach added (the light march). cameraAltM below zero counts as zero.
        internal static double RegionDeg(double radiusM, double cameraAltM, double outerM, double marginM)
        {
            double hc = Math.Max(cameraAltM, 0.0), ho = Math.Max(outerM, 0.0);
            double a = Math.Acos(radiusM / (radiusM + hc)) + Math.Acos(radiusM / (radiusM + ho));
            return (a + marginM / radiusM) * (180.0 / Math.PI);
        }

        // The base and top shares over the type values vMin to vMax (0 to 1): the table's interpolation is linear between neighbouring types, so the extremes lie at
        // the ends of the range or at a type between them.
        internal static void ShareRange(LayerTypeTable table, float vMin, float vMax, out float lo, out float hi)
        {
            int count = table.Count;
            lo = 1f;
            hi = 0f;
            if (count <= 0) return;
            float x0 = Clamp01(vMin) * (count - 1), x1 = Clamp01(vMax) * (count - 1);
            Accumulate(table, x0, ref lo, ref hi);
            Accumulate(table, x1, ref lo, ref hi);
            for (int t = (int)Math.Ceiling(x0); t <= (int)Math.Floor(x1); t++) Accumulate(table, t, ref lo, ref hi);
        }

        private static float Clamp01(float v)
        {
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }

        private static void Accumulate(LayerTypeTable table, float x, ref float lo, ref float hi)
        {
            int count = table.Count;
            int ta = Math.Min((int)Math.Floor(x), count - 1);
            int tb = Math.Min(ta + 1, count - 1);
            float f = x - ta;
            float ba, ta0, ea, bb, tb0, eb;
            table.Info(ta, out ba, out ta0, out ea);
            table.Info(tb, out bb, out tb0, out eb);
            float b = ba + (bb - ba) * f, top = ta0 + (tb0 - ta0) * f;
            if (b < lo) lo = b;
            if (top > hi) hi = top;
        }

        // The extremes of a layer's height maps over the region, in shares of the ranges (-1 to 1): the lowest and highest root offset, the highest top term, the
        // lowest and highest variation. Start with Neutral and call once per map the frame may sample (the one the GPU holds, the one a queued upload brings).
        internal struct HeightExtremes
        {
            public float RootMin, RootMax, TopMax, VariationMin, VariationMax;
            // False: the region was too large to scan; the ranges themselves are the bound.
            public bool Scanned;

            internal static HeightExtremes Neutral
            {
                get { return new HeightExtremes { Scanned = true }; }
            }

            internal static HeightExtremes Whole
            {
                get { return new HeightExtremes { RootMin = -1f, RootMax = 1f, TopMax = 1f, VariationMin = -1f, VariationMax = 1f, Scanned = false }; }
            }
        }

        // The rows and columns of a w x h map (row 0 at the north pole, column 0 at longitude -180) that a bilinear read inside the cap can touch. False: more than
        // MaxTexels.
        private static bool Window(int width, int height, double latDeg, double lonDeg, double regionDeg, out int y0, out int y1, out int xStart, out int columns)
        {
            return Window(width, height, latDeg, lonDeg, regionDeg, MaxTexels, out y0, out y1, out xStart, out columns);
        }

        private static bool Window(int width, int height, double latDeg, double lonDeg, double regionDeg, int maxTexels, out int y0, out int y1, out int xStart, out int columns)
        {
            double degPerRow = 180.0 / height, degPerCol = 360.0 / width;
            y0 = Math.Max(0, (int)Math.Floor((90.0 - (latDeg + regionDeg)) / degPerRow - 0.5) - 1);
            y1 = Math.Min(height - 1, (int)Math.Ceiling((90.0 - (latDeg - regionDeg)) / degPerRow - 0.5) + 1);
            double polar = Math.Abs(latDeg) + regionDeg;
            int half = polar >= 88.0 ? width : (int)Math.Ceiling(regionDeg / Math.Cos(polar * Math.PI / 180.0) / degPerCol) + 2;
            columns = Math.Min(width, 2 * half + 1);
            int xc = (int)Math.Floor((lonDeg + 180.0) / degPerCol);
            xStart = columns >= width ? 0 : xc - half;
            int rows = y1 - y0 + 1;
            return rows > 0 && (long)rows * columns <= maxTexels;
        }

        // Widens e by the bytes of one height map (HeightMaps.Bytes: three bytes per texel, root, top, variation; a byte b is (b - 128) / 127 of the range).
        internal static HeightExtremes Widen(HeightExtremes e, byte[] bytes3, int width, int height, double latDeg, double lonDeg, double regionDeg)
        {
            if (!e.Scanned) return e;
            int y0, y1, xStart, columns;
            if (bytes3 == null || width < 2 || height < 2 || !Window(width, height, latDeg, lonDeg, regionDeg, out y0, out y1, out xStart, out columns))
                return HeightExtremes.Whole;
            int rootMin = 128, rootMax = 128, topMax = 128, varMin = 128, varMax = 128;
            for (int y = y0; y <= y1; y++)
            {
                int row = y * width;
                for (int k = 0; k < columns; k++)
                {
                    int i = (row + ((xStart + k) % width + width) % width) * 3;
                    int r = bytes3[i], t = bytes3[i + 1], v = bytes3[i + 2];
                    if (r < rootMin) rootMin = r;
                    if (r > rootMax) rootMax = r;
                    if (t > topMax) topMax = t;
                    if (v < varMin) varMin = v;
                    if (v > varMax) varMax = v;
                }
            }
            e.RootMin = Math.Min(e.RootMin, (rootMin - 128) / 127f);
            e.RootMax = Math.Max(e.RootMax, (rootMax - 128) / 127f);
            e.TopMax = Math.Max(e.TopMax, (topMax - 128) / 127f);
            e.VariationMin = Math.Min(e.VariationMin, (varMin - 128) / 127f);
            e.VariationMax = Math.Max(e.VariationMax, (varMax - 128) / 127f);
            return e;
        }

        // How far the height variation lowers a base and raises a top in the region, in m: the shader's HeightReachDown / HeightReachUp with each range replaced by the
        // share of it the maps reach there. rootRangeM, topRangeM, variationRangeM, raggedM: _LayerH; minThicknessM: _LayerH2.y (0: the layer has no height variation).
        // The root offset enters with a weight of 0 to 1 (HeightRootWeight), so its share is taken from zero; the ragged boundary moves by up to twice raggedM.
        internal static void Reach(HeightExtremes e, float rootRangeM, float topRangeM, float variationRangeM, float raggedM, float minThicknessM, out float downM, out float upM)
        {
            downM = upM = 0f;
            if (!(minThicknessM > 0f)) return;
            downM = rootRangeM * Math.Max(0f, -e.RootMin) + variationRangeM * Math.Max(0f, -e.VariationMin) + 2f * raggedM;
            upM = rootRangeM * Math.Max(0f, e.RootMax) + variationRangeM * Math.Max(0f, e.VariationMax) + topRangeM * Math.Max(0f, e.TopMax) + 2f * raggedM + minThicknessM;
        }

        // The bound of one layer. coverageBytes / typeBytes: the maps as uploaded (width x height, row 0 at the north pole, column 0 at longitude -180); latDeg and
        // lonDeg: the camera's place in the layer's map (the longitude already turned by the layer's map shift); regionDeg: RegionDeg. coverage, noiseAmount and edge are
        // the numbers the shader's coverage bound uses (_LayerB.y, _LayerD.x, _LayerB.z). staticInnerM / staticOuterM: the table's tight interval, never left.
        internal static Result Bound(byte[] coverageBytes, byte[] typeBytes, int width, int height, double latDeg, double lonDeg, double regionDeg, LayerTypeTable table,
                                     CloudLayerDef layer, float coverage, float noiseAmount, float edge, float staticInnerM, float staticOuterM,
                                     float downM = -1f, float upM = -1f)
        {
            var r = new Result();
            if (coverageBytes == null || typeBytes == null || width < 2 || height < 2 || table == null || table.Count <= 0 || !(regionDeg >= 0.0)) return r;
            double degPerRow = 180.0 / height, degPerCol = 360.0 / width;
            // Rows: the texel centres whose bilinear footprint the cap can touch (one texel of margin on each side).
            int y0 = (int)Math.Floor((90.0 - (latDeg + regionDeg)) / degPerRow - 0.5) - 1;
            int y1 = (int)Math.Ceiling((90.0 - (latDeg - regionDeg)) / degPerRow - 0.5) + 1;
            y0 = Math.Max(0, y0);
            y1 = Math.Min(height - 1, y1);
            // Columns: the cap's longitude half width at its row nearest a pole; all columns once it reaches 88 degrees.
            double polar = Math.Abs(latDeg) + regionDeg;
            int half = polar >= 88.0 ? width : (int)Math.Ceiling(regionDeg / Math.Cos(polar * Math.PI / 180.0) / degPerCol) + 2;
            int columns = Math.Min(width, 2 * half + 1);
            int rows = y1 - y0 + 1;
            if (rows <= 0 || (long)rows * columns > MaxTexels) return r;
            int xc = (int)Math.Floor((lonDeg + 180.0) / degPerCol);
            int xStart = columns >= width ? 0 : xc - half;
            int covMax = 0, typeMin = 255, typeMax = 0;
            for (int y = y0; y <= y1; y++)
            {
                int row = y * width;
                for (int k = 0; k < columns; k++)
                {
                    int x = ((xStart + k) % width + width) % width;
                    int c = coverageBytes[row + x], t = typeBytes[row + x];
                    if (c > covMax) covMax = c;
                    if (t < typeMin) typeMin = t;
                    if (t > typeMax) typeMax = t;
                }
            }
            r.Texels = rows * columns;
            r.Tightened = true;
            // The shader's coverage bound (LayerDensity): the shape is zero where cov + coverage - 1 + 0.5 amount + 0.5 edge <= 0.
            if (covMax / 255.0 + coverage - 1.0 + 0.5 * noiseAmount + 0.5 * Math.Max(edge, 1e-4f) <= -1e-4)
            {
                r.Empty = true;
                return r;
            }
            float lo, hi;
            ShareRange(table, typeMin / 255f, typeMax / 255f, out lo, out hi);
            if (hi <= lo)
            {
                r.Empty = true;
                return r;
            }
            float thickness = layer.ThicknessM;
            float inner = layer.InnerAltitudeM + lo * thickness, outer = layer.InnerAltitudeM + hi * thickness;
            // The height variation's reach, as CloudLayerDef.TightAltitudes adds it: the layer's own worst case, or the caller's (Reach: what the maps hold in the region).
            float down = downM >= 0f ? Math.Min(downM, layer.HeightDownM) : layer.HeightDownM, up = upM >= 0f ? Math.Min(upM, layer.HeightUpM) : layer.HeightUpM;
            if (down > 0f || up > 0f)
            {
                inner = inner > 0f ? Math.Max(0.5f, inner - down) : 0f;
                outer += up;
            }
            r.InnerM = Math.Max(inner, staticInnerM);
            r.OuterM = Math.Min(outer, staticOuterM);
            if (staticInnerM <= 0f && inner <= 0f) r.InnerM = 0f;
            return r;
        }
    }
}
