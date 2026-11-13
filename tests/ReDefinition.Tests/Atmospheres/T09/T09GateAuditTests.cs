using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The K-T2 gate audit. Start 41 failed in the clock step because the generator's field was never installed: GeneratorModule.Tick installs it only while
    // CloudGates.GeneratorInstalled is true, and weatherEffects is off by default. Nobody had listed the gates the chain stands behind. This file does:
    // it takes the starts from tests/series/K-T2.json, runs the probe (T09Probe, against the fake world of the walk-through) with the settings a start has
    // before the probe touches them (the defaults of the Atmospheres: the install holds no settings.cfg of the Atmospheres, and the series pins none),
    // and on every frame evaluates the REAL gate expressions (CloudGates, OwnCloudPlan.Wanted, OwnCloudOptions.Active, CloudModeModel) with the live
    // settings. The fake world installs the field by the same gate (Drive), so a gate that is closed shows as a NullField, as in the game.
    //
    // The preconditions of the chain, and where each is decided:
    //   1. Atmospheres enabled (key enabled, default on)                               CloudGates.GeneratorInstalled / OwnPassSwitches / EvePainterOn
    //   2. weatherEffects on (default OFF)                                              CloudGates.GeneratorInstalled, EvePainterOn; the probe sets it
    //   3. genEnabled on (default on)                                                   CloudGates.GeneratorInstalled
    //   4. the generator's field in WeatherHub.Field (GeneratorModule.Install)          follows 1 to 3; WeatherHub.Field is not a NullField
    //   5. flight scene, atmosphere on the body, map view off, a body status that runs weather, layers for the body   OwnCloudPlan.Wanted; data below
    //   6. own variants: ownClouds on and the source in force Ours                      OwnCloudOptions.Active; the variants set both (UseOwn)
    //   7. TVC: cloudSource Eve, ownClouds off, EVE's painter on                        CloudGates.EvePainterOn; ApplyVariant("tvc")
    //   8. off: cloudSource None (the baseline draws no cloud of ours)                  ApplyVariant("off")
    //   9. Kerbin's data: BODY status Auto, GENERATOR enabled, no cloudSource override, the layers  GameData/ReDefinition/Atmospheres/Bodies
    //  10. the series' own pins and arguments: raytracing off, frame generation off, NativeAA, no frame cap, -redefinitionGpuProfile, -atmoProbe T09Probe
    // Not decidable offline: EVE and Scatterer present (tvc), the shader bundle, the noise volumes and the GPU profiler's labels; the probe checks them.
    public partial class T09WalkThroughTests
    {
        private sealed class StartDef
        {
            internal string Id;
            internal string[] Variants;
            internal string Args;
            internal readonly Dictionary<string, string> Switches = new Dictionary<string, string>();
        }

        private static string RepoFile(params string[] relative)
        {
            return Path.Combine(RepoRoot.Root(relative), Path.Combine(relative));
        }

        private static Dictionary<string, string> SwitchPairs(string text)
        {
            var pairs = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(text, "\"(\\w+)\":\\s*\"?([\\w.]+)\"?")) pairs[m.Groups[1].Value] = m.Groups[2].Value;
            return pairs;
        }

        // The starts of K-T2.json with their variants, arguments and the product switches (the series' merged with the start's).
        private static List<StartDef> KT2Starts()
        {
            string text = File.ReadAllText(RepoFile("tests", "series", "K-T2.json"));
            Dictionary<string, string> series = SwitchPairs(Regex.Match(text, "\"productSwitches\":\\s*\\{([^}]*)\\},\\s*\"exclusive\"").Groups[1].Value);
            var starts = new List<StartDef>();
            foreach (string line in text.Split('\n'))
            {
                Match id = Regex.Match(line, "^\\s*\\{ \"id\": \"(\\w+)\"");
                if (!id.Success || !line.Contains("\"probeArgs\"")) continue;
                var s = new StartDef { Id = id.Groups[1].Value };
                s.Variants = Regex.Matches(Regex.Match(line, "\"variants\":\\s*\\[([^\\]]*)\\]").Groups[1].Value, "\"(\\w+)\"").Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
                s.Args = Regex.Match(line, "\"probeArgs\":\\s*\\[([^\\]]*)\\]").Groups[1].Value;
                foreach (KeyValuePair<string, string> kv in series) s.Switches[kv.Key] = kv.Value;
                foreach (KeyValuePair<string, string> kv in SwitchPairs(Regex.Match(line, "\"productSwitches\":\\s*\\{([^}]*)\\}").Groups[1].Value)) s.Switches[kv.Key] = kv.Value;
                starts.Add(s);
            }
            return starts;
        }

        private static string KerbinData(string file)
        {
            return File.ReadAllText(RepoFile("GameData", "ReDefinition", "Atmospheres", "Bodies", file));
        }

        // The data half of the gate on Kerbin: the body's status runs weather (Auto), its generator is enabled, nothing overrides the cloud source, layers exist.
        private static List<string> KerbinDataProblems(out bool weatherRuns, out int layers)
        {
            var problems = new List<string>();
            string body = KerbinData("Kerbin.cfg"), clouds = KerbinData("Clouds-Kerbin.cfg");
            weatherRuns = Regex.IsMatch(body, @"^\s*status\s*=\s*Auto", RegexOptions.Multiline);
            if (!weatherRuns) problems.Add("Kerbin.cfg: the body status is not Auto");
            if (!Regex.IsMatch(body, @"GENERATOR\s*\{.*?^\s*enabled\s*=\s*true", RegexOptions.Multiline | RegexOptions.Singleline)) problems.Add("Kerbin.cfg: the GENERATOR is not enabled");
            if (Regex.IsMatch(body, @"^\s*cloudSource\s*=", RegexOptions.Multiline) || Regex.IsMatch(clouds, @"^\s*(cloudSource|source)\s*=", RegexOptions.Multiline))
                problems.Add("Kerbin's cloud files override the cloud source (the player's setting no longer decides)");
            layers = Regex.Matches(clouds, @"^\s*LAYER\s*$|^\s*LAYER\s*\{", RegexOptions.Multiline).Count;
            if (layers == 0) problems.Add("Clouds-Kerbin.cfg has no LAYER");
            return problems;
        }

        private static bool IsOwnVariant(string v)
        {
            return v != "off" && v != "tvc";
        }

        // The gates that are closed on this frame, by name, for the variant that is on (null: before the first).
        private static void EvaluateGates(Run r, ISet<string> closed, bool weatherRuns, int layers)
        {
            // From the clock step on: the probe has its first 120 frames and the window check behind it (12 s at 0.1 s a frame).
            if (!r.World.FlightReady || r.World.RealS < r.World.FlightReadyAtS + 15.0 || r.Probe.Finished) return;
            Func<string, double> v = AtmospheresSettings.Number;
            if (!CloudGates.GeneratorInstalled(v)) closed.Add("generator gate (enabled, weatherEffects, genEnabled)");
            if (WeatherHub.Field is NullField) closed.Add("the weather field is installed in the hub (it is the NullField)");
            string variant = r.Probe.CurrentVariant;
            if (variant == null) return;
            CloudSourceKind source = CloudGates.EffectiveSource(v, false);
            if (variant == "off")
            {
                if (source != CloudSourceKind.None) closed.Add("off: the cloud source is None");
            }
            else if (variant == "tvc")
            {
                if (!CloudGates.EvePainterOn(v)) closed.Add("tvc: EVE's painter gate (enabled, weatherEffects)");
                if (source != CloudSourceKind.Eve) closed.Add("tvc: the cloud source in force is Eve");
                if (v(AtmospheresSettings.Keys.OwnClouds) != 0.0) closed.Add("tvc: ownClouds is off");
            }
            else
            {
                if (!CloudGates.OwnPassSwitches(v, false)) closed.Add(variant + ": the own pass's switches (enabled, ownClouds, source Ours)");
                bool wanted = OwnCloudPlan.Wanted(v(AtmospheresSettings.Keys.Enabled) != 0.0, true, true, weatherRuns, OwnCloudOptions.Active(v, source), layers, false);
                if (!wanted) closed.Add(variant + ": OwnCloudPlan.Wanted (flight scene, atmosphere, body status, own pass, layers, no map view)");
            }
        }

        // Runs the start's variants through the probe with the settings at their start state, and returns the closed gates (with the start's name).
        private List<string> AuditStart(StartDef s, bool probeSetsSwitches)
        {
            bool weatherRuns;
            int layers;
            Assert.AreEqual(0, KerbinDataProblems(out weatherRuns, out layers).Count, string.Join("; ", KerbinDataProblems(out weatherRuns, out layers).ToArray()));
            AtmospheresSettings.ResetToDefaults();
            Tuning.ClearOverrides();
            var closed = new HashSet<string>();
            var declared = new List<string>();
            Run r = Start(s.Variants, x => x.Probe.SetsProductSwitches = probeSetsSwitches, "1707", s.Id);
            Drive(r, 0.1, 3600.0, rr =>
            {
                EvaluateGates(rr, closed, weatherRuns, layers);
                // The declared product switches of the start hold while its main variant runs.
                string main = s.Variants.Contains("plain") ? "plain" : s.Variants.Contains("tvc") ? "tvc" : null;
                if (main != null && rr.Probe.CurrentVariant == main)
                {
                    foreach (KeyValuePair<string, string> kv in s.Switches)
                    {
                        double now = AtmospheresSettings.Number(kv.Key);
                        double want;
                        if (!double.TryParse(kv.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out want))
                            want = (int)Enum.Parse(typeof(CloudSourceKind), kv.Value);
                        if (Math.Abs(now - want) > 1e-9 && !declared.Contains(kv.Key)) declared.Add(kv.Key);
                    }
                }
            });
            var result = closed.Select(c => s.Id + ": " + c).ToList();
            result.AddRange(declared.Select(d => s.Id + ": the declared product switch " + d + " does not hold while the main variant runs"));
            if (probeSetsSwitches) Assert.IsTrue(r.Probe.Passed, "the start's probe passes: " + Dump(r));
            return result;
        }

        [TestMethod]
        public void K_T2_GateAudit_EveryGateOfTheChainIsOpenForEveryStartWithTheStartStateOfTheSettings()
        {
            List<StartDef> starts = KT2Starts();
            Assert.AreEqual(6, starts.Count, "the starts of K-T2.json were read");
            foreach (StartDef s in starts)
            {
                Assert.IsTrue(s.Switches.ContainsKey("weatherEffects") && s.Switches.ContainsKey("genEnabled") && s.Switches.ContainsKey("enabled") && s.Switches.ContainsKey("cloudSource"),
                              s.Id + " declares the product switches it needs");
                // The start state is the defaults: weatherEffects is off, which is what closed the gate in start 41.
                AtmospheresSettings.ResetToDefaults();
                Assert.AreEqual(0.0, AtmospheresSettings.Number(AtmospheresSettings.Keys.WeatherEffects), "weatherEffects is off by default");
                List<string> closed = AuditStart(s, true);
                Assert.AreEqual(0, closed.Count, "closed gates:\n" + string.Join("\n", closed.ToArray()));
            }
        }

        [TestMethod]
        public void K_T2_GateAudit_ReportsTheClosedGeneratorGateWhenTheProbeDoesNotSetTheSwitches()
        {
            StartDef s = KT2Starts().First(x => x.Id == "own1707");
            List<string> closed = AuditStart(s, false);
            Assert.IsTrue(closed.Any(c => c.Contains("generator gate")), "the audit names the closed generator gate:\n" + string.Join("\n", closed.ToArray()));
            Assert.IsTrue(closed.Any(c => c.Contains("NullField")), "and the missing field");
        }

        [TestMethod]
        public void K_T2_GateAudit_TheDeclaredSwitchesAreOnesTheProbeSetsOrTheDefaultsHave()
        {
            // The preflight (tools/atmo_preflight.py, pf_product_switches) accepts a switch that is off by default only where the probe sets it; the same rule here,
            // from the probe's source, so that the declaration in K-T2.json and the code cannot drift apart.
            string probe = File.ReadAllText(RepoFile("src", "Atmospheres", "Light", "Cloud", "Probe", "T09Probe.cs"));
            AtmospheresSettings.ResetToDefaults();
            foreach (StartDef s in KT2Starts())
            {
                foreach (KeyValuePair<string, string> kv in s.Switches)
                {
                    string constant = char.ToUpperInvariant(kv.Key[0]) + kv.Key.Substring(1);
                    bool sets = Regex.IsMatch(probe, @"SetSetting\(\s*AtmospheresSettings\.Keys\." + constant + @"\b");
                    double now = AtmospheresSettings.Number(kv.Key), want;
                    if (!double.TryParse(kv.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out want))
                        want = (int)Enum.Parse(typeof(CloudSourceKind), kv.Value);
                    Assert.IsTrue(sets || Math.Abs(now - want) < 1e-9, s.Id + ": " + kv.Key + " = " + kv.Value + " is neither the default nor set by the probe");
                }
            }
        }

        [TestMethod]
        public void K_T2_GateAudit_TheSeriesPinsAndArgumentsTheTimingDependsOnAreThere()
        {
            string text = File.ReadAllText(RepoFile("tests", "series", "K-T2.json"));
            foreach (string pin in new[] { "\"raytracing\": \"Off\"", "\"frameGeneration\": false", "\"qualityAuto\": false", "\"quality\": \"NativeAA\"", "\"SYNC_VBL\": 0", "\"FRAMERATE_LIMIT\": 1000" })
                Assert.IsTrue(text.Contains(pin), "pinned: " + pin);
            foreach (StartDef s in KT2Starts())
            {
                Assert.IsTrue(s.Args.Contains("\"-redefinitionGpuProfile\""), s.Id + ": the GPU profiler is on");
                Assert.IsTrue(s.Args.Contains("\"-atmoProbe\", \"T09Probe\""), s.Id + ": the probe is named");
                Assert.IsTrue(s.Args.Contains("\"-redefinitionProbeClouds\""), s.Id + ": the probe's clouds switch is on");
            }
            Assert.IsTrue(text.Contains("\"bundle\": true"), "the shader bundle is built");
        }

        // --- Start 41 as a fault injection ----------------------------------------------------------------------------------------------

        [TestMethod]
        public void WeatherEffectsOffInTheInstalledProfileFailsTheProbeAsItWasBeforeStart41AndPassesWithTheFix()
        {
            // The probe as it was (it set no product switch): the generator's gate stays closed, the hub keeps the NullField, the clock step waits and fails.
            Run old = Start(new[] { "plain" }, x =>
            {
                AtmospheresSettings.SetNumber(AtmospheresSettings.Keys.WeatherEffects, 0.0);
                x.Probe.SetsProductSwitches = false;
            });
            Drive(old);
            AssertNamedFailure(old, "the weather field did not answer at the site for 48 of 48 candidate times within 120 s (field NullField, readiness Ready; gates: enabled 1, weatherEffects 0, genEnabled 1");

            // The probe now: it turns the switches on before the clock step and says so.
            Run fixedRun = Start(new[] { "plain" }, x => AtmospheresSettings.SetNumber(AtmospheresSettings.Keys.WeatherEffects, 0.0));
            Drive(fixedRun);
            Assert.IsTrue(fixedRun.Probe.Passed, Dump(fixedRun));
            Assert.IsTrue(fixedRun.Log.Any(l => l == "T09 gates: enabled 1, weatherEffects 1, genEnabled 1, cloudSource Eve, ownClouds 0"), Dump(fixedRun));
            Assert.IsTrue(fixedRun.Log.Any(l => l.StartsWith("T09 gates (plain): enabled 1, weatherEffects 1, genEnabled 1, cloudSource Ours, ownClouds 1")), Dump(fixedRun));
        }

        [TestMethod]
        public void EachMasterSwitchOffInThePlayersSettingsIsTurnedOnByTheProbe()
        {
            foreach (string key in new[] { AtmospheresSettings.Keys.Enabled, AtmospheresSettings.Keys.WeatherEffects, AtmospheresSettings.Keys.GenEnabled })
            {
                Run r = Start(new[] { "plain" }, x => AtmospheresSettings.SetNumber(key, 0.0));
                Drive(r);
                Assert.IsTrue(r.Probe.Passed, key + " off: " + Dump(r));
                Assert.AreEqual(1.0, AtmospheresSettings.Number(key), key + " is on after the probe");
            }
        }

        [TestMethod]
        public void ATvcStartAlsoGetsTheFieldAndEvesPainterGateFromTheProbe()
        {
            Run r = Start(new[] { "tvc" }, x => AtmospheresSettings.SetNumber(AtmospheresSettings.Keys.WeatherEffects, 0.0), "1707", "tvc1707");
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("T09 gates (tvc): enabled 1, weatherEffects 1, genEnabled 1, cloudSource Eve, ownClouds 0")), Dump(r));
        }
    }
}
