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
                Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(metric, @"^P\d+\."), metric);
                premises.Add(metric.Substring(0, metric.IndexOf('.')));
                Assert.AreEqual("K-pre", row["series"]);
                if (row.ContainsKey("start")) Assert.IsTrue(row["start"].Equals("run1") || row["start"].Equals("run2"), metric);
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

            foreach (string body in RequiredBodies)
            {
                string file = Path.Combine(folder, "run1-temperature-" + body + ".json");
                Assert.IsTrue(File.Exists(file), file + " is missing");
                var t = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(file));
                int expected = ((List<object>)t["phase_ut"]).Count * ((List<object>)t["longitude_deg"]).Count * ((List<object>)t["altitude_m"]).Count
                               * ((List<object>)t["latitude_deg"]).Count;
                var values = (List<object>)t["temperature_k"];
                Assert.AreEqual(expected, values.Count, file);
                Assert.IsFalse(values.Any(v => v == null), file + " has a non-finite temperature");
                Assert.IsTrue(values.All(v => (double)v > 0 && (double)v < 2000), file + " has an implausible temperature");
            }
        }
    }
}
