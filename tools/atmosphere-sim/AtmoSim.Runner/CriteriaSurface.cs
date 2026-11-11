using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AtmoSim.Core;

namespace AtmoSim.Runner
{
    // Criterion 10 (surface layer) as the signed header defines it. Each check is a Report item; the checks that depend on the map run in the
    // data mode of the process (R: B09 grid, S: synthetic seeds A, B, C).
    public static class CriteriaSurface
    {
        private static double Rad(double deg) { return deg * Math.PI / 180.0; }

        // Sea sites of a row: texel centres every 5 degrees of longitude with an ocean fraction of at least 0.5.
        private static List<SurfaceSite> SeaSites(WeatherGenerator g, double latDeg, double lonStepDeg = 5.0)
        {
            var l = new List<SurfaceSite>();
            for (double lon = -180.0 + 0.5 * lonStepDeg; lon < 180.0; lon += lonStepDeg)
            {
                if (g.Grid.OceanAt(Rad(latDeg), Rad(lon)) < 0.5) continue;
                l.Add(new SurfaceSite(g, Rad(latDeg), Rad(lon)));
            }
            return l;
        }

        // (b) The lowest latitude (absolute degrees) at which at least half of the sea texels of a 2.5-degree row have a 10-day mean ice fraction of at least 0.15 in midwinter; NaN when none.
        private static double IceEdge(WeatherGenerator g, int hemi)
        {
            double phase = hemi == 0 ? 0.0 : 0.5;
            double year = g.Body.YearS;
            double edge = double.NaN;
            for (double a = 30.0; a <= 88.0; a += 2.5)
            {
                double lat = hemi == 0 ? a : -a;
                var sites = SeaSites(g, lat);
                if (sites.Count < 5) continue;
                int hit = 0;
                foreach (var s in sites)
                {
                    double m = 0;
                    for (int k = -2; k <= 2; k++) m += s.Evaluate((phase + 2.0) * year + k * 2.0 * 86400.0).IceFraction / 5.0;
                    if (m >= 0.15) hit++;
                }
                if (hit * 2 >= sites.Count) { edge = a; break; }
            }
            return edge;
        }

