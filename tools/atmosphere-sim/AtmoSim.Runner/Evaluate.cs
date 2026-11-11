using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AtmoSim.Runner
{
    // Applies the signed header thresholds (RESULT.md, section H4) to the raw numbers of the runs and writes metrics.json and
    // criteria.md. evaluate <root>: <root> holds cost.json and determinism.json (phase 1 tools) and, in phase 2, one folder per
    // run with stats.json written by the study commands. A criterion whose input file is missing is reported NOT RUN, never PASS.
    public static class Evaluate
    {
        private sealed class J
        {
            public readonly JsonElement E;
            public J(JsonElement e) { E = e; }
            public static J Load(string path) { return File.Exists(path) ? new J(JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone()) : null; }
            public double D(string name) { var e = E.GetProperty(name); return e.ValueKind == JsonValueKind.Null ? double.NaN : e.GetDouble(); }
            public bool B(string name) { return E.GetProperty(name).GetBoolean(); }
            public bool Has(string name) { JsonElement e; return E.TryGetProperty(name, out e); }
        }

        public sealed class Crit
        {
            public int Id; public string Name; public bool Ran;
            public List<Dictionary<string, object>> Items = new List<Dictionary<string, object>>();
            public bool Pass { get { if (!Ran) return false; foreach (var i in Items) if (!(bool)i["pass"]) return false; return Items.Count > 0; } }
            public void Add(string test, object value, string threshold, bool pass)
            {
                Ran = true;
                Items.Add(new Dictionary<string, object> { { "test", test }, { "value", value }, { "threshold", threshold }, { "pass", pass } });
            }
        }

        private static string F(double v) { return v.ToString("G5", CultureInfo.InvariantCulture); }

        public static int Run(string[] args)
        {
            string root = args[1];
            var crits = new List<Crit>();
            var cost = J.Load(Path.Combine(root, "cost.json")); var det = J.Load(Path.Combine(root, "determinism.json"));

            var c6 = new Crit { Id = 6, Name = "Cost" };
            if (cost != null)
            {
                c6.Add("Sample median, map pattern (us)", F(cost.D("sample_median_us_map_pattern")), "<= 3", cost.D("sample_median_us_map_pattern") <= Thresholds.SampleMedianUs);
                c6.Add("Sample median, new UT on every call (us, info)", F(cost.D("sample_median_us_new_ut_each_call")), "reported", true);
                c6.Add("Sample allocations over 40000 calls (bytes)", F(cost.D("sample_allocated_bytes_40000_calls")), "0", cost.D("sample_allocated_bytes_40000_calls") == 0);
                c6.Add("512x256 map update (ms)", F(cost.D("map_512x256_median_ms")), "<= 300", cost.D("map_512x256_median_ms") <= Thresholds.MapMs);
                c6.Add("32x32x24 window (ms)", F(cost.D("window_32x32x24_median_ms")), "<= 5", cost.D("window_32x32x24_median_ms") <= Thresholds.WindowMs);
                c6.Add("24 hourly forecast steps of one site (ms)", F(cost.D("forecast_24_steps_median_ms")), "<= 0.1", cost.D("forecast_24_steps_median_ms") <= Thresholds.ForecastMs);
            }
            crits.Add(c6);

            var c7 = new Crit { Id = 7, Name = "Determinism" };
            var c8 = new Crit { Id = 8, Name = "UT rewind and jump" };
            if (det != null)
            {
                c7.Add("identical hashes across runs, threads, evaluation order, instances", det.B("identical_runs_threads_order_instances"), "true", det.B("identical_runs_threads_order_instances"));
                c7.Add("identical map hashes across chunkings", det.B("identical_chunkings"), "true", det.B("identical_chunkings"));
                c7.Add("another seed differs", det.B("seed2_differs"), "true", det.B("seed2_differs"));
                c8.Add("UT - 2 days equals the direct evaluation bit for bit", det.B("rewind_back2d_equals_direct"), "true", det.B("rewind_back2d_equals_direct"));
                c8.Add("jump forward and back returns the first result", det.B("rewind_return_equals_first"), "true", det.B("rewind_return_equals_first"));
            }
            crits.Add(c7); crits.Add(c8);

            // Criteria 1-5, 9-12 are filled by the phase 2 study commands.
            foreach (int id in new[] { 1, 2, 3, 4, 5, 9, 10, 11, 12 })
                if (!crits.Exists(c => c.Id == id)) crits.Add(new Crit { Id = id, Name = "(phase 2)" });
            crits.Sort((x, y) => x.Id.CompareTo(y.Id));

            var sb = new StringBuilder();
            sb.AppendLine("| # | criterion | result |"); sb.AppendLine("|---|---|---|");
            var list = new List<object>();
            bool all = true;
            foreach (var c in crits)
            {
                string res = !c.Ran ? "NOT RUN" : (c.Pass ? "PASS" : "FAIL");
                if (res != "PASS") all = false;
                sb.AppendLine("| " + c.Id + " | " + c.Name + " | " + res + " |");
                foreach (var i in c.Items) sb.AppendLine("|  |   " + i["test"] + " | " + i["value"] + " (" + i["threshold"] + ") " + ((bool)i["pass"] ? "ok" : "FAIL") + " |");
                list.Add(new Dictionary<string, object> { { "id", c.Id }, { "name", c.Name }, { "result", res }, { "items", c.Items } });
            }
            File.WriteAllText(Path.Combine(root, "criteria.md"), sb.ToString());
            File.WriteAllText(Path.Combine(root, "metrics.json"), Json.Write(new Dictionary<string, object> { { "all_pass", all }, { "criteria", list } }));
            Console.Write(sb.ToString());
            return all ? 0 : 1;
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
