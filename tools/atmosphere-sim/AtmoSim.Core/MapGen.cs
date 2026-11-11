using System;

namespace AtmoSim.Core
{
    // Height and ocean data of a body on a regular grid (row 0 north, column 0 at longitude -180, cell centres), the forcing input
    // that B09 delivers for Kerbin, Duna, Laythe and Eve. Moist is the ocean fraction smoothed over a wide neighbourhood: the
    // humidity proxy reads it so that the interior of a continent is drier than its coast.
    public sealed class HeightGrid
    {
        public readonly int NLon, NLat;
        public readonly double[] Ocean, TerrainM, Moist;

        public HeightGrid(int nlon, int nlat)
        {
            NLon = nlon; NLat = nlat;
            Ocean = new double[nlon * nlat]; TerrainM = new double[nlon * nlat]; Moist = new double[nlon * nlat];
        }

        public double LatDeg(int j) { return 90.0 - (j + 0.5) * 180.0 / NLat; }
        public double LonDeg(int i) { return -180.0 + (i + 0.5) * 360.0 / NLon; }

        public double OceanAt(double latRad, double lonRad) { return Bilinear(Ocean, latRad, lonRad); }
        public double MoistAt(double latRad, double lonRad) { return Bilinear(Moist, latRad, lonRad); }
        public double TerrainAt(double latRad, double lonRad) { return Bilinear(TerrainM, latRad, lonRad); }

        // Ocean fraction and the smoothed moisture source at once (one index computation).
        public void Lookup(double latRad, double lonRad, out double ocean, out double moist)
        {
            double fy = (DMath.HalfPi - latRad) / DMath.Pi * NLat - 0.5;
            double fx = (DMath.WrapPi(lonRad) + DMath.Pi) / DMath.TwoPi * NLon - 0.5;
            if (fy < 0.0) fy = 0.0;
            if (fy > NLat - 1.0) fy = NLat - 1.0;
            int j0 = (int)fy; int j1 = j0 + 1 < NLat ? j0 + 1 : j0;
            double ty = fy - j0;
            double fl = DMath.Floor(fx);
            int i0 = (int)fl; double tx = fx - fl;
            i0 = ((i0 % NLon) + NLon) % NLon; int i1 = i0 + 1 == NLon ? 0 : i0 + 1;
            int a00 = j0 * NLon + i0, a01 = j0 * NLon + i1, a10 = j1 * NLon + i0, a11 = j1 * NLon + i1;
            double w00 = (1.0 - tx) * (1.0 - ty), w01 = tx * (1.0 - ty), w10 = (1.0 - tx) * ty, w11 = tx * ty;
            ocean = Ocean[a00] * w00 + Ocean[a01] * w01 + Ocean[a10] * w10 + Ocean[a11] * w11;
            moist = Moist[a00] * w00 + Moist[a01] * w01 + Moist[a10] * w10 + Moist[a11] * w11;
        }

        // Bilinear lookup; longitude wraps, latitude clamps to the first and last row centre.
        public double Bilinear(double[] f, double latRad, double lonRad)
        {
            double fy = (DMath.HalfPi - latRad) / DMath.Pi * NLat - 0.5;
            double fx = (DMath.WrapPi(lonRad) + DMath.Pi) / DMath.TwoPi * NLon - 0.5;
            if (fy < 0.0) fy = 0.0;
            if (fy > NLat - 1.0) fy = NLat - 1.0;
            int j0 = (int)fy; int j1 = j0 + 1 < NLat ? j0 + 1 : j0;
            double ty = fy - j0;
            double fl = DMath.Floor(fx);
            int i0 = (int)fl; double tx = fx - fl;
            i0 = ((i0 % NLon) + NLon) % NLon; int i1 = i0 + 1 == NLon ? 0 : i0 + 1;
            double a = f[j0 * NLon + i0] * (1.0 - tx) + f[j0 * NLon + i1] * tx;
            double b = f[j1 * NLon + i0] * (1.0 - tx) + f[j1 * NLon + i1] * tx;
            return a * (1.0 - ty) + b * ty;
        }

        // Fills Moist from Ocean with a box blur of the given radius in degrees of arc.
        public void BuildMoist(double radiusDeg)
        {
            int rj = Math.Max(1, (int)(radiusDeg / (180.0 / NLat)));
            var tmp = new double[NLon * NLat];
            for (int j = 0; j < NLat; j++)
            {
                double c = Math.Max(0.05, Math.Cos(LatDeg(j) * Math.PI / 180.0));
                int ri = Math.Min(NLon / 2 - 1, Math.Max(1, (int)(rj / c)));
                for (int i = 0; i < NLon; i++)
                {
                    double s = 0; int n = 0;
                    for (int d = -ri; d <= ri; d++) { s += Ocean[j * NLon + ((i + d + NLon) % NLon)]; n++; }
                    tmp[j * NLon + i] = s / n;
                }
            }
            for (int j = 0; j < NLat; j++)
                for (int i = 0; i < NLon; i++)
                {
                    double s = 0; int n = 0;
                    for (int d = -rj; d <= rj; d++)
                    {
                        int jj = j + d; if (jj < 0 || jj >= NLat) continue;
                        s += tmp[jj * NLon + i]; n++;
                    }
                    Moist[j * NLon + i] = s / n;
                }
        }
    }