        public static int Run(string[] args)
        {
            string dir = args[1]; string mode = Criteria.ModeName;
            var tag = "surface_" + mode;
            var reports = new List<Report>();
            var seedRuns = new List<(ulong seed, string name)>();
            if (mode == "R") seedRuns.Add((1UL, "A")); else { seedRuns.Add((1UL, "A")); seedRuns.Add((2UL, "B")); seedRuns.Add((3UL, "C")); }
            var rep = new Report(10, tag);

            // (a) Stefan law, (e) melt, solar and snow insulation: the ice step itself
            {
                double tf = BodyParams.KerbinLike().FreezePointK, cap = 4.186e6 * 12.0;
                double tml = tf, h = 0.05, fdd = 0;
                for (int d = 0; d < 60; d++) { IceModel.Step(ref tml, ref h, tf - 10.0, 0.0, 0.0, tf, cap, 1.0); fdd += 10.0; }
                double exp = IceModel.StefanThickness(0.05, fdd);
                rep.Add("(a) Stefan law: thickness after 60 days at 10 K below freezing, model " + Criteria.Fmt(h) + " m, formula " + Criteria.Fmt(exp) + " m (relative difference)", Math.Abs(h - exp) / exp, "<= 2 %", Math.Abs(h - exp) / exp <= 0.02, true, "-", "-");
                double tm2 = tf, h2 = 1.0; IceModel.Step(ref tm2, ref h2, tf + 5.0, 0.0, 0.0, tf, cap, 1.0);
                double rate = 1.0 - h2, want = IceModel.MeltFactorMPerKDay * 5.0;
                rep.Add("(e) melt of one day at 5 K above freezing, model " + Criteria.Fmt(rate) + " m, melt factor times excess " + Criteria.Fmt(want) + " m (relative difference)", Math.Abs(rate - want) / want, "<= 5 %", Math.Abs(rate - want) / want <= 0.05, true, "-", "-");
                double tm3 = tf, h3 = 1.0; IceModel.Step(ref tm3, ref h3, tf + 5.0, 200.0, 0.0, tf, cap, 1.0);
                rep.Add("(e) a solar input of 200 W/m2 adds melt (extra melt in m)", h2 - h3, "> 0", h3 < h2, true, "-", "-");
                double tm4 = tf, h4 = 0.3, tm5 = tf, h5 = 0.3;
                IceModel.Step(ref tm4, ref h4, tf - 10.0, 0.0, 0.0, tf, cap, 1.0); IceModel.Step(ref tm5, ref h5, tf - 10.0, 0.0, 0.3, tf, cap, 1.0);
                rep.Add("(e) a snow cover of 0.3 m reduces the growth of one day (bare " + Criteria.Fmt(h4 - 0.3) + " m, covered " + Criteria.Fmt(h5 - 0.3) + " m)", h5 - 0.3, "< bare", h5 < h4, true, "-", "-");
            }

            var tasks = new List<Task>();
            var locker = new object();
            foreach (var sr in seedRuns)
            {
                var run = sr;
                tasks.Add(Task.Run(() =>
                {
                    var r = new Report(10, tag + "_" + run.name);
                    var bp = Study.ByName("kerbin"); var g = new WeatherGenerator(bp, run.seed);
                    // (b) ice edge
                    for (int h = 0; h < 2; h++)
                    {
                        double e = IceEdge(g, h);
                        r.Add("(b) sea-ice edge in midwinter, " + (h == 0 ? "north" : "south") + " (absolute degrees)", e, "50 .. 60", !double.IsNaN(e) && e >= 50.0 && e <= 60.0, true, mode, run.name);
                    }
                    // (c) heat-storage lag at seasonally icy sea sites (north, 55-75 degrees)
                    IceLag(r, g, mode, run.name);
                    // (d) no ice near the equator, none while the 30-day mean air temperature is at least T_f + 3 K
                    NoIceChecks(r, g, mode, run.name);
                    // (f) continuity at the anchor boundaries
                    Continuity(r, g, mode, run.name);
                    // snow line
                    SnowLine(r, bp, run.seed, mode, run.name);
                    lock (locker) reports.Add(r);
                }));
            }
            if (mode == "R" || args.Length > 2)
            {
                tasks.Add(Task.Run(() =>
                {
                    var r = new Report(10, tag + "_sets");
                    // (d) no ice at ocean fraction 0, perennial ice on the cold set, salinity
                    var gn = new WeatherGenerator(Study.ByName("noocean"), 1);
                    double maxN = 0; int sea = 0; var rnd = new Random(3);
                    for (int n = 0; n < 150; n++)
                    {
                        double la = Math.Asin(2 * rnd.NextDouble() - 1), lo = (rnd.NextDouble() * 2 - 1) * Math.PI;
                        var s = new SurfaceSite(gn, la, lo); if (s.IsSea) sea++;
                        for (int k = 0; k < 8; k++) maxN = Math.Max(maxN, s.Evaluate((2.0 + k / 8.0) * gn.Body.YearS).IceThicknessM);
                    }
                    r.Add("(d) ocean fraction 0: sea texels among 150 sites / maximum ice thickness (m)", Criteria.Fmt(sea) + " / " + Criteria.Fmt(maxN), "0 / 0", sea == 0 && maxN == 0.0, true, mode, "A");
                    var gc = new WeatherGenerator(Study.ByName("cold"), 1);
                    double minPer = 1e9; int nper = 0;
                    for (double lat = 76.0; lat <= 89.0; lat += 3.0)
                        foreach (var s in SeaSites(gc, lat, 20.0))
                        {
                            nper++;
                            for (int k = 0; k < 24; k++) minPer = Math.Min(minPer, s.Evaluate((2.0 + k / 24.0) * gc.Body.YearS).IceThicknessM);
                        }
                    r.Add("(d) cold set: smallest ice thickness over a year at " + nper + " sea texels above 75 degrees (m)", nper > 0 ? minPer : double.NaN, "> 0.1 (perennial)", nper > 0 && minPer > 0.1, true, mode, "A");
                    var b35 = Study.ByName("kerbin"); b35.SalinityPsu = 35.0; var b0 = Study.ByName("kerbin"); b0.SalinityPsu = 0.0;
                    var g35 = new WeatherGenerator(b35, 1); var g0 = new WeatherGenerator(b0, 1);
                    double worst = -1e9; int nsal = 0;
                    for (double lat = 55.0; lat <= 85.0; lat += 5.0)
                    {
                        var s35 = SeaSites(g35, lat, 30.0); var s0 = SeaSites(g0, lat, 30.0);
                        for (int i = 0; i < Math.Min(s35.Count, s0.Count); i++)
                            for (int k = 0; k < 12; k++)
                            {
                                double ut = (2.0 + k / 12.0) * b35.YearS;
                                worst = Math.Max(worst, s35[i].Evaluate(ut).IceThicknessM - s0[i].Evaluate(ut).IceThicknessM); nsal++;
                            }
                    }
                    r.Add("(d) ice at 35 psu minus ice at 0 psu, largest difference over " + nsal + " site-times (m)", worst, "<= 0", worst <= 1e-9, true, mode, "A");
                    double dTf = b0.FreezePointK - b35.FreezePointK;
                    r.Add("(d) freezing point at 35 psu lower than at 0 psu by (K)", dTf, "1.89 +- 0.01", Math.Abs(dTf - 1.89) <= 0.01, true, mode, "A");
                    // dust and wet/dry state, snow bounds
                    Dust(r, mode); WetState(r, mode); SnowBounds(r, mode);
                    lock (locker) reports.Add(r);
                }));
            }
            Task.WaitAll(tasks.ToArray());
            rep.Save(dir);
            foreach (var r in reports) r.Save(dir);
            return 0;
        }

