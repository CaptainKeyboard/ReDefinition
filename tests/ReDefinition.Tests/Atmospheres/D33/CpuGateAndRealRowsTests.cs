using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres.Audio;

namespace ReDefinition.Tests.Atmospheres.D33
{
    // The CPU gate that depends on the build, and the shape of the rows that read the game's own state.
    [TestClass]
    public class CpuGateAndRealRowsTests
    {
        private static string RepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "ReDefinition.sln"))) dir = Path.GetDirectoryName(dir);
            Assert.IsNotNull(dir, "repository root");
            return dir;
        }

        private static string Rows()
        {
            return File.ReadAllText(Path.Combine(RepoRoot(), "tests", "series", "rows", "D33.json"));
        }

        private static string Row(string json, string metric)
        {
            Match m = Regex.Match(json, "\\{[^{}]*\"metric\": \"" + Regex.Escape(metric) + "\"[^{}]*\\}");
            Assert.IsTrue(m.Success, "row " + metric);
            return m.Value;
        }

        [TestMethod]
        public void TheLimitFollowsTheBuildAndTheUsedShareCanFailInBoth()
        {
            Assert.AreEqual(1.0, CpuGate.LimitPct(true));
            Assert.AreEqual(5.0, CpuGate.LimitPct(false));
            // The number a Debug build gave offline (2.7 %): inside the Debug limit, outside the Release budget.
            Assert.IsTrue(CpuGate.BudgetUsed(2.7, false) <= 1.0);
            Assert.IsTrue(CpuGate.BudgetUsed(2.7, true) > 1.0);
            // Above the plan's fail line: fails in a Debug build too.
            Assert.IsTrue(CpuGate.BudgetUsed(5.5, false) > 1.0);
            Assert.IsTrue(CpuGate.BudgetUsed(0.37, true) <= 1.0);
            Assert.AreEqual(CpuGate.IsReleaseBuild, !Debugging());
        }

        private static bool Debugging()
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }

        [TestMethod]
        public void TheCpuRowGatesTheBudgetUsedAndOnlyInformsAboutTheRawPercent()
        {
            string json = Rows();
            Assert.IsTrue(Row(json, "D33.cpu_budget_used").Contains("\"max\": 1"), "gated at 1");
            Assert.IsFalse(Row(json, "D33.cpu_budget_used").Contains("\"required\": false"), "required");
            Assert.IsTrue(Row(json, "D33.cpu_pct_core").Contains("\"required\": false"), "the raw percent informs");
            Assert.IsTrue(Row(json, "D33.build_release").Contains("\"required\": false"));
            string probe = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Atmospheres", "Audio", "Runtime", "Probe", "D33Probe.cs"));
            StringAssert.Contains(probe, "CpuGate.BudgetUsed(");
            StringAssert.Contains(probe, "\"D33.build_release\"");
        }

        [TestMethod]
        public void TheRowsThatReadTheGamesOwnStateAreRequiredAndTheProbeDrivesTheRealChain()
        {
            string json = Rows();
            string[] required =
            {
                "D33.real_idle_reads_open", "D33.real_pause_read", "D33.real_pause_gate_paused", "D33.real_pause_silent_db",
                "D33.real_warp_landed", "D33.real_warp_read", "D33.real_warp_gate_warp", "D33.real_warp_silent_db",
                "D33.real_physwarp_gate_open", "D33.real_map_flag", "D33.real_iva_available", "D33.real_iva_cabin_flag",
                "D33.real_iva_delta_db", "D33.real_weather_air", "D33.real_volume_ambience_error", "D33.real_cell_pan_error_right",
                "D33.real_cell_pan_error_left", "D33.real_cell_found_right", "D33.rse_state"
            };
            foreach (string metric in required) Assert.IsFalse(Row(json, metric).Contains("\"required\": false"), metric + " is required");

            string probe = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Atmospheres", "Audio", "Runtime", "Probe", "D33Probe.cs"));
            // The real section lets the module read the game: the overrides it would force are null there.
            int start = probe.IndexOf("private IEnumerable<ProbeWait> RealState(", StringComparison.Ordinal);
            int end = probe.IndexOf("private IEnumerable<ProbeWait> RealCellState(", StringComparison.Ordinal);
            Assert.IsTrue(start > 0 && end > start);
            string real = probe.Substring(start, end - start);
            foreach (string call in new[] { "AudioOverrides.Listener = null;", "AudioOverrides.Paused = null;", "AudioOverrides.OnRailsWarp = null;",
                "FlightDriver.SetPause(true", "TimeWarp.SetRate(3", "SetCameraIVA()", "MapView.EnterMapView()", "WeatherAudioModule.ReadPaused()" })
            {
                StringAssert.Contains(real, call);
            }
            StringAssert.Contains(probe, "AudioOverrides.RealCell = true;");
            StringAssert.Contains(probe, "WeatherHooks.BodyTracks = i =>");
        }

        [TestMethod]
        public void ARowThatReadsAForcedValueIsLabelledAsConsistencyAndLeftOutOfTheAcceptance()
        {
            string json = Rows();
            string[] consistency = { "D33.param_intensity_light", "D33.param_wind_blizzard", "D33.peak_blizzard", "D33.cell_far_db",
                "D33.nan_samples", "D33.clipped_samples" };
            foreach (string metric in consistency)
            {
                string row = Row(json, metric);
                StringAssert.Contains(row, "consistency, not acceptance");
                StringAssert.Contains(row, "\"required\": false");
            }
        }

        [TestMethod]
        public void EveryRowIsEmittedByTheProbe()
        {
            // The other direction of the existing check: a row nobody emits would fail the series for no reason.
            string json = Rows();
            string probe = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Atmospheres", "Audio", "Runtime", "Probe", "D33Probe.cs"));
            var dynamicPrefixes = new List<string> { "D33.rms_cell_", "D33.real_cell_found_", "D33.real_cell_edge_m_", "D33.real_cell_pan_error_",
                "D33.real_cell_left_share_", "D33.eve_rms_" };
            foreach (Match m in Regex.Matches(json, "\"metric\": \"(D33\\.[A-Za-z0-9_.]+)\""))
            {
                string metric = m.Groups[1].Value;
                bool dynamicRow = dynamicPrefixes.Exists(p => metric.StartsWith(p, StringComparison.Ordinal));
                Assert.IsTrue(dynamicRow || probe.Contains("\"" + metric + "\""), "no Emit for " + metric);
            }
        }
    }
}
