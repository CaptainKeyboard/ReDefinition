using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.B09
{
    // The writer of the dumps, the row file of the series, and the schema of the dumps the K-pre run produced (grid sizes, finite
    // values, the body list).
    [TestClass]
    public class ReconDumpTests
    {
        private static readonly string[] RequiredBodies = { "Kerbin", "Duna", "Laythe", "Eve" };

        [TestMethod]
        public void TheWriterMakesJsonTheReaderParses()
        {
            var json = new ReconJson().BeginObject();
            json.Text("name", "a \"quoted\" line\nbreak").Number("whole", 3).Number("fraction", 0.25).Number("nan", double.NaN).Flag("yes", true);
            json.Numbers("values", new float[] { 1.04f, 2f, float.NaN }, 1);
            json.BeginArray("items").BeginObject().Text("k", "v").EndObject().BeginObject().Number("n", -2).EndObject().EndArray();
            json.EndObject();
            var parsed = (Dictionary<string, object>)MiniJson.Parse(json.ToString());
            Assert.AreEqual("a \"quoted\" line\nbreak", parsed["name"]);
            Assert.AreEqual(3.0, parsed["whole"]);
            Assert.AreEqual(0.25, parsed["fraction"]);
            Assert.IsNull(parsed["nan"]);
            Assert.AreEqual(true, parsed["yes"]);
            var values = (List<object>)parsed["values"];
            Assert.AreEqual(3, values.Count);
            Assert.AreEqual(1.0, (double)values[0], 1e-9);
            Assert.IsNull(values[2]);
            Assert.AreEqual(2, ((List<object>)parsed["items"]).Count);
        }

        [TestMethod]
        public void TheRowFileCoversEveryPremiseAndIsConsistent()
        {
            string root = RepoRoot.Root("tests", "series", "rows", "B09.json");
            var file = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(Path.Combine(root, "tests", "series", "rows", "B09.json")));
            Assert.AreEqual("B09", file["package"]);
            var rows = ((List<object>)file["rows"]).Cast<Dictionary<string, object>>().ToList();
            var premises = new HashSet<string>();
            foreach (Dictionary<string, object> row in rows)
            {
                string metric = (string)row["metric"];
                Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(metric, @"^(P\d+|Env)\."), metric);
                if (metric.StartsWith("P")) premises.Add(metric.Substring(0, metric.IndexOf('.')));
                Assert.AreEqual("K-pre", row["series"]);
                if (row.ContainsKey("start")) Assert.IsTrue(new[] { "run1", "run2", "run3", "run4", "run5" }.Contains((string)row["start"]), metric);
                if (row.ContainsKey("min") && row.ContainsKey("max")) Assert.IsTrue((double)row["min"] <= (double)row["max"], metric);
                Assert.IsTrue(row.ContainsKey("min") || row.ContainsKey("max") || row.ContainsKey("equals"), metric + " has no threshold");
                Assert.IsTrue(row.ContainsKey("note"), metric + " has no note");
            }
            for (int p = 1; p <= 12; p++) Assert.IsTrue(premises.Contains("P" + p), "no row for P" + p);
        }

        [TestMethod]
        public void TheDumpsOfTheRunValidate()
        {
            string root = RepoRoot.Root("tests", "series", "rows", "B09.json");
            string folder = Path.Combine(root, "tests", "data", "recon", "dumps");
            if (!Directory.Exists(folder)) Assert.Inconclusive("tests/data/recon/dumps does not exist: the K-pre series has not been run in this checkout");

            string bodiesFile = Path.Combine(folder, "run1-bodies.json");
            Assert.IsTrue(File.Exists(bodiesFile), "run1-bodies.json is missing");
            var bodies = (List<object>)((Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(bodiesFile)))["bodies"];
            var names = bodies.Cast<Dictionary<string, object>>().Select(b => (string)b["name"]).ToList();
            foreach (string required in RequiredBodies) Assert.IsTrue(names.Contains(required), "bodies.json lacks " + required);
            Assert.IsTrue(names.Count >= 15, "bodies.json lists only " + names.Count + " bodies");
            foreach (Dictionary<string, object> body in bodies.Cast<Dictionary<string, object>>())
            {
                Assert.IsTrue((double)body["radius_m"] > 0, (string)body["name"] + " radius");
            }

            string kerbinHeights = Path.Combine(folder, "run1-heights-Kerbin.json");
            Assert.IsTrue(File.Exists(kerbinHeights), "run1-heights-Kerbin.json is missing");
            foreach (string file in Directory.GetFiles(folder, "run1-heights-*.json"))
            {
                var grid = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(file));
                Assert.AreEqual((double)ReconMath.GridWidth, grid["width"], file);
                Assert.AreEqual((double)ReconMath.GridHeight, grid["height"], file);
                var heights = (List<object>)grid["heights_m"];
                Assert.AreEqual(ReconMath.GridWidth * ReconMath.GridHeight, heights.Count, file);
                Assert.IsFalse(heights.Any(h => h == null || double.IsNaN((double)h)), file + " has a non-finite height");
                Assert.AreEqual(heights.Count, ((string)grid["ocean_mask"]).Length, file);
                double fraction = (double)grid["ocean_fraction"];
                Assert.IsTrue(fraction >= 0 && fraction <= 1, file);
            }
            var kerbin = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(kerbinHeights));
            double ocean = (double)kerbin["ocean_fraction"];
            Assert.IsTrue(ocean >= 0.5 && ocean <= 0.8, "Kerbin ocean fraction " + ocean);
        }

        // R13: the dumps of run2 and later starts that are not grids are checked for their shape too: the body list, the environment (the
        // mods found, by their assemblies) and the temperature range of each body against the plan's plausible interval.
        [TestMethod]
        public void TheDumpsOfLaterStartsValidate()
        {
            string root = RepoRoot.Root("tests", "series", "rows", "B09.json");
            string folder = Path.Combine(root, "tests", "data", "recon", "dumps");
            if (!Directory.Exists(folder)) Assert.Inconclusive("tests/data/recon/dumps does not exist: the K-pre series has not been run in this checkout");
            string bodiesFile = Path.Combine(folder, "run2-bodies.json");
            string envFile = Path.Combine(folder, "run2-env.json");
            if (!File.Exists(bodiesFile) || !File.Exists(envFile)) Assert.Inconclusive("run2 dumps are not in this checkout");

            var bodies = (List<object>)((Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(bodiesFile)))["bodies"];
            foreach (Dictionary<string, object> body in bodies.Cast<Dictionary<string, object>>())
            {
                Assert.IsTrue((double)body["radius_m"] > 0, (string)body["name"] + " radius");
            }
            var env = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(envFile));
            var mods = (Dictionary<string, object>)env["mods"];
            Assert.IsTrue((bool)mods["EnvironmentalVisualEnhancements"] && (bool)mods["Deferred"], "run2 reports EVE or Deferred as absent: the assembly names are wrong");
            Assert.IsFalse((bool)mods["Scatterer"], "run2 is the start without Scatterer");

            // Plausible air temperatures at sea level and 10 km per body, from the values the measurement gave (Kerbin 205 .. 314 K, Duna
            // 177 .. 272, Laythe 205 .. 289, Eve 300 .. 429), widened by 30 K: a dump outside is a defect of the probe, not a climate.
            var limits = new Dictionary<string, double[]> { { "Kerbin", new[] { 175.0, 345.0 } }, { "Duna", new[] { 147.0, 302.0 } },
                                                             { "Laythe", new[] { 175.0, 320.0 } }, { "Eve", new[] { 270.0, 460.0 } } };
            foreach (KeyValuePair<string, double[]> limit in limits)
            {
                string file = Path.Combine(folder, "run2-temperature-" + limit.Key + ".json");
                if (!File.Exists(file)) continue;
                var t = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(file));
                var values = ((List<object>)t["temperature_k"]).Cast<double>().ToList();
                Assert.IsTrue(values.Min() >= limit.Value[0] && values.Max() <= limit.Value[1],
                              limit.Key + " temperatures " + values.Min() + " .. " + values.Max() + " K lie outside " + limit.Value[0] + " .. " + limit.Value[1]);
            }
        }

        // The temperature dumps are judged for what they are: air temperatures in kelvin that fall with altitude. A dump counts only if it
        // names its method (the files of run1 do not: they held the sun-based offset of CelestialBody.GetFullTemperature(Vector3d), -9.1e6 .. +9.1e6,
        // and were removed). The dumps of the latest start that wrote them are read; with none the test is inconclusive.
        [TestMethod]
        public void TheTemperatureDumpsAreAirTemperatures()
        {
            string root = RepoRoot.Root("tests", "series", "rows", "B09.json");
            string folder = Path.Combine(root, "tests", "data", "recon", "dumps");
            if (!Directory.Exists(folder)) Assert.Inconclusive("tests/data/recon/dumps does not exist: the K-pre series has not been run in this checkout");

            int checkedBodies = 0;
            foreach (string body in RequiredBodies)
            {
                string[] files = Directory.GetFiles(folder, "run*-temperature-" + body + ".json");
                if (files.Length == 0) continue;
                string file = files.OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).First();
                var t = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(file));
                Assert.IsTrue(t.ContainsKey("method") && ((string)t["method"]).Contains("GetAtmoThermalStats"),
                              file + " does not name the method that made it: it predates the fix of the probe and is not a temperature");
                int phases = ((List<object>)t["phase_ut"]).Count, longitudes = ((List<object>)t["longitude_deg"]).Count;
                var altitudes = ((List<object>)t["altitude_m"]).Cast<double>().ToList();
                var latitudes = ((List<object>)t["latitude_deg"]).Cast<double>().ToList();
                var values = (List<object>)t["temperature_k"];
                Assert.AreEqual(phases * longitudes * altitudes.Count * latitudes.Count, values.Count, file);
                Assert.IsFalse(values.Any(v => v == null), file + " has a non-finite temperature");
                Assert.IsTrue(values.All(v => (double)v > 0 && (double)v < 2000), file + " has an implausible temperature");

                int sea = altitudes.IndexOf(0.0), top = altitudes.IndexOf(10000.0), equator = latitudes.IndexOf(0.0);
                Assert.IsTrue(sea >= 0 && top >= 0 && equator >= 0, file + " lacks the sea level, 10 km or equator samples");
                double largestLapse = 0;
                for (int ph = 0; ph < phases; ph++)
                {
                    for (int lo = 0; lo < longitudes; lo++)
                    {
                        for (int la = 0; la < latitudes.Count; la++)
                        {
                            double atSea = (double)values[((ph * longitudes + lo) * altitudes.Count + sea) * latitudes.Count + la];
                            double atTop = (double)values[((ph * longitudes + lo) * altitudes.Count + top) * latitudes.Count + la];
                            Assert.IsTrue(atSea >= atTop - 0.5, file + ": the air is colder at sea level than at 10 km (phase " + ph + ", longitude " + lo + ", latitude " + latitudes[la] + ")");
                            largestLapse = Math.Max(largestLapse, atSea - atTop);
                        }
                    }
                }
                Assert.IsTrue(largestLapse > 1.0, file + ": altitude has no effect on the temperature (largest difference " + largestLapse + " K)");
                if (body == "Kerbin")
                {
                    Assert.IsTrue(largestLapse > 15.0, file + ": the Kerbin lapse between sea level and 10 km is only " + largestLapse + " K");
                    for (int ph = 0; ph < phases; ph++)
                    {
                        for (int lo = 0; lo < longitudes; lo++)
                        {
                            double atSea = (double)values[((ph * longitudes + lo) * altitudes.Count + sea) * latitudes.Count + equator];
                            Assert.IsTrue(atSea > 233 && atSea < 333, file + ": the equator at sea level reads " + atSea + " K (phase " + ph + ", longitude " + lo + ")");
                        }
                    }
                }
                checkedBodies++;
            }
            if (checkedBodies == 0) Assert.Inconclusive("no temperature dump exists: run1's were not temperatures and were removed; run2 writes them");
        }
    }
}
