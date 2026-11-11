using System;

namespace AtmoSim.Core
{
    // Spherical-harmonic transform on a Gaussian latitude-longitude grid (triangular truncation M), written for this
    // prototype. Row 0 is the northernmost latitude, column 0 is longitude -180 degrees. All trigonometry of the tables
    // comes from DMath, so the tables are the same on every platform. Normalised associated Legendre functions
    // (orthonormal over mu = sin(lat) on [-1, 1]).
    public sealed class Spectral
    {
        public readonly int M, NLon, NLat, NCoef;
        public readonly double[] Mu, Weight, CosLat, LatRad, LonRad;
        private readonly int[] off;      // offset of order m in the coefficient layout
        private readonly int[] offExt;   // offset of order m in the layout with one more degree
        private readonly double[][] pExt; // pExt[j][offExt[m] + (n - m)], n up to M + 1
        private readonly double[][] hCos; // (1 - mu^2) dP/dmu at the same layout as the coefficients
        private readonly double[] cosT, sinT; // cos(m lon_i), sin(m lon_i): [m * NLon + i]

        public Spectral(int nlon, int nlat, int m)
        {
            if (nlon < 3 * m + 1 || nlat * 2 < 3 * m + 1) throw new ArgumentException("grid too small for truncation");
            M = m; NLon = nlon; NLat = nlat;
            off = new int[m + 2];
            offExt = new int[m + 2];
            int c = 0, ce = 0;
            for (int k = 0; k <= m; k++) { off[k] = c; c += m - k + 1; offExt[k] = ce; ce += m - k + 2; }
            off[m + 1] = c; offExt[m + 1] = ce;
            NCoef = c;
            Mu = new double[nlat]; Weight = new double[nlat]; CosLat = new double[nlat]; LatRad = new double[nlat];
            GaussLegendre(nlat, Mu, Weight);
            for (int j = 0; j < nlat; j++)
            {
                CosLat[j] = DMath.Sqrt(1.0 - Mu[j] * Mu[j]);
                LatRad[j] = DMath.Asin(Mu[j]);
            }
            LonRad = new double[nlon];
            for (int i = 0; i < nlon; i++) LonRad[i] = DMath.TwoPi * i / nlon - DMath.Pi;
            cosT = new double[(m + 1) * nlon]; sinT = new double[(m + 1) * nlon];
            for (int k = 0; k <= m; k++)
                for (int i = 0; i < nlon; i++)
                {
                    double ang = DMath.TwoPi * ((long)k * i % nlon) / nlon; // longitude origin shift handled in the phase below
                    cosT[k * nlon + i] = DMath.Cos(ang - k * DMath.Pi);
                    sinT[k * nlon + i] = DMath.Sin(ang - k * DMath.Pi);
                }
            pExt = new double[nlat][]; hCos = new double[nlat][];
            for (int j = 0; j < nlat; j++)
            {
                pExt[j] = new double[ce];
                hCos[j] = new double[c];
                Legendre(m, Mu[j], pExt[j]);
                for (int k = 0; k <= m; k++)
                    for (int n = k; n <= m; n++)
                    {
                        // (1 - mu^2) dP_n^m/dmu = -n eps_{n+1} P_{n+1} + (n + 1) eps_n P_{n-1}
                        double up = pExt[j][offExt[k] + (n + 1 - k)];
                        double dn = n > k ? pExt[j][offExt[k] + (n - 1 - k)] : 0.0;
                        hCos[j][off[k] + (n - k)] = -n * Eps(n + 1, k) * up + (n + 1) * (n > k ? Eps(n, k) : 0.0) * dn;
                    }
            }
        }

        public int Index(int m, int n) { return off[m] + (n - m); }

        private static double Eps(int n, int m)
        {
            return DMath.Sqrt((double)(n * n - m * m) / (4.0 * n * n - 1.0));
        }

