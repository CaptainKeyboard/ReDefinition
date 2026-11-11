using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.B11
{
    // Every setting switch of the Atmospheres settings table (an on/off or a choice) has a rendering in tools/benchmark_matrix.json
    // (the groups weather-1 to weather-3, live key atmo.<key>), so that the standing comparison measures it.
    [TestClass]
    public class AtmospheresSwitchesTests
    {
        // Settings that are not switches of a feature but states of the test harness itself.
        private static readonly HashSet<string> NotSwitches = new HashSet<string>(StringComparer.Ordinal);

        [TestMethod]
        public void Every_bool_and_choice_setting_has_a_rendering_in_the_benchmark_matrix()
        {
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            string json = File.ReadAllText(Path.Combine(root, "tools", "benchmark_matrix.json"));
            var covered = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(json, "\"id\": \"weather-(\\w+)\", \"name\""))
                covered.Add(m.Groups[1].Value);
            var gaps = new List<string>();
            int switches = 0;
            foreach (SettingDef def in AtmospheresSettings.Definitions)
            {
                if (def.Kind != SettingKind.Bool && def.Kind != SettingKind.Choice) continue;
                if (NotSwitches.Contains(def.Key)) continue;
                switches++;
                if (!covered.Contains(def.Key) || json.IndexOf("\"atmo." + def.Key + "\"", StringComparison.Ordinal) < 0) gaps.Add(def.Key);
            }
            Assert.IsTrue(switches >= 50, "the table holds " + switches + " switches");
            Assert.AreEqual(0, gaps.Count, "no rendering in tools/benchmark_matrix.json for: " + string.Join(", ", gaps));
        }
    }
}
