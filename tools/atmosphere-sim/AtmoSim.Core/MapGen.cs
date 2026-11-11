using System;

namespace AtmoSim.Core
{
    // Synthetic ocean and terrain maps for the proof runs (B09's real grids replace them when they exist). A random
    // low-order spectral field is thresholded at the quantile that gives the requested ocean fraction.
    public static class MapGen
    {
        public static void Continents(Spectral g, double oceanTarget, ulong seed, double[] ocean, double[] terrain)
        {
            int n = g.NLon * g.NLat;
            if (oceanTarget <= 0.0) { for (int i = 0; i < n; i++) { ocean[i] = 0.0; terrain[i] = 0.0; } FillTerrain(g, seed, ocean, terrain, double.NegativeInfinity, 1.0); return; }
            if (oceanTarget >= 1.0) { for (int i = 0; i < n; i++) { ocean[i] = 1.0; terrain[i] = 0.0; } return; }
            double[] f = RandomField(g, seed, 8, 1.2);
            double mean = 0, wsum = 0;
            for (int j = 0; j < g.NLat; j++) for (int i = 0; i < g.NLon; i++) { mean += f[j * g.NLon + i] * g.Weight[j]; wsum += g.Weight[j]; }
            mean /= wsum;
            double var = 0;
            for (int j = 0; j < g.NLat; j++) for (int i = 0; i < g.NLon; i++) { double d = f[j * g.NLon + i] - mean; var += d * d * g.Weight[j]; }
            double sd = DMath.Sqrt(var / wsum);
            double lo = -6.0 * sd + mean, hi = 6.0 * sd + mean;
            for (int it = 0; it < 50; it++)
            {
                double mid = 0.5 * (lo + hi), share = 0;
                for (int j = 0; j < g.NLat; j++) for (int i = 0; i < g.NLon; i++) if (f[j * g.NLon + i] < mid) share += g.Weight[j];
                share /= wsum;
                if (share < oceanTarget) lo = mid; else hi = mid;
            }
            double thr = 0.5 * (lo + hi), soft = 0.12 * sd;
            for (int i = 0; i < n; i++)
            {
                double o = 0.5 + (thr - f[i]) / (2.0 * soft);
                ocean[i] = o < 0 ? 0.0 : (o > 1 ? 1.0 : o);
                terrain[i] = 0.0;
            }
            FillTerrain(g, seed, ocean, terrain, thr, sd);
        }

        private static void FillTerrain(Spectral g, ulong seed, double[] ocean, double[] terrain, double thr, double sd)
        {
            double[] f = RandomField(g, seed ^ 0x51ED270B3A6C1F27UL, 14, 1.0);
            double[] h = RandomField(g, seed, 8, 1.2);
            int n = g.NLon * g.NLat;
            double fs = 0;
            for (int i = 0; i < n; i++) fs = DMath.Max(fs, DMath.Abs(f[i]));
            for (int i = 0; i < n; i++)
            {
                if (ocean[i] >= 0.5) { terrain[i] = 0.0; continue; }
                double up = double.IsNegativeInfinity(thr) ? 0.3 : DMath.Max(0.0, (h[i] - thr) / sd);
                double hh = 1200.0 * up + 400.0 * DMath.Max(0.0, f[i] / (fs > 0 ? fs : 1.0));
                terrain[i] = DMath.Min(hh, 4500.0);
            }
        }

        private static double[] RandomField(Spectral g, ulong seed, int nmax, double slope)
        {
            var rng = new Rng(seed);
            double[] re = new double[g.NCoef], im = new double[g.NCoef];
            for (int m = 0; m <= g.M; m++)
                for (int n = m; n <= g.M && n <= nmax; n++)
                {
                    if (n < 1) continue;
                    int i = g.Index(m, n);
                    double amp = 1.0 / DMath.Pow(n, slope);
                    re[i] = amp * rng.Gauss(); im[i] = m == 0 ? 0.0 : amp * rng.Gauss();
                }
            double[] grid = new double[g.NLon * g.NLat];
            g.Synthesize(re, im, grid);
            return grid;
        }
    }
}
