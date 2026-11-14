using System;
using System.Threading.Tasks;

namespace ReDefinition.Atmospheres
{
    // The constants of the cloud noise model (package T02). A length of the model is a multiple of the layer's noise scale s (the lattice
    // cell of the shape volume's finest octave). tools/weather_metrics/noise_model.py holds the same numbers; the values were tuned there
    // against the parity checks R02 (spectrum slope, repetition).
    internal static class NoiseModel
    {
        // Shape volume: Perlin octaves (cells per period of the volume) and their weights, Worley octaves and weights.
        internal static readonly int[] ShapePerlinCells = { 2, 4, 8, 16 };
        internal static readonly double[] ShapePerlinWeights = { 2.8, 0.8, 0.7, 0.9 };
        internal static readonly int[] ShapeWorleyCells = { 4, 8, 16 };
        internal static readonly double[] ShapeWorleyWeights = { 0.625, 0.25, 0.125 };
        // Detail volume: inverted Worley octaves.
        internal static readonly int[] DetailWorleyCells = { 2, 4, 8 };
        internal static readonly double[] DetailWorleyWeights = { 0.25, 0.3, 0.45 };
        // Curl volume: octaves of the vector potential.
        internal static readonly int[] CurlCells = { 2, 4 };
        internal static readonly double[] CurlWeights = { 1.0, 0.5 };

        // The period of the shape volume and of the detail volume in noise scales.
        internal const double ShapePeriodScales = 16.0;
        internal const double DetailPeriodScales = 4.0;
        // The untiling displacement: amplitude and lattice cell of the value noise, in noise scales.
        // Amplitude over cell is the nominal gradient of the displacement (0.46; it was 8 / 7 = 1.14, which folded the lookup over itself in 31 % of
        // the volume and smeared the deck). Measured (noise_model.py, 20000 points): spectral norm of the displacement's Jacobian p99 1.74, folded
        // share 3.9 %, p99 stretch 2.5 (before: 4.3, 31 %, 4.8).
        // Stream T: 3 / 20 would lower the grain measure of the look sheet's storm cell (hf 0.040 to 0.028) but fails the repetition check
        // (tile lag 0.33 over seeds against 0.12) and the spectrum band 3 (slope 2.88 against 2.6), so 6.5 / 14 stays.
        internal const double WarpAmplitudeScales = 6.5;
        internal const double WarpCellScales = 14.0;
        // The curl displacement of the detail lookup, in noise scales.
        internal const double CurlAmplitudeScales = 0.6;
        // The distance LOD of the erosion detail (CloudShapeModel.DetailLod): the detail's finest cell in noise scales (the detail period over
        // its largest cell count, DetailPeriodScales / 8), and the march's sample spacing in those cells up to which the detail is read in
        // full and from which it is dropped (the sample spacing then exceeds the cell, so the detail could only shimmer).
        internal const double DetailFinestCellScales = 0.5;
        internal const double DetailLodFullFootCells = 1.0;
        internal const double DetailLodOffFootCells = 2.0;
        // The fine octave of the erosion detail (stream T): the same detail volume read again at FineDetailFrequency times the frequency (a cell of
        // 0.5 / 2.37 noise scales, about 150 m at a scale of 700 m, where the first read has 350 m), blended in with FineDetailWeight. The volume's
        // texel is then 37 m, so the finer octave is not magnified. The distance LOD drops it once the march spacing exceeds its own cell.
        internal const double FineDetailWeight = 0.4;
        internal const double FineDetailFrequency = 2.37;
    }

    // The sizes of the volumes by quality tier.
    internal struct NoiseSizes
    {
        internal int Shape, Detail, Curl;

        internal NoiseSizes(int shape, int detail, int curl)
        {
            Shape = shape;
            Detail = detail;
            Curl = curl;
        }

        // The size level 0 (Low tier) 32/16/16 (35 KB), 1 (Medium) 64/32/32 (0.4 MB), 2 (High and above) 128/64/32 (2.4 MB).
        internal static NoiseSizes ForLevel(int level)
        {
            return level <= 0 ? new NoiseSizes(32, 16, 16) : level >= 2 ? new NoiseSizes(128, 64, 32) : new NoiseSizes(64, 32, 32);
        }

        // The bytes the three volumes occupy (R8 shape and detail, RGBA8 curl).
        internal long Bytes { get { return (long)Shape * Shape * Shape + (long)Detail * Detail * Detail + 4L * Curl * Curl * Curl; } }
    }