        // Normalised associated Legendre functions up to degree M + 1 at mu, layout offExt.
        private void Legendre(int m, double mu, double[] p)
        {
            double s = DMath.Sqrt(1.0 - mu * mu);
            double pmm = 1.0 / DMath.Sqrt(2.0);
            for (int k = 0; k <= m; k++)
            {
                if (k > 0) pmm *= DMath.Sqrt((2.0 * k + 1.0) / (2.0 * k)) * s;
                int b = offExt[k];
                p[b] = pmm;
                p[b + 1] = DMath.Sqrt(2.0 * k + 3.0) * mu * pmm;
                for (int n = k + 2; n <= m + 1; n++)
                    p[b + (n - k)] = (mu * p[b + (n - 1 - k)] - Eps(n - 1, k) * p[b + (n - 2 - k)]) / Eps(n, k);
            }
        }

        // Roots of the Legendre polynomial of degree n (descending: north first) and the Gauss weights (sum 2).
        private static void GaussLegendre(int n, double[] x, double[] w)
        {
            int half = (n + 1) / 2;
            for (int i = 0; i < half; i++)
            {
                double z = DMath.Cos(DMath.Pi * (i + 0.75) / (n + 0.5));
                double pp = 1.0;
                for (int it = 0; it < 12; it++)
                {
                    double p1 = 1.0, p2 = 0.0;
                    for (int j = 1; j <= n; j++)
                    {
                        double p3 = p2; p2 = p1;
                        p1 = ((2.0 * j - 1.0) * z * p2 - (j - 1.0) * p3) / j;
                    }
                    pp = n * (z * p1 - p2) / (z * z - 1.0);
                    z = z - p1 / pp;
                }
                x[i] = z; x[n - 1 - i] = -z;
                w[i] = 2.0 / ((1.0 - z * z) * pp * pp); w[n - 1 - i] = w[i];
            }
        }

        // Grid (NLat * NLon) to spectral coefficients (re, im), length NCoef each.
        public void Analyze(double[] grid, double[] re, double[] im)
        {
            Array.Clear(re, 0, NCoef); Array.Clear(im, 0, NCoef);
            double inv = 1.0 / NLon;
            for (int j = 0; j < NLat; j++)
            {
                int row = j * NLon;
                double[] pj = pExt[j];
                for (int k = 0; k <= M; k++)
                {
                    double fr = 0.0, fi = 0.0;
                    int t = k * NLon;
                    for (int i = 0; i < NLon; i++) { double v = grid[row + i]; fr += v * cosT[t + i]; fi -= v * sinT[t + i]; }
                    fr *= inv * Weight[j]; fi *= inv * Weight[j];
                    int b = offExt[k], o = off[k];
                    for (int n = k; n <= M; n++) { double pv = pj[b + (n - k)]; re[o + (n - k)] += fr * pv; im[o + (n - k)] += fi * pv; }
                }
            }
        }

        // Spectral to grid.
        public void Synthesize(double[] re, double[] im, double[] grid)
        {
            for (int j = 0; j < NLat; j++)
            {
                int row = j * NLon;
                double[] pj = pExt[j];
                for (int i = 0; i < NLon; i++) grid[row + i] = 0.0;
                for (int k = 0; k <= M; k++)
                {
                    double fr = 0.0, fi = 0.0;
                    int b = offExt[k], o = off[k];
                    for (int n = k; n <= M; n++) { double pv = pj[b + (n - k)]; fr += re[o + (n - k)] * pv; fi += im[o + (n - k)] * pv; }
                    int t = k * NLon;
                    double f = k == 0 ? 1.0 : 2.0;
                    if (k == 0) { for (int i = 0; i < NLon; i++) grid[row + i] += fr; }
                    else for (int i = 0; i < NLon; i++) grid[row + i] += f * (fr * cosT[t + i] - fi * sinT[t + i]);
                }
            }
        }

