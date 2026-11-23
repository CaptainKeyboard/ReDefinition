using System;

namespace ReDefinition.Atmospheres
{
    // CLOUD FIELD (owner 2026-11-19): the arrangement and the puffy body of the cumuliform cloud types from the weather map and ONE multi-scale field, without a lattice. The C# twin of
    // tools/weather_metrics/cellfield.py (make_field) and of the field block of ReDefinitionAtmospheresLayers.shader (LayerDensityEx).
    //
    // The field u(x) is horizontal and tileable (TileM), a Gaussian field with the power spectrum |k|^-Slope between the wavelengths LambdaMaxM and LambdaMinM (spectral synthesis: amplitude
    // |k|^(-Slope/2), the phase a hash of the wavenumber, the real part of the inverse FFT), its value replaced by its rank, so the share of the points with u < f is f for every f. A cloud exists
    // where u(x) is below the map's local cloud share f: the mean cover stays the map's, and the sets {u < f} have the power-law sizes, fractal edges, clusters and gaps of the field's spectrum.
    // The texture's channels: r u, g the gradient of u in rank per km (the distance to the edge of {u < f} in m is (f - u) / (g / 1000), the dome's horizontal depth), b the field smoothed over
    // CoarseSigmaM and ranked again (low in the cores of the clusters: the clouds there are taller).
    //
    // Numbers and their sources (each checked against the paper's text, 2026-11-19):
    //  - Slope 8/3: the 2D power spectrum of a field whose 1D spectrum is k^-5/3, the scaling of cloud fields from tens of m to hundreds of km (Cahalan and Snider 1989; Davis et al. 1994, 1996 for
    //    the liquid water path and radiance spectra). The excursion sets of such a field are the clouds of a scale-free size distribution.
    //  - The size distribution of the sets the measurement is made against: n(l) ~ l^-1.66 (Wood and Field 2011, chord and sqrt-area size, 0.1 < l < 1000 km, all cloud types) and ~ l^-1.7 for cumulus
    //    up to a scale break (Neggers, Jonker and Siebesma 2003), equivalent to a density of the area n(A) ~ A^-1.33 to A^-1.35.
    //  - The perimeter-area dimension of the clouds' outlines: 1.2 to 1.35 for clouds under about 1 km^2, 1.5 to 1.55 above (Lovejoy 1982; Cahalan and Joseph 1989; Benner and Curry 1998).
    //  - Cloud streets: the horizontal convective rolls' spacing is 2 to 3 times the depth of the boundary layer, their length several times the spacing (Etling and Brown 1993): the class's Stretch lengthens the lookup along the wind.
    //  The aspect, the top gain and the scale factor of each class are tuned against the owner's accepted single-cloud look (pictures), not measured values.
    internal static class CloudField
    {
        internal const int Size = 1024;
        internal const double TileM = 102400.0;
        internal const double LambdaMinM = 400.0, LambdaMaxM = 20000.0, CoarseSigmaM = 3000.0;
        internal const double Slope = 8.0 / 3.0;
        internal const uint Seed = 0x51ED;
        // The class ids of a type's puff (cfg key puff); 0: none.
        internal const int ClassCount = 8;

        private static readonly string[] Names = { "none", "humilis", "mediocris", "congestus", "altocumulus", "stratocumulus", "calvus", "incus" };

