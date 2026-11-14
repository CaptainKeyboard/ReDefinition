using System;
using System.Collections.Generic;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The sizes and the type mix of a map pair (cover and type bytes of 512 x 256, row 0 north, Kerbin's radius), as tools/e40_type_mix.py measures them, below 60 degrees latitude.
    internal static class TypeMixMetrics
    {
        internal const int W = 512, H = 256;
        internal const double R = 600.0;

        private static double LatDeg(int j) { return 90.0 - (j + 0.5) * 180.0 / H; }
        private static double DxKm(int j) { return 2 * Math.PI * R / W * Math.Cos(LatDeg(j) * Math.PI / 180.0); }
        private static readonly double DyKm = Math.PI * R / H;
        private static bool InBand(int j) { return Math.Abs(LatDeg(j)) < 60.0; }
        private static double Area(int j) { return DxKm(j) * DyKm; }

        internal sealed class Sizes
        {
            internal double CorrZonalKm, CorrMeridionalKm, VarianceUnder100, VarianceOver300, FieldDiameterKm, LargestFieldShare;
        }

        // Mean-removed autocorrelation (1/e length in km along rows and along columns) of a map, the variance share at zonal wavelengths under 100 km and over 300 km.
        internal static Sizes Of(byte[] cover, double fieldThreshold)
        {
            var s = new Sizes();
            var rows = new List<int>();
            for (int j = 0; j < H; j++) if (InBand(j)) rows.Add(j);
            double mean = 0.0;
            foreach (int j in rows) for (int i = 0; i < W; i++) mean += cover[j * W + i] / 255.0;
            mean /= rows.Count * W;
            // zonal: periodic autocorrelation averaged over rows
            var ac = new double[W / 2];
            double dxMean = 0.0;
            double shortV = 0.0, longV = 0.0, totalV = 0.0;
            foreach (int j in rows)
            {
                var f = new double[W];
                for (int i = 0; i < W; i++) f[i] = cover[j * W + i] / 255.0 - mean;
                for (int lag = 0; lag < W / 2; lag++)
                {
                    double a = 0.0;
                    for (int i = 0; i < W; i++) a += f[i] * f[(i + lag) % W];
                    ac[lag] += a;
                }
                dxMean += DxKm(j);
                // spectrum of the row (naive DFT, harmonics 1..W/2)
                for (int hmc = 1; hmc <= W / 2; hmc++)
                {
                    double re = 0.0, im = 0.0;
                    for (int i = 0; i < W; i++)
                    {
                        double ang = 2 * Math.PI * hmc * i / W;
                        re += f[i] * Math.Cos(ang);
                        im -= f[i] * Math.Sin(ang);
                    }
                    double p = re * re + im * im;
                    double wl = W * DxKm(j) / hmc;
                    totalV += p;
                    if (wl < 100.0) shortV += p;
                    else if (wl >= 300.0) longV += p;
                }
            }
            dxMean /= rows.Count;
            s.CorrZonalKm = FirstBelow(ac, 1.0 / Math.E) * dxMean;
            s.VarianceUnder100 = totalV > 0 ? shortV / totalV : 0;
            s.VarianceOver300 = totalV > 0 ? longV / totalV : 0;
            // meridional: not periodic
            var mc = new double[Math.Min(40, H / 2)];
            for (int lag = 0; lag < mc.Length; lag++)
            {
                double a = 0.0;
                int n = 0;
                for (int j = 0; j + lag < H; j++)
                {
                    if (!InBand(j) || !InBand(j + lag)) continue;
                    for (int i = 0; i < W; i++) { a += (cover[j * W + i] / 255.0 - mean) * (cover[(j + lag) * W + i] / 255.0 - mean); n++; }
                }
                mc[lag] = n > 0 ? a / n : 0;
            }
            s.CorrMeridionalKm = FirstBelow(mc, 1.0 / Math.E) * DyKm;
            // fields: connected components of map >= threshold
            var mask = new bool[W * H];
            for (int j = 0; j < H; j++) if (InBand(j)) for (int i = 0; i < W; i++) mask[j * W + i] = cover[j * W + i] / 255.0 >= fieldThreshold;
            List<double> areas = Components(mask);
            double total = 0.0, big = 0.0;
            foreach (double a in areas) { total += a; big = Math.Max(big, a); }
            s.LargestFieldShare = total > 0 ? big / total : 0;
            s.FieldDiameterKm = WeightedMedianDiameter(areas);
            return s;
        }

        private static double FirstBelow(double[] ac, double level)
        {
            double a0 = ac[0];
            if (!(a0 > 0)) return 0.0;
            for (int i = 0; i < ac.Length; i++)
            {
                double v = ac[i] / a0;
                if (v < level)
                {
                    if (i == 0) return 0.0;
                    double p = ac[i - 1] / a0;
                    return i - 1 + (p - level) / Math.Max(p - v, 1e-9);
                }
            }
            return ac.Length;
        }

        // The area (km^2) of each 4-connected component of a mask, the longitude wrapped.
        internal static List<double> Components(bool[] mask)
        {
            var seen = new bool[W * H];
            var result = new List<double>();
            var stack = new Stack<int>();
            for (int start = 0; start < mask.Length; start++)
            {
                if (!mask[start] || seen[start]) continue;
                double area = 0.0;
                stack.Push(start);
                seen[start] = true;
                while (stack.Count > 0)
                {
                    int p = stack.Pop();
                    int j = p / W, i = p % W;
                    area += Area(j);
                    Visit(mask, seen, stack, j, (i + 1) % W);
                    Visit(mask, seen, stack, j, (i + W - 1) % W);
                    if (j > 0) Visit(mask, seen, stack, j - 1, i);
                    if (j < H - 1) Visit(mask, seen, stack, j + 1, i);
                }
                result.Add(area);
            }
            return result;
        }

        private static void Visit(bool[] mask, bool[] seen, Stack<int> stack, int j, int i)
        {
            int q = j * W + i;
            if (!mask[q] || seen[q]) return;
            seen[q] = true;
            stack.Push(q);
        }

        // The diameter of the component a random point of the mask lies in (the area-weighted median of the components' diameters), km.
        internal static double WeightedMedianDiameter(List<double> areas)
        {
            if (areas.Count == 0) return 0.0;
            var sorted = new List<double>(areas);
            sorted.Sort();
            double total = 0.0;
            foreach (double a in sorted) total += a;
            double run = 0.0;
            foreach (double a in sorted)
            {
                run += a;
                if (run >= 0.5 * total) return 2.0 * Math.Sqrt(a / Math.PI);
            }
            return 0.0;
        }

        internal sealed class Mix
        {
            internal double TypesIn300kmWindow, DominantShare, LargestTypeComponentShare, TypeCorrZonalKm, TypeCorrMeridionalKm, CloudShare;
            internal double[] ShareOfType;
        }

        // The mix of types of a layer: a layer shows a type where its cover is at least `present` (the nearest type of the type bytes).
        internal static Mix MixOf(byte[] cover, byte[] type, int typeCount, double present)
        {
            var m = new Mix { ShareOfType = new double[typeCount] };
            var idx = new int[W * H];
            var pres = new bool[W * H];
            double cloudArea = 0.0;
            double[] cnt = new double[typeCount];
            for (int j = 0; j < H; j++)
            {
                for (int i = 0; i < W; i++)
                {
                    int p = j * W + i;
                    idx[p] = Math.Max(0, Math.Min(typeCount - 1, (int)Math.Round(type[p] / 255.0 * (typeCount - 1))));
                    pres[p] = InBand(j) && cover[p] / 255.0 >= present;
                    if (pres[p]) { cnt[idx[p]] += Area(j); cloudArea += Area(j); }
                }
            }
            if (cloudArea <= 0) return m;
            int dom = 0;
            for (int t = 0; t < typeCount; t++) { m.ShareOfType[t] = cnt[t] / cloudArea; if (cnt[t] > cnt[dom]) dom = t; }
            m.DominantShare = cnt[dom] / cloudArea;
            // the dominant type's largest component against the cloudy area
            var mask = new bool[W * H];
            for (int p = 0; p < mask.Length; p++) mask[p] = pres[p] && idx[p] == dom;
            double big = 0.0;
            foreach (double a in Components(mask)) big = Math.Max(big, a);
            m.LargestTypeComponentShare = big / cloudArea;
            // distinct types in windows of 40 x 41 texels (about 300 km) that hold cloud
            double sum = 0.0;
            int windows = 0;
            for (int r = 10; r < H - 10; r += 20)
            {
                for (int q = 0; q < W; q += 41)
                {
                    var set = new HashSet<int>();
                    for (int dj = 0; dj < 40 && r + dj < H; dj++)
                        for (int di = 0; di < 41 && q + di < W; di++)
                        {
                            int p = (r + dj) * W + q + di;
                            if (pres[p]) set.Add(idx[p]);
                        }
                    if (set.Count > 0) { sum += set.Count; windows++; }
                }
            }
            m.TypesIn300kmWindow = windows > 0 ? sum / windows : 0.0;
            // the type map's own correlation length
            Sizes ts = Of(type, 2.0);
            m.TypeCorrZonalKm = ts.CorrZonalKm;
            m.TypeCorrMeridionalKm = ts.CorrMeridionalKm;
            return m;
        }
    }
}