    // The generators of the tileable noise volumes (package T02, P02 P03 P05 P11): Perlin-Worley for the shape, an inverted Worley fbm for
    // the erosion detail and a divergence-free curl field for the distortion. Every volume is periodic on its three axes by construction (the
    // lattice indices wrap), so it tiles; the repetition is broken by the lookup (CloudShapeModel.LookupPoint). Voxel (x, y, z) is at index
    // (z * n + y) * n + x and holds the field at the voxel centre ((i + 0.5) / n of the period).
    //
    // Technique: Schneider, "The real-time volumetric cloudscapes of Horizon Zero Dawn", SIGGRAPH 2015 (Perlin-Worley shape, Worley fbm
    // detail, curl distortion); Perlin, "Improving noise", SIGGRAPH 2002 (the quintic fade); Worley, "A cellular texture basis function",
    // SIGGRAPH 1996. The code is the project's own.
    internal static class NoiseVolumes
    {
        internal const int ShapeSeedOffset = 0, DetailSeedOffset = 1, CurlSeedOffset = 2;

        // The shape volume, n^3 bytes (R8).
        internal static byte[] Shape(int n, uint seed)
        {
            double[] perlinSum = new double[n * n * n], worleySum = new double[n * n * n];
            Parallel.For(0, n, z =>
            {
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        double ux = (x + 0.5) / n, uy = (y + 0.5) / n, uz = (z + 0.5) / n;
                        double p = 0.0;
                        for (int k = 0; k < NoiseModel.ShapePerlinCells.Length; k++)
                            p += NoiseModel.ShapePerlinWeights[k] * Perlin(ux, uy, uz, NoiseModel.ShapePerlinCells[k], seed + 7u * (uint)k);
                        perlinSum[(z * n + y) * n + x] = p;
                        worleySum[(z * n + y) * n + x] = WorleyFbm(ux, uy, uz, NoiseModel.ShapeWorleyCells, NoiseModel.ShapeWorleyWeights, seed + 101u);
                    }
            });
            Stretch(perlinSum);
            for (int i = 0; i < perlinSum.Length; i++) perlinSum[i] = worleySum[i] + perlinSum[i] * (1.0 - worleySum[i]);
            Equalize(perlinSum);
            return ToBytes(perlinSum);
        }

        // Replaces each value by its rank over the volume (0 to 1, uniform): the shape's median is 0.5, so that a coverage threshold at the
        // middle of the noise covers half the sky and the noise moves it by as much one way as the other.
        private static void Equalize(double[] v)
        {
            var keys = (double[])v.Clone();
            var order = new int[v.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(keys, order);
            for (int rank = 0; rank < order.Length; rank++) v[order[rank]] = (rank + 0.5) / order.Length;
        }

        // The detail volume, n^3 bytes (R8).
        internal static byte[] Detail(int n, uint seed)
        {
            var v = new double[n * n * n];
            Parallel.For(0, n, z =>
            {
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                        v[(z * n + y) * n + x] = WorleyFbm((x + 0.5) / n, (y + 0.5) / n, (z + 0.5) / n, NoiseModel.DetailWorleyCells, NoiseModel.DetailWorleyWeights, seed);
            });
            Stretch(v);
            return ToBytes(v);
        }

        // The curl volume, 4 n^3 bytes (RGBA8: the curl scaled by the largest magnitude as 0.5 + 0.5 c, alpha 255).
        internal static byte[] Curl(int n, uint seed)
        {
            var pot = new double[3][];
            for (int c = 0; c < 3; c++) pot[c] = new double[n * n * n];
            Parallel.For(0, n, z =>
            {
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                        for (int c = 0; c < 3; c++)
                        {
                            double v = 0.0;
                            for (int k = 0; k < NoiseModel.CurlCells.Length; k++)
                                v += NoiseModel.CurlWeights[k] * Perlin((x + 0.5) / n, (y + 0.5) / n, (z + 0.5) / n, NoiseModel.CurlCells[k], seed + 211u * (uint)c + 13u * (uint)k);
                            pot[c][(z * n + y) * n + x] = v;
                        }
            });
            double h = 1.0 / n;
            var curl = new double[n * n * n * 3];
            double peak = 0.0;
            for (int z = 0; z < n; z++)
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        int i = (z * n + y) * n + x;
                        double dzp2 = Diff(pot[2], n, x, y, z, 1) - Diff(pot[1], n, x, y, z, 2);
                        double dzp0 = Diff(pot[0], n, x, y, z, 2) - Diff(pot[2], n, x, y, z, 0);
                        double dzp1 = Diff(pot[1], n, x, y, z, 0) - Diff(pot[0], n, x, y, z, 1);
                        curl[i * 3] = dzp2 / (2.0 * h);
                        curl[i * 3 + 1] = dzp0 / (2.0 * h);
                        curl[i * 3 + 2] = dzp1 / (2.0 * h);
                        peak = Math.Max(peak, Math.Sqrt(curl[i * 3] * curl[i * 3] + curl[i * 3 + 1] * curl[i * 3 + 1] + curl[i * 3 + 2] * curl[i * 3 + 2]));
                    }
            var bytes = new byte[n * n * n * 4];
            for (int i = 0; i < n * n * n; i++)
            {
                for (int c = 0; c < 3; c++) bytes[i * 4 + c] = ToByte(0.5 + 0.5 * curl[i * 3 + c] / peak);
                bytes[i * 4 + 3] = 255;
            }
            return bytes;
        }

