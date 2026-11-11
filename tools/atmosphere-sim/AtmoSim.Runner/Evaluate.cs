using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace AtmoSim.Runner
{
    // Merges the measured pieces of phase 2 into the PASS/FAIL table of the signed header (RESULT.md H5) and writes metrics.json and criteria.md.
    //   evaluate <root>: <root> holds cost.json and determinism.json and the crit<N>_<tag>.json pieces written by the study commands.
    // The thresholds live next to the measurements (Criteria*.cs); this class applies the decision rule of the header:
    //   * mode R (and mode-free items, mode "-") decide; REPORT items never decide;
    //   * the map-dependent GATE criteria 4, 10 and 11 must also pass in mode S seed A; a GATE item that fails in mode S seeds B or C is an OPEN RISK;
    //   * mode S GATE items of the other criteria are reported only.
    // A criterion without a piece is NOT RUN, never PASS.
    public static class Evaluate
    {
        private sealed class Item
        {
            public int Id; public string Test, Value, Threshold, Kind, Mode, Seed, Tag; public bool Pass;
            public string Verdict; // ok, FAIL, risk, info, outside, requirement-fail
        }

        private static readonly int[] GateIds = { 1, 2, 4, 6, 7, 8, 10, 11, 12 };
        private static readonly int[] MapDependent = { 4, 10, 11 };
        private static readonly string[] Names = { "", "Coherent systems", "Fronts", "Climate zones (report)", "Level shares", "Circulation and wind (report)", "Cost", "Determinism", "UT rewind and jump", "Smoothness (report)", "Surface layer", "Convective cells", "Pre-signing and causality" };

        private static string Str(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Number) return e.GetDouble().ToString("G5", CultureInfo.InvariantCulture);
            if (e.ValueKind == JsonValueKind.True) return "true"; if (e.ValueKind == JsonValueKind.False) return "false";
            if (e.ValueKind == JsonValueKind.Null) return "NaN";
            return e.GetString();
        }

        private static double Num(JsonElement root, string name) { var e = root.GetProperty(name); return e.ValueKind == JsonValueKind.Null ? double.NaN : e.GetDouble(); }

        private static void Add(List<Item> l, int id, string test, double v, string thr, bool pass, bool gate = true)
        {
            l.Add(new Item { Id = id, Test = test, Value = v.ToString("G5", CultureInfo.InvariantCulture), Threshold = thr, Kind = gate ? "GATE" : "REPORT", Mode = "-", Seed = "-", Pass = pass, Tag = "timing" });
        }

        public static int Run(string[] args)
        {
            string root = args[1];
            var items = new List<Item>();
            string costPath = Path.Combine(root, "cost.json"), detPath = Path.Combine(root, "determinism.json");
            if (File.Exists(costPath))
            {
                var c = JsonDocument.Parse(File.ReadAllText(costPath)).RootElement;
                Add(items, 6, "Sample median, map pattern (us)", Num(c, "sample_median_us_map_pattern"), "<= 3", Num(c, "sample_median_us_map_pattern") <= Thresholds.SampleMedianUs);
                Add(items, 6, "Sample median, new UT on every call (us, info)", Num(c, "sample_median_us_new_ut_each_call"), "reported", true, false);
                Add(items, 6, "Sample allocations over 40000 calls (bytes)", Num(c, "sample_allocated_bytes_40000_calls"), "0", Num(c, "sample_allocated_bytes_40000_calls") == 0);
                Add(items, 6, "512x256 map update (ms)", Num(c, "map_512x256_median_ms"), "<= 300", Num(c, "map_512x256_median_ms") <= Thresholds.MapMs);
                Add(items, 6, "32x32x24 window (ms)", Num(c, "window_32x32x24_median_ms"), "<= 5", Num(c, "window_32x32x24_median_ms") <= Thresholds.WindowMs);
                Add(items, 6, "24 hourly forecast steps of one site (ms)", Num(c, "forecast_24_steps_median_ms"), "<= 0.1", Num(c, "forecast_24_steps_median_ms") <= Thresholds.ForecastMs);
                Add(items, 10, "(g) one sea site, window cached (us)", Num(c, "surface_sea_site_median_us_warm_step"), "<= 20", Num(c, "surface_sea_site_median_us_warm_step") <= 20.0);
                Add(items, 10, "(g) one sea site with a new window step (us, reported)", Num(c, "surface_sea_site_median_us_with_new_step"), "reported", true, false);
                if (c.TryGetProperty("covermap_next_step_update_median_ms", out _))
                {
                    Add(items, 10, "(g) 256x128 cover map update, window cached (ms)", Num(c, "covermap_next_step_update_median_ms"), "<= 150", Num(c, "covermap_next_step_update_median_ms") <= 150.0);
                    Add(items, 10, "(g) 256x128 cover map: first update (ms, reported)", Num(c, "covermap_first_update_ms"), "reported", true, false);
                }
            }
            if (File.Exists(detPath))
            {
                var d = JsonDocument.Parse(File.ReadAllText(detPath)).RootElement;
                Func<string, bool> b = n => d.GetProperty(n).GetBoolean();
                Func<int, string, bool, string, Item> mk = (id, t, v, thr) => new Item { Id = id, Test = t, Value = v ? "true" : "false", Threshold = thr, Kind = "GATE", Mode = "-", Seed = "-", Pass = v, Tag = "determinism" };
                items.Add(mk(7, "identical hashes across runs, threads, evaluation order, instances", b("identical_runs_threads_order_instances"), "true"));
                items.Add(mk(7, "identical map hashes across chunkings", b("identical_chunkings"), "true"));
                items.Add(mk(7, "another seed differs", b("seed2_differs"), "true"));
                items.Add(mk(8, "UT - 2 days equals the direct evaluation bit for bit", b("rewind_back2d_equals_direct"), "true"));
                items.Add(mk(8, "jump forward and back returns the first result", b("rewind_return_equals_first"), "true"));
            }
            foreach (string f in Directory.GetFiles(root, "crit*_*.json").OrderBy(x => x))
            {
                var doc = JsonDocument.Parse(File.ReadAllText(f)).RootElement;
                int id = doc.GetProperty("id").GetInt32(); string tag = doc.GetProperty("tag").GetString();
                foreach (var it in doc.GetProperty("items").EnumerateArray())
                    items.Add(new Item
                    {
                        Id = id, Tag = tag, Test = it.GetProperty("test").GetString(), Value = Str(it.GetProperty("value")), Threshold = it.GetProperty("threshold").GetString(),
                        Pass = it.GetProperty("pass").GetBoolean(), Kind = it.GetProperty("kind").GetString(), Mode = it.GetProperty("mode").GetString(), Seed = it.GetProperty("seed").GetString()
                    });
            }

            // verdicts of the items
            var risks = new List<Item>();
            foreach (var it in items)
            {
                bool mapDep = MapDependent.Contains(it.Id);
                bool ne = it.Value == "NOT EVALUATED";
                if (ne) { it.Verdict = "info"; continue; }
                if (it.Kind != "GATE") { it.Verdict = it.Threshold.StartsWith("reported") && it.Pass ? "info" : (it.Pass ? "ok" : "outside"); continue; }
                if (it.Mode == "R" || it.Mode == "-") it.Verdict = it.Pass ? "ok" : "FAIL";
                else if (mapDep && it.Seed == "A") it.Verdict = it.Pass ? "ok" : "FAIL";
                else if (mapDep) { it.Verdict = it.Pass ? "ok" : "risk"; if (!it.Pass) risks.Add(it); }
                else it.Verdict = it.Pass ? "ok" : "info-S";
            }
            // criterion 10, bands that no data set can evaluate
            foreach (var g in items.Where(x => x.Id == 10 && x.Test.StartsWith("snow line at")).GroupBy(x => x.Test.Substring(0, x.Test.IndexOf(" (", StringComparison.Ordinal))))
                if (g.All(x => x.Value == "NOT EVALUATED"))
                    items.Add(new Item { Id = 10, Test = g.Key + ": no data set can evaluate the band", Value = "NOT EVALUATED", Threshold = "evaluable in at least one data set", Kind = "GATE", Mode = "R", Seed = "A", Pass = false, Verdict = "FAIL", Tag = "snow" });
            // mode R cannot evaluate a band: the synthetic seeds decide it (all must pass)
            foreach (var g in items.Where(x => x.Id == 10 && x.Test.StartsWith("snow line at")).GroupBy(x => x.Test.Substring(0, x.Test.IndexOf(" (", StringComparison.Ordinal))))
            {
                bool rNe = g.Any(x => x.Mode == "R" && x.Value == "NOT EVALUATED");
                if (!rNe) continue;
                foreach (var x in g.Where(y => y.Mode == "S" && y.Value != "NOT EVALUATED" && y.Kind == "GATE"))
                    if (!x.Pass) { x.Verdict = "FAIL"; }
                    else x.Verdict = "ok";
            }

            var sb = new StringBuilder();
            var list = new List<object>();
            var sumRows = new List<string>();
            bool anyFail = false, anyNotRun = false;
            sb.AppendLine("| # | criterion | result |"); sb.AppendLine("|---|---|---|");
            var body = new StringBuilder();
            for (int id = 1; id <= 12; id++)
            {
                var its = items.Where(x => x.Id == id).ToList();
                bool gateId = GateIds.Contains(id);
                string res;
                if (its.Count == 0) { res = "NOT RUN"; if (gateId) anyNotRun = true; }
                else if (!gateId) res = "REPORT (" + its.Count(x => x.Verdict == "outside") + " items outside the expectation)";
                else
                {
                    bool fail = its.Any(x => x.Verdict == "FAIL");
                    int nRisk = its.Count(x => x.Verdict == "risk");
                    res = fail ? "FAIL" : (nRisk > 0 ? "PASS with " + nRisk + " open risks" : "PASS");
                    if (fail) anyFail = true;
                }
                sb.AppendLine("| " + id + " | " + Names[id] + " | " + res + " |");
                body.AppendLine(); body.AppendLine("### " + id + " " + Names[id] + ": " + res); body.AppendLine();
                body.AppendLine("| item | value | threshold | kind | mode | seed | verdict |"); body.AppendLine("|---|---|---|---|---|---|---|");
                foreach (var x in its) body.AppendLine("| " + x.Test.Replace("|", "/") + " | " + x.Value.Replace("|", "/") + " | " + x.Threshold + " | " + x.Kind + " | " + x.Mode + " | " + x.Seed + " | " + x.Verdict + " |");
                list.Add(new Dictionary<string, object>
                {
                    { "id", id }, { "name", Names[id] }, { "result", res },
                    { "items", its.Select(x => (object)new Dictionary<string, object> { { "test", x.Test }, { "value", x.Value }, { "threshold", x.Threshold }, { "kind", x.Kind }, { "mode", x.Mode }, { "seed", x.Seed }, { "pass", x.Pass }, { "verdict", x.Verdict }, { "piece", x.Tag } }).ToList() }
                });
            }
            string decision = anyNotRun ? "INCOMPLETE (a GATE criterion has not run)" : anyFail ? "REWORK" : (risks.Count > 0 ? "GO with open risks" : "GO");
            sb.AppendLine(); sb.AppendLine("Decision: " + decision);
            if (risks.Count > 0) { sb.AppendLine("Open risks (map-dependent GATE items that fail in a synthetic set):"); foreach (var r in risks) sb.AppendLine("* criterion " + r.Id + ", mode " + r.Mode + ", seed " + r.Seed + ": " + r.Test + " = " + r.Value + " (" + r.Threshold + ")"); }
            sb.Append(body);
            File.WriteAllText(Path.Combine(root, "criteria.md"), sb.ToString());
            File.WriteAllText(Path.Combine(root, "metrics.json"), Json.Write(new Dictionary<string, object> { { "decision", decision }, { "criteria", list } }));
            Console.Write(sb.ToString());
            return decision == "GO" || decision == "GO with open risks" ? 0 : 1;
        }
    }

    // The numeric thresholds of the signed header that code needs (the rest live in the study commands next to the measurement).
    public static class Thresholds
    {
        public const double SampleMedianUs = 3.0;
        public const double MapMs = 300.0;
        public const double WindowMs = 5.0;
        public const double ForecastMs = 0.1;
    }
}
