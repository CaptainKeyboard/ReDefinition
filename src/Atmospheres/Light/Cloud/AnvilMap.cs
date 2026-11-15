using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // The anvil of a cumulonimbus as map data (E40 CB). The density of a layer is a 2D map value lowered by the type's lift times a height profile, so a column is never wider at
    // the top than the map's footprint: the strongest tower ends as a shelf, never as an anvil (E40 L6 shape, cbtower sheet). A real anvil is the tower's outflow at the tropopause:
    // the updraft carries air up through the tower's cross-section, and at the level of neutral buoyancy it spreads sideways into a sheet a few km deep. Volume kept, the sheet's
    // area grows by the outflow over its lifetime, pi R^2 d = pi r^2 (d + w tau), so its radius is R = r sqrt(1 + w tau / d) (r the tower's radius where it reaches the anvil, w the
    // updraft there, tau the time it has spread, d the sheet's depth), and the wind shear between the steering level and the anvil carries it downwind.
    // Here: every tower of the layer's coverage map that reaches the anvil base (its map value above the median-noise threshold, its type one that feeds an outflow: cfg type key
    // `outflow`) is one connected source; its anvil is drawn into the coverage map around it, from the tower's own value at its edge down to the value at which the noise shows the first
    // wisp beyond the anvil's radius (thin translucent edges by construction), and the type map there becomes the layer's anvil type (cfg layer key anvilType: a type that spans the
    // layer like the towers' and whose LIFT closes every column below the sheet and whose profile holds density only in it). The type map is bilinear, so between a tower and its sheet
    // the types mix: with one base for both, the mix raises the threshold below the sheet (the tower's wall narrows by its own noise) instead of sloping a smooth base from the ground
    // to the sheet (the funnel of the first player run, sheet base 0.75). Every GPU and CPU reader of the layer reads the maps and the type table, so the layer pass, the
    // cloud shadow, the CPU twins, the culling and the raytracing pack see the same anvil without code of their own.
    internal struct AnvilInputs
    {
        internal int Width, Height;
        // The body's radius (m): the size of a texel.
        internal double RadiusM;
        // The angular rate (rad/s, eastward) of the anvil's level against the tower's steering level: the shear that carries the sheet downwind (0: none).
        internal double ShearRadS;
    }

    internal static class AnvilMap
    {
        // The most a source reaches, in texels (a guard against a deck-sized source: its anvil is cut there).
        internal const int MaxReachTexels = 64;

        // The anvil's radius over the tower's: R / r = 1 + scale (sqrt(1 + w tau / d) - 1), the outflow law with the body's multiplier on the spread beyond the tower (1 = derived).
        internal static double RadiusRatio(double updraftMs, double seconds, double depthM, double scale)
        {
            if (!(depthM > 0.0) || !(updraftMs > 0.0) || !(seconds > 0.0)) return 1.0;
            return 1.0 + Math.Max(0.0, scale) * (Math.Sqrt(1.0 + updraftMs * seconds / depthM) - 1.0);
        }

        // The anvil type's depth (m): the band of its type where its profile holds density (the type spans the layer so that the bilinear type between a tower and its sheet keeps one
        // base; its LIFT closes it below the sheet).
        internal static double SheetDepthM(CloudLayerDef layer)
        {
            int t = AnvilTypeIndex(layer);
            if (t < 0) return 0.0;
            CloudTypeDef type = layer.Types[t];
            const int n = 256;
            int held = 0;
            for (int i = 0; i < n; i++)
                if (type.Profile.Evaluate((i + 0.5f) / n) > 0.01f && type.BakedLiftAt((i + 0.5) / n) < 0.999) held++;
            return held / (double)n * Math.Max(0.0, type.TopShare - type.BaseShare) * layer.ThicknessM;
        }

        internal static int AnvilTypeIndex(CloudLayerDef layer)
        {
            if (layer == null || layer.AnvilType < 0f || layer.Types.Count < 2) return -1;
            return (int)Math.Round(Math.Min(1f, layer.AnvilType) * (layer.Types.Count - 1));
        }

        // The type's outflow weight at a type value (the two neighbouring types interpolated, as the shader interpolates the table).
        private static double OutflowAt(CloudLayerDef layer, double typeValue)
        {
            int n = layer.Types.Count;
            double x = Math.Max(0.0, Math.Min(1.0, typeValue)) * (n - 1);
            int a = Math.Min((int)Math.Floor(x), n - 1), b = Math.Min(a + 1, n - 1);
            double f = x - a;
            return layer.Types[a].Outflow * (1.0 - f) + layer.Types[b].Outflow * f;
        }

        // The raw map value whose clear-sky value is e (the inverse of CloudLayerDef.ClearSkyMap: min(m, 2m - K)).
        private static double RawOf(double e, double k)
        {
            return k > 0.0 && e < k ? 0.5 * (e + k) : e;
        }

        // The raw map value from which a column with the noise's middle value is cloud (the layer's outline: the sheet's radius is where its value falls to this).
        internal static double MedianRaw(CloudLayerDef layer)
        {
            return Math.Max(0.0, RawOf(1.0 - layer.EffectiveCoverage, layer.ClearSkyK));
        }

        // The raw map value from which the noise's highest value shows (the first wisp).
        internal static double WispRaw(CloudLayerDef layer)
        {
            double vis = 1.0 - layer.EffectiveCoverage - 0.5 * layer.EffectiveNoiseAmount - 0.5 * Math.Max(layer.EffectiveEdgeSoftness, 1e-4f);
            return Math.Max(0.0, RawOf(vis, layer.ClearSkyK));
        }

        // The share of the sheet's radius over which it is solid (the tower's own value); from there it falls to the layer's outline at the radius and on, at the same slope, to the
        // first wisp: the thin translucent edge of a real anvil is beyond the radius the outflow law gives.
        internal const double SolidShare = 0.6;

        // The sheet's raw value at a distance d from the tower (reach: the law's radius beyond the tower).
        internal static double SheetValue(double covMax, double medianRaw, double wispRaw, double d, double reach)
        {
            if (d <= SolidShare * reach) return covMax;
            double slope = (covMax - medianRaw) / Math.Max((1.0 - SolidShare) * reach, 1e-6);
            if (!(slope > 0.0)) return d <= reach ? covMax : 0.0;
            double v = covMax - slope * (d - SolidShare * reach);
            return v >= wispRaw ? v : 0.0;
        }

        // The farthest distance (beyond the tower) at which the sheet still has a value.
        internal static double SheetEnd(double covMax, double medianRaw, double wispRaw, double reach)
        {
            double slope = (covMax - medianRaw) / Math.Max((1.0 - SolidShare) * reach, 1e-6);
            if (!(slope > 0.0)) return reach;
            return SolidShare * reach + (covMax - wispRaw) / slope;
        }

        // The result of the last Apply (tests, the log): sources, and per source its tower radius, anvil reach beyond it (m), updraft and strength.
        internal sealed class Report
        {
            internal readonly List<double> TowerRadiusM = new List<double>(), ReachM = new List<double>(), UpdraftMs = new List<double>(), Strength = new List<double>();
            internal double ShiftTexels;
            internal int Changed;
        }

        // Draws the anvils of a layer into its coverage and type bytes in place (width x height, row 0 at the north pole, column 0 at -180, as LayerMapStore holds them).
        // False (nothing changed) where the layer has no anvil type.
        internal static bool Apply(CloudLayerDef layer, byte[] cover, byte[] type, AnvilInputs inp, Report report = null)
        {
            int anvilIndex = AnvilTypeIndex(layer);
            int w = inp.Width, h = inp.Height, n = w * h;
            if (anvilIndex < 0 || cover == null || type == null || cover.Length < n || type.Length < n || w < 2 || h < 2 || !(inp.RadiusM > 0.0)) return false;
            double depth = SheetDepthM(layer);
            if (!(depth > 0.0)) return false;
            // The median-noise threshold (a column with the noise's middle value passes from here, a clear-sky value) and the first wisp (the highest noise value passes), raw.
            double m0 = 1.0 - layer.EffectiveCoverage;
            double medianRaw = MedianRaw(layer), visRaw = WispRaw(layer);
            byte anvilByte = (byte)Math.Round(anvilIndex / (double)(layer.Types.Count - 1) * 255.0);
            double dy = Math.PI * inp.RadiusM / h;
            var strength = new double[n];
            bool any = false;
            for (int i = 0; i < n; i++)
            {
                double outflow = OutflowAt(layer, type[i] / 255.0);
                if (!(outflow > 0.0)) continue;
                double e = layer.ClearSkyMap(cover[i] / 255.0);
                double s = (e - m0) / Math.Max(1.0 - m0, 1e-3);
                if (s <= 0.0) continue;
                strength[i] = Math.Min(1.0, s) * outflow;
                any = true;
            }
            if (report != null) report.ShiftTexels = inp.ShearRadS * layer.AnvilSeconds * w / (2.0 * Math.PI);
            if (!any) return false;
            var label = new int[n];
            var anvil = new float[n];
            var core = new bool[n];
            var queue = new int[n];
            var members = new List<int>();
            int next = 0;
            for (int start = 0; start < n; start++)
            {
                if (strength[start] <= 0.0 || label[start] != 0) continue;
                next++;
                members.Clear();
                int head = 0, tail = 0;
                queue[tail++] = start;
                label[start] = next;
                while (head < tail)
                {
                    int p = queue[head++];
                    members.Add(p);
                    int py = p / w, px = p - py * w;
                    for (int oy = -1; oy <= 1; oy++)
                    {
                        int qy = py + oy;
                        if (qy < 0 || qy >= h) continue;
                        for (int ox = -1; ox <= 1; ox++)
                        {
                            if (ox == 0 && oy == 0) continue;
                            int q = qy * w + ((px + ox) % w + w) % w;
                            if (strength[q] > 0.0 && label[q] == 0) { label[q] = next; queue[tail++] = q; }
                        }
                    }
                }
                // The tower where it reaches the anvil: its cross-section's area, its strongest value, and the updraft that strength carries.
                double area = 0.0, sMax = 0.0, covMax = 0.0;
                foreach (int p in members)
                {
                    area += dy * 2.0 * Math.PI * inp.RadiusM * Math.Cos(Lat(p / w, h)) / w;
                    sMax = Math.Max(sMax, strength[p]);
                    covMax = Math.Max(covMax, cover[p] / 255.0);
                    core[p] = true;
                }
                double towerR = Math.Sqrt(area / Math.PI), updraft = layer.AnvilUpdraftMs * sMax;
                double reach = towerR * (RadiusRatio(updraft, layer.AnvilSeconds, depth, layer.AnvilScale) - 1.0);
                if (report != null) { report.TowerRadiusM.Add(towerR); report.ReachM.Add(reach); report.UpdraftMs.Add(updraft); report.Strength.Add(sMax); }
                if (!(reach > 1.0) || covMax <= medianRaw) continue;
                double end = SheetEnd(covMax, medianRaw, visRaw, reach);
                // The boundary texels of the tower (a neighbour outside it): the distance of a texel to the tower is its distance to the nearest of them.
                var boundary = new List<int>();
                int yMin = h, yMax = -1;
                foreach (int p in members)
                {
                    int py = p / w, px = p - py * w;
                    yMin = Math.Min(yMin, py);
                    yMax = Math.Max(yMax, py);
                    bool edgeTexel = false;
                    for (int o = 0; o < 4 && !edgeTexel; o++)
                    {
                        int qy = py + (o == 2 ? -1 : o == 3 ? 1 : 0), qx = px + (o == 0 ? -1 : o == 1 ? 1 : 0);
                        if (qy < 0 || qy >= h) continue;
                        edgeTexel = label[qy * w + (qx % w + w) % w] != label[p];
                    }
                    if (edgeTexel) boundary.Add(p);
                }
                int reachRows = Math.Min(MaxReachTexels, (int)Math.Ceiling(end / dy) + 1);
                for (int y = Math.Max(0, yMin - reachRows); y <= Math.Min(h - 1, yMax + reachRows); y++)
                {
                    double dx = 2.0 * Math.PI * inp.RadiusM * Math.Max(Math.Cos(Lat(y, h)), 1e-3) / w;
                    int reachCols = Math.Min(Math.Min(MaxReachTexels, w / 2), (int)Math.Ceiling(end / dx) + 1);
                    // The columns: the tower's own span on its boundary, widened by the reach.
                    foreach (int x in ColumnsNear(boundary, w, reachCols))
                    {
                        int i = y * w + x;
                        if (label[i] == label[members[0]]) continue;
                        double best = double.MaxValue;
                        foreach (int p in boundary)
                        {
                            int py = p / w, px = p - py * w;
                            int ddx = x - px;
                            if (ddx > w / 2) ddx -= w;
                            if (ddx < -w / 2) ddx += w;
                            double ex = ddx * dx, ey = (y - py) * dy;
                            double d2 = ex * ex + ey * ey;
                            if (d2 < best) best = d2;
                        }
                        double v = SheetValue(covMax, medianRaw, visRaw, Math.Sqrt(best), reach);
                        if (v > anvil[i]) anvil[i] = (float)v;
                    }
                }
            }
            // Downwind: the sheet is carried by the shear over its lifetime; each texel takes the largest value of the sheet upwind of it along the parallel (a constant angular rate is
            // the same number of texels on every row).
            double shift = inp.ShearRadS * layer.AnvilSeconds * w / (2.0 * Math.PI);
            shift = Math.Max(-w / 4.0, Math.Min(w / 4.0, shift));
            float[] carried = anvil;
            if (Math.Abs(shift) >= 0.25)
            {
                int steps = Math.Min(32, (int)Math.Ceiling(Math.Abs(shift) * 2.0));
                carried = new float[n];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        float m = anvil[y * w + x];
                        for (int s = 1; s <= steps; s++)
                        {
                            double sx = x - shift * s / steps;
                            double fx = Math.Floor(sx);
                            int x0 = (((int)fx) % w + w) % w, x1 = (x0 + 1) % w;
                            double f = sx - fx;
                            float v = (float)(anvil[y * w + x0] * (1.0 - f) + anvil[y * w + x1] * f);
                            if (v > m) m = v;
                        }
                        carried[y * w + x] = m;
                    }
            }
            // The ring of texels around a tower is half tower, half sheet: the type map is bilinear, and a sheet texel next to the tower mixed the sheet's type (closed below the sheet) into
            // the tower across the whole texel between them, which emptied the tower's body below the sheet in the player (a stub under a floating sheet, E40 CB run 4); a ring that kept
            // its own maps left a hole in the sheet around the tower (run 5). The ring takes the sheet's coverage and the type halfway between its own and the sheet's, so the closure of
            // the sheet's type below its base reaches the tower over two texels and the sheet is whole over the tower.
            var ring = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (!core[i]) continue;
                int py = i / w, px = i - py * w;
                for (int oy = -1; oy <= 1; oy++)
                {
                    int qy = py + oy;
                    if (qy < 0 || qy >= h) continue;
                    for (int ox = -1; ox <= 1; ox++) ring[qy * w + ((px + ox) % w + w) % w] = true;
                }
            }
            int changed = 0;
            var sheet = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (core[i] || carried[i] <= cover[i] / 255.0f) continue;
                if (ring[i])
                {
                    byte rb = (byte)Math.Min(255.0, Math.Round(carried[i] * 255.0));
                    if (rb <= cover[i]) continue;
                    cover[i] = rb;
                    type[i] = (byte)((type[i] + anvilByte + 1) / 2);
                    changed++;
                    continue;
                }
                byte b = (byte)Math.Min(255.0, Math.Round(carried[i] * 255.0));
                if (b <= cover[i]) continue;
                cover[i] = b;
                type[i] = anvilByte;
                sheet[i] = true;
                changed++;
            }
            // The texels around the sheet that show nothing (below the first wisp) take the sheet's type too, so that the bilinear type does not hang the types between the sheet and an
            // empty texel's type under the sheet's edge.
            byte visByte = (byte)Math.Floor(visRaw * 255.0);
            for (int i = 0; i < n; i++)
            {
                if (!sheet[i]) continue;
                int py = i / w, px = i - py * w;
                for (int oy = -1; oy <= 1; oy++)
                {
                    int qy = py + oy;
                    if (qy < 0 || qy >= h) continue;
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        int q = qy * w + ((px + ox) % w + w) % w;
                        if (!sheet[q] && !ring[q] && cover[q] <= visByte) type[q] = anvilByte;
                    }
                }
            }
            if (report != null) report.Changed = changed;
            return changed > 0;
        }

        private static double Lat(int row, int h)
        {
            return Math.PI * (0.5 - (row + 0.5) / h);
        }

        // The columns within reach of a tower's boundary (wrapped), each once.
        private static IEnumerable<int> ColumnsNear(List<int> boundary, int w, int reachCols)
        {
            var seen = new bool[w];
            foreach (int p in boundary)
            {
                int px = p % w;
                for (int o = -reachCols; o <= reachCols; o++)
                {
                    int x = ((px + o) % w + w) % w;
                    if (seen[x]) continue;
                    seen[x] = true;
                    yield return x;
                }
            }
        }
    }
}