        // Seasonal ice sites of the north: thickness in spring over autumn, and the delays of ice formation and ice loss after the daily climatological air temperature falls and rises through T_f.
        private static void IceLag(Report r, WeatherGenerator g, string mode, string seed)
        {
            double year = g.Body.YearS, tf = g.Body.FreezePointK, day = 86400.0;
            var ratios = new List<double>(); var freeze = new List<double>(); var brk = new List<double>();
            int cand = 0;
            for (double lat = 54.0; lat <= 76.0; lat += 4.0)
                for (double lon = -180.0 + 7.5; lon < 180.0; lon += 15.0)
                {
                    if (g.Grid.OceanAt(Rad(lat), Rad(lon)) < 0.5) continue;
                    var s = new SurfaceSite(g, Rad(lat), Rad(lon));
                    int nd = (int)(2.0 * year / day);
                    var h = new double[nd]; var fr = new double[nd]; var tc = new double[nd];
                    double mx = 0, mn = 1e9;
                    for (int d = 0; d < nd; d++)
                    {
                        double ut = year + d * day;
                        var st = s.Evaluate(ut); h[d] = st.IceThicknessM; fr[d] = st.IceFraction;
                        tc[d] = g.ClimateTemperature(Rad(lat), Rad(lon), ut);
                        if (d < nd / 2) { mx = Math.Max(mx, h[d]); mn = Math.Min(mn, h[d]); }
                    }
                    if (mx < 0.1 || mn > 0.02) continue;
                    cand++;
                    double spring = 0, autumn = 0;
                    for (int d = 0; d < nd / 2; d++)
                    {
                        double ph = (d * day) / year;
                        if (Math.Abs(ph - 0.25) < 5.0 * day / year) spring += h[d];
                        if (Math.Abs(ph - 0.75) < 5.0 * day / year) autumn += h[d];
                    }
                    if (autumn > 1e-6) ratios.Add(spring / autumn);
                    // second year: first downward and upward crossing of T_f, then the first ice day / ice-free day after it
                    int half = nd / 2, down = -1, up = -1;
                    for (int d = half; d < nd - 1 && (down < 0 || up < 0); d++)
                    {
                        if (down < 0 && tc[d] > tf && tc[d + 1] <= tf) down = d + 1;
                        if (up < 0 && tc[d] <= tf && tc[d + 1] > tf) up = d + 1;
                    }
                    if (down >= 0)
                    {
                        int first = -1; for (int d = down; d < nd; d++) if (fr[d] >= 0.15) { first = d; break; }
                        if (first >= 0) freeze.Add(first - down);
                    }
                    if (up >= 0)
                    {
                        int lost = -1; for (int d = up; d < nd; d++) if (fr[d] < 0.15) { lost = d; break; }
                        if (lost >= 0) brk.Add(lost - up);
                    }
                }
            double ratio = Tracker.Median(ratios), fz = Tracker.Median(freeze), bk = Tracker.Median(brk);
            r.Add("(c) spring over autumn ice thickness at " + cand + " seasonally icy sea sites, median", ratio, ">= 1.2", cand >= 3 && ratio >= 1.2, true, mode, seed);
            r.Add("(c) delay of ice formation after the climatological air temperature falls through T_f (days, median of " + freeze.Count + " sites)", fz, ">= 1", fz >= 1.0, true, mode, seed);
            r.Add("(c) delay of ice loss after the air temperature rises through T_f (days, median of " + brk.Count + " sites)", bk, ">= 1", bk >= 1.0, true, mode, seed);
            double q = bk / fz;
            r.Add("(c) break-up delay over freeze-up delay", q, "0.3 .. 3.0", !double.IsNaN(q) && q >= 0.3 && q <= 3.0, true, mode, seed);
        }

