using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ReDefinition.Tests
{
    // The layering of src/: which area may use which other, as the usings between areas say. An area is a
    // folder of src (the files directly in src are "(root)"); its namespace is ReDefinition.<Area>. The table
    // below is the current truth, read from the code. A using that is not in it fails the test, and so does
    // a table entry the code no longer needs. So the table only ever shrinks or grows by a decision made here.
    //
    // Core uses nothing; Proxy and Scaling use only Core. Settings, Vendor and Ui sit just above, and Window,
    // Modules, Upscaler and Raytracing form the large middle, which use each other in places. Hud, Engineer,
    // Menu, Scenery, Dashboard, Diagnostics and Probe are the features on top. The edges that run against that
    // order (Modules naming Hud, Dashboard, Scenery and Probe; Raytracing, Upscaler and Menu naming Probe for
    // its markers and switches; Ui and Settings naming Upscaler and Raytracing) are the ones to remove first.
    [TestClass]
    public class ArchitectureTests
    {
        // Each area and the areas it uses, space-separated.
        private static readonly Dictionary<string, string> Allowed = new Dictionary<string, string>
        {
            ["(root)"] = "Api Core Probe Proxy Raytracing Settings Ui Upscaler Window",
            ["Api"] = "Core Proxy Settings Ui Upscaler Window",
            ["Core"] = "",
            ["Dashboard"] = "Core Engineer Modules",
            ["Diagnostics"] = "Api Core Modules Proxy Raytracing Settings Ui Upscaler Window",
            ["Engineer"] = "Autopilot Core Hud Ui Window",
            ["Fsr3"] = "Upscaler",
            ["Hud"] = "Core Engineer Modules Ui Window",
            ["Menu"] = "Core Modules Probe Upscaler",
            ["Migration"] = "",
            ["Autopilot"] = "Orbits",
            ["Orbits"] = "",
            ["Modules"] = "Core Dashboard Diagnostics Hud Migration Probe Proxy Raytracing Scenery Settings Upscaler Vendor Window",
            ["Probe"] = "Autopilot Core Engineer Modules Orbits Proxy Raytracing Scenery Settings Ui Window",
            ["Proxy"] = "Core",
            ["Raytracing"] = "Api Core Modules Probe Proxy Scenery Upscaler",
            ["Scaling"] = "Core",
            ["Scenery"] = "Core Raytracing Upscaler",
            ["Settings"] = "Core Migration Proxy Raytracing Vendor",
            ["Ui"] = "Core Scaling Upscaler Window",
            ["Upscaler"] = "Api Core Probe Proxy Raytracing Settings",
            ["Vendor"] = "Core Modules Proxy Ui Window",
            ["Window"] = "Core Hud Migration Modules Proxy Settings Ui Vendor",
        };

        // Every using of another area's namespace in a file of an area is an edge the table allows.
        [TestMethod]
        public void EveryDependencyBetweenAreasIsInTheTable()
        {
            var unlisted = new List<string>();
            foreach (var edge in Edges.Value)
            {
                string allowed;
                if (!Allowed.TryGetValue(edge.Key.Item1, out allowed) || !allowed.Split(' ').Contains(edge.Key.Item2))
                    unlisted.Add(edge.Key.Item1 + " uses " + edge.Key.Item2 + " (" + edge.Value + ")");
            }
            Assert.AreEqual(0, unlisted.Count, "Not in the table of ArchitectureTests:\n" + string.Join("\n", unlisted));
        }

        // The table lists only edges that exist, so that removing a dependency is kept.
        [TestMethod]
        public void NoEdgeInTheTableIsGone()
        {
            var edges = Edges.Value;
            var gone = new List<string>();
            foreach (var area in Allowed)
                foreach (string used in area.Value.Split(' ').Where(u => u.Length > 0))
                    if (!edges.ContainsKey(System.Tuple.Create(area.Key, used))) gone.Add(area.Key + " no longer uses " + used);
            Assert.AreEqual(0, gone.Count, "Remove from the table of ArchitectureTests:\n" + string.Join("\n", gone));
        }

        // The edges found: (area, used area) and one file that makes it. Read once, for both tests: it reads every file of src.
        private static readonly Lazy<Dictionary<System.Tuple<string, string>, string>> Edges =
            new Lazy<Dictionary<System.Tuple<string, string>, string>>(ReadEdges);

        // Reads the edges from the code.
        private static Dictionary<System.Tuple<string, string>, string> ReadEdges()
        {
            string src = Path.Combine(RepoRoot.Root("src", "ReDefinition.csproj"), "src");
            var result = new Dictionary<System.Tuple<string, string>, string>();
            var usingOfOurs = new Regex(@"^using ReDefinition\.(\w+)", RegexOptions.Multiline);
            var qualifiedUse = new Regex(@"(?<![\w.])ReDefinition\.(\w+)\.[A-Z]");
            // Only areas whose name is not also a common member name, so that a property called Settings is no use.
            var partlyQualified = new Regex(@"(?<![\w.])(Probe|Raytracing|Scenery|Hud|Engineer|Upscaler|Proxy|Vendor|Diagnostics|Modules|Dashboard|Menu|Scaling|Ui)\.[A-Z]");
            foreach (string file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
            {
                string relative = file.Substring(src.Length + 1).Replace('\\', '/');
                if (relative.StartsWith("obj/") || relative.StartsWith("bin/") || relative.StartsWith("DxgiProxy/")) continue;
                int slash = relative.IndexOf('/');
                string area = slash < 0 ? "(root)" : relative.Substring(0, slash);
                string text = File.ReadAllText(file);
                foreach (Match m in usingOfOurs.Matches(text))
                    AddEdge(result, area, m.Groups[1].Value, relative);
                // Names qualified with the area's namespace (ReDefinition.Probe.GpuProfiler) or with its last part
                // (Probe.GpuProfiler), outside comments and strings, are uses as much as a using is.
                string code = Regex.Replace(Regex.Replace(text, "\"(\\\\.|[^\"\\\\])*\"", "\"\""), "//[^\n]*", "");
                foreach (Match m in qualifiedUse.Matches(code))
                    AddEdge(result, area, m.Groups[1].Value, relative);
                foreach (Match m in partlyQualified.Matches(code))
                    AddEdge(result, area, m.Groups[1].Value, relative);
            }
            return result;
        }

        // Records that the area uses another, with the first file that shows it; an area's own name is no edge.
        private static void AddEdge(Dictionary<System.Tuple<string, string>, string> edges, string area, string used, string file)
        {
            var key = System.Tuple.Create(area, used);
            if (used != area && !edges.ContainsKey(key)) edges[key] = file;
        }
    }
}