        // The central difference of a field along an axis (0 = x, 1 = y, 2 = z) at a voxel, periodic: f(+1) - f(-1) in field units.
        private static double Diff(double[] f, int n, int x, int y, int z, int axis)
        {
            int xp = x, yp = y, zp = z, xm = x, ym = y, zm = z;
            if (axis == 0) { xp = (x + 1) % n; xm = (x + n - 1) % n; }
            else if (axis == 1) { yp = (y + 1) % n; ym = (y + n - 1) % n; }
            else { zp = (z + 1) % n; zm = (z + n - 1) % n; }
            return f[(zp * n + yp) * n + xp] - f[(zm * n + ym) * n + xm];
        }

        // Periodic gradient noise at a point of the unit cube, `cells` lattice cells per period; about -0.9 to 0.9.
        internal static double Perlin(double ux, double uy, double uz, int cells, uint seed)
        {
            double px = ux * cells, py = uy * cells, pz = uz * cells;
            int ix = (int)Math.Floor(px), iy = (int)Math.Floor(py), iz = (int)Math.Floor(pz);
            double fx = px - ix, fy = py - iy, fz = pz - iz;
            double u = NoiseHash.Fade(fx), v = NoiseHash.Fade(fy), w = NoiseHash.Fade(fz);
            double acc = 0.0;
            for (int dx = 0; dx <= 1; dx++)
                for (int dy = 0; dy <= 1; dy++)
                    for (int dz = 0; dz <= 1; dz++)
                    {
                        double a, b, c;
                        NoiseHash.Vec(NoiseHash.Hash3(NoiseHash.Mod(ix + dx, cells), NoiseHash.Mod(iy + dy, cells), NoiseHash.Mod(iz + dz, cells), seed), out a, out b, out c);
                        double gx = a * 2.0 - 1.0, gy = b * 2.0 - 1.0, gz = c * 2.0 - 1.0;
                        double len = Math.Max(Math.Sqrt(gx * gx + gy * gy + gz * gz), 1e-6);
                        double dot = (gx * (fx - dx) + gy * (fy - dy) + gz * (fz - dz)) / len;
                        acc += (dx == 1 ? u : 1.0 - u) * (dy == 1 ? v : 1.0 - v) * (dz == 1 ? w : 1.0 - w) * dot;
                    }
            return acc;
        }

        // Periodic Worley F1 at a point of the unit cube: the distance to the nearest feature point in cell units.
        internal static double Worley(double ux, double uy, double uz, int cells, uint seed)
        {
            double px = ux * cells, py = uy * cells, pz = uz * cells;
            int ix = (int)Math.Floor(px), iy = (int)Math.Floor(py), iz = (int)Math.Floor(pz);
            double best = 9.0;
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int cx = ix + dx, cy = iy + dy, cz = iz + dz;
                        double a, b, c;
                        NoiseHash.Vec(NoiseHash.Hash3(NoiseHash.Mod(cx, cells), NoiseHash.Mod(cy, cells), NoiseHash.Mod(cz, cells), seed), out a, out b, out c);
                        double ex = cx + a - px, ey = cy + b - py, ez = cz + c - pz;
                        best = Math.Min(best, ex * ex + ey * ey + ez * ez);
                    }
            return Math.Sqrt(best);
        }

        // The weighted sum of inverted Worley octaves, 0 to 1.
        internal static double WorleyFbm(double ux, double uy, double uz, int[] cells, double[] weights, uint seed)
        {
            double total = 0.0, sum = 0.0;
            for (int k = 0; k < cells.Length; k++)
            {
                double f1 = Worley(ux, uy, uz, cells[k], seed + 31u * (uint)k);
                total += weights[k] * Math.Min(Math.Max(1.0 - f1 / 0.9, 0.0), 1.0);
                sum += weights[k];
            }
            return total / sum;
        }

        private static void Stretch(double[] v)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = 0; i < v.Length; i++)
            {
                if (v[i] < lo) lo = v[i];
                if (v[i] > hi) hi = v[i];
            }
            double range = hi - lo;
            for (int i = 0; i < v.Length; i++) v[i] = (v[i] - lo) / range;
        }

        private static byte ToByte(double v)
        {
            return (byte)Math.Min(255.0, Math.Max(0.0, Math.Round(v * 255.0, MidpointRounding.ToEven)));
        }

        private static byte[] ToBytes(double[] v)
        {
            var b = new byte[v.Length];
            for (int i = 0; i < v.Length; i++) b[i] = ToByte(v[i]);
            return b;
        }
    }
}
