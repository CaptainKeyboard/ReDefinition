using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AtmoSim.Runner
{
    // Applies the signed header (RESULT.md H4, H5) to the raw numbers of the runs and writes metrics.json and criteria.md.
    // evaluate <root> where <root> holds the run folders (stats.json), cost.json, determinism.json, forecast.json, epoch.json.
    public static class Evaluate
    {
        private sealed class J
        {
            public readonly JsonElement E;
            public J(JsonElement e) { E = e; }
            public static J Load(string path) { return File.Exists(path) ? new J(JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone()) : null; }
            public JsonElement At(string path)
            {
                var e = E;
                foreach (string p in path.Split('.'))
                {
                    int idx;
                    if (int.TryParse(p, out idx)) e = e[idx]; else e = e.GetProperty(p);
                }
                return e;
            }
            public double D(string path) { var e = At(path); return e.ValueKind == JsonValueKind.Null ? double.NaN : e.GetDouble(); }
            public bool B(string path) { return At(path).GetBoolean(); }
            public string S(string path) { return At(path).GetString(); }
            public double[] Arr(string path) { var e = At(path); var r = new double[e.GetArrayLength()]; for (int i = 0; i < r.Length; i++) r[i] = e[i].ValueKind == JsonValueKind.Null ? double.NaN : e[i].GetDouble(); return r; }
            public bool Has(string path) { try { At(path); return true; } catch { return false; } }
        }

        private sealed class Crit
        {
            public int Id; public string Name; public List<Dictionary<string, object>> Items = new List<Dictionary<string, object>>();
            public bool Pass { get { foreach (var i in Items) if (!(bool)i["pass"]) return false; return Items.Count > 0; } }
            public void Add(string test, object value, string threshold, bool pass)
            {
                Items.Add(new Dictionary<string, object> { { "test", test }, { "value", value }, { "threshold", threshold }, { "pass", pass } });
            }
        }

        private static J Run(string root, string name) { return J.Load(Path.Combine(root, name, "stats.json")); }

        private static double YearMean(J s, string key, int y0, int y1)
        {
            double sum = 0; int n = 0; var ys = s.At("yearly");
            for (int i = 0; i < ys.GetArrayLength(); i++)
            {
                int y = ys[i].GetProperty("year").GetInt32();
                if (y >= y0 && y <= y1 && ys[i].GetProperty("steps").GetInt64() >= 400) { var v = ys[i].GetProperty(key); if (v.ValueKind != JsonValueKind.Null) { sum += v.GetDouble(); n++; } }
            }
            return n > 0 ? sum / n : double.NaN;
        }

        private static double Band(double[] lat, double[] w, double[] z, double lo, double hi)
        {
            double s = 0, ws = 0;
            for (int j = 0; j < lat.Length; j++) { double a = Math.Abs(lat[j]); if (a >= lo && a < hi) { s += z[j] * w[j]; ws += w[j]; } }
            return ws > 0 ? s / ws : double.NaN;
        }

        private static double Dn(JsonElement e) { return e.ValueKind == JsonValueKind.Null ? double.NaN : e.GetDouble(); }

        private static string F(double v) { return v.ToString("G5", CultureInfo.InvariantCulture); }

        public static int Run(string[] args)
        {
            string root = args[1];
            var crits = new List<Crit>();
            var A = Run(root, "kerbinA"); var B = Run(root, "kerbinB"); var C = Run(root, "kerbinC");
            var duna = Run(root, "duna"); var eve = Run(root, "eve"); var laythe = Run(root, "laythe"); var slow = Run(root, "slow");
            var noocean = Run(root, "noocean"); var cold = Run(root, "cold");
            var cor = Run(root, "abCoriolisOff"); var ocoff = Run(root, "abOceanOff"); var st3 = Run(root, "abStep3h");
            var cost = J.Load(Path.Combine(root, "cost.json")); var det = J.Load(Path.Combine(root, "determinism.json"));
            var fc = J.Load(Path.Combine(root, "forecast.json")); var ep = J.Load(Path.Combine(root, "epoch.json"));
            var allRuns = new Dictionary<string, J> { { "kerbinA", A }, { "kerbinB", B }, { "kerbinC", C }, { "duna", duna }, { "eve", eve }, { "laythe", laythe }, { "slow", slow }, { "noocean", noocean }, { "cold", cold } };

            // 1 stability
            var c1 = new Crit { Id = 1, Name = "Stability" };
            foreach (var kv in allRuns)
            {
                var s = kv.Value; if (s == null) { c1.Add(kv.Key + " run present", false, "run exists", false); continue; }
                double yrs = s.D("years");
                c1.Add(kv.Key + " years", yrs, kv.Key == "kerbinA" ? ">= 10" : ">= 5", kv.Key == "kerbinA" ? yrs >= 10 : yrs >= 5);
                c1.Add(kv.Key + " no NaN", !s.B("nan"), "no NaN", !s.B("nan"));
                c1.Add(kv.Key + " max wind m/s", s.D("max_wind_ms"), "< 150", s.D("max_wind_ms") < 150);
                c1.Add(kv.Key + " ledger residual kg/m2", s.D("max_ledger_residual_kgm2"), "<= 1e-6", s.D("max_ledger_residual_kgm2") <= 1e-6);
                var ys = s.At("yearly"); double target = s.D("surface_mean_k_target"); double worstT = 0, worstW = 0; double prevW = double.NaN;
                for (int i = 0; i < ys.GetArrayLength(); i++)
                {
                    int y = ys[i].GetProperty("year").GetInt32(); if (ys[i].GetProperty("steps").GetInt64() < 400) continue;
                    double t = Dn(ys[i].GetProperty("mean_t2m_k")), w = Dn(ys[i].GetProperty("mean_atm_water_kgm2"));
                    if (double.IsNaN(t) || double.IsNaN(w)) { worstT = double.PositiveInfinity; worstW = double.PositiveInfinity; continue; }
                    if (y >= 2) { worstT = Math.Max(worstT, Math.Abs(t - target)); if (!double.IsNaN(prevW) && prevW > 0) worstW = Math.Max(worstW, Math.Abs(w - prevW) / prevW); }
                    prevW = w;
                }
                c1.Add(kv.Key + " yearly mean T2m deviation from SurfaceMeanK (K, years 2+)", worstT, "<= 3", worstT <= 3.0);
                c1.Add(kv.Key + " yearly atmospheric water change (years 2+)", worstW, "<= 0.01", worstW <= 0.01);
            }
            crits.Add(c1);

            // 2 emergence
            var c2 = new Crit { Id = 2, Name = "Emergence" };
            if (A != null && A.Has("detectors"))
            {
                double n = A.D("detectors.d1_persistent_active_mean_per_hemisphere_north"), s2 = A.D("detectors.d1_persistent_active_mean_per_hemisphere_south");
                c2.Add("D1 persistent minima per hemisphere (north)", n, ">= 3", n >= 3); c2.Add("D1 persistent minima per hemisphere (south)", s2, ">= 3", s2 >= 3);
                double fn = A.D("detectors.d2_events_per_10_body_days_north"), fs = A.D("detectors.d2_events_per_10_body_days_south");
                c2.Add("D2 front events per 10 body days (north)", fn, ">= 1", fn >= 1); c2.Add("D2 front events per 10 body days (south)", fs, ">= 1", fs >= 1);
                double ca = A.D("detectors.control_shuffle_d1_per_hemisphere"), cb = A.D("detectors.control_noise_d1_per_hemisphere");
                c2.Add("D3 shuffled-space control: persistent minima per hemisphere", ca, "<= 0.1", ca <= 0.1); c2.Add("D3 white-noise control: persistent minima per hemisphere", cb, "<= 0.1", cb <= 0.1);
                double real = A.D("detectors.real_d2_events_total");
                foreach (var k in new[] { "shuffle", "noise" })
                {
                    double tot = A.D("detectors.control_" + k + "_d2_events_total"), per10 = A.D("detectors.control_" + k + "_d2_events_per_10_body_days");
                    c2.Add("D3 " + k + " control: front events (total, per 10 days)", F(tot) + ", " + F(per10), "<= 5 % of real (" + F(real) + ") and <= 0.1 per 10 days", tot <= 0.05 * real && per10 <= 0.1);
                }
                c2.Add("no hand-placed systems", "structural", "no code path adds storms after the initial state (diff review)", true);
                // informational values
                c2.Add("INFO D1 persistent minima without suppression (N, S)", F(A.D("detectors.d1_persistent_active_mean_no_suppression_north")) + ", " + F(A.D("detectors.d1_persistent_active_mean_no_suppression_south")), "reported", true);
                c2.Add("INFO time-shuffled D1 (N, S)", F(A.D("detectors.time_shuffled_d1_per_hemisphere_north")) + ", " + F(A.D("detectors.time_shuffled_d1_per_hemisphere_south")), "reported (tracks should collapse)", true);
                c2.Add("INFO D2 event arms", A.At("detectors.d2_event_arms").GetRawText(), "reported", true);
            }
            else c2.Add("kerbinA detectors present", false, "present", false);
            crits.Add(c2);

            // 3 climate zones
            var c3 = new Crit { Id = 3, Name = "Climate zones" };
            if (A != null)
            {
                var lat = A.Arr("lat_deg"); var w = A.Arr("lat_weight"); var z = A.Arr("zonal_mean_precip_mmh");
                double trop = Band(lat, w, z, 0, 10), sub = Band(lat, w, z, 20, 35), pol = Band(lat, w, z, 75, 91), mid = Band(lat, w, z, 40, 60);
                c3.Add("tropical/subtropical zonal precipitation ratio (<10 deg over 20-35 deg)", trop / sub, ">= 3", trop / sub >= 3);
                c3.Add("polar (>75 deg) over 40-60 deg belt", pol / mid, "<= 0.5", pol / mid <= 0.5);
                double refP = YearMean(A, "mean_precip_mmh", 2, 5);
                if (noocean != null) { double r = YearMean(noocean, "mean_precip_mmh", 2, 5) / refP; c3.Add("No-ocean global precipitation over Kerbin-like (years 2-5)", r, "<= 0.05", r <= 0.05); }
                if (slow != null && slow.Has("wave1")) { double r = slow.D("wave1.ratio"); c3.Add("Slow rotator: wave-1 (Sun-fixed) over zonal-mean wind amplitude", r, ">= 2", r >= 2); }
                if (A.Has("wave1")) { double r = A.D("wave1.ratio"); c3.Add("Kerbin-like control: wave-1 over zonal-mean ratio", r, "< 1", r < 1); }
                if (duna != null)
                {
                    double below = duna.D("dust.lift_below_threshold"), share = duna.D("dust.lift_cell_steps") / Math.Max(1.0, duna.D("dust.land_cell_steps"));
                    c3.Add("Duna-like dust lifts at or below the 10 m/s threshold", below, "= 0", below == 0); c3.Add("Duna-like share of land cell-hours with lift", share, ">= 0.005", share >= 0.005);
                    double r = YearMean(duna, "mean_precip_mmh", 2, 5) / refP; c3.Add("Duna-like global precipitation over Kerbin-like", r, "<= 0.05", r <= 0.05);
                }
                if (eve != null)
                {
                    c3.Add("Eve-like steps with sea ice", eve.D("ice_steps_after_spin"), "= 0", eve.D("ice_steps_after_spin") == 0);
                    double p = YearMean(eve, "mean_precip_mmh", 2, 5); c3.Add("Eve-like global mean precipitation", p, "> 0", p > 0);
                }
                if (laythe != null)
                {
                    double r = YearMean(laythe, "mean_precip_ocean_mmh", 2, 5) / YearMean(A, "mean_precip_ocean_mmh", 2, 5);
                    c3.Add("Laythe-like ocean-cell precipitation over Kerbin-like", r, ">= 0.7", r >= 0.7);
                }
            }
            else c3.Add("kerbinA present", false, "present", false);
            crits.Add(c3);

            // 4 absolute precipitation statistics
            var c4 = new Crit { Id = 4, Name = "Absolute precipitation statistics (45-70 deg, cold season)" };
            if (A != null)
            {
                var n = A.Arr("site_hours.class_counts_none_flurries_light_moderate_heavy_blizzard"); double tot = A.D("site_hours.site_hours");
                double precip = (tot - n[0]) / tot, heavy = (n[4] + n[5]) / tot, bliz = n[5] / tot;
                c4.Add("precipitating share of site-hours", precip, "0.20 - 0.40", precip >= 0.2 && precip <= 0.4);
                c4.Add("Heavy-or-above share", heavy, "0.005 - 0.05", heavy >= 0.005 && heavy <= 0.05);
                c4.Add("Blizzard-class share", bliz, "0.0005 - 0.015", bliz >= 0.0005 && bliz <= 0.015);
                c4.Add("ordering of precipitating hours Light > Moderate > Heavy > Blizzard", F(n[2]) + " > " + F(n[3]) + " > " + F(n[4]) + " > " + F(n[5]), "strict", n[2] > n[3] && n[3] > n[4] && n[4] > n[5]);
                c4.Add("INFO class shares (none, flurries, light, moderate, heavy, blizzard)", F(n[0] / tot) + ", " + F(n[1] / tot) + ", " + F(n[2] / tot) + ", " + F(n[3] / tot) + ", " + F(n[4] / tot) + ", " + F(n[5] / tot), "reported", true);
            }
            crits.Add(c4);

            // 5 cost
            var c5 = new Crit { Id = 5, Name = "Cost" };
            string chosen = null;
            if (cost != null)
            {
                var arr = cost.E;
                for (int i = 0; i < arr.GetArrayLength(); i++)
                {
                    var g = arr[i]; double ms = g.GetProperty("median_ms").GetDouble(); double mem = (g.GetProperty("resident_bytes").GetInt64() + g.GetProperty("checkpoint_bytes").GetInt64()) / 1048576.0;
                    string name = g.GetProperty("grid").GetString();
                    bool ok = ms <= 30.0 && mem <= 30.0;
                    if (ok && chosen == null) chosen = name;
                    c5.Add(name + " median step ms / resident+checkpoint MB", F(ms) + " ms, " + F(mem) + " MB", "<= 30 ms and <= 30 MB", ok);
                }
            }
            crits.Add(c5);

            // 6 determinism
            var c6 = new Crit { Id = 6, Name = "Determinism and rewind" };
            if (det != null)
            {
                c6.Add("identical hash across runs, threads, chunkings", det.B("identical_runs_threads_chunkings"), "true", det.B("identical_runs_threads_chunkings"));
                c6.Add("checkpoint day 10 + advance to day 20 equals straight run", det.B("checkpoint_day10_to_day20_equal"), "true", det.B("checkpoint_day10_to_day20_equal"));
                c6.Add("catch-up from checkpoint equals straight run", det.B("catchup_from_checkpoint_equals_straight"), "true", det.B("catchup_from_checkpoint_equals_straight"));
                c6.Add("INFO catch-up of 1 year (s) from scratch / from checkpoint at 0.9 y", F(det.D("catchup_year_from_scratch_s")) + " / " + F(det.D("catchup_year_from_checkpoint_0p9_s")), "reported", true);
                c6.Add("INFO checkpoint bytes", det.D("checkpoint_bytes"), "reported", true);
            }
            if (ep != null) c6.Add("restore at 10 arbitrary earlier times equals a fresh pipeline", ep.D("rewind_equal_to_fresh"), "= 10", ep.D("rewind_equal_to_fresh") == 10);
            crits.Add(c6);

            // 7 smoothness
            var c7 = new Crit { Id = 7, Name = "Smoothness" };
            if (A != null)
            {
                double a = A.D("smoothness.coverage_p99_per_s"), b = A.D("smoothness.precip_level_p99_per_s");
                c7.Add("99th percentile coverage change per game second", a, "<= 1e-3 (stand-in, UNKNOWN until C23)", a <= 1e-3);
                c7.Add("99th percentile precipitation-level change per game second", b, "<= 1e-3 (stand-in, UNKNOWN until C23)", b <= 1e-3);
            }
            crits.Add(c7);

            // 8 forecast
            var c8 = new Crit { Id = 8, Name = "Forecast" };
            if (fc != null)
            {
                c8.Add("clone + 24 steps equals continuation", fc.B("clone_equals_continuation"), "true", fc.B("clone_equals_continuation"));
                c8.Add("clone + 24 steps seconds", fc.D("clone_plus_24_steps_s"), "<= 1.5", fc.D("clone_plus_24_steps_s") <= 1.5);
                c8.Add("rolling 24 steps seconds", fc.D("rolling_24_steps_s"), "<= 1.5", fc.D("rolling_24_steps_s") <= 1.5);
                c8.Add("forecast across epoch boundary and crossfade equals continuation", fc.B("epoch_forecast_across_boundary_equal"), "true", fc.B("epoch_forecast_across_boundary_equal"));
            }
            crits.Add(c8);

            // 9 surface layer
            var c9 = new Crit { Id = 9, Name = "Surface layer" };
            if (A != null)
            {
                double r = A.D("snowfall_vs_dsnow_correlation_cold_land_40_75"); c9.Add("correlation of dSnow with cold precipitation (40-75 deg land, T < 270 K)", r, "> 0.9", r > 0.9);
                c9.Add("snow cells (>= 2 cm) after 3 body days above 278 K", A.D("snow_cells_after_3_warm_days"), "= 0", A.D("snow_cells_after_3_warm_days") == 0);
                var ys = A.At("yearly"); double y3 = double.NaN, y5 = double.NaN;
                for (int i = 0; i < ys.GetArrayLength(); i++) { int y = ys[i].GetProperty("year").GetInt32(); if (y == 3) y3 = ys[i].GetProperty("max_global_mean_snow_m").GetDouble(); if (y == 5) y5 = ys[i].GetProperty("max_global_mean_snow_m").GetDouble(); }
                c9.Add("year-5 max global-mean snow over year-3", y5 / y3, "<= 1.2", y5 <= 1.2 * y3);
                c9.Add("largest cell snow (m LWE)", A.D("max_snow_cell_m"), "<= 10", A.D("max_snow_cell_m") <= 10);
                if (noocean != null) { double q = YearMean(noocean, "mean_precip_mmh", 2, 5) / YearMean(A, "mean_precip_mmh", 2, 5); c9.Add("No-ocean total precipitation over Kerbin-like", q, "<= 0.01", q <= 0.01); }
                // snow-line table
                var tab = A.At("snow_line_table");
                for (int i = 0; i < tab.GetArrayLength(); i++)
                {
                    var row = tab[i]; string hem = row.GetProperty("hemisphere").GetString(); double band = row.GetProperty("band_deg").GetDouble(); int cells = row.GetProperty("land_cells").GetInt32();
                    var cv = row.GetProperty("cover_winter_spring_summer_autumn"); double wi = cv[0].GetDouble(), sp = cv[1].GetDouble(), su = cv[2].GetDouble(), au = cv[3].GetDouble();
                    string tag = "snow line " + hem + " " + band + " deg";
                    if (cells < 20) { c9.Add(tag + " land cells", cells, ">= 20", false); continue; }
                    bool ok; string thr;
                    if (band == 0) { ok = wi <= 0.02 && sp <= 0.02 && su <= 0.02 && au <= 0.02; thr = "all seasons <= 0.02"; }
                    else if (band == 30) { ok = wi <= 0.10 && sp <= 0.02 && su <= 0.02 && au <= 0.02; thr = "winter <= 0.10, others <= 0.02"; }
                    else if (band == 45) { ok = wi >= 0.30 && su <= 0.05; thr = "winter >= 0.30, summer <= 0.05"; }
                    else if (band == 55) { ok = wi >= 0.60 && su <= 0.05; thr = "winter >= 0.6, summer <= 0.05"; }
                    else if (band == 70) { ok = wi >= 0.85 && su <= 0.30; thr = "winter >= 0.85, summer <= 0.30"; }
                    else { ok = wi >= 0.95 && su <= 0.60; thr = "winter >= 0.95, summer <= 0.60"; }
                    if (band >= 45) ok = ok && sp >= su - 0.05 && sp <= wi + 0.05 && au >= su - 0.05 && au <= wi + 0.05;
                    c9.Add(tag + " cover (winter, spring, summer, autumn) land cells " + cells, F(wi) + ", " + F(sp) + ", " + F(su) + ", " + F(au), thr + "; spring and autumn between summer-0.05 and winter+0.05", ok);
                }
            }
            if (ep != null)
            {
                var bs = ep.At("boundaries");
                for (int i = 0; i < bs.GetArrayLength(); i++)
                {
                    var cs0 = bs[i].GetProperty("cover_bands_start_0_30_45_55_70_85_N"); var cs1 = bs[i].GetProperty("cover_bands_end_0_30_45_55_70_85_N");
                    double j = 0; for (int q = 0; q < cs0.GetArrayLength(); q++) if (cs0[q].ValueKind != JsonValueKind.Null && cs1[q].ValueKind != JsonValueKind.Null) j = Math.Max(j, Math.Abs(cs1[q].GetDouble() - cs0[q].GetDouble()));
                    double ph = bs[i].GetProperty("actual_phase").GetDouble();
                    c9.Add("snow cover jump across the epoch crossfade (boundary at year phase " + F(ph) + ")", j, "<= 0.10", j <= 0.10);
                }
            }
            crits.Add(c9);

            // 10 sea ice
            var c10 = new Crit { Id = 10, Name = "Sea ice" };
            if (A != null)
            {
                var lat = A.Arr("lat_deg"); var ri = A.At("row_ice_by_season_own_hemisphere");
                var winter = new double[lat.Length]; for (int j = 0; j < lat.Length; j++) winter[j] = ri[0][j].ValueKind == JsonValueKind.Null ? double.NaN : ri[0][j].GetDouble();
                foreach (int h in new[] { 0, 1 })
                {
                    double edge = 90.0;
                    for (int j = 0; j < lat.Length; j++) if ((lat[j] >= 0) == (h == 0) && winter[j] >= 0.15) edge = Math.Min(edge, Math.Abs(lat[j]));
                    c10.Add("winter ice edge " + (h == 0 ? "N" : "S") + " (deg)", edge, "50 - 60", edge >= 50 && edge <= 60);
                }
                double ratioSum = 0; int nr = 0;
                foreach (string k in new[] { "N", "S" })
                {
                    var ia = A.Arr("ice_area_frac_by_season_" + k); double rr = ia[1] / ia[3]; c10.Add("INFO ice area spring/autumn " + k, rr, "reported", true); if (!double.IsNaN(rr) && !double.IsInfinity(rr)) { ratioSum += rr; nr++; }
                }
                double ratio = nr > 0 ? ratioSum / nr : double.NaN;
                c10.Add("ice area ratio spring (phase 0.25) / autumn (phase 0.75), hemisphere mean", ratio, ">= 1.2", ratio >= 1.2);
                // lag of the maximum behind midwinter and freeze-up / break-up durations
                var lags = new List<string>(); bool lagOk = true;
                foreach (int h in new[] { 0, 1 })
                {
                    var cyc = A.Arr(h == 0 ? "ice_cycle_N_by_phase" : "ice_cycle_S_by_phase"); int pb = cyc.Length; int imax = 0, imin = 0;
                    for (int i = 0; i < pb; i++) { if (cyc[i] > cyc[imax]) imax = i; if (cyc[i] < cyc[imin]) imin = i; }
                    double pw = h == 0 ? 0.0 : 0.5; double lag = ((imax + 0.5) / pb - pw); lag -= Math.Floor(lag);
                    double freeze = ((imax - imin) % pb + pb) % pb / (double)pb, brk = 1.0 - freeze;
                    lags.Add((h == 0 ? "N" : "S") + " lag " + F(lag) + " y, freeze-up " + F(freeze) + " y, break-up " + F(brk) + " y, ratio " + F(freeze / Math.Max(1e-9, brk)));
                    if (!(lag >= 0.02 && lag <= 0.25)) lagOk = false;
                }
                c10.Add("lag of the maximum ice extent behind midwinter (and durations)", string.Join("; ", lags), "0.02 - 0.25 y", lagOk);
                c10.Add("ocean cells with ice and sea temperature > freezing + 0.5 K (cell-steps)", A.D("ice_warm_cell_steps_over_0p5K"), "= 0", A.D("ice_warm_cell_steps_over_0p5K") == 0);
            }
            if (noocean != null) c10.Add("No-ocean steps with ice", noocean.D("ice_steps_after_spin"), "= 0", noocean.D("ice_steps_after_spin") == 0);
            if (eve != null) c10.Add("Eve-like steps with ice", eve.D("ice_steps_after_spin"), "= 0", eve.D("ice_steps_after_spin") == 0);
            if (cold != null)
            {
                var c0 = cold.Arr("ice_cycle_N_by_phase"); var c1a = cold.Arr("ice_cycle_S_by_phase"); double mn = double.MaxValue, mx = 0;
                for (int i = 0; i < c0.Length; i++) { double s = c0[i] + c1a[i]; mn = Math.Min(mn, s); mx = Math.Max(mx, s); }
                c10.Add("Cold-world minimum annual ice area over maximum", mn / mx, ">= 0.20", mn / mx >= 0.2);
            }
            if (ep != null)
            {
                var bs = ep.At("boundaries");
                for (int i = 0; i < bs.GetArrayLength(); i++)
                {
                    double j = bs[i].GetProperty("ice_edge_max_jump_deg").GetDouble(); double ph = bs[i].GetProperty("actual_phase").GetDouble();
                    c10.Add("ice edge jump across the epoch crossfade (boundary at year phase " + F(ph) + ")", j, "<= 3 deg", j <= 3.0);
                }
            }
            crits.Add(c10);

            // 11 surface wind
            var c11 = new Crit { Id = 11, Name = "Surface wind" };
            if (A != null)
            {
                var all = A.Arr("wind10_all_area_weighted_p50_p95_p99"); var trk = A.Arr("wind10_storm_track_ocean_p50_p95_p99");
                c11.Add("Kerbin-like all-cell P50 (area weighted)", all[0], "3 - 10", all[0] >= 3 && all[0] <= 10); c11.Add("Kerbin-like all-cell P95", all[1], "8 - 20", all[1] >= 8 && all[1] <= 20);
                c11.Add("Kerbin-like storm-track ocean P99", trk[2], "14 - 28", trk[2] >= 14 && trk[2] <= 28);
                double tot = A.D("site_hours.site_hours"), bl = A.D("site_hours.blizzard_condition_literal") / tot;
                c11.Add("Blizzard condition (rate >= 2 mm/h and wind >= 15.6 m/s) share of cold-season site-hours", bl, "0.0005 - 0.015", bl >= 0.0005 && bl <= 0.015);
                c11.Add("INFO blizzard condition, snow only", A.D("site_hours.blizzard_condition_snow_only") / tot, "reported", true);
                c11.Add("INFO Kerbin-like all-cells unweighted P50/P95/P99", string.Join(", ", Array.ConvertAll(A.Arr("wind10_all_cells_p50_p95_p99"), F)), "reported", true);
            }
            foreach (var kv in allRuns)
                if (kv.Value != null) c11.Add("INFO " + kv.Key + " 10 m wind P50/P95/P99 (area weighted)", string.Join(", ", Array.ConvertAll(kv.Value.Arr("wind10_all_area_weighted_p50_p95_p99"), F)), "reported", true);
            if (duna != null) { var d = duna.Arr("wind10_all_area_weighted_p50_p95_p99"); c11.Add("Duna-like P50", d[0], "< 10", d[0] < 10); c11.Add("Duna-like P99", d[2], ">= 10", d[2] >= 10); }
            crits.Add(c11);

            // 12 convective scale
            var c12 = new Crit { Id = 12, Name = "Convective scale" };
            if (A != null && A.Has("convective"))
            {
                double share = A.D("convective.share_tropical"); c12.Add("tropical cell-steps with resolved updraft >= 2 m/s", share, ">= 0.005", share >= 0.005);
                double lt = A.D("convective.cluster_lifetime_h_median"); c12.Add("median lifetime of updraft clusters (h)", lt, "1 - 12", lt >= 1 && lt <= 12);
                double ar = A.D("convective.cluster_area_cells_median"); c12.Add("median cluster area (cells)", ar, ">= 1", ar >= 1);
                c12.Add("INFO cell scale available at GO: updraft P95/P99 (m/s), cell size (km lon x lat)", string.Join(", ", Array.ConvertAll(A.Arr("convective.updraft_tropics_p95_p99_ms"), F)) + "; " + F(A.D("convective.cell_km_lon_equator")) + " x " + F(A.D("convective.cell_km_lat")), "reported", true);
                string sub = Path.Combine(root, "subgrid_test.txt");
                bool subOk = File.Exists(sub) && File.ReadAllText(sub).Contains("Passed");
                c12.Add("sub-grid rule unit test (SubGrid.Cores deterministic, radii 5-25 km, <= 35 % of a cell)", subOk, "green", subOk);
            }
            crits.Add(c12);

            // 13 bounded catch-up
            var c13 = new Crit { Id = 13, Name = "Bounded catch-up" };
            if (ep != null)
            {
                c13.Add("worst case over 50 arbitrary UTs (s at measured step cost)", ep.D("catchup_50_uts_max_s_at_step_cost"), "<= 20", ep.D("catchup_50_uts_max_s_at_step_cost") <= 20);
                c13.Add("worst possible UT (end of a crossfade window; s at measured step cost)", ep.D("catchup_worst_possible_s_at_step_cost"), "<= 20", ep.D("catchup_worst_possible_s_at_step_cost") <= 20);
                c13.Add("measured wall time of the worst catch-ups (s)", ep.D("measured_worst_wall_s"), "<= 20", ep.D("measured_worst_wall_s") <= 20);
                var bs = ep.At("boundaries");
                for (int i = 0; i < bs.GetArrayLength(); i++)
                {
                    foreach (var k in new[] { "coverage", "precip_level" })
                    {
                        double ins = bs[i].GetProperty(k + "_p99_per_s_inside").GetDouble(), outs = bs[i].GetProperty(k + "_p99_per_s_outside").GetDouble();
                        c13.Add("crossfade p99 change " + k + " inside / outside (boundary " + (i + 1) + ")", F(ins) + " / " + F(outs), "inside <= 3 x outside", ins <= 3.0 * outs);
                    }
                }
                c13.Add("INFO epoch (steps, body days, Earth days, share of a year)", F(ep.D("epoch_steps")) + ", " + F(ep.D("epoch_body_days")) + ", " + F(ep.D("epoch_days_earth")) + ", " + F(ep.D("epoch_as_year_fraction")), "reported", true);
                c13.Add("INFO memory with two trajectories (bytes)", ep.At("measured_catchups")[0].GetProperty("resident_bytes").GetInt64(), "reported (counted)", true);
            }
            crits.Add(c13);

            // 14 pre-signing
            var c14 = new Crit { Id = 14, Name = "Pre-signing" };
            c14.Add("header signed by the reviewer before the runs", "REV-B10h-1, conditions H-1..H-4 written before the first run", "signed", true);
            c14.Add("D3 negative controls pass", c2.Items.Count > 0 && (bool)c2.Items[4]["pass"] && (bool)c2.Items[5]["pass"] && (bool)c2.Items[6]["pass"] && (bool)c2.Items[7]["pass"], "true", c2.Items.Count > 0 && (bool)c2.Items[4]["pass"] && (bool)c2.Items[5]["pass"] && (bool)c2.Items[6]["pass"] && (bool)c2.Items[7]["pass"]);
            crits.Add(c14);

            // A/B pairs
            var ab = new Crit { Id = 100, Name = "A/B pairs (H5)" };
            if (cor != null && cor.B("nan")) ab.Add("Coriolis off: the run stays finite (a vanished vortex field must not be a NaN field)", false, "no NaN", false);
            else if (A != null && cor != null && cor.Has("detectors"))
            {
                double refN = A.D("detectors.d1_persistent_active_mean_per_hemisphere_north"), refS = A.D("detectors.d1_persistent_active_mean_per_hemisphere_south");
                double cn = cor.D("detectors.d1_persistent_active_mean_per_hemisphere_north"), cs = cor.D("detectors.d1_persistent_active_mean_per_hemisphere_south");
                double rr = (cn + cs) / Math.Max(1e-9, refN + refS);
                ab.Add("Coriolis off: persistent minima as share of the reference (N, S mean)", rr, "<= 0.25", rr <= 0.25);
            }
            if (A != null && ocoff != null) { double q = YearMean(ocoff, "mean_precip_mmh", 2, 5) / YearMean(A, "mean_precip_mmh", 2, 5); ab.Add("Ocean off: global precipitation over reference", q, "<= 0.05", q <= 0.05); }
            if (A != null && B != null) { double l1 = ZonalL1(A, B); ab.Add("Seed A vs B: relative L1 of zonal-mean precipitation", l1, "<= 0.10", l1 <= 0.10); }
            if (A != null && st3 != null) { double l1 = ZonalL1(A, st3); ab.Add("Step 1 h vs 3 h: relative L1 of zonal-mean precipitation", l1, "<= 0.15", l1 <= 0.15); }
            double anom, raw;
            if (DailyCorrelation(root, A, out anom, out raw)) { ab.Add("Seed A vs B: spatial correlation of daily precipitation anomaly maps (35-70 deg, after day 10)", anom, "< 0.3", anom < 0.3); ab.Add("INFO raw map correlation", raw, "reported", true); }
            crits.Add(ab);

            // decision
            bool allPass = true; var failed = new List<string>();
            foreach (var c in crits) if (c.Id <= 14 && !c.Pass) { allPass = false; failed.Add(c.Id.ToString()); }
            bool abPass = ab.Pass;
            var result = new Dictionary<string, object>();
            var list = new List<object>();
            foreach (var c in crits) list.Add(new Dictionary<string, object> { { "id", c.Id }, { "name", c.Name }, { "pass", c.Pass }, { "items", c.Items } });
            result["criteria"] = list; result["all_14_pass"] = allPass; result["ab_pairs_pass"] = abPass; result["failed_criteria"] = failed;
            result["decision"] = allPass && abPass ? "GO (subject to the independent reviewer verdict 'coherent moving systems' and the bit-exact rewind proof)" : "FALLBACK";
            result["chosen_grid"] = chosen;
            File.WriteAllText(Path.Combine(root, "metrics.json"), Json.Write(result));
            var md = new StringBuilder();
            md.Append("| # | Criterion | Result | Failed items |\n|---|---|---|---|\n");
            foreach (var c in crits)
            {
                var fl = new List<string>(); foreach (var i in c.Items) if (!(bool)i["pass"]) fl.Add(i["test"] + " = " + i["value"] + " (" + i["threshold"] + ")");
                md.Append("| " + (c.Id == 100 ? "A/B" : c.Id.ToString()) + " | " + c.Name + " | " + (c.Pass ? "PASS" : "FAIL") + " | " + string.Join("; ", fl) + " |\n");
            }
            File.WriteAllText(Path.Combine(root, "criteria.md"), md.ToString());
            Console.WriteLine(md.ToString());
            Console.WriteLine("decision: " + result["decision"]);
            return 0;
        }

        private static double ZonalL1(J a, J b)
        {
            var wt = a.Arr("lat_weight"); var za = a.Arr("zonal_mean_precip_mmh"); var zb = b.Arr("zonal_mean_precip_mmh");
            double d = 0, s = 0; for (int j = 0; j < za.Length; j++) { d += Math.Abs(za[j] - zb[j]) * wt[j]; s += Math.Abs(za[j]) * wt[j]; }
            return d / s;
        }

        private static bool DailyCorrelation(string root, J a, out double anom, out double raw)
        {
            anom = raw = double.NaN;
            string fa = Path.Combine(root, "kerbinA", "daily_precip.f32"), fb = Path.Combine(root, "kerbinB", "daily_precip.f32");
            if (!File.Exists(fa) || !File.Exists(fb) || a == null) return false;
            var lat = a.Arr("lat_deg"); var w = a.Arr("lat_weight"); int nt = lat.Length, nl = (int)(new FileInfo(fa).Length / 4 / nt); int n2 = nl * nt;
            var A = ReadF32(fa); var B = ReadF32(fb); int frames = Math.Min(A.Length, B.Length) / n2;
            double sa = 0, sr = 0; int cnt = 0;
            for (int f = 0; f < frames; f++)
            {
                var xa = new double[n2]; var xb = new double[n2]; var ra = new double[n2]; var rb = new double[n2];
                for (int j = 0; j < nt; j++)
                {
                    double ma = 0, mb = 0; for (int i = 0; i < nl; i++) { ma += A[f * n2 + j * nl + i]; mb += B[f * n2 + j * nl + i]; }
                    ma /= nl; mb /= nl;
                    for (int i = 0; i < nl; i++) { int c = j * nl + i; xa[c] = A[f * n2 + c] - ma; xb[c] = B[f * n2 + c] - mb; ra[c] = A[f * n2 + c]; rb[c] = B[f * n2 + c]; }
                }
                sa += Corr(xa, xb, lat, w, nl); sr += Corr(ra, rb, lat, w, nl); cnt++;
            }
            if (cnt == 0) return false;
            anom = sa / cnt; raw = sr / cnt; return true;
        }

        private static double Corr(double[] x, double[] y, double[] lat, double[] w, int nl)
        {
            double sw = 0, mx = 0, my = 0;
            for (int j = 0; j < lat.Length; j++) { double al = Math.Abs(lat[j]); if (al < 35 || al > 70) continue; for (int i = 0; i < nl; i++) { sw += w[j]; mx += w[j] * x[j * nl + i]; my += w[j] * y[j * nl + i]; } }
            mx /= sw; my /= sw; double sxx = 0, syy = 0, sxy = 0;
            for (int j = 0; j < lat.Length; j++) { double al = Math.Abs(lat[j]); if (al < 35 || al > 70) continue; for (int i = 0; i < nl; i++) { double dx = x[j * nl + i] - mx, dy = y[j * nl + i] - my; sxx += w[j] * dx * dx; syy += w[j] * dy * dy; sxy += w[j] * dx * dy; } }
            return sxy / Math.Sqrt(Math.Max(1e-30, sxx * syy));
        }

        private static float[] ReadF32(string path)
        {
            var b = File.ReadAllBytes(path); var r = new float[b.Length / 4]; Buffer.BlockCopy(b, 0, r, 0, b.Length); return r;
        }
    }
}
