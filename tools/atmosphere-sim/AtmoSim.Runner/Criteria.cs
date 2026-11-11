using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AtmoSim.Core;

namespace AtmoSim.Runner
{
    // One piece of the phase-2 table: a set of measured items of one criterion, each with the threshold of the signed header (RESULT.md H5),
    // the data mode (R: B09 dumps, S: synthetic maps) and the seed it came from. Evaluate merges the pieces. Kind GATE counts for GO,
    // REPORT is shown with its expectation.
    public sealed class Report
    {
        public readonly int Id; public readonly string Tag;
        public readonly List<Dictionary<string, object>> Items = new List<Dictionary<string, object>>();

        public Report(int id, string tag) { Id = id; Tag = tag; }

        public void Add(string test, object value, string threshold, bool pass, bool gate, string mode, string seed)
        {
            Items.Add(new Dictionary<string, object> { { "test", test }, { "value", value is double ? Criteria.Fmt((double)value) : value }, { "threshold", threshold },
                { "pass", pass }, { "kind", gate ? "GATE" : "REPORT" }, { "mode", mode }, { "seed", seed } });
        }

        // A report item without a pass/fail verdict.
        public void Info(string test, object value, string expectation, string mode, string seed) { Add(test, value, expectation, true, false, mode, seed); }