        private static void NoIceChecks(Report r, WeatherGenerator g, string mode, string seed)
        {
            double year = g.Body.YearS, tf = g.Body.FreezePointK;
            double maxEq = 0; int nEq = 0;
            for (double lat = -14.0; lat <= 14.0; lat += 4.0)
                foreach (var s in SeaSites(g, lat, 20.0))
                {
                    nEq++;
                    for (int k = 0; k < 24; k++) maxEq = Math.Max(maxEq, s.Evaluate((2.0 + k / 24.0) * year).IceThicknessM);
                }
            r.Add("(d) largest ice thickness within 15 degrees of the equator over a year, " + nEq + " sea texels (m)", maxEq, "0", maxEq == 0.0, true, mode, seed);
            int viol = 0, tested = 0;
            for (double lat = -80.0; lat <= 80.0; lat += 10.0)
                for (double lon = -165.0; lon < 180.0; lon += 30.0)
                {
                    if (g.Grid.OceanAt(Rad(lat), Rad(lon)) < 0.5) continue;
                    var s = new SurfaceSite(g, Rad(lat), Rad(lon));
                    for (int k = 0; k < 16; k++)
                    {
                        double ut = (2.0 + k / 16.0) * year;
                        double m = 0; Weather w;
                        for (int d = 1; d <= 30; d++) { g.Sample(Rad(lat), Rad(lon), 0.0, ut - d * 86400.0, out w); m += w.TempMeanK / 30.0; }
                        tested++;
                        if (m >= tf + 3.0 && s.Evaluate(ut).IceThicknessM > 0.01) viol++;
                    }
                }
            r.Add("(d) ice (> 0.01 m) at a sea texel while the 30-day mean air temperature is at least T_f + 3 K, violations among " + tested + " site-times", viol, "0", viol == 0, true, mode, seed);
        }

        private static void Continuity(Report r, WeatherGenerator g, string mode, string seed)
        {
            double year = g.Body.YearS, quarter = year / 4.0;
            double[] lats = { 55, 60, 65, 70, 75 };
            double worst = 0; int sites = 0;
            foreach (double lat in lats)
            {
                SurfaceSite s = null;
                for (double lon = -175.0; lon < 180.0 && s == null; lon += 10.0)
                    if (g.Grid.OceanAt(Rad(lat), Rad(lon)) >= 0.5) s = new SurfaceSite(g, Rad(lat), Rad(lon));
                if (s == null) continue;
                sites++;
                for (int n = 4; n < 16; n++)
                {
                    double b = n * quarter;
                    double prev = s.Evaluate(b - 86400.0).IceThicknessM;
                    for (double t = b - 86400.0 + 600.0; t <= b + 86400.0; t += 600.0)
                    {
                        double cur = s.Evaluate(t).IceThicknessM;
                        worst = Math.Max(worst, Math.Abs(cur - prev)); prev = cur;
                    }
                }
            }
            r.Add("(f) largest ice thickness change per 10 minutes across the quarter-year boundaries, 3 years, " + sites + " sites (m)", worst, "<= 0.03", sites > 0 && worst <= 0.03, true, mode, seed);
        }