    // Synthetic ocean and terrain for the proof runs (the grids of B09 replace them when they exist). A random field of plane waves
    // on the sphere is thresholded at the quantile that gives the requested ocean fraction.
    public static class MapGen
    {
        // Radius in degrees of arc of the smoothing that turns the ocean fraction into the moisture source and the land/sea temperature contrast.
        public const double MoistBlurDeg = 30.0;

        public static HeightGrid Continents(int nlon, int nlat, double oceanTarget, ulong seed)
        {
            var g = new HeightGrid(nlon, nlat);
            int n = nlon * nlat;
            if (oceanTarget >= 1.0) { for (int i = 0; i < n; i++) g.Ocean[i] = 1.0; g.BuildMoist(MoistBlurDeg); return g; }
            double[] f = Field(nlon, nlat, seed, 40, 1.2);
            double[] w = new double[nlat];
            for (int j = 0; j < nlat; j++) w[j] = Math.Cos(g.LatDeg(j) * Math.PI / 180.0);
            double mean = 0, wsum = 0;
            for (int j = 0; j < nlat; j++) for (int i = 0; i < nlon; i++) { mean += f[j * nlon + i] * w[j]; wsum += w[j]; }
            mean /= wsum;
            double var = 0;
            for (int j = 0; j < nlat; j++) for (int i = 0; i < nlon; i++) { double d = f[j * nlon + i] - mean; var += d * d * w[j]; }
            double sd = DMath.Sqrt(var / wsum);
            double thr = double.NegativeInfinity;
            if (oceanTarget > 0.0)
            {
                double lo = mean - 6.0 * sd, hi = mean + 6.0 * sd;
                for (int it = 0; it < 50; it++)
                {
                    double mid = 0.5 * (lo + hi), share = 0;
                    for (int j = 0; j < nlat; j++) for (int i = 0; i < nlon; i++) if (f[j * nlon + i] < mid) share += w[j];
                    share /= wsum;
                    if (share < oceanTarget) lo = mid; else hi = mid;
                }
                thr = 0.5 * (lo + hi);
            }
            double soft = 0.1 * sd;
            double[] h = Field(nlon, nlat, seed ^ 0x51ED270B3A6C1F27UL, 60, 0.9);
            double hmax = 1e-9; for (int i = 0; i < n; i++) hmax = Math.Max(hmax, Math.Abs(h[i]));
            for (int i = 0; i < n; i++)
            {
                double o = oceanTarget <= 0.0 ? 0.0 : 0.5 + (thr - f[i]) / (2.0 * soft);
                g.Ocean[i] = o < 0 ? 0.0 : (o > 1 ? 1.0 : o);
                if (g.Ocean[i] >= 0.5) { g.TerrainM[i] = 0.0; continue; }
                double up = oceanTarget <= 0.0 ? 0.3 : DMath.Max(0.0, (f[i] - thr) / sd);
                g.TerrainM[i] = DMath.Min(1200.0 * up + 400.0 * DMath.Max(0.0, h[i] / hmax), 4500.0);
            }
            g.BuildMoist(MoistBlurDeg);
            return g;
        }

        private static double[] Field(int nlon, int nlat, ulong seed, int waves, double slope)
        {
            var rng = new Rng(seed);
            var kx = new double[waves]; var ky = new double[waves]; var kz = new double[waves]; var amp = new double[waves]; var ph = new double[waves];
            for (int m = 0; m < waves; m++)
            {
                double x = rng.Gauss(), y = rng.Gauss(), z = rng.Gauss();
                double l = DMath.Sqrt(x * x + y * y + z * z); if (l < 1e-9) { x = 1; l = 1; }
                double kmag = 1.5 + 5.0 * rng.Uniform();
                kx[m] = x / l * kmag; ky[m] = y / l * kmag; kz[m] = z / l * kmag;
                amp[m] = 1.0 / DMath.Pow(kmag, slope); ph[m] = DMath.TwoPi * rng.Uniform();
            }
            var f = new double[nlon * nlat];
            for (int j = 0; j < nlat; j++)
            {
                double lat = (90.0 - (j + 0.5) * 180.0 / nlat) * DMath.Pi / 180.0;
                double sl = DMath.Sin(lat), cl = DMath.Cos(lat);
                for (int i = 0; i < nlon; i++)
                {
                    double lon = (-180.0 + (i + 0.5) * 360.0 / nlon) * DMath.Pi / 180.0;
                    double px = cl * DMath.Cos(lon), py = cl * DMath.Sin(lon), pz = sl;
                    double s = 0;
                    for (int m = 0; m < waves; m++) s += amp[m] * DMath.Cos(kx[m] * px + ky[m] * py + kz[m] * pz + ph[m]);
                    f[j * nlon + i] = s;
                }
            }
            return f;
        }
    }
}