        public void Save(string dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "crit" + Id + "_" + Tag + ".json"), Json.Write(new Dictionary<string, object> { { "id", Id }, { "tag", Tag }, { "items", Items } }));
        }
    }

    // The phase-2 measurements of criteria 1 to 5 and 9 to 12 as the signed header defines them.
    public static class Criteria
    {
        public static string Fmt(double v) { return double.IsNaN(v) ? "NaN" : v.ToString("G5", CultureInfo.InvariantCulture); }

        public static double D(Dictionary<string, object> d, string k)
        {
            object o = d[k];
            if (o == null) return double.NaN;
            return Convert.ToDouble(o, CultureInfo.InvariantCulture);
        }

        public static Dictionary<string, object> Sub(Dictionary<string, object> d, string k) { return (Dictionary<string, object>)d[k]; }

        public static string ModeName { get { return Study.ReconDir != null ? "R" : "S"; } }

        // ---- criteria 1, 2 and the tracker part of 12 --------------------------------------------------------------------------------------

        // crit-tracker <outDir>: Kerbin-like seeds A, B, C (1, 2, 3) for 60 days of 3 h frames from UT 0. GATE items come from the deciding run (mode R, seed A);
        // mode S and the seeds B and C are reported beside. Mode R also runs the waves-off and the rotation-times-1000 controls.
        public static int Tracker(string[] args)
        {
            string dir = args[1];
            string mode = ModeName;
            var seeds = new ulong[] { 1, 2, 3 };
            var results = new Dictionary<ulong, Dictionary<string, object>>();
            var tasks = new List<System.Threading.Tasks.Task>();
            foreach (ulong s in seeds)
            {
                ulong seed = s;
                tasks.Add(System.Threading.Tasks.Task.Run(() =>
                {
                    var r = Study.RunTracker(Study.ByName("kerbin"), seed, 60.0, 3.0, 0.0);
                    lock (results) results[seed] = r;
                }));
            }
            Dictionary<string, object> off = null, rot = null;
            double rotGate = double.NaN; int rotActive = -1;
            if (mode == "R" || args.Length > 2)
            {
                tasks.Add(System.Threading.Tasks.Task.Run(() =>
                {
                    var bp = Study.ByName("kerbin"); bp.WavesEnabled = false;
                    off = Study.RunTracker(bp, 1, 60.0, 3.0, 0.0);
                }));
                tasks.Add(System.Threading.Tasks.Task.Run(() =>
                {
                    var bp = Study.ByName("kerbin"); bp.RotationPeriodS *= 1000.0;
                    rot = Study.RunTracker(bp, 1, 60.0, 3.0, 0.0);
                    var g = new WeatherGenerator(bp, 1);
                    rotGate = g.D.WaveGate;
                    int act = 0; var buf = new double[60];
                    for (int d = 0; d < 60; d++) act += g.ActiveSystems(0, d * 86400.0, buf) + g.ActiveSystems(1, d * 86400.0, buf);
                    rotActive = act;
                }));
            }
            System.Threading.Tasks.Task.WaitAll(tasks.ToArray());
            double popMax, popLimit;
            Popping(Study.ByName("kerbin"), 1, out popMax, out popLimit);

            var r1 = new Report(1, "tracker_" + mode); var r2 = new Report(2, "tracker_" + mode); var r12 = new Report(12, "tracker_" + mode);
            foreach (ulong s in seeds)
            {
                var r = results[s];
                bool decide = mode == "R" && s == 1;
                string sd = s == 1 ? "A" : s == 2 ? "B" : "C";
                File.WriteAllText(Path.Combine(dir, "tracker_" + mode + "_seed" + s + ".json"), Json.Write(r));
                Crit1And2(r1, r2, r, decide, mode, sd);
                if (decide)
                {
                    r1.Add("popping: largest amplitude change of a slot in one hour at a hand-over, share of the largest amplitude", popMax / popLimit * 0.005, "<= 0.005", popMax <= 0.005 * popLimit, true, mode, sd);
                    Crit12Tracker(r12, r, mode, sd);
                }
                else r1.Info("popping (same for all seeds)", popMax / popLimit, "<= 0.005", mode, sd);
            }
            if (off != null)
            {
                File.WriteAllText(Path.Combine(dir, "tracker_" + mode + "_wavesoff.json"), Json.Write(off));
                double days = (D(off, "frames") * D(off, "frame_seconds")) / 86400.0;
                double evN = Events(off, 0), evS = Events(off, 1);
                r12.Add("waves off: front events per 10 days north", evN / days * 10.0, "<= 0.1", evN / days * 10.0 <= 0.1, true, mode, "A");
                r12.Add("waves off: front events per 10 days south", evS / days * 10.0, "<= 0.1", evS / days * 10.0 <= 0.1, true, mode, "A");
                r12.Add("waves off: persistent D1 tracks north / south", D(Sub(off, "d1_tracker_north"), "count") + D(Sub(off, "d1_tracker_south"), "count"), "0", D(Sub(off, "d1_tracker_north"), "count") + D(Sub(off, "d1_tracker_south"), "count") == 0, true, mode, "A");
            }
            if (rot != null)
            {
                File.WriteAllText(Path.Combine(dir, "tracker_" + mode + "_rotx1000.json"), Json.Write(rot));
                double cnt = D(Sub(rot, "d1_tracker_north"), "count") + D(Sub(rot, "d1_tracker_south"), "count");
                r12.Add("rotation period x 1000: wave gate", rotGate, "0", rotGate == 0.0, true, mode, "A");
                r12.Add("rotation period x 1000: active systems over 60 daily samples", rotActive, "0", rotActive == 0, true, mode, "A");
                r12.Add("rotation period x 1000: persistent D1 tracks", cnt, "0", cnt == 0, true, mode, "A");
            }
            r1.Save(dir); r2.Save(dir); r12.Save(dir);
            return 0;
        }

        private static double Events(Dictionary<string, object> r, int hemi)
        {
            return D(Sub(r, hemi == 0 ? "d2_events_assigned_north" : "d2_events_assigned_south"), "events");
        }

        private static void Crit1And2(Report r1, Report r2, Dictionary<string, object> r, bool gate, string mode, string seed)
        {
            string[] hn = { "north", "south" };
            double days = (D(r, "frames") * D(r, "frame_seconds")) / 86400.0;
            double bodyDayS = 21549.425;
            for (int h = 0; h < 2; h++)
            {
                string nm = hn[h];
                double jet = D(r, h == 0 ? "jet_mean_north" : "jet_mean_south");
                var t1 = Sub(r, "d1_tracker_" + nm);
                double act = D(r, "d1_persistent_active_mean_per_hemisphere_" + nm);
                r1.Add("persistent D1 tracks alive per hemisphere (mean), " + nm, act, ">= 3.0", act >= 3.0, gate, mode, seed);
                double life = D(t1, "lifetime_h_median");
                r1.Add("median lifetime of persistent tracks (h), " + nm, life, ">= 48", life >= 48.0, gate, mode, seed);
                double drift = D(t1, "east_drift_ms_median");
                r1.Add("median east drift (m/s) within 30 % of the mean jet speed " + Fmt(jet) + ", " + nm, drift, "within 30 % of jet", Math.Abs(drift - jet) <= 0.3 * jet, gate, mode, seed);
                r1.Info("tracks / straightness median / swaps / drift share within 30 % / eastward share, " + nm,
                    Fmt(D(t1, "count")) + " / " + Fmt(D(t1, "straightness_median")) + " / " + Fmt(D(t1, "swaps")) + " / " + Fmt(D(t1, "share_drift_within_30pct_of_jet")) + " / " + Fmt(D(t1, "share_eastward")), "reported (shares >= 0.6 / >= 0.9 expected)", mode, seed);
                double ts = D(r, "time_shuffled_d1_per_hemisphere_" + nm);
                r1.Info("time-shuffle ratio of the persistent count, " + nm, ts / Math.Max(1e-9, act), "reported, <= 0.35 expected", mode, seed);

                var ev = Sub(r, "d2_events_assigned_" + nm);
                double n = D(ev, "events");
                r2.Add("persistent front events per 10 days (SI), " + nm, n / days * 10.0, ">= 1", n / days * 10.0 >= 1.0, gate, mode, seed);
                r2.Info("front events per 10 body days, " + nm, n / (days * 86400.0 / bodyDayS) * 10.0, "reported", mode, seed);
                double sh = D(ev, "share_with_system");
                r2.Add("share of events moving with their system, " + nm, sh, ">= 0.8", sh >= 0.8, gate, mode, seed);
                double ed = D(ev, "east_drift_ms_median");
                r2.Add("median east drift of the events (m/s) within 50 % of the jet speed " + Fmt(jet) + ", " + nm, ed, "within 50 % of jet", Math.Abs(ed - jet) <= 0.5 * jet, gate, mode, seed);
                var ft = Sub(r, "d2_tracker_" + nm); var fw = Sub(r, "d2_free_tracks_with_system_" + nm);
                r2.Info("free band tracks / median drift / share moving with a system / swaps / orphan band share, " + nm,
                    Fmt(D(ft, "count")) + " / " + Fmt(D(ft, "east_drift_ms_median")) + " / " + Fmt(D(fw, "share_moving_with_system")) + " / " + Fmt(D(ft, "swaps")) + " / " + Fmt(D(ev, "orphan_band_share")), "reported", mode, seed);
            }
            r1.Info("cloud water within R_system of the minima against the row mean (mean ratio)", D(r, "cloud_water_ratio_at_minima_mean"), "reported, > 1 expected", mode, seed);
        }

        private static void Crit12Tracker(Report r12, Dictionary<string, object> r, string mode, string seed)
        {
            double days = (D(r, "frames") * D(r, "frame_seconds")) / 86400.0;
            double cs = D(r, "control_shuffle_d1_per_hemisphere"), cn = D(r, "control_noise_d1_per_hemisphere");
            r12.Add("control, spatially shuffled field: persistent D1 per hemisphere", cs, "<= 0.1", cs <= 0.1, true, mode, seed);
            r12.Add("control, white noise: persistent D1 per hemisphere", cn, "<= 0.1", cn <= 0.1, true, mode, seed);
            double real = D(r, "real_d2_events_total");
            double es = D(r, "control_shuffle_d2_events_total"), en = D(r, "control_noise_d2_events_total");
            r12.Add("control, shuffled anomaly fields: free band tracks (real " + Fmt(real) + "); share of real and per 10 days", Fmt(es) + " (" + Fmt(es / Math.Max(1, real)) + "; " + Fmt(es / days * 10.0) + ")", "<= 5 % and <= 0.1 per 10 days",
                es <= 0.05 * real && es / days * 10.0 <= 0.1, true, mode, seed);
            r12.Add("control, white-noise anomaly fields: free band tracks; share of real and per 10 days", Fmt(en) + " (" + Fmt(en / Math.Max(1, real)) + "; " + Fmt(en / days * 10.0) + ")", "<= 5 % and <= 0.1 per 10 days",
                en <= 0.05 * real && en / days * 10.0 <= 0.1, true, mode, seed);
        }

        // The largest change of one slot's amplitude over any one-hour interval that contains a hand-over instant, over all slots and hand-overs in 60 days.
        public static void Popping(BodyParams bp, ulong seed, out double maxChange, out double largestAmp)
        {
            var g = new WeatherGenerator(bp, seed);
            largestAmp = 1.2 * g.D.PressureAmpPa * g.D.WaveGate;
            maxChange = 0.0;
            double horizon = 60.0 * 86400.0, hour = 3600.0;
            for (int h = 0; h < 2; h++)
                for (int j = 0; j < g.D.SlotsPerHemisphere; j++)
                {
                    double life, off;
                    g.SlotTiming(h, j, out life, out off);
                    for (double th = off + Math.Ceiling((0.0 - off) / life) * life; th < horizon; th += life)
                        for (double x = 0.0; x <= hour; x += 300.0)
                        {
                            double t0 = th - x;
                            double d = Math.Abs(g.SlotAmplitude(h, j, t0 + hour) - g.SlotAmplitude(h, j, t0));
                            if (d > maxChange) maxChange = d;
                        }
                }
        }

        // ---- criterion 4: level shares ----------------------------------------------------------------------------------------------------

        // crit-levels <outDir>: Kerbin-like seeds A, B, C; sites = grid cells with 45 to 70 degrees of latitude in the hemisphere's cold season (the quarter of the
        // year centred on its midwinter), one sample every 6 h.
        public static int Levels(string[] args)
        {
            string dir = args[1]; string mode = ModeName;
            var rep = new Report(4, "levels_" + mode);
            var rep5 = new Report(5, "blizzard_" + mode);
            var res = new Dictionary<ulong, double[]>();
            var tasks = new List<System.Threading.Tasks.Task>();
            for (ulong s = 1; s <= 3; s++)
            {
                ulong seed = s;
                tasks.Add(System.Threading.Tasks.Task.Run(() =>
                {
                    var bp = Study.ByName("kerbin");
                    var g = new WeatherGenerator(bp, seed);
                    int nx = 256, ny = 128;
                    var cnt = new double[7]; // total, precipitating, flurries, light, moderate, heavy (no blizzard), blizzard
                    double year = bp.YearS;
                    Weather w;
                    for (int hemi = 0; hemi < 2; hemi++)
                    {
                        double phase0 = hemi == 0 ? 0.0 : 0.5;
                        double t0 = (phase0 - 0.125) * year, t1 = (phase0 + 0.125) * year;
                        for (double ut = t0; ut < t1; ut += 21600.0)
                            for (int j = 0; j < ny; j++)
                            {
                                double latD = 90.0 - (j + 0.5) * 180.0 / ny;
                                if (hemi == 0 ? (latD < 45.0 || latD > 70.0) : (latD > -45.0 || latD < -70.0)) continue;
                                double lat = latD * Math.PI / 180.0;
                                for (int i = 0; i < nx; i++)
                                {
                                    g.Sample(lat, 2.0 * Math.PI * (i + 0.5) / nx, 0.0, ut, out w);
                                    cnt[0]++;
                                    if (w.PrecipMmH >= 0.05) cnt[1]++;
                                    var lv = g.LevelOf(w.PrecipMmH, w.Type, w.WindSpeed);
                                    if (lv == PrecipLevel.Flurries) cnt[2]++; else if (lv == PrecipLevel.Light) cnt[3]++; else if (lv == PrecipLevel.Moderate) cnt[4]++;
                                    else if (lv == PrecipLevel.Heavy) cnt[5]++; else if (lv == PrecipLevel.Blizzard) cnt[6]++;
                                }
                            }
                    }
                    lock (res) res[seed] = cnt;
                }));
            }
            System.Threading.Tasks.Task.WaitAll(tasks.ToArray());
            // The criterion passes if seed A passes (bounds and order) and seeds B and C pass the bounds (20-40 %, heavy, blizzard).
            for (ulong s = 1; s <= 3; s++)
            {
                var c = res[s]; double n = c[0];
                double prec = c[1] / n, fl = c[2] / n, li = c[3] / n, mo = c[4] / n, he = c[5] / n, bl = c[6] / n;
                bool share = prec >= 0.20 && prec <= 0.40, order = li > mo && mo > he && he > bl, hv = he < 0.05, blz = bl < 0.015;
                string sd = s == 1 ? "A" : s == 2 ? "B" : "C";
                rep.Add("precipitating share (rate >= 0.05 mm/h) at 45-70 degrees, cold season", prec, "20 % to 40 %", share, true, mode, sd);
                rep.Add("share Light > Moderate > Heavy > Blizzard (" + Fmt(li) + " > " + Fmt(mo) + " > " + Fmt(he) + " > " + Fmt(bl) + ")", order, "true (seed A gates; B and C reported)", order, s == 1, mode, sd);
                rep.Add("Heavy share (blizzard excluded)", he, "< 5 %", hv, true, mode, sd);
                rep.Add("Blizzard share", bl, "< 1.5 %", blz, true, mode, sd);
                rep.Info("Flurries share / Heavy-or-above share", Fmt(fl) + " / " + Fmt(he + bl), "reported", mode, sd);
                rep5.Add("blizzard condition reachable (samples at the criterion 4 sites) and rare (< 1.5 %)", bl, "> 0 and < 1.5 %", bl > 0.0 && blz, false, mode, sd);
            }
            rep.Save(dir); rep5.Save(dir);
            return 0;
        }

        // ---- criteria 3, 5, 9, 12 (fields over a year) ----------------------------------------------------------------------------------

        private sealed class YearStats
        {
            public double[] LatDeg; public double[] Precip, Cover, Hum, Wind, Cell, Dust; // per latitude band, area-mean over longitude and time
            public double GlobalCover, GlobalPrecip;
        }

        private static YearStats Year(BodyParams bp, ulong seed, int nSamples, int nx, int ny, double altM = 0.0)
        {
            var g = new WeatherGenerator(bp, seed);
            var ys = new YearStats { LatDeg = new double[ny], Precip = new double[ny], Cover = new double[ny], Hum = new double[ny], Wind = new double[ny], Cell = new double[ny], Dust = new double[ny] };
            double wsum = 0; Weather w;
            for (int k = 0; k < nSamples; k++)
            {
                double ut = (k + 0.5) / nSamples * bp.YearS;
                for (int j = 0; j < ny; j++)
                {
                    double lat = (90.0 - (j + 0.5) * 180.0 / ny) * Math.PI / 180.0;
                    ys.LatDeg[j] = lat * 180.0 / Math.PI;
                    double cw = Math.Cos(lat);
                    for (int i = 0; i < nx; i++)
                    {
                        g.Sample(lat, 2.0 * Math.PI * (i + 0.5) / nx, altM, ut, out w);
                        ys.Precip[j] += w.PrecipMmH; ys.Cover[j] += w.Coverage; ys.Hum[j] += w.Humidity; ys.Wind[j] += w.WindEast; ys.Cell[j] += w.CellStrength; ys.Dust[j] += w.Dust;
                        ys.GlobalCover += w.Coverage * cw; ys.GlobalPrecip += w.PrecipMmH * cw; wsum += cw;
                    }
                }
            }
            double per = (double)nSamples * nx;
            for (int j = 0; j < ny; j++) { ys.Precip[j] /= per; ys.Cover[j] /= per; ys.Hum[j] /= per; ys.Wind[j] /= per; ys.Cell[j] /= per; ys.Dust[j] /= per; }
            ys.GlobalCover /= wsum; ys.GlobalPrecip /= wsum;
            return ys;
        }

        private static double BandMean(YearStats y, double[] f, double lo, double hi)
        {
            double s = 0, n = 0;
            for (int j = 0; j < y.LatDeg.Length; j++)
            {
                double a = Math.Abs(y.LatDeg[j]);
                if (a >= lo && a < hi) { double c = Math.Cos(y.LatDeg[j] * Math.PI / 180.0); s += f[j] * c; n += c; }
            }
            return n > 0 ? s / n : double.NaN;
        }

        // Pearson correlation and the share of variance explained by latitude bands of precipitation samples of a set at 24 times of a year.
        private static void SubSolarCorrelation(BodyParams bp, out double corr, out double latShare)
        {
            var g = new WeatherGenerator(bp, 1);
            int nx = 72, ny = 36, nt = 24;
            var x = new List<double>(); var y = new List<double>(); var lat = new List<int>();
            Weather w;
            for (int k = 0; k < nt; k++)
            {
                double ut = (k + 0.5) / nt * bp.YearS;
                double sunLon = DMath.WrapPi(-(bp.Omega - DMath.TwoPi / bp.YearS) * ut);
                double decl = -bp.ObliquityRad * DMath.Cos(DMath.TwoPi * DMath.Frac(ut / bp.YearS));
                for (int j = 0; j < ny; j++)
                {
                    double la = (90.0 - (j + 0.5) * 180.0 / ny) * Math.PI / 180.0;
                    for (int i = 0; i < nx; i++)
                    {
                        double lo = 2.0 * Math.PI * (i + 0.5) / nx;
                        g.Sample(la, lo, 0.0, ut, out w);
                        double ca = Math.Sin(la) * Math.Sin(decl) + Math.Cos(la) * Math.Cos(decl) * Math.Cos(lo - sunLon);
                        x.Add(ca); y.Add(w.PrecipMmH); lat.Add(j);
                    }
                }
            }
            int n = x.Count; double mx = 0, my = 0; for (int i = 0; i < n; i++) { mx += x[i]; my += y[i]; } mx /= n; my /= n;
            double sxy = 0, sxx = 0, syy = 0; for (int i = 0; i < n; i++) { sxy += (x[i] - mx) * (y[i] - my); sxx += (x[i] - mx) * (x[i] - mx); syy += (y[i] - my) * (y[i] - my); }
            corr = sxy / Math.Sqrt(Math.Max(1e-30, sxx * syy));
            var bm = new double[ny]; var bc = new int[ny];
            for (int i = 0; i < n; i++) { bm[lat[i]] += y[i]; bc[lat[i]]++; }
            double between = 0; for (int j = 0; j < ny; j++) if (bc[j] > 0) { bm[j] /= bc[j]; between += bc[j] * (bm[j] - my) * (bm[j] - my); }
            latShare = between / Math.Max(1e-30, syy);
        }

        // crit-fields <outDir>: criteria 3 and 5 (REPORT), 9 (REPORT), the cell and coverage parts of 12 and the A/B pairs.
        public static int Fields(string[] args)
        {
            string dir = args[1]; string mode = ModeName;
            var r3 = new Report(3, "zones_" + mode); var r5 = new Report(5, "circulation_" + mode); var r9 = new Report(9, "smooth_" + mode); var r12 = new Report(12, "causal_" + mode);
            var names = new[] { "kerbin", "noocean", "slow", "locked", "duna", "eve", "laythe", "cold" };
            var stats = new Dictionary<string, YearStats>();
            var tasks = new List<System.Threading.Tasks.Task>();
            foreach (string nm in names)
            {
                string n2 = nm;
                tasks.Add(System.Threading.Tasks.Task.Run(() => { var y = Year(Study.ByName(n2), 1, 24, 72, 36); lock (stats) stats[n2] = y; }));
            }
            System.Threading.Tasks.Task.WaitAll(tasks.ToArray());
            var k = stats["kerbin"];
            // criterion 3
            {
                int jm = 0; double best = -1; for (int j = 0; j < k.LatDeg.Length; j++) if (k.Precip[j] > best) { best = k.Precip[j]; jm = j; }
                r3.Add("Kerbin-like: latitude of the zonal-mean precipitation maximum (deg)", k.LatDeg[jm], "within 15 of the equator", Math.Abs(k.LatDeg[jm]) <= 15.0, false, mode, "A");
                double eq = BandMean(k, k.Precip, 0, 10), sub = BandMean(k, k.Precip, 20, 35), pol = BandMean(k, k.Precip, 75, 91), mid = BandMean(k, k.Precip, 40, 60);
                r3.Add("Kerbin-like: 20-35 degree mean over 0-10 degree mean precipitation", sub / eq, "<= 0.6", sub / eq <= 0.6, false, mode, "A");
                r3.Add("Kerbin-like: polar (> 75) mean over 40-60 degree mean precipitation", pol / mid, "<= 0.5", pol / mid <= 0.5, false, mode, "A");
                var no = stats["noocean"];
                r3.Add("No-ocean: global mean cloud cover", no.GlobalCover, "<= 0.05", no.GlobalCover <= 0.05, false, mode, "A");
                r3.Add("No-ocean: mean precipitation over that of the Kerbin-like set", no.GlobalPrecip / Math.Max(1e-12, k.GlobalPrecip), "<= 0.05", no.GlobalPrecip <= 0.05 * k.GlobalPrecip, false, mode, "A");
                double ck, lk; SubSolarCorrelation(Study.ByName("kerbin"), out ck, out lk);
                foreach (string nm in new[] { "slow", "locked" })
                {
                    double c, l; SubSolarCorrelation(Study.ByName(nm), out c, out l);
                    r3.Add(nm + ": correlation of precipitation with the cosine of the angle to the sub-solar point", c, ">= 0.3", c >= 0.3, false, mode, "A");
                    r3.Add(nm + ": share of precipitation variance explained by latitude (Kerbin-like " + Fmt(lk) + ")", l, "< Kerbin-like", l < lk, false, mode, "A");
                }
                foreach (string nm in new[] { "duna" })
                {
                    var bp = Study.ByName(nm); var g = new WeatherGenerator(bp, 1); Weather w; int bad = 0;
                    for (int t = 0; t < 24; t++)
                        for (int j = 0; j < 36; j++)
                            for (int i = 0; i < 72; i++)
                            {
                                g.Sample((90.0 - (j + 0.5) * 5.0) * Math.PI / 180.0, 2 * Math.PI * (i + 0.5) / 72, 0.0, (t + 0.5) / 24.0 * bp.YearS, out w);
                                if (w.Dust > 0.0 && w.WindSpeed < 0.8 * bp.DustWindThresholdMs) bad++;
                            }
                    r3.Add("Duna-like: dust flag where the wind is below 0.8 of the threshold (count)", bad, "0", bad == 0, false, mode, "A");
                }
                foreach (string nm in names) r3.Info("zonal mean over a year, set " + nm + ": mean cover / mean precipitation (mm/h)", Fmt(stats[nm].GlobalCover) + " / " + Fmt(stats[nm].GlobalPrecip), "reported", mode, "A");
            }
            // criterion 5
            {
                string[] sets = { "kerbin", "duna", "eve", "laythe", "slow", "locked" };
                int[] expect = { 3, 1, 2, 2, -1, -1 };
                for (int q = 0; q < sets.Length; q++)
                {
                    var d = new WeatherGenerator(Study.ByName(sets[q]), 1).D;
                    if (expect[q] > 0) r5.Add(sets[q] + ": cells per hemisphere", d.Cells, expect[q].ToString(), d.Cells == expect[q], false, mode, "A");
                    else r5.Add(sets[q] + ": sub-solar regime weight", d.SubSolarWeight, ">= 0.9", d.SubSolarWeight >= 0.9, false, mode, "A");
                }
                int prev = 99; bool mono = true; var seq = new List<string>();
                foreach (double per in new[] { 5400.0, 21600.0, 43200.0, 86164.0, 200000.0, 864000.0 })
                {
                    var bp = Study.ByName("kerbin"); bp.RotationPeriodS = per;
                    int c = new WeatherGenerator(bp, 1).D.Cells; seq.Add(c.ToString());
                    if (c > prev) mono = false; prev = c;
                }
                r5.Add("cell count over the rotation sweep 5400, 21600, 43200, 86164, 200000, 864000 s (" + string.Join(",", seq) + ")", mono, "non-increasing", mono, false, mode, "A");
                double u1 = BandMean(k, k.Wind, 5, 25), u2 = BandMean(k, k.Wind, 40, 55), u3 = BandMean(k, k.Wind, 70, 85);
                r5.Add("Kerbin-like zonal-mean 10 m zonal wind 5-25 degrees (trades), m/s", u1, "-10 .. -1.5", u1 >= -10 && u1 <= -1.5, false, mode, "A");
                r5.Add("Kerbin-like zonal-mean 10 m zonal wind 40-55 degrees (westerlies), m/s", u2, "+1.5 .. +10", u2 >= 1.5 && u2 <= 10, false, mode, "A");
                r5.Add("Kerbin-like zonal-mean 10 m zonal wind 70-85 degrees (polar easterlies), m/s", u3, "-6 .. -0.3", u3 >= -6 && u3 <= -0.3, false, mode, "A");
                JetAndSeasons(r5, mode);
                foreach (string nm in sets) WindPercentiles(r5, nm, mode);
            }
            // criterion 9
            Smoothness(r9, mode);
            // criterion 12
            CausalFields(r12, mode);
            r3.Save(dir); r5.Save(dir); r9.Save(dir); r12.Save(dir);
            return 0;
        }

        private static void JetAndSeasons(Report r5, string mode)
        {
            var bp = Study.ByName("kerbin");
            var g = new WeatherGenerator(bp, 1);
            double alt = 0.8 * bp.TropopauseM;
            int nt = 24, ny = 90, nx = 36;
            var zm = new double[ny]; Weather w;
            var itcz = new double[nt]; var jetLat = new double[2, nt];
            for (int k = 0; k < nt; k++)
            {
                double ut = (k + 0.5) / nt * bp.YearS;
                var zk = new double[ny]; var hk = new double[ny];
                for (int j = 0; j < ny; j++)
                {
                    double la = (90.0 - (j + 0.5) * 2.0) * Math.PI / 180.0;
                    for (int i = 0; i < nx; i++)
                    {
                        g.Sample(la, 2 * Math.PI * (i + 0.5) / nx, alt, ut, out w); zk[j] += w.WindEast / nx;
                        g.Sample(la, 2 * Math.PI * (i + 0.5) / nx, 0.0, ut, out w); hk[j] += w.Humidity / nx;
                    }
                    zm[j] += zk[j] / nt;
                }
                double bh = -1; int jh = 0;
                for (int j = 0; j < ny; j++) { double lat = 90.0 - (j + 0.5) * 2.0; if (Math.Abs(lat) < 30 && hk[j] > bh) { bh = hk[j]; jh = j; } }
                itcz[k] = 90.0 - (jh + 0.5) * 2.0;
                for (int h = 0; h < 2; h++)
                {
                    double bw = -1e9; int jw = 0;
                    for (int j = 0; j < ny; j++) { double lat = 90.0 - (j + 0.5) * 2.0; if ((h == 0 ? lat > 20 : lat < -20) && zk[j] > bw) { bw = zk[j]; jw = j; } }
                    jetLat[h, k] = Math.Abs(90.0 - (jw + 0.5) * 2.0);
                }
            }
            for (int h = 0; h < 2; h++)
            {
                double bw = -1e9; int jw = 0;
                for (int j = 0; j < ny; j++) { double lat = 90.0 - (j + 0.5) * 2.0; if ((h == 0 ? lat > 20 : lat < -20) && zm[j] > bw) { bw = zm[j]; jw = j; } }
                double jl = Math.Abs(90.0 - (jw + 0.5) * 2.0);
                r5.Add("Kerbin-like jet latitude (zonal-mean wind maximum at 0.8 of the tropopause), " + (h == 0 ? "north" : "south") + " (deg)", jl, "40 .. 56", jl >= 40 && jl <= 56, false, mode, "A");
            }
            r5.Add("Kerbin-like steering jet speed (m/s)", g.D.JetSpeed0, "10 .. 30", g.D.JetSpeed0 >= 10 && g.D.JetSpeed0 <= 30, false, mode, "A");
            double mx = -1e9, mn = 1e9; foreach (double v in itcz) { mx = Math.Max(mx, v); mn = Math.Min(mn, v); }
            r5.Add("ITCZ latitude amplitude over a year (deg, humidity maximum within 30 degrees)", 0.5 * (mx - mn), "4 .. 15", 0.5 * (mx - mn) >= 4 && 0.5 * (mx - mn) <= 15, false, mode, "A");
            double bestLag = 0, bestC = -1e9;
            for (double lag = 0.0; lag <= 0.3; lag += 0.005)
            {
                double c = 0; for (int k = 0; k < nt; k++) c += itcz[k] * (-Math.Cos(2 * Math.PI * ((k + 0.5) / nt - lag)));
                if (c > bestC) { bestC = c; bestLag = lag; }
            }
            r5.Add("ITCZ lag behind the sub-solar latitude (fraction of the year; resolution 0.04)", bestLag, "0.02 .. 0.12", bestLag >= 0.02 && bestLag <= 0.12, false, mode, "A");
            for (int h = 0; h < 2; h++)
            {
                double jm = -1e9, jn = 1e9; for (int k = 0; k < nt; k++) { jm = Math.Max(jm, jetLat[h, k]); jn = Math.Min(jn, jetLat[h, k]); }
                r5.Add("jet latitude amplitude over a year, " + (h == 0 ? "north" : "south") + " (deg, resolution 2)", 0.5 * (jm - jn), "2 .. 8", 0.5 * (jm - jn) >= 2 && 0.5 * (jm - jn) <= 8, false, mode, "A");
            }
            double wN = g.JetSpeed(1, 0.0), sN = g.JetSpeed(-1, 0.0);
            r5.Add("jet speed in the winter hemisphere over the summer hemisphere (north winter, UT 0)", wN / sN, "> 1", wN > sN, false, mode, "A");
        }

        private static void WindPercentiles(Report r5, string set, string mode)
        {
            var bp = Study.ByName(set); var g = new WeatherGenerator(bp, 1);
            int nx = 72, ny = 36; Weather w;
            var val = new List<double>(); var wgt = new List<double>(); var vst = new List<double>();
            double[] phases = { 0.0, 0.25, 0.5, 0.75 };
            foreach (double ph in phases)
                for (int s = 0; s < 40; s++)
                {
                    double ut = (ph + (s - 20) * 6.0 * 3600.0 / bp.YearS) * bp.YearS;
                    if (ut < 0) ut += bp.YearS;
                    for (int j = 0; j < ny; j++)
                    {
                        double lat = (90.0 - (j + 0.5) * 5.0) * Math.PI / 180.0; double cw = Math.Cos(lat);
                        for (int i = 0; i < nx; i++)
                        {
                            g.Sample(lat, 2 * Math.PI * (i + 0.5) / nx, 0.0, ut, out w);
                            val.Add(w.WindSpeed); wgt.Add(cw);
                            if (Math.Abs(lat) >= 45 * Math.PI / 180 && Math.Abs(lat) <= 65 * Math.PI / 180 && w.OceanFraction >= 0.5) vst.Add(w.WindSpeed);
                        }
                    }
                }
            double p50 = WPercentile(val, wgt, 0.5), p95 = WPercentile(val, wgt, 0.95), p99 = WPercentile(val, wgt, 0.99);
            string label = set + ": 10 m wind P50 / P95 / P99 (m/s)";
            bool inRange = true; string exp = "reported";
            double lo = 0, hi = 0;
            switch (set)
            {
                case "kerbin": inRange = p50 >= 3 && p50 <= 9 && p95 >= 8 && p95 <= 20 && p99 >= 11 && p99 <= 26; exp = "P50 3-9, P95 8-20, P99 11-26"; break;
                case "duna": lo = 8; hi = 30; break;
                case "eve": lo = 2; hi = 15; break;
                case "laythe": lo = 3; hi = 20; break;
                case "slow": case "locked": lo = 3; hi = 15; break;
            }
            if (set != "kerbin") { inRange = p99 >= lo && p99 <= hi; exp = "P99 " + lo + "-" + hi; }
            r5.Add(label, Fmt(p50) + " / " + Fmt(p95) + " / " + Fmt(p99), exp, inRange, false, mode, "A");
            if (set == "kerbin")
            {
                vst.Sort(); double st = vst.Count > 0 ? vst[(int)(0.99 * (vst.Count - 1))] : double.NaN;
                r5.Add("Kerbin-like storm-track ocean cells (45-65 degrees) P99 (m/s)", st, "14 .. 30", st >= 14 && st <= 30, false, mode, "A");
            }
        }

        private static double WPercentile(List<double> v, List<double> w, double p)
        {
            int n = v.Count; var idx = new int[n]; for (int i = 0; i < n; i++) idx[i] = i;
            Array.Sort(idx, (a, b) => v[a].CompareTo(v[b]));
            double tot = 0; for (int i = 0; i < n; i++) tot += w[i];
            double acc = 0; for (int q = 0; q < n; q++) { acc += w[idx[q]]; if (acc >= p * tot) return v[idx[q]]; }
            return v[idx[n - 1]];
        }

        // Criterion 9: the 99th percentile of the change per game second of coverage and Level01 over all texels of a 512 x 256 map (maps 60 s apart at 24 times of a year).
        private static void Smoothness(Report r9, string mode)
        {
            var bp = Study.ByName("kerbin"); var g = new WeatherGenerator(bp, 1);
            int nx = 512, ny = 256;
            var c0 = new float[nx * ny]; var c1 = new float[nx * ny]; var l0 = new float[nx * ny]; var l1 = new float[nx * ny];
            var dc = new List<float>(); var dl = new List<float>();
            for (int k = 0; k < 24; k++)
            {
                double ut = (k + 0.5) / 24.0 * bp.YearS;
                SubGrid.Map(g, ut, nx, ny, c0, null, l0, null, null, null);
                SubGrid.Map(g, ut + 60.0, nx, ny, c1, null, l1, null, null, null);
                for (int i = 0; i < c0.Length; i++) { dc.Add(Math.Abs(c1[i] - c0[i]) / 60f); dl.Add(Math.Abs(l1[i] - l0[i]) / 60f); }
            }
            dc.Sort(); dl.Sort();
            double pc = dc[(int)(0.99 * (dc.Count - 1))], pl = dl[(int)(0.99 * (dl.Count - 1))];
            r9.Add("coverage change per game second, 99th percentile over texels and 24 times", pc, "< 2e-3 (provisional)", pc < 2e-3, false, mode, "A");
            r9.Add("Level01 change per game second, 99th percentile", pl, "< 2e-3 (provisional)", pl < 2e-3, false, mode, "A");
        }

        // The A/B and switch pairs of criterion 12 that are not tracker runs.
        private static void CausalFields(Report r12, string mode)
        {
            var bp = Study.ByName("kerbin");
            var ga = new WeatherGenerator(bp, 1); var gb = new WeatherGenerator(bp, 2);
            int nx = 128, ny = 64; Weather w;
            var ca = new double[nx * ny]; var cb = new double[nx * ny];
            double cors = 0; int nc = 0;
            for (int k = 0; k < 12; k++)
            {
                double ut = 3.0e6 + k * 5.0 * 86400.0;
                for (int j = 0; j < ny; j++)
                    for (int i = 0; i < nx; i++)
                    {
                        double la = (90.0 - (j + 0.5) * 180.0 / ny) * Math.PI / 180.0, lo = 2 * Math.PI * (i + 0.5) / nx;
                        ga.Sample(la, lo, 0.0, ut, out w); ca[j * nx + i] = w.Coverage;
                        gb.Sample(la, lo, 0.0, ut, out w); cb[j * nx + i] = w.Coverage;
                    }
                var za = Detect.ZonalAnomaly(new MetricGrid(nx, ny, bp.RadiusM), ca); var zb = Detect.ZonalAnomaly(new MetricGrid(nx, ny, bp.RadiusM), cb);
                double sxy = 0, sxx = 0, syy = 0; for (int i = 0; i < za.Length; i++) { sxy += za[i] * zb[i]; sxx += za[i] * za[i]; syy += zb[i] * zb[i]; }
                cors += sxy / Math.Sqrt(Math.Max(1e-30, sxx * syy)); nc++;
            }
            double cor = cors / nc;
            r12.Add("A/B: correlation of the coverage anomaly (zonal mean removed) of seed A and seed B, mean over 12 times", cor, "< 0.3", Math.Abs(cor) < 0.3, true, mode, "A");
            // the same seed twice gives the identical result (hash over 5000 positions)
            ulong h1 = 1, h2 = 1; var rnd = new Random(5);
            var g2 = new WeatherGenerator(bp.Clone(), 1);
            for (int n = 0; n < 5000; n++)
            {
                double la = (rnd.NextDouble() - 0.5) * Math.PI, lo = (rnd.NextDouble() - 0.5) * 2 * Math.PI, ut = rnd.NextDouble() * 1e7;
                ga.Sample(la, lo, 100.0, ut, out w); h1 = WeatherGenerator.HashOf(ref w, h1);
                g2.Sample(la, lo, 100.0, ut, out w); h2 = WeatherGenerator.HashOf(ref w, h2);
            }
            r12.Add("A/B: the same seed twice gives the identical hash", h1 == h2, "true", h1 == h2, true, mode, "A");
            // waves off: the mean cell strength in the belt 35-70 degrees at most 30 % of that with waves
            double on = 0, off = 0; var offBody = bp.Clone(); offBody.WavesEnabled = false;
            var gOff = new WeatherGenerator(offBody, 1);
            int cnt = 0;
            for (int k = 0; k < 60; k++)
            {
                double ut = 1.0e6 + k * 86400.0 + 3333.0;
                for (int j = 0; j < ny; j++)
                {
                    double latD = 90.0 - (j + 0.5) * 180.0 / ny; if (Math.Abs(latD) < 35 || Math.Abs(latD) > 70) continue;
                    double la = latD * Math.PI / 180.0;
                    for (int i = 0; i < nx; i++)
                    {
                        double lo = 2 * Math.PI * (i + 0.5) / nx;
                        ga.Sample(la, lo, 0.0, ut, out w); on += w.CellStrength;
                        gOff.Sample(la, lo, 0.0, ut, out w); off += w.CellStrength; cnt++;
                    }
                }
            }
            r12.Add("waves off: mean cell strength in the belt 35-70 degrees over that with waves (with " + Fmt(on / cnt) + ", without " + Fmt(off / cnt) + ")", off / Math.Max(1e-12, on), "<= 0.30", off <= 0.3 * on, true, mode, "A");
            // ocean fraction 0 versus 0.7: the coverage of the ocean-off body
            var noo = new WeatherGenerator(Study.ByName("noocean"), 1);
            double cov = 0, wsum = 0;
            for (int k = 0; k < 24; k++)
                for (int j = 0; j < ny; j++)
                {
                    double la = (90.0 - (j + 0.5) * 180.0 / ny) * Math.PI / 180.0; double cw = Math.Cos(la);
                    for (int i = 0; i < nx; i++) { noo.Sample(la, 2 * Math.PI * (i + 0.5) / nx, 0.0, (k + 0.5) / 24.0 * bp.YearS, out w); cov += w.Coverage * cw; wsum += cw; }
                }
            double kcov = 0; wsum = 0;
            for (int k = 0; k < 24; k++)
                for (int j = 0; j < ny; j++)
                {
                    double la = (90.0 - (j + 0.5) * 180.0 / ny) * Math.PI / 180.0; double cw = Math.Cos(la);
                    for (int i = 0; i < nx; i++) { ga.Sample(la, 2 * Math.PI * (i + 0.5) / nx, 0.0, (k + 0.5) / 24.0 * bp.YearS, out w); kcov += w.Coverage * cw; wsum += cw; }
                }
            double nov = cov / wsum;
            r12.Add("ocean fraction 0: mean coverage (the Kerbin-like set has " + Fmt(kcov / wsum) + ")", nov, "<= 0.05", nov <= 0.05, true, mode, "A");
        }
    }
}