        internal static int ClassOf(string name)
        {
            if (name == null) return 0;
            for (int i = 0; i < Names.Length; i++) if (string.Equals(Names[i], name.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }

        // The class table (x aspect: the dome's horizontal semi-axis over its height, y the top gain by the coarse channel, z the scale factor of the lookup (features 1 / z of the field's size),
        // w the street stretch along the wind).
        internal static readonly float[][] Classes =
        {
            new float[] { 1f, 0f, 1f, 1f },          // none
            new float[] { 1.0f, 0.30f, 1.5f, 1f },   // humilis: as wide as tall, the smallest clouds
            new float[] { 1.0f, 0.35f, 1.0f, 1f },   // mediocris
            new float[] { 0.8f, 0.40f, 0.7f, 1f },   // congestus: taller than wide, towers
            new float[] { 2.5f, 0.20f, 2.0f, 2f },   // altocumulus: small flat puffs
            new float[] { 4.0f, 0.15f, 0.8f, 3f },   // stratocumulus: flat, merged tessellations, rounded masses and rolls (WMO definition)
            new float[] { 0.8f, 0.30f, 0.7f, 1f },   // calvus: round heads
            new float[] { 0.7f, 0.30f, 0.5f, 1f }    // incus tower (the anvil is another type and has no puff)
        };

        // The hash of CloudCells (the same constants), for the phase of a wavenumber.
        private static uint PhaseHash(int kx, int ky, uint seed)
        {
            unchecked { return CloudCells.Hash((uint)kx * 0x9E3779B1u ^ (uint)ky * 0x85EBCA6Bu ^ seed * 0xC2B2AE35u); }
        }

        // The Gaussian field (not ranked) of the spectrum between the wavelengths, size x size, row j (y), column i (x); sigmaM > 0 smooths it with a Gaussian of that width.
        internal static double[] Gaussian(int size, double tileM, double slope, double lambdaMinM, double lambdaMaxM, uint seed, double sigmaM)
        {
            double[] re = new double[size * size], im = new double[size * size];
            double kmin = tileM / lambdaMaxM, kmax = tileM / lambdaMinM;
            for (int j = 0; j < size; j++)
            {
                int ky = j < size / 2 ? j : j - size;
                for (int i = 0; i < size; i++)
                {
                    int kx = i < size / 2 ? i : i - size;
                    double kk = Math.Sqrt((double)kx * kx + (double)ky * ky);
                    if (kk < kmin || kk > kmax) continue;
                    double amp = Math.Pow(kk, -0.5 * slope);
                    if (sigmaM > 0.0)
                    {
                        double w = 2.0 * Math.PI * sigmaM / tileM * kk;
                        amp *= Math.Exp(-0.5 * w * w);
                    }
                    double phase = 2.0 * Math.PI * CloudCells.Unit(PhaseHash(kx, ky, seed));
                    re[j * size + i] = amp * Math.Cos(phase);
                    im[j * size + i] = amp * Math.Sin(phase);
                }
            }
            Fft2(re, im, size, true);
            return re;
        }

        // The ranks of the values, 0..1 (the uniform field).
        internal static float[] Rank(double[] v)
        {
            int n = v.Length;
            int[] order = new int[n];
            double[] keys = (double[])v.Clone();
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(keys, order);
            float[] rank = new float[n];
            for (int r = 0; r < n; r++) rank[order[r]] = (float)((r + 0.5) / n);
            return rank;
        }

        // The field's four channels (r u, g the gradient in rank per km, b the coarse rank, a unused 0) as size * size * 4 floats.
        internal static float[] Build(int size = Size)
        {
            float[] u = Rank(Gaussian(size, TileM, Slope, LambdaMinM, LambdaMaxM, Seed, 0.0));
            float[] c = Rank(Gaussian(size, TileM, Slope, LambdaMinM, LambdaMaxM, Seed, CoarseSigmaM));
            double px = TileM / size;
            // The gradient of the rank, per km, then smoothed by a box of 7 pixels (the edge's distance is read from its slope, not from the finest octave's).
            float[] g = new float[size * size];
            for (int j = 0; j < size; j++)
                for (int i = 0; i < size; i++)
                {
                    double dx = (u[j * size + (i + 1) % size] - u[j * size + (i + size - 1) % size]) / (2.0 * px);
                    double dy = (u[((j + 1) % size) * size + i] - u[((j + size - 1) % size) * size + i]) / (2.0 * px);
                    g[j * size + i] = (float)(Math.Sqrt(dx * dx + dy * dy) * 1000.0);
                }
            float[] gs = new float[size * size];
            const int R = 3;
            for (int j = 0; j < size; j++)
                for (int i = 0; i < size; i++)
                {
                    double s = 0;
                    for (int dj = -R; dj <= R; dj++)
                        for (int di = -R; di <= R; di++) s += g[((j + dj + size) % size) * size + (i + di + size) % size];
                    gs[j * size + i] = (float)(s / ((2 * R + 1) * (2 * R + 1)));
                }
            float[] o = new float[size * size * 4];
            for (int k = 0; k < size * size; k++)
            {
                o[k * 4] = u[k];
                o[k * 4 + 1] = gs[k];
                o[k * 4 + 2] = c[k];
            }
            return o;
        }

        // ---- the CPU lookup (the shader's tex2Dlod with the bilinear filter and the repeat wrap) ----

        // The channel of the field at the metres (x east, y north) of the map frame, bilinear, tiling; scale lengthens (below 1) or shortens the features.
        internal static double Sample(float[] field, int size, int channel, double xM, double yM, double scale)
        {
            double gx = xM * scale / TileM * size - 0.5, gy = yM * scale / TileM * size - 0.5;
            double fx0 = Math.Floor(gx), fy0 = Math.Floor(gy);
            double fx = gx - fx0, fy = gy - fy0;
            int x0 = (int)(((long)fx0 % size + size) % size), y0 = (int)(((long)fy0 % size + size) % size);
            int x1 = (x0 + 1) % size, y1 = (y0 + 1) % size;
            double a = field[(y0 * size + x0) * 4 + channel], b = field[(y0 * size + x1) * 4 + channel];
            double c = field[(y1 * size + x0) * 4 + channel], d = field[(y1 * size + x1) * 4 + channel];
            return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy;
        }

        // ---- the FFT ----

        private static void Fft2(double[] re, double[] im, int n, bool inverse)
        {
            double[] r = new double[n], i2 = new double[n];
            for (int row = 0; row < n; row++)
            {
                Array.Copy(re, row * n, r, 0, n);
                Array.Copy(im, row * n, i2, 0, n);
                Fft(r, i2, inverse);
                Array.Copy(r, 0, re, row * n, n);
                Array.Copy(i2, 0, im, row * n, n);
            }
            for (int col = 0; col < n; col++)
            {
                for (int row = 0; row < n; row++) { r[row] = re[row * n + col]; i2[row] = im[row * n + col]; }
                Fft(r, i2, inverse);
                for (int row = 0; row < n; row++) { re[row * n + col] = r[row]; im[row * n + col] = i2[row]; }
            }
        }

        // In place radix-2 FFT, the sign of the exponent + for the inverse (unnormalised: the field is ranked).
        private static void Fft(double[] re, double[] im, bool inverse)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    double t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = 2.0 * Math.PI / len * (inverse ? 1.0 : -1.0);
                double wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1.0, ci = 0.0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = i + k + len / 2;
                        double xr = re[b] * cr - im[b] * ci, xi = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - xr; im[b] = im[a] - xi;
                        re[a] += xr; im[a] += xi;
                        double nr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = nr;
                    }
                }
            }
        }
    }
}