        private static void SnowLine(Report r, BodyParams bp, ulong seed, string mode, string name)
        {
            var g = new WeatherGenerator(bp, seed);
            double year = bp.YearS;
            double[] bands = { 0, 30, 45, 55, 70, 85 };
            double[] phases = { 0.0, 0.25, 0.5, 0.75 };
            foreach (double band in bands)
            {
                var sites = new List<SurfaceSite>();
                for (double lon = -179.375; lon < 180.0; lon += 1.25)
                    if (g.Grid.OceanAt(Rad(band), Rad(lon)) < 0.5) sites.Add(new SurfaceSite(g, Rad(band), Rad(lon)));
                string key = "snow line at " + band + " degrees north";
                if (sites.Count < 20) { r.Add(key + " (" + sites.Count + " land texels)", "NOT EVALUATED", "at least 20 land texels", true, false, mode, name); continue; }
                var cov = new double[4];
                for (int p = 0; p < 4; p++)
                {
                    int hit = 0, n = 0;
                    foreach (var s in sites)
                        for (int k = -2; k <= 2; k++) { n++; if (s.Evaluate((2.0 + phases[p]) * year + k * 2.0 * 86400.0).SnowSweMm >= 2.5) hit++; }
                    cov[p] = (double)hit / n;
                }
                string vals = "winter " + Criteria.Fmt(cov[0]) + ", spring " + Criteria.Fmt(cov[1]) + ", summer " + Criteria.Fmt(cov[2]) + ", autumn " + Criteria.Fmt(cov[3]);
                bool ok; string thr;
                switch ((int)band)
                {
                    case 0: ok = cov[0] <= 0.01 && cov[1] <= 0.01 && cov[2] <= 0.01 && cov[3] <= 0.01; thr = "<= 0.01 in all seasons"; break;
                    case 30: ok = cov[0] <= 0.10 && cov[1] <= 0.10 && cov[2] <= 0.10 && cov[3] <= 0.10; thr = "<= 0.10 in all seasons"; break;
                    case 45: ok = cov[0] >= 0.30 && cov[2] <= 0.05; thr = "winter >= 0.30, summer <= 0.05"; break;
                    case 55: ok = cov[0] > 0.6 && cov[2] < 0.05; thr = "winter > 0.6, summer < 0.05"; break;
                    case 70: ok = cov[0] >= 0.9; thr = "winter >= 0.9"; break;
                    default: ok = cov[0] >= 0.95; thr = "winter >= 0.95"; break;
                }
                r.Add(key + " (" + sites.Count + " land texels)", vals, thr, ok, true, mode, name);
            }
        }

        private static void Dust(Report r, string mode)
        {
            // Kerbin-like set: no dust deposit anywhere; Duna-like: lifting only where the wind of the 6-hour step exceeds the threshold
            var gk = new WeatherGenerator(Study.ByName("kerbin"), 1);
            double maxK = 0; var rnd = new Random(11);
            for (int n = 0; n < 60; n++)
            {
                double la = Math.Asin(2 * rnd.NextDouble() - 1), lo = (rnd.NextDouble() * 2 - 1) * Math.PI;
                var s = new SurfaceSite(gk, la, lo);
                for (int k = 0; k < 6; k++) maxK = Math.Max(maxK, s.Evaluate((2.0 + k / 6.0) * gk.Body.YearS).DustDepositMm);
            }
            r.Add("dust deposit on the Kerbin-like set, largest value over 60 sites (mm)", maxK, "0", maxK == 0.0, true, mode, "A");
            var bd = Study.ByName("duna"); var gd = new WeatherGenerator(bd, 1);
            int viol = 0, lifts = 0, tested = 0; double maxDep = 0; Weather w;
            for (int n = 0; n < 120; n++)
            {
                double la = Math.Asin(2 * rnd.NextDouble() - 1), lo = (rnd.NextDouble() * 2 - 1) * Math.PI;
                var s = new SurfaceSite(gd, la, lo);
                double terr = gd.Grid.TerrainAt(la, lo);
                for (int k = 0; k < 40; k++)
                {
                    double ut = (2.0 + k * 0.011) * bd.YearS;
                    var st = s.Evaluate(ut); tested++;
                    maxDep = Math.Max(maxDep, st.DustDepositMm);
                    if (st.DustLifting)
                    {
                        lifts++;
                        long step = (long)Math.Floor(ut / SurfaceSite.StepS); double f = ut / SurfaceSite.StepS - step;
                        gd.Sample(la, lo, terr, (f < 0.5 ? step : step + 1) * SurfaceSite.StepS, out w);
                        if (w.WindSpeed <= bd.DustWindThresholdMs) viol++;
                    }
                }
            }
            r.Add("Duna-like: dust lifting flagged where the wind of the step is not above the threshold (violations among " + lifts + " lifting site-times of " + tested + "; largest deposit " + Criteria.Fmt(maxDep) + " mm)", viol, "0", viol == 0 && maxDep <= 5.0 + 1e-9, true, mode, "A");
        }

