using System;
using System.IO;
using System.Text.Json;
using AtmoSim.Core;

namespace AtmoSim.Runner
{
    // The B09 dumps (tests/data/recon/dumps) as input of the generator: the real height and ocean grid of a body and the climatology fitted to
    // the samples of KSP's temperature curves (zonal profile at sea level, lapse rate, day-night swing). The season is not in the samples
    // (the zonal profile is the same at all four phases), so the seasonal part of the generator stays its own: the insolation of H1.
    public static class ReconData
    {
        public static string FindDumpDir()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 12 && dir != null; i++)
            {
                string c = Path.Combine(dir, "tests", "data", "recon", "dumps");
                if (Directory.Exists(c)) return c;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        // The height and ocean grid; the dump has row 0 at the south pole and an ocean mask string of '0' and '1', this class flips it to row 0 north.
        public static HeightGrid LoadHeights(string path)
        {
            using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                var e = doc.RootElement;
                int w = e.GetProperty("width").GetInt32(), h = e.GetProperty("height").GetInt32();
                var g = new HeightGrid(w, h);
                var hs = e.GetProperty("heights_m");
                string mask = e.GetProperty("ocean_mask").GetString();
                for (int j = 0; j < h; j++)
                    for (int i = 0; i < w; i++)
                    {
                        int src = (h - 1 - j) * w + i;
                        bool ocean = mask[src] == '1';
                        double hm = hs[src].GetDouble();
                        g.Ocean[j * w + i] = ocean ? 1.0 : 0.0;
                        g.TerrainM[j * w + i] = ocean ? 0.0 : Math.Max(0.0, hm);
                    }
                g.BuildMoist(MapGen.MoistBlurDeg);
                return g;
            }
        }

        public sealed class Fit
        {
            public double MeanK, LapseKPerM, DiurnalAmpK, EquatorK, PoleK, MaxProfileStepK;
            public double[] ProfileK;
        }

        // Reads the sea-level zonal profile (mean over phases and longitudes, averaged over both hemispheres at |latitude| 0, 15, ..., 90), the mean
        // (cosine weights), the lapse rate from the 0 and 5000 m samples and the diurnal amplitude from the spread over longitude at the equator,
        // and writes them into the body. No search and no tuning: the generator takes the annual-mean profile as measured.
        public static Fit FitClimate(BodyParams b, string temperaturePath)
        {
            using (var doc = JsonDocument.Parse(File.ReadAllText(temperaturePath)))
            {
                var e = doc.RootElement;
                var lat = e.GetProperty("latitude_deg"); var alt = e.GetProperty("altitude_m"); var lon = e.GetProperty("longitude_deg");
                var ph = e.GetProperty("phase_ut"); var t = e.GetProperty("temperature_k");
                int nl = lat.GetArrayLength(), na = alt.GetArrayLength(), no = lon.GetArrayLength(), np = ph.GetArrayLength();
                Func<int, int, int, int, double> T = (p, o, a, l) => t[((p * no + o) * na + a) * nl + l].GetDouble();
                int a0 = -1, a5 = -1;
                for (int a = 0; a < na; a++) { if (alt[a].GetDouble() == 0.0) a0 = a; if (alt[a].GetDouble() == 5000.0) a5 = a; }
                var obs = new double[nl];
                for (int l = 0; l < nl; l++)
                {
                    double s = 0; for (int p = 0; p < np; p++) for (int o = 0; o < no; o++) s += T(p, o, a0, l);
                    obs[l] = s / (np * no);
                }
                var fit = new Fit { ProfileK = new double[7] };
                int[] cnt = new int[7];
                for (int l = 0; l < nl; l++)
                {
                    int k = (int)Math.Round(Math.Abs(lat[l].GetDouble()) / 15.0);
                    if (k < 0 || k > 6) continue;
                    fit.ProfileK[k] += obs[l]; cnt[k]++;
                }
                for (int k = 0; k < 7; k++) fit.ProfileK[k] /= Math.Max(1, cnt[k]);
                double mean = 0, ws = 0;
                for (int l = 0; l < nl; l++) { double w = Math.Max(0.05, Math.Cos(lat[l].GetDouble() * Math.PI / 180.0)); mean += obs[l] * w; ws += w; }
                fit.MeanK = mean / ws;
                fit.EquatorK = fit.ProfileK[0]; fit.PoleK = fit.ProfileK[6];
                for (int k = 1; k < 7; k++) fit.MaxProfileStepK = Math.Max(fit.MaxProfileStepK, Math.Abs(fit.ProfileK[k] - fit.ProfileK[k - 1]));
                int lEq = 0; double bd = 1e9;
                for (int l = 0; l < nl; l++) if (Math.Abs(lat[l].GetDouble()) < bd) { bd = Math.Abs(lat[l].GetDouble()); lEq = l; }
                double dsum = 0; int dn = 0;
                for (int p = 0; p < np; p++) for (int o = 0; o < no; o++) for (int l = 0; l < nl; l++) { dsum += T(p, o, a0, l) - T(p, o, a5, l); dn++; }
                fit.LapseKPerM = dsum / dn / 5000.0;
                double sp = 0;
                for (int p = 0; p < np; p++)
                {
                    double m = 0; for (int o = 0; o < no; o++) m += T(p, o, a0, lEq); m /= no;
                    for (int o = 0; o < no; o++) sp = Math.Max(sp, Math.Abs(T(p, o, a0, lEq) - m));
                }
                fit.DiurnalAmpK = sp;
                b.SurfaceMeanK = fit.MeanK; b.ProfileK = fit.ProfileK; b.LapseKPerM = fit.LapseKPerM; b.DiurnalLandAmpK = fit.DiurnalAmpK;
                return fit;
            }
        }

        // Applies the real grid and the fitted climatology of one of the four bodies the probe dumped (Kerbin, Duna, Laythe, Eve).
        public static Fit Apply(BodyParams b, string body, string dumpDir)
        {
            b.Grid = LoadHeights(Path.Combine(dumpDir, "run1-heights-" + body + ".json"));
            return FitClimate(b, Path.Combine(dumpDir, "run2-temperature-" + body + ".json"));
        }
    }
}
