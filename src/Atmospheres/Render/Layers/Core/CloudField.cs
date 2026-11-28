using System;

namespace ReDefinition.Atmospheres
{
    // CLOUD FIELD (owner 2026-11-19): the arrangement and the body of the cumuliform cloud types from the weather map and ONE multi-scale field, without a lattice. The shaders read the
    // field through FieldCloud (CloudCells.cginc); Cloud below is that function's twin on the CPU.
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
        // The classes of a type's cloud form by name (cfg key form); 0: none.
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
            new float[] { 2.5f, 0.20f, 2.0f, 2f },   // altocumulus: small flat elements
            new float[] { 4.0f, 0.15f, 0.8f, 3f },   // stratocumulus: flat, merged tessellations, rounded masses and rolls (WMO definition)
            new float[] { 0.8f, 0.30f, 0.7f, 1f },   // calvus: round heads
            new float[] { 0.7f, 0.30f, 0.5f, 1f }    // incus tower (the anvil is another type and has no form here)
        };

        // THE FORM NUMBERS ARE KEYS OF THE TYPE (master plan 47.U stage 2): a type names a class (cfg key form) and may set each of the four numbers itself (cfg keys formAspect,
        // formTopGain, formScale, formStretch). Every distinct set of four is a FORM SLOT (the shaders' _FieldC; slot 0: no form, slots 1 to 7 the classes as named); the type
        // tables carry the slot. Between two neighbouring types the form is the nearer type's, as the class was (a blend of the aspect and the top gain across
        // the transition changes the seven accepted types' looks there: it waits for the owner's pair, PRUEFPROTOKOLL 406).
        internal const int FormSlots = 16;
        // The slots: the classes first; a slot is written once, before the count that publishes it rises, so the readers (a per-sample path on worker threads) take no lock.
        private static readonly float[][] forms = InitialForms();
        private static volatile int formCount = Classes.Length;
        // A type's own numbers found no free slot (more than FormSlots - 1 distinct forms in the session): CloudFieldGpu says so once in the log.
        internal static bool FormsFull { get; private set; }

        private static float[][] InitialForms()
        {
            var all = new float[FormSlots][];
            for (int i = 0; i < Classes.Length; i++) all[i] = (float[])Classes[i].Clone();
            return all;
        }

        // The four numbers of a slot (x aspect, y top gain, z scale, w stretch), not to be written to; slot 0 and an unknown slot: no form's.
        internal static float[] Form(int slot)
        {
            return slot > 0 && slot < formCount ? forms[slot] : forms[0];
        }

        // The slot of a type's form: the named class with the numbers the type sets itself (the aspect, the scale and the stretch above 0, the top gain from 0; else the
        // class's own). 0: no form (no class and no number of its own).
        internal static int SlotOf(string name, float aspect, float topGain, float scale, float stretch)
        {
            int cls = ClassOf(name);
            bool ownAspect = aspect > 0f, ownTop = topGain >= 0f, ownScale = scale > 0f, ownStretch = stretch > 0f;
            if (!ownAspect && !ownTop && !ownScale && !ownStretch) return cls;
            float[] c = Classes[cls];
            float[] own = { ownAspect ? aspect : c[0], ownTop ? topGain : c[1], ownScale ? scale : c[2], ownStretch ? stretch : c[3] };
            lock (forms)
            {
                int count = formCount;
                for (int i = 1; i < count; i++)
                    if (forms[i][0] == own[0] && forms[i][1] == own[1] && forms[i][2] == own[2] && forms[i][3] == own[3]) return i;
                if (count >= FormSlots)
                {
                    // The table is full: the type draws its class's own numbers (no class: no form).
                    FormsFull = true;
                    return cls;
                }
                forms[count] = own;
                formCount = count + 1;
                return count;
            }
        }

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

        // ---- the field's spread inside a footprint (master plan 46.4, the far view's middle stage) ----

        // The mip levels the spread is given for (a 1024 texture has 11; the shader's array holds 12).
        internal const int SpreadLevels = 12;

        // For each mip level L of a channel: the half width a of the channel's values inside one texel of that level, taken as spread evenly over mean +- a.
        // The variance inside a box of level L is the whole field's variance less the variance of the level's means (the law of total variance, exact for the 2 x 2
        // box filter the mip chain is made with), and an even spread of variance v has the half width sqrt(3 v). Level 0: 0 (a texel holds one value); the coarsest
        // levels of the rank-uniform field: about 0.5 (its variance 1 / 12). FieldCloud takes it for the share of a pixel's footprint that lies inside the single
        // clouds, {u < f}: saturate((f - mean) / (2 a) + 0.5), the field's own filtered picture between the clouds and the map's share.
        internal static float[] MipSpread(float[] field, int size, int channel, int levels = SpreadLevels)
        {
            var spread = new float[levels];
            int n = size;
            var level = new double[n * n];
            for (int k = 0; k < n * n; k++) level[k] = field[k * 4 + channel];
            double whole = Variance(level, n * n);
            for (int l = 0; l < levels; l++)
            {
                spread[l] = (float)Math.Sqrt(3.0 * Math.Max(whole - Variance(level, n * n), 0.0));
                if (n == 1) continue;   // the last level is one texel: every further level repeats it
                int half = n / 2;
                for (int j = 0; j < half; j++)
                    for (int i = 0; i < half; i++)
                        level[j * half + i] = 0.25 * (level[(2 * j) * n + 2 * i] + level[(2 * j) * n + 2 * i + 1] + level[(2 * j + 1) * n + 2 * i] + level[(2 * j + 1) * n + 2 * i + 1]);
                n = half;
            }
            return spread;
        }

        private static double Variance(double[] v, int count)
        {
            double sum = 0.0, squares = 0.0;
            for (int k = 0; k < count; k++)
            {
                sum += v[k];
                squares += v[k] * v[k];
            }
            double mean = sum / count;
            return Math.Max(squares / count - mean * mean, 0.0);
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

        // ---- the field's cloud on the CPU (the shader's FieldCloud for a point: a footprint of 0) ----

        // What the field says at a point of a layer's map, as the shader's FieldCloud (CloudCells.cginc) says it for a footprint of 0, so that the CPU readers of the clouds' place (the cloud
        // shadow's field: the sun's dimming) stand where the picture draws them. Returns 1: the map has a share here (Body then says whether the point is within a body's reach);
        // 0: none (no cloud). The field carries every share (master plan 47.U stage 2): the closed layer is the field at a share of 1, the map's surplus above
        // it carried on (see FieldCloud). The far mean of the shader is not here: a CPU reader asks for a point, not for a pixel's footprint.
        // field: the channels of Build (size x size x 4); cls: the type's form (aspect, top gain, scale, stretch: Form); windCos, windSin: the street direction; cov: the layer map's
        // value after the clear-sky rule; coverage, edge, amount: the layer's; u, v: the layer map's coordinate (u with the layer's shift, any value; v 0 north to 1 south); rm: the radius
        // of the layer's middle in m; typeHeightM: the type's height in m.
        // THE TWO STEPS (master plan 47.U stage 3, as the shaders' FieldCloud and FieldBody): Cloud reads the field (1: the map has a share here; the read holds the point's place
        // across the body, the body's size and its top); the caller then takes the point's height in the body from the type's outline at h / read.Top (LayerTypeTable.FormHeight;
        // without an outline curve that quotient itself: the half ellipsoid) and asks Body, which says whether the point lies within the body's reach.
        internal static int Cloud(float[] field, int size, float[] cls, double windCos, double windSin, double cov, double coverage, double edge, double amount, double u, double v,
                                  double rm, double typeHeightM, out CloudCells.CellRead read)
        {
            read.XBest = -1.0;
            read.RBest = 0.0;
            read.Core = 0.0;
            read.Hv = 0.0;
            read.Top = 1.0;
            double f = CloudCells.Share(cov, coverage, edge, amount);
            if (f <= 0.0) return 0;
            double fField = f + Math.Max(cov + coverage - 1.0 - 0.5 * (edge + Math.Abs(amount)), 0.0);
            double lat = (0.5 - v) * Math.PI;
            double xM = u * 2.0 * Math.PI * rm * Math.Max(Math.Cos(lat), 0.02), yM = lat * rm;
            double along = (xM * windCos + yM * windSin) / cls[3], across = -xM * windSin + yM * windCos;
            double px = along * windCos - across * windSin, py = along * windSin + across * windCos;
            double fu = Sample(field, size, 0, px, py, cls[2]), fg = Sample(field, size, 1, px, py, cls[2]), fb = Sample(field, size, 2, px, py, cls[2]);
            double dm = (fField - fu) / Math.Max(fg * cls[2] * 0.001, 1e-6);
            double coreF = Clamp(0.5 * (cov + coverage - 1.0) + 0.35, 0.15, 0.5) * (1.0 + cls[1] * (1.0 - 2.0 * fb));
            double topF = Clamp(coreF / 0.5, 0.15, 1.0);
            double semiH = Math.Max(topF * typeHeightM, 1.0), rC = cls[0] * semiH;
            read.XBest = CloudCells.EdgeX * (1.0 - Clamp(dm / rC, -2.0, 1.0));
            read.RBest = rC / CloudCells.EdgeX;
            read.Core = coreF;
            read.Top = topF;
            return 1;
        }

        // The second step: the point's height in the body (hv, from the type's outline) and the reach test. 0: outside the body's reach (the read says "no body" then).
        // bodyBulge, band, fineGain: the layer's body numbers (the bulges' swing bounds the body's reach).
        internal static int Body(ref CloudCells.CellRead read, double hv, double typeHeightM, double bodyBulge, double band, double fineGain)
        {
            double semiH = Math.Max(read.Top * typeHeightM, 1.0), rC = read.RBest * CloudCells.EdgeX;
            double xv = read.XBest / CloudCells.EdgeX;
            double axis = Math.Min(rC, semiH);
            double swing = bodyBulge * axis * (0.5 + 0.5 * fineGain * (0.37 + 0.137));
            if ((1.0 - Math.Sqrt(xv * xv + hv * hv)) * axis + swing + 0.5 * Math.Max(band, 1.0) < 0.0)
            {
                read.XBest = -1.0;
                return 0;
            }
            read.Hv = hv;
            return 1;
        }

        // The equivalent height of an outline's width share w (1: the whole footprint, 0: closed): the height of the half ellipsoid at which it is as wide.
        internal static double OutlineHeight(double w)
        {
            w = Clamp(w, 0.0, 1.0);
            return Math.Sqrt(1.0 - w * w);
        }

        private static double Clamp(double x, double a, double b)
        {
            return x < a ? a : x > b ? b : x;
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
