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
            ["(root)"] = "Api Core Hud Scaling Diagnostics Modules Probe Proxy Raytracing Settings Ui Upscaler Window",
            ["Api"] = "Core Proxy Settings Ui Upscaler Window",
            ["Common"] = "Core Ui",
            ["Core"] = "",
            ["Dashboard"] = "Core Ui",
            ["Hooks"] = "Autopilot Core Driver Engineer Planning PlannerUi",
            ["Diagnostics"] = "Api Core Modules Proxy Raytracing Settings Ui Upscaler Window",
            ["Engineer"] = "Autopilot Core",
            ["Planning"] = "Autopilot Core Engineer",
            ["PlannerUi"] = "Autopilot Core Driver Engineer Planning Ui",
            ["Driver"] = "Autopilot Core Engineer Planning PlannerUi",
            ["Fsr3"] = "Upscaler",
            ["Hud"] = "Core Modules Ui Window",
            ["PlannerHud"] = "Autopilot Core Driver PlannerUi Ui",
            ["Menu"] = "Core Modules Probe Upscaler",
            ["Migration"] = "",
            ["Autopilot"] = "Core Landing Orbits Planner",
            ["Landing"] = "",
            ["Orbits"] = "",
            ["Planner"] = "Core",
            ["Modules"] = "Core Diagnostics Hud Migration Probe Proxy Raytracing Scenery Settings Upscaler Vendor Window",
            ["Probe"] = "Autopilot Core Driver Engineer Modules Planning PlannerUi Orbits Proxy Raytracing Scenery Settings Ui Window",
            ["Proxy"] = "Core",
            ["Raytracing"] = "Api Core Modules Probe Proxy Scenery Upscaler",
            ["Scaling"] = "Core",
            ["Seasons"] = "Core",
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

        // The planner, the dashboard, the seasons and the DLL they share with ReDefinition build without ReDefinition, Harmony and ZTheme: the project
        // files name none of them.
        [TestMethod]
        public void ThePlannerTheDashboardTheSeasonsAndCommonProjectsNameNeitherTheMainModNorHarmonyNorZTheme()
        {
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            var found = new List<string>();
            foreach (string project in new[] { "Planner/ReDefinition.Planner.csproj", "Dashboard/ReDefinition.Dashboard.csproj", "Seasons/ReDefinition.Seasons.csproj", "Common/ReDefinition.Common.csproj" })
            {
                string text = File.ReadAllText(Path.Combine(root, "src", project));
                bool common = project.StartsWith("Common");
                if (Regex.IsMatch(text, @"ProjectReference[^>]*ReDefinition\.csproj")) found.Add(project + " references ReDefinition.csproj");
                if (!common && Regex.IsMatch(text, @"<Reference Include=""0Harmony""")) found.Add(project + " references Harmony");
                if (Regex.IsMatch(text, @"<(Reference|ProjectReference|PackageReference) Include=""[^""]*ZTheme")) found.Add(project + " references ZTheme");
            }
            Assert.AreEqual(0, found.Count, string.Join("\n", found));
        }

        // Harmony is named in the shared DLL only inside HarmonyHooks, whose Install checks that it is loaded.
        [TestMethod]
        public void HarmonyIsNamedOnlyInHarmonyHooks()
        {
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            var found = new List<string>();
            foreach (string area in new[] { "Planner", "Dashboard", "Seasons", "Common" })
                foreach (string file in Directory.GetFiles(Path.Combine(root, "src", area), "*.cs", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(file) == "HarmonyHooks.cs" || file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
                    if (Regex.IsMatch(File.ReadAllText(file), @"using HarmonyLib|HarmonyLib\.")) found.Add(file.Substring(root.Length));
                }
            Assert.AreEqual(0, found.Count, string.Join("\n", found));
        }

        // The player build of ReDefinition.dll names nothing of the planner. Only developer probes do, behind REDEFINITION_PLANNER_PROBES.
        [TestMethod]
        public void OnlyTheGuardedProbesNameThePlanner()
        {
            string src = Path.Combine(RepoRoot.Root("src", "ReDefinition.csproj"), "src");
            string sep = Path.DirectorySeparatorChar.ToString();
            var found = new List<string>();
            foreach (string file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
            {
                string relative = file.Substring(src.Length + 1);
                if (relative.StartsWith("Planner" + sep) || relative.StartsWith("Common" + sep)) continue;
                if (relative.Contains(sep + "obj" + sep) || relative.StartsWith("obj" + sep) || relative.Contains(sep + "bin" + sep)) continue;
                string text = File.ReadAllText(file);
                int first = text.IndexOf("ReDefinition.Planner", StringComparison.Ordinal);
                if (first < 0) continue;
                if (!relative.StartsWith("Probe" + sep)) { found.Add(relative + " names the planner and is not a probe"); continue; }
                int guard = text.IndexOf("#if REDEFINITION_PLANNER_PROBES", StringComparison.Ordinal);
                if (guard < 0 || guard > first) found.Add(relative + " names the planner before '#if REDEFINITION_PLANNER_PROBES'");
            }
            Assert.AreEqual(0, found.Count, string.Join("\n", found));
        }

        // The player build of ReDefinition.dll names nothing of the dashboard, a mod of its own, not even behind a define.
        [TestMethod]
        public void NothingOfTheMainModNamesTheDashboardAssembly()
        {
            string src = Path.Combine(RepoRoot.Root("src", "ReDefinition.csproj"), "src");
            string sep = Path.DirectorySeparatorChar.ToString();
            var found = new List<string>();
            foreach (string file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
            {
                string relative = file.Substring(src.Length + 1);
                if (relative.StartsWith("Dashboard" + sep) || relative.StartsWith("Common" + sep) || relative.StartsWith("Planner" + sep)) continue;
                if (relative.Contains(sep + "obj" + sep) || relative.StartsWith("obj" + sep) || relative.Contains(sep + "bin" + sep)) continue;
                if (File.ReadAllText(file).Contains("ReDefinition.Dashboard")) found.Add(relative + " names the dashboard");
            }
            Assert.AreEqual(0, found.Count, string.Join("\n", found));
        }

        // The player build of ReDefinition.dll names nothing of the seasons, a mod of its own.
        [TestMethod]
        public void NothingOfTheMainModNamesTheSeasonsAssembly()
        {
            string src = Path.Combine(RepoRoot.Root("src", "ReDefinition.csproj"), "src");
            string sep = Path.DirectorySeparatorChar.ToString();
            var found = new List<string>();
            foreach (string file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
            {
                string relative = file.Substring(src.Length + 1);
                if (relative.StartsWith("Seasons" + sep) || relative.StartsWith("Dashboard" + sep) || relative.StartsWith("Common" + sep) || relative.StartsWith("Planner" + sep)) continue;
                if (relative.Contains(sep + "obj" + sep) || relative.StartsWith("obj" + sep) || relative.Contains(sep + "bin" + sep)) continue;
                if (File.ReadAllText(file).Contains("ReDefinition.Seasons")) found.Add(relative + " names the seasons");
            }
            Assert.AreEqual(0, found.Count, string.Join("\n", found));
        }

        // The seasons are a soft neighbour: ReDefinition does not declare them as a KSP assembly dependency, and the main project builds
        // them first without referencing their assembly.
        [TestMethod]
        public void TheMainModDoesNotDeclareOrReferenceTheSeasons()
        {
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            string info = File.ReadAllText(Path.Combine(root, "src", "KspAssemblyInfo.cs"));
            Assert.IsFalse(Regex.IsMatch(info, @"KSPAssemblyDependency\(\s*""ReDefinition\.Seasons"""), "src/KspAssemblyInfo.cs depends on the seasons.");
            string project = File.ReadAllText(Path.Combine(root, "src", "ReDefinition.csproj"));
            MatchCollection references = Regex.Matches(project, @"<ProjectReference\b[^>]*ReDefinition\.Seasons\.csproj[^>]*>");
            Assert.IsTrue(references.Count > 0, "The main project no longer builds the seasons before it.");
            foreach (Match reference in references)
                Assert.IsTrue(reference.Value.Contains("ReferenceOutputAssembly=\"false\""), "A seasons reference that is not ReferenceOutputAssembly=false: " + reference.Value);
            Assert.IsTrue(Regex.IsMatch(project, @"<Compile Remove=""[^""]*Seasons\\\*\*"), "The main project still compiles the seasons' files.");
        }

        // The seasons' side (their folder and Common) uses nothing of ReDefinition beyond Core, whatever the table allows.
        [TestMethod]
        public void TheSeasonsSideUsesNothingOfReDefinitionBeyondCore()
        {
            var side = new HashSet<string> { "Seasons", "Common", "Core" };
            var found = Edges.Value.Where(e => e.Key.Item1 == "Seasons" && !side.Contains(e.Key.Item2))
                .Select(e => e.Key.Item1 + " uses " + e.Key.Item2 + " (" + e.Value + ")").ToList();
            Assert.AreEqual(0, found.Count, "The seasons may not use these:\n" + string.Join("\n", found));
        }

        // The dashboard is a soft neighbour too: ReDefinition does not declare it as a KSP assembly dependency, and the main project
        // builds it first without referencing its assembly.
        [TestMethod]
        public void TheMainModDoesNotDeclareOrReferenceTheDashboard()
        {
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            string info = File.ReadAllText(Path.Combine(root, "src", "KspAssemblyInfo.cs"));
            Assert.IsFalse(Regex.IsMatch(info, @"KSPAssemblyDependency\(\s*""ReDefinition\.Dashboard"""), "src/KspAssemblyInfo.cs depends on the dashboard.");
            string project = File.ReadAllText(Path.Combine(root, "src", "ReDefinition.csproj"));
            MatchCollection references = Regex.Matches(project, @"<ProjectReference\b[^>]*ReDefinition\.Dashboard\.csproj[^>]*>");
            Assert.IsTrue(references.Count > 0, "The main project no longer builds the dashboard before it.");
            foreach (Match reference in references)
                Assert.IsTrue(reference.Value.Contains("ReferenceOutputAssembly=\"false\""), "A dashboard reference that is not ReferenceOutputAssembly=false: " + reference.Value);
            Assert.IsTrue(Regex.IsMatch(project, @"<Compile Remove=""[^""]*Dashboard\\\*\*"), "The main project still compiles the dashboard's files.");
        }

        // The dashboard's side (its folder and Common) uses nothing of ReDefinition beyond Core and Ui, whatever the table allows.
        [TestMethod]
        public void TheDashboardSideUsesNothingOfReDefinitionBeyondCoreAndUi()
        {
            var side = new HashSet<string> { "Dashboard", "Common", "Core", "Ui" };
            var found = Edges.Value.Where(e => e.Key.Item1 == "Dashboard" && !side.Contains(e.Key.Item2))
                .Select(e => e.Key.Item1 + " uses " + e.Key.Item2 + " (" + e.Value + ")").ToList();
            Assert.AreEqual(0, found.Count, "The dashboard may not use these:\n" + string.Join("\n", found));
        }

        // The planner is a soft neighbour: ReDefinition does not declare it as a KSP assembly dependency.
        [TestMethod]
        public void TheMainModDoesNotDeclareTheAssemblyDependencyOnThePlanner()
        {
            string text = File.ReadAllText(Path.Combine(RepoRoot.Root("src", "ReDefinition.csproj"), "src", "KspAssemblyInfo.cs"));
            Assert.IsFalse(Regex.IsMatch(text, @"KSPAssemblyDependency\(\s*""ReDefinition\.Planner"""), "src/KspAssemblyInfo.cs depends on the planner.");
        }

        // Every reference to the planner is either the one of the probe build (PlannerProbes) or one that does not reference its assembly.
        [TestMethod]
        public void TheMainProjectReferencesThePlannerOnlyForTheProbes()
        {
            string text = File.ReadAllText(Path.Combine(RepoRoot.Root("src", "ReDefinition.csproj"), "src", "ReDefinition.csproj"));
            MatchCollection references = Regex.Matches(text, @"<ProjectReference\b[^>]*ReDefinition\.Planner\.csproj[^>]*>");
            Assert.IsTrue(references.Count > 0, "The main project no longer builds the planner before it.");
            foreach (Match reference in references)
            {
                bool probes = Regex.IsMatch(reference.Value, @"Condition=""\s*'\$\(PlannerProbes\)'\s*==\s*'true'\s*""");
                bool notReferenced = reference.Value.Contains("ReferenceOutputAssembly=\"false\"");
                Assert.IsTrue(probes || notReferenced, "A planner reference that is neither conditioned on PlannerProbes nor ReferenceOutputAssembly=false: " + reference.Value);
            }
        }

        // The route's code does not know the game's probes: what a probe changes is a switch in RouteTuning, and the command line is read there alone.
        [TestMethod]
        public void TheRouteCodeKnowsNeitherProbesNorTheCommandLine()
        {
            string src = Path.Combine(RepoRoot.Root("src", "ReDefinition.csproj"), "src");
            var found = new List<string>();
            var probeName = new Regex(@"\bProbe[A-Z]\w*|ReDefinition\.Probe\b|\bCommandLine\b");
            // Every folder of the planner, and the files directly in it.
            var files = new List<string>(Directory.GetFiles(Path.Combine(src, "Planner"), "*.cs", SearchOption.TopDirectoryOnly));
            foreach (string area in new[] { "Autopilot", "Engineer", "Planning", "Ui", "Hud", "Landing", "Orbits" })
                files.AddRange(Directory.GetFiles(Path.Combine(src, "Planner", area), "*.cs", SearchOption.AllDirectories));
            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                if (name == "RouteTuning.cs" || name == "PlannerTiming.cs") continue;
                string text = File.ReadAllText(file);
                string code = Regex.Replace(Regex.Replace(text, "\"(\\\\.|[^\"\\\\])*\"", "\"\""), "//[^\n]*", "");
                foreach (Match m in probeName.Matches(code)) found.Add(file.Substring(src.Length + 1).Replace('\\', '/') + ": " + m.Value);
            }
            Assert.AreEqual(0, found.Count, "Move to RouteTuning:\n" + string.Join("\n", found));
        }

        // The vessel and stage maths (ReDefinition.Planner.Engineer) knows nothing of goals, routes, the autopilot's driver or the windows:
        // it uses no using of Planning, Ui or any Autopilot namespace except Autopilot.Background, whose interfaces it implements.
        [TestMethod]
        public void TheEngineerMathsUsesNothingOfPlanningTheDriverOrTheWindows()
        {
            string folder = Path.Combine(RepoRoot.Root("src", "ReDefinition.csproj"), "src", "Planner", "Engineer");
            var forbidden = new Regex(@"ReDefinition\.Planner\.(Planning|Ui)\b|ReDefinition\.Planner\.Autopilot(?!\.Background\b)\b(\.\w+)?");
            var found = new List<string>();
            foreach (string file in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories))
            {
                string code = Regex.Replace(File.ReadAllText(file), "//[^\n]*", "");
                foreach (Match m in forbidden.Matches(code)) found.Add(Path.GetFileName(file) + ": " + m.Value);
            }
            Assert.AreEqual(0, found.Count, "Engineer may not use these:\n" + string.Join("\n", found));
        }

        // The planner's side (its folders and Common) uses nothing of ReDefinition beyond Core and Ui, whatever the table allows.
        // The planner works without ReDefinition, so a using of Window, Modules or Settings in it is a defect even if the table were widened.
        [TestMethod]
        public void ThePlannerSideUsesNothingOfReDefinitionBeyondCoreAndUi()
        {
            var plannerSide = new HashSet<string> { "Planner", "Autopilot", "Engineer", "Planning", "PlannerUi", "Driver", "Landing", "Orbits", "PlannerHud", "Hooks", "Common" };
            var fine = new HashSet<string>(plannerSide) { "Core", "Ui" };
            var found = Edges.Value.Where(e => plannerSide.Contains(e.Key.Item1) && !fine.Contains(e.Key.Item2))
                .Select(e => e.Key.Item1 + " uses " + e.Key.Item2 + " (" + e.Value + ")").ToList();
            Assert.AreEqual(0, found.Count, "The planner side may not use these:\n" + string.Join("\n", found));
        }

        // The edges found: (area, used area) and one file that makes it. Read once, for both tests: it reads every file of src.
        private static readonly Lazy<Dictionary<System.Tuple<string, string>, string>> Edges =
            new Lazy<Dictionary<System.Tuple<string, string>, string>>(ReadEdges);

        // Reads the edges from the code.
        private static Dictionary<System.Tuple<string, string>, string> ReadEdges()
        {
            string src = Path.Combine(RepoRoot.Root("src", "ReDefinition.csproj"), "src");
            var result = new Dictionary<System.Tuple<string, string>, string>();
            var usingOfOurs = new Regex(@"^using ReDefinition\.(?:Planner\.(?:Autopilot\.(?=Driver\b)|(?=Engineer|Planning|Autopilot|Orbits|Landing|Hud\b|Ui\b)))?(\w+)", RegexOptions.Multiline);
            var qualifiedUse = new Regex(@"(?<![\w.])ReDefinition\.(?:Planner\.(?:Autopilot\.(?=Driver\b)|(?=Engineer|Planning|Autopilot|Orbits|Landing|Hud\b|Ui\b)))?(?!Planner\.[A-Z])(\w+)\.[A-Z]");
            // Only areas whose name is not also a common member name, so that a property called Settings is no use.
            var partlyQualified = new Regex(@"(?<![\w.])(Probe|Raytracing|Scenery|Hud|Engineer|Upscaler|Proxy|Vendor|Diagnostics|Modules|Dashboard|Menu|Scaling|Ui)\.[A-Z]");
            foreach (string file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
            {
                string relative = file.Substring(src.Length + 1).Replace('\\', '/');
                if (relative.StartsWith("obj/") || relative.StartsWith("bin/") || relative.StartsWith("DxgiProxy/")) continue;
                // The planner's folders are areas of their own, as their namespaces are.
                bool inPlanner = relative.StartsWith("Planner/");
                if (inPlanner && relative.IndexOf('/', 8) > 0) relative = relative.Substring(8);
                int slash = relative.IndexOf('/');
                string area = slash < 0 ? "(root)" : relative.Substring(0, slash);
                // The planner's Hud (ReDefinition.Planner.Hud) is another area than ReDefinition's own (ReDefinition.Hud).
                if (inPlanner && area == "Hud") area = "PlannerHud";
                if (inPlanner && area == "Ui") area = "PlannerUi";
                // The autopilot's driver (ReDefinition.Planner.Autopilot.Driver) sits above the rest of the autopilot.
                if (relative.StartsWith("Autopilot/Driver/")) area = "Driver";
                string text = File.ReadAllText(file);
                foreach (Match m in usingOfOurs.Matches(text))
                    AddEdge(result, area, Named(m), relative);
                // Names qualified with the area's namespace (ReDefinition.Probe.GpuProfiler) or with its last part
                // (Probe.GpuProfiler), outside comments and strings, are uses as much as a using is.
                string code = Regex.Replace(Regex.Replace(text, "\"(\\\\.|[^\"\\\\])*\"", "\"\""), "//[^\n]*", "");
                foreach (Match m in qualifiedUse.Matches(code))
                    AddEdge(result, area, Named(m), relative);
                foreach (Match m in partlyQualified.Matches(code))
                    AddEdge(result, area, m.Groups[1].Value, relative);
            }
            return result;
        }

        // The area a using or a qualified name points to; the planner's Hud is told from ReDefinition's own by its namespace.
        private static string Named(Match match)
        {
            string area = match.Groups[1].Value;
            if (area == "Hud" && match.Value.Contains("Planner.Hud")) return "PlannerHud";
            return area == "Ui" && match.Value.Contains("Planner.Ui") ? "PlannerUi" : area;
        }

        // Records that the area uses another, with the first file that shows it; an area's own name is no edge.
        private static void AddEdge(Dictionary<System.Tuple<string, string>, string> edges, string area, string used, string file)
        {
            var key = System.Tuple.Create(area, used);
            if (used != area && !edges.ContainsKey(key)) edges[key] = file;
        }
    }
}
