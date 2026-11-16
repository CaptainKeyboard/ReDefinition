using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ReDefinition.Atmospheres
{
    // The constants of the heap volume (package CLOUDR1, P2 and P3). tools/weather_metrics/noise_model.py holds the same numbers (gen_heap).
    internal static class HeapModel
    {
        // The cell counts per period of the octaves: FirstCells * 2^k (lacunarity 2, so the cascade is self-similar), up to the octave whose cell is MinOctaveVoxels voxels wide.
        internal const int FirstCells = 2;
        internal const int MinOctaveVoxels = 4;
        // The dome radius round a feature point, in cells of the octave, and the width of the polynomial smooth maximum that joins the domes of neighbouring cells (a crease forms
        // between two lobes).
        internal const double DomeRadius = 0.9;
        internal const double SmoothWidth = 0.15;
        // The Hurst exponent of the cascade of preset 1 (billow) and preset 2 (rough): octave k has the weight 2^(-H k), so the outline of a level set of the field has the
        // dimension 2 - H (1.35 and 1.55); the power spectrum of a slice falls as k^-(2 H + 2).
        internal static readonly double[] Hurst = { 0.65, 0.45 };
        internal static readonly string[] PresetNames = { "none", "billow", "rough" };
        // The cavity channel: 1 - gain x max(blur - field, 0), the blur a box of (2 r + 1)^3 voxels with r = N / 32 (at least 1).
        internal const double CavityGain = 3.0;
        // The levels below this size (voxels per side) are box means of the level above; the levels above hold the octaves whose cell is at least MinOctaveVoxels wide there.
        internal const int AnalyticLevelMin = 8;
        internal const int PresetCount = 2;

        // 0 (none) for an unknown name, 1 billow, 2 rough.
        internal static int PresetOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            for (int i = 1; i < PresetNames.Length; i++)
                if (string.Equals(PresetNames[i], name.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }

        // The cells per period of the octaves a level of `size` voxels per side holds (at least the first).
        internal static int[] Octaves(int size)
        {
            var list = new System.Collections.Generic.List<int>();
            for (int cells = FirstCells; cells * MinOctaveVoxels <= size || list.Count == 0; cells *= 2) list.Add(cells);
            return list.ToArray();
        }
    }

    // The heap volume: the macro shape of a cumulus as a cascade of heaps on heaps (package CLOUDR1 P2), plus its cavity (P3). One RGBA8 volume of N^3 voxels with a mip chain: R the
    // heap field of preset 1, G of preset 2, B the cavity of preset 1, A of preset 2. The presets are channels of one volume (not slabs of an atlas: the mips of a slab atlas would
    // average neighbouring slabs and the wrap in z would blend them), so one fetch reads a preset's field and its cavity together.
    internal sealed class HeapVolumeSet
    {
        internal int N;
        // Level m (size N >> m) as RGBA bytes; Levels[0] is the volume itself, the chain ends at 1 x 1 x 1.
        internal byte[][] Levels;
        internal double GenerationMs;

        internal long Bytes
        {
            get
            {
                long sum = 0;
                for (int i = 0; i < Levels.Length; i++) sum += Levels[i].Length;
                return sum;
            }
        }

        internal static HeapVolumeSet Generate(int n, uint seed)
        {
            var watch = Stopwatch.StartNew();
            var set = new HeapVolumeSet { N = n, Levels = HeapVolumes.Generate(n, seed) };
            set.GenerationMs = watch.Elapsed.TotalMilliseconds;
            return set;
        }
    }

    // The generator of the heap volume.
    //
    // Technique: the rounded heaps of the shape volume are domes sqrt(1 - (f1 / R)^2) round jittered feature points (Worley, "A cellular texture basis function", SIGGRAPH 1996, for the
    // points; the dome profile and the smooth maximum of Quilez, "Smooth minimum", iquilezles.org, as the join), summed over octaves of doubled frequency with the weights 2^(-H k) (the
    // spectral synthesis of fractional Brownian fields, Mandelbrot and Van Ness 1968; Musgrave et al. in "Texturing and Modeling"), and rank-equalised like the shape volume. The
    // shape-by-type with a volume of heap structure follows Schneider, "The real-time volumetric cloudscapes of Horizon Zero Dawn", SIGGRAPH 2015, and "Nubis", SIGGRAPH 2017 and 2022;
    // the cavity channel is the baked ambient occlusion of such a volume. The code is the project's own.
    internal static class HeapVolumes
    {
        // The levels of the volume of size n, RGBA bytes each.
        internal static byte[][] Generate(int n, uint seed)
        {
            int levels = NoiseVolumeSet.MipLevels(n);
            var result = new byte[levels][];
            var fields = new double[HeapModel.PresetCount][];
            for (int p = 0; p < HeapModel.PresetCount; p++) fields[p] = HeapField(n, HeapModel.Hurst[p], seed + 17u * (uint)p);
            var cavity = new double[HeapModel.PresetCount][];
            for (int p = 0; p < HeapModel.PresetCount; p++) cavity[p] = Cavity(fields[p], n);
            result[0] = Pack(fields, cavity);
            int size = n;
            for (int m = 1; m < levels; m++)
            {
                int half = size / 2;
                var nextFields = new double[HeapModel.PresetCount][];
                var nextCavity = new double[HeapModel.PresetCount][];
                for (int p = 0; p < HeapModel.PresetCount; p++)
                {
                    nextFields[p] = half >= HeapModel.AnalyticLevelMin ? HeapField(half, HeapModel.Hurst[p], seed + 17u * (uint)p) : Equalize(Mean8(fields[p], size));
                    nextCavity[p] = Mean8(cavity[p], size);
                }
                fields = nextFields;
                cavity = nextCavity;
                result[m] = Pack(fields, cavity);
                size = half;
            }
            return result;
        }

        private static byte[] Pack(double[][] fields, double[][] cavity)
        {
            int count = fields[0].Length;
            var bytes = new byte[count * 4];
            for (int i = 0; i < count; i++)
            {
                bytes[i * 4] = ToByte(fields[0][i]);
                bytes[i * 4 + 1] = ToByte(fields[1][i]);
                bytes[i * 4 + 2] = ToByte(cavity[0][i]);
                bytes[i * 4 + 3] = ToByte(cavity[1][i]);
            }
            return bytes;
        }

        private static byte ToByte(double v)
        {
            return (byte)Math.Min(255.0, Math.Max(0.0, Math.Round(v * 255.0, MidpointRounding.ToEven)));
        }

        // The ranked heap field of a level of `size` voxels per side: the octaves that level holds, summed with the weights 2^(-hurst k), then equalised (ties share their mean rank).
        internal static double[] HeapField(int size, double hurst, uint seed)
        {
            int[] octaves = HeapModel.Octaves(size);
            var total = new double[size * size * size];
            Parallel.For(0, size, z =>
            {
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        double ux = (x + 0.5) / size, uy = (y + 0.5) / size, uz = (z + 0.5) / size;
                        double sum = 0.0;
                        for (int k = 0; k < octaves.Length; k++) sum += Math.Pow(2.0, -hurst * k) * Octave(ux, uy, uz, octaves[k], seed + 53u * (uint)k);
                        total[(z * size + y) * size + x] = sum;
                    }
            });
            return Equalize(total);
        }

        // One octave at a point of the unit cube (periodic, `cells` cells per period): the domes of the 27 neighbouring feature points joined by the smooth maximum; 0 to about 1.
        internal static double Octave(double ux, double uy, double uz, int cells, uint seed)
        {
            double px = ux * cells, py = uy * cells, pz = uz * cells;
            int ix = (int)Math.Floor(px), iy = (int)Math.Floor(py), iz = (int)Math.Floor(pz);
            double acc = 0.0;
            bool first = true;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int cx = ix + dx, cy = iy + dy, cz = iz + dz;
                        double a, b, c;
                        NoiseHash.Vec(NoiseHash.Hash3(NoiseHash.Mod(cx, cells), NoiseHash.Mod(cy, cells), NoiseHash.Mod(cz, cells), seed), out a, out b, out c);
                        double ex = cx + a - px, ey = cy + b - py, ez = cz + c - pz;
                        double r = Math.Sqrt(ex * ex + ey * ey + ez * ez) / HeapModel.DomeRadius;
                        double dome = Math.Sqrt(Math.Max(0.0, 1.0 - r * r));
                        if (first) { acc = dome; first = false; }
                        else acc = SmoothMax(acc, dome, HeapModel.SmoothWidth);
                    }
            return acc;
        }

        // The polynomial smooth maximum of a and b over the width k.
        internal static double SmoothMax(double a, double b, double k)
        {
            double h = Math.Max(k - Math.Abs(a - b), 0.0) / k;
            return Math.Max(a, b) + h * h * k * 0.25;
        }

        // The rank of each value over the volume, (rank + 0.5) / count, equal values sharing the mean of their ranks.
        internal static double[] Equalize(double[] v)
        {
            var keys = (double[])v.Clone();
            var order = new int[v.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(keys, order);
            var ranked = new double[v.Length];
            for (int start = 0; start < order.Length;)
            {
                int end = start + 1;
                while (end < order.Length && keys[end] == keys[start]) end++;
                double rank = (start + end - 1) * 0.5;
                for (int k = start; k < end; k++) ranked[order[k]] = (rank + 0.5) / order.Length;
                start = end;
            }
            return ranked;
        }

        // The cavity channel of a level-0 field: 1 on tops and flat parts, lower in the creases between lobes (the field's box blur minus the field, times the gain).
        internal static double[] Cavity(double[] field, int n)
        {
            double[] blur = BoxBlur(field, n, Math.Max(1, n / 32));
            var cavity = new double[field.Length];
            for (int i = 0; i < cavity.Length; i++)
            {
                double c = 1.0 - HeapModel.CavityGain * Math.Max(blur[i] - field[i], 0.0);
                cavity[i] = c < 0.0 ? 0.0 : c > 1.0 ? 1.0 : c;
            }
            return cavity;
        }

        private static double[] BoxBlur(double[] v, int n, int radius)
        {
            double[] a = v;
            for (int axis = 0; axis < 3; axis++)
            {
                var b = new double[a.Length];
                double[] src = a;
                int ax = axis;
                Parallel.For(0, n, z =>
                {
                    for (int y = 0; y < n; y++)
                        for (int x = 0; x < n; x++)
                        {
                            double acc = 0.0;
                            for (int d = -radius; d <= radius; d++)
                            {
                                int xx = ax == 0 ? NoiseHash.Mod(x + d, n) : x, yy = ax == 1 ? NoiseHash.Mod(y + d, n) : y, zz = ax == 2 ? NoiseHash.Mod(z + d, n) : z;
                                acc += src[(zz * n + yy) * n + xx];
                            }
                            b[(z * n + y) * n + x] = acc / (2 * radius + 1);
                        }
                });
                a = b;
            }
            return a;
        }

        // The mean of each 2 x 2 x 2 block of a volume of size `size`.
        internal static double[] Mean8(double[] v, int size)
        {
            int half = size / 2;
            var mean = new double[half * half * half];
            for (int z = 0; z < half; z++)
                for (int y = 0; y < half; y++)
                    for (int x = 0; x < half; x++)
                    {
                        double sum = 0.0;
                        for (int dz = 0; dz < 2; dz++)
                            for (int dy = 0; dy < 2; dy++)
                                for (int dx = 0; dx < 2; dx++)
                                    sum += v[((2 * z + dz) * size + 2 * y + dy) * size + 2 * x + dx];
                        mean[(z * half + y) * half + x] = sum / 8.0;
                    }
            return mean;
        }
    }
}