        private static void WetState(Report r, string mode)
        {
            var bp = Study.ByName("kerbin"); var g = new WeatherGenerator(bp, 1);
            var rnd = new Random(21); Weather w;
            int rainy = 0, rainyWet = 0, dry = 0, dryDry = 0;
            for (int n = 0; n < 150; n++)
            {
                double la = Math.Asin(2 * rnd.NextDouble() - 1), lo = (rnd.NextDouble() * 2 - 1) * Math.PI;
                var s = new SurfaceSite(g, la, lo);
                double terr = g.Grid.TerrainAt(la, lo);
                for (int k = 0; k < 40; k++)
                {
                    double ut = (2.0 + k * 0.027) * bp.YearS;
                    double rain = 0;
                    for (int q = 0; q <= 2; q++) { g.Sample(la, lo, terr, ut - q * 10800.0, out w); rain += w.PrecipMmH / 3.0; }
                    bool anyRain = false;
                    for (int q = 0; q <= 20 && !anyRain; q++) { g.Sample(la, lo, terr, ut - q * 21600.0, out w); if (w.PrecipMmH > 0.0) anyRain = true; }
                    double wet = s.Evaluate(ut).Wet01;
                    if (rain >= 0.5) { rainy++; if (wet >= 0.5) rainyWet++; }
                    if (!anyRain) { dry++; if (wet <= 0.05) dryDry++; }
                }
            }
            double a = rainy > 0 ? (double)rainyWet / rainy : double.NaN, b = dry > 0 ? (double)dryDry / dry : double.NaN;
            r.Add("wet state >= 0.5 where the rain of the previous 6 h averaged >= 0.5 mm/h (share of " + rainy + " pairs)", a, ">= 0.9", rainy >= 30 && a >= 0.9, true, mode, "A");
            r.Add("wet state <= 0.05 where it did not rain in the previous 5 days (share of " + dry + " pairs)", b, ">= 0.9", dry >= 30 && b >= 0.9, true, mode, "A");
        }

        private static void SnowBounds(Report r, string mode)
        {
            var bp = Study.ByName("kerbin"); var g = new WeatherGenerator(bp, 1);
            double[,] pos = { { 70, 20 }, { 55, -100 }, { 45, 60 }, { 20, 0 }, { 0, 30 }, { -30, 120 }, { -55, -60 }, { -70, 150 }, { 80, -10 }, { 62, 170 }, { -10, -140 }, { 35, -40 } };
            double lo = 1e9, hi = -1e9; bool nan = false; int sites = 0;
            for (int i = 0; i < pos.GetLength(0); i++)
            {
                var s = new SurfaceSite(g, Rad(pos[i, 0]), Rad(pos[i, 1])); sites++;
                for (double t = bp.YearS; t < 6.0 * bp.YearS; t += 2.0 * 86400.0)
                {
                    var st = s.Evaluate(t); double v = st.SnowSweMm;
                    if (double.IsNaN(v) || double.IsNaN(st.IceThicknessM)) nan = true;
                    lo = Math.Min(lo, v); hi = Math.Max(hi, v);
                }
            }
            r.Add("snow water equivalent over 5 simulated years at " + sites + " sites: smallest / largest (mm)", Criteria.Fmt(lo) + " / " + Criteria.Fmt(hi), "0 .. 2000, no NaN", !nan && lo >= 0.0 && hi <= 2000.0, true, mode, "A");
        }
    }
}