        // The cos(lat)-weighted wind of a streamfunction: U = u cos(lat) = -(1/a) cos(lat) dpsi/dlat, V = v cos(lat) = (1/a) dpsi/dlon.
        public void SynthesizeWindCos(double[] psiRe, double[] psiIm, double radius, double[] ucos, double[] vcos)
        {
            double ia = 1.0 / radius;
            for (int j = 0; j < NLat; j++)
            {
                int row = j * NLon;
                double[] hj = hCos[j], pj = pExt[j];
                for (int i = 0; i < NLon; i++) { ucos[row + i] = 0.0; vcos[row + i] = 0.0; }
                for (int k = 0; k <= M; k++)
                {
                    double ur = 0.0, ui = 0.0, vr = 0.0, vi = 0.0;
                    int o = off[k], b = offExt[k];
                    for (int n = k; n <= M; n++)
                    {
                        double a = psiRe[o + (n - k)], c = psiIm[o + (n - k)];
                        double hv = hj[o + (n - k)], pv = pj[b + (n - k)];
                        ur -= a * hv; ui -= c * hv;
                        // d/dlon: multiply by i k: (a + i c) * i k = -k c + i k a
                        vr += -k * c * pv; vi += k * a * pv;
                    }
                    int t = k * NLon;
                    double f = k == 0 ? 1.0 : 2.0;
                    for (int i = 0; i < NLon; i++)
                    {
                        ucos[row + i] += f * (ur * cosT[t + i] - ui * sinT[t + i]) * ia;
                        vcos[row + i] += f * (vr * cosT[t + i] - vi * sinT[t + i]) * ia;
                    }
                }
            }
        }

        // Gradient parts of a scalar: dlon = d/dlon, dlatcos = cos(lat) d/dlat (both per radian, unscaled by the radius).
        public void SynthesizeGradient(double[] re, double[] im, double[] dlon, double[] dlatcos)
        {
            for (int j = 0; j < NLat; j++)
            {
                int row = j * NLon;
                double[] hj = hCos[j], pj = pExt[j];
                for (int i = 0; i < NLon; i++) { dlon[row + i] = 0.0; dlatcos[row + i] = 0.0; }
                for (int k = 0; k <= M; k++)
                {
                    double xr = 0.0, xi = 0.0, yr = 0.0, yi = 0.0;
                    int o = off[k], b = offExt[k];
                    for (int n = k; n <= M; n++)
                    {
                        double a = re[o + (n - k)], c = im[o + (n - k)];
                        double hv = hj[o + (n - k)], pv = pj[b + (n - k)];
                        xr += -k * c * pv; xi += k * a * pv;
                        yr += a * hv; yi += c * hv;
                    }
                    int t = k * NLon;
                    double f = k == 0 ? 1.0 : 2.0;
                    for (int i = 0; i < NLon; i++)
                    {
                        dlon[row + i] += f * (xr * cosT[t + i] - xi * sinT[t + i]);
                        dlatcos[row + i] += f * (yr * cosT[t + i] - yi * sinT[t + i]);
                    }
                }
            }
        }

        // d/dt of a scalar q from the cos-weighted fluxes A = U q, B = V q:  dq/dt = -(1/(a cos^2)) dA/dlon - (1/(a)) dB/dmu ... (spectral, accumulated into re/im)
        public void FluxDivergence(double[] A, double[] B, double radius, double[] re, double[] im)
        {
            Array.Clear(re, 0, NCoef); Array.Clear(im, 0, NCoef);
            double inv = 1.0 / NLon, ia = 1.0 / radius;
            for (int j = 0; j < NLat; j++)
            {
                int row = j * NLon;
                double[] pj = pExt[j], hj = hCos[j];
                double w = Weight[j] / (1.0 - Mu[j] * Mu[j]);
                for (int k = 0; k <= M; k++)
                {
                    double ar = 0.0, ai = 0.0, br = 0.0, bi = 0.0;
                    int t = k * NLon;
                    for (int i = 0; i < NLon; i++)
                    {
                        double va = A[row + i], vb = B[row + i];
                        ar += va * cosT[t + i]; ai -= va * sinT[t + i];
                        br += vb * cosT[t + i]; bi -= vb * sinT[t + i];
                    }
                    ar *= inv; ai *= inv; br *= inv; bi *= inv;
                    int o = off[k], b = offExt[k];
                    for (int n = k; n <= M; n++)
                    {
                        double pv = pj[b + (n - k)], hv = hj[o + (n - k)];
                        // (1/a) w [ -i k A P + B H ]
                        re[o + (n - k)] += ia * w * (k * ai * pv + br * hv);
                        im[o + (n - k)] += ia * w * (-k * ar * pv + bi * hv);
                    }
                }
            }
        }
    }
}
