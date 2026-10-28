using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Migration;
using ReDefinition.Modules;
using Surface;

namespace ReDefinition.Tests
{
    // The surface of the build (tools/Surface/SurfaceBuilder.cs) against the one written down, and against the last release's.
    //
    // src/Migration/surface.txt is the surface as it stands. A change to a setting, a registration, a profile or the proxy's ini
    // changes it, and the commit holds the new file: the hook that asks what a commit changed in the connectors reads that
    // (tools/hooks). REDEFINITION_UPDATE_SURFACE=1 writes the file anew.
    //
    // src/Migration/surface.shipped.txt is the surface of the last release. What differs from it in ReDefinition's own settings
    // must be the doing of a step of Migrations.ToCome, run on a file that holds the last release's standards.
    [TestClass]
    public class SurfaceTests
    {
        private const string Folder = "src/Migration";

        private static SortedDictionary<string, string> Current()
        {
            string root = RepoRoot.Root("ReDefinition.sln");
            return SurfaceBuilder.Build(typeof(OwnSettings).Assembly, root);
        }

        // The surface as it is is the one in the file, which a commit changes with the change.
        [TestMethod]
        public void TheWrittenSurfaceIsTheSurfaceNow()
        {
            string root = RepoRoot.Root("ReDefinition.sln");
            string path = Path.Combine(root, Folder, "surface.txt");
            string now = SurfaceBuilder.Text(Current());
            if (Environment.GetEnvironmentVariable("REDEFINITION_UPDATE_SURFACE") == "1")
            {
                File.WriteAllText(path, now, new System.Text.UTF8Encoding(false));
                return;
            }
            string written = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : "";
            if (written == now) return;
            SortedDictionary<string, string> before = SurfaceBuilder.Parse(written), after = SurfaceBuilder.Parse(now);
            var differences = new List<string>();
            foreach (string key in before.Keys.Union(after.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                string a, b;
                before.TryGetValue(key, out a);
                after.TryGetValue(key, out b);
                if (a != b) differences.Add(key + ": " + (a ?? "(none)") + " -> " + (b ?? "(none)"));
            }
            Assert.Fail("The surface changed. Say what that does to a player's file (src/Migration/Migrations.cs), then write it anew with"
                        + " REDEFINITION_UPDATE_SURFACE=1. " + differences.Count + " lines differ:\n" + string.Join("\n", differences.Take(40)));
        }

        // A setting of a bundled mod that is gone from its registration leaves a kept value in the player's bundled.cfg: a step of
        // Migrations.BundledToCome must remove that key ("<mod>.<setting>").
        [TestMethod]
        public void TheBundledStepsExplainTheSettingsThatWentSinceTheLastRelease()
        {
            string shippedPath = Path.Combine(RepoRoot.Root("ReDefinition.sln"), Folder, "surface.shipped.txt");
            SortedDictionary<string, string> shipped = SurfaceBuilder.Parse(File.ReadAllText(shippedPath));
            SortedDictionary<string, string> now = Current();
            var removed = new HashSet<string>(Migrations.BundledToCome.OfType<RemovedSetting>().Select(step => step.Key));
            var unexplained = new List<string>();
            foreach (string key in shipped.Keys)
            {
                System.Text.RegularExpressions.Match setting = System.Text.RegularExpressions.Regex.Match(
                    key, @"^mod\.MOD_SETTINGS\[([^\]]+)\]\.SETTING\[([^\]]+)\]\.@exists$");
                if (!setting.Success || now.ContainsKey(key)) continue;
                string stored = setting.Groups[1].Value + "." + setting.Groups[2].Value;
                if (!removed.Contains(stored)) unexplained.Add(stored + " is gone, and no bundled step removes its kept value");
            }
            Assert.AreEqual(0, unexplained.Count, "Settings of bundled mods that went since the last release, and no step says what bundled.cfg does:\n"
                                                  + string.Join("\n", unexplained));
        }

        // A file that holds every standard of the last release, once migrated, holds the standards now. A key that is gone from the
        // build is gone from the file, and a standard that changed is carried by a step; one nothing explains fails here.
        [TestMethod]
        public void TheStepsExplainWhatChangedInTheSettingsSinceTheLastRelease()
        {
            string shippedPath = Path.Combine(RepoRoot.Root("ReDefinition.sln"), Folder, "surface.shipped.txt");
            Assert.IsTrue(File.Exists(shippedPath), "surface.shipped.txt, the surface of the last release, is missing");
            SortedDictionary<string, string> shipped = SurfaceBuilder.Parse(File.ReadAllText(shippedPath));
            SortedDictionary<string, string> now = Current();
            var file = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> line in shipped)
                if (line.Key.StartsWith("own.", StringComparison.Ordinal)) file[line.Key.Substring(4)] = line.Value;
            SettingsMigration.Run(file, Migrations.ToCome, SettingsMigration.Current);
            var unexplained = new List<string>();
            foreach (KeyValuePair<string, string> setting in file)
            {
                if (setting.Key == SettingsMigration.VersionKey || setting.Key == SettingsMigration.AppliedKey) continue;
                string standard;
                if (!now.TryGetValue("own." + setting.Key, out standard))
                    unexplained.Add(setting.Key + " is gone, and no step removes it");
                else if (standard != setting.Value)
                    unexplained.Add(setting.Key + ": the last release's standard went to '" + setting.Value + "' by the steps, but is '" + standard + "' now");
            }
            Assert.AreEqual(0, unexplained.Count, "What changed in the settings since the last release, and no step says what a player's file does:\n"
                                                  + string.Join("\n", unexplained));
        }
    }
}
