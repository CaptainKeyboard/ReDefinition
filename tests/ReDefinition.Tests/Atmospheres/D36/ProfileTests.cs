using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.D36
{
    // The quality tier: how it is resolved, what each tier writes, that it writes cost and never taste, and Reset.
    [TestClass]
    public class ProfileTests
    {
        private ConfigNode saved;
        private Func<long> vram;
        private Func<QualityTier> host;
        private bool gate;

        [TestInitialize]
        public void Save()
        {
            saved = AtmospheresSettings.Snapshot();
            vram = QualityProfile.VramSource;
            host = QualityProfile.HostTier;
            gate = VolumeResolutionGate.Passed;
            AtmospheresSettings.ResetToDefaults();
            QualityProfile.VramSource = () => 16384;
            QualityProfile.HostTier = () => QualityTier.Auto;
            ForeignChanges.Clear();
            report = ForeignChanges.Report;
            ForeignChanges.Report = line => { };
        }

        private Action<string> report;

        [TestCleanup]
        public void Restore()
        {
            ForeignChanges.Report = report;
            AtmospheresSettings.Restore(saved);
            QualityProfile.VramSource = vram;
            QualityProfile.HostTier = host;
            VolumeResolutionGate.Passed = gate;
            ForeignChanges.Clear();
        }

        private static Dictionary<string, string> Dump()
        {
            var dump = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (SettingDef def in AtmospheresSettings.Definitions)
            {
                dump[def.Key] = def.Kind == SettingKind.Text ? AtmospheresSettings.Text(def.Key) : def.Format(AtmospheresSettings.Number(def.Key));
            }
            return dump;
        }

        private static List<string> Differences(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            var keys = new List<string>();
            foreach (KeyValuePair<string, string> pair in a)
            {
                if (b[pair.Key] != pair.Value) keys.Add(pair.Key);
            }
            return keys;
        }

        // --- Resolution ---

        [TestMethod]
        public void TheTierResolvesFromTheRequestTheHostAndTheMemory()
        {
            // requested, host, VRAM MB, expected
            object[][] table =
            {
                new object[] { QualityTier.High, QualityTier.Low, 4096L, QualityTier.High },
                new object[] { QualityTier.Max, QualityTier.Auto, 0L, QualityTier.Max },
                new object[] { QualityTier.Auto, QualityTier.Ultra, 24576L, QualityTier.Ultra },
                new object[] { QualityTier.Auto, QualityTier.Auto, 16384L, QualityTier.Medium },
                new object[] { QualityTier.Auto, QualityTier.Auto, 0L, QualityTier.Medium },
                new object[] { QualityTier.Auto, QualityTier.High, 8192L, QualityTier.Low },
                new object[] { QualityTier.Auto, QualityTier.High, 10239L, QualityTier.Low },
                new object[] { QualityTier.Auto, QualityTier.High, 10240L, QualityTier.High },
                new object[] { QualityTier.Auto, QualityTier.Auto, 6144L, QualityTier.Low },
                new object[] { QualityTier.Low, QualityTier.Ultra, 32768L, QualityTier.Low },
            };
            foreach (object[] row in table)
            {
                Assert.AreEqual((QualityTier)row[3], QualityProfile.Resolve((QualityTier)row[0], (QualityTier)row[1], (long)row[2]),
                    row[0] + " / host " + row[1] + " / " + row[2] + " MB");
            }
        }

        [TestMethod]
        public void TheTierInForceFollowsTheSettingTheHostAndTheMemory()
        {
            QualityProfile.HostTier = () => QualityTier.High;
            Assert.AreEqual(QualityTier.High, QualityProfile.Effective());
            QualityProfile.VramSource = () => 8192;
            Assert.AreEqual(QualityTier.Low, QualityProfile.Effective());
            AtmospheresSettings.SetNumber(AtmospheresSettings.Keys.QualityTier, (double)(int)QualityTier.Ultra);
            Assert.AreEqual(QualityTier.Ultra, QualityProfile.Effective(), "a named tier is kept under 10 GB");
            QualityProfile.VramSource = () => { throw new InvalidOperationException("no card"); };
            AtmospheresSettings.SetNumber(AtmospheresSettings.Keys.QualityTier, 0.0);
            Assert.AreEqual(QualityTier.High, QualityProfile.Effective(), "an unreadable memory switches the guard off");
        }

        // --- The table ---

        [TestMethod]
        public void EveryTierWritesItsRowToTheBudgetKeys()
        {
            // tier, steps, lamps, grid, orbit map, resolution after the gate
            object[][] table =
            {
                new object[] { QualityTier.Low, 16, 2, SimGridChoice.Coarse, 1024, 0.25 },
                new object[] { QualityTier.Medium, 20, 4, SimGridChoice.Normal, 2048, 0.5 },
                new object[] { QualityTier.High, 28, 8, SimGridChoice.Normal, 4096, 1.0 },
                new object[] { QualityTier.Ultra, 36, 8, SimGridChoice.Fine, 4096, 1.0 },
                new object[] { QualityTier.Max, 48, 8, SimGridChoice.Fine, 4096, 1.0 },
            };
            foreach (object[] row in table)
            {
                var tier = (QualityTier)row[0];
                foreach (bool open in new[] { false, true })
                {
                    AtmospheresSettings.ResetToDefaults();
                    VolumeResolutionGate.Passed = open;
                    QualityProfile.Apply(tier);
                    Assert.AreEqual((int)row[1], AtmospheresSettings.VolumeSteps, tier + " steps");
                    Assert.AreEqual((int)row[2], AtmospheresSettings.LightCount, tier + " lamps");
                    Assert.AreEqual((SimGridChoice)row[3], AtmospheresSettings.SimGrid, tier + " grid");
                    Assert.AreEqual((int)row[4], AtmospheresSettings.OrbitMapWidth, tier + " orbit map");
                    Assert.AreEqual(open ? (float)(double)row[5] : 1f, AtmospheresSettings.VolumeResolution, 1e-6,
                        tier + " resolution, gate " + (open ? "open" : "closed"));
                }
            }
        }

        [TestMethod]
        public void TheHalfResolutionRowsApplyOnlyAfterTheGate()
        {
            Assert.AreEqual(1f, TierRows.For(QualityTier.Low).VolumeResolution(false));
            Assert.AreEqual(1f, TierRows.For(QualityTier.Medium).VolumeResolution(false));
            Assert.AreEqual(0.25f, TierRows.For(QualityTier.Low).VolumeResolution(true));
            Assert.AreEqual(0.5f, TierRows.For(QualityTier.Medium).VolumeResolution(true));
            Assert.IsFalse(VolumeResolutionGate.Passed || gate, "the gate is closed in the shipped code");
        }

        [TestMethod]
        public void TheBudgetNumbersGrowWithTheTierAndMaxHasNoCap()
        {
            TierRow previous = null;
            foreach (QualityTier tier in TierRows.All)
            {
                TierRow row = TierRows.For(tier);
                Assert.AreEqual(tier, row.Tier);
                if (previous != null && row.ParticleCap != TierRows.NoLimit)
                {
                    Assert.IsTrue(row.ParticleCap > previous.ParticleCap, tier + " particle cap");
                    Assert.IsTrue(row.MapUpdateSeconds < previous.MapUpdateSeconds, tier + " map update interval");
                    Assert.IsTrue(row.VolumeSteps > previous.VolumeSteps, tier + " steps");
                }
                previous = row;
            }
            Assert.AreEqual(TierRows.NoLimit, TierRows.For(QualityTier.Max).ParticleCap);
            Assert.AreEqual(20000, TierRows.For(QualityTier.High).ParticleCap, "20 000 billboards at High (brief C22)");
            Assert.AreEqual(TierRows.For(QualityTier.Medium), TierRows.For(QualityTier.Auto), "Auto is read as Medium");
        }

        [TestMethod]
        public void EveryRowValueIsInsideItsKeysRange()
        {
            foreach (QualityTier tier in TierRows.All)
            {
                AtmospheresSettings.ResetToDefaults();
                foreach (bool open in new[] { false, true })
                {
                    VolumeResolutionGate.Passed = open;
                    TierRow row = TierRows.For(tier);
                    Check(AtmospheresSettings.Keys.VolumeSteps, row.VolumeSteps);
                    Check(AtmospheresSettings.Keys.VolumeResolution, row.VolumeResolution(open));
                    Check(AtmospheresSettings.Keys.LightCount, row.LightCount);
                    Check(AtmospheresSettings.Keys.SimGrid, (int)row.SimGrid);
                    Check(AtmospheresSettings.Keys.OrbitMapWidth, row.OrbitMapWidth);
                }
            }
        }

        private static void Check(string key, double value)
        {
            SettingDef def = AtmospheresSettings.Definition(key);
            Assert.AreEqual(value, def.Limit(value), 1e-9, key + " is outside its range or not whole: " + value);
        }

        [TestMethod]
        public void EveryKeyWithAProfileRowIsSetByATierOrNamedWithItsReason()
        {
            var set = new HashSet<string>(TierRows.SetKeys, StringComparer.Ordinal);
            var without = new HashSet<string>(TierRows.SetWithoutRow, StringComparer.Ordinal);
            foreach (SettingDef def in AtmospheresSettings.Definitions)
            {
                bool hasRow = def.ProfileRow.Length > 0;
                if (set.Contains(def.Key))
                {
                    Assert.AreEqual(without.Contains(def.Key), !hasRow, def.Key + ": a key a tier sets carries a profile row, but " + string.Join(",", TierRows.SetWithoutRow));
                    Assert.IsFalse(TierRows.NotSet.ContainsKey(def.Key), def.Key + " is both set and listed as not set");
                    continue;
                }
                if (hasRow)
                {
                    string reason;
                    Assert.IsTrue(TierRows.NotSet.TryGetValue(def.Key, out reason) && reason.Length > 0, def.Key + " has the row " + def.ProfileRow + " and no reason why no tier sets it");
                }
                else
                {
                    Assert.IsFalse(TierRows.NotSet.ContainsKey(def.Key), def.Key + " has no row but is listed");
                }
            }
            foreach (string key in TierRows.NotSet.Keys) Assert.IsNotNull(AtmospheresSettings.Definition(key), key + " is not a key");
            foreach (string key in set) Assert.IsNotNull(AtmospheresSettings.Definition(key), key + " is not a key");
        }

        [TestMethod]
        public void NoTierTouchesATasteKey()
        {
            // The taste keys of the player: frequencies, strengths, colours and looks, the switches, the audio and the forecast limits.
            var taste = new HashSet<string>(StringComparer.Ordinal)
            {
                "yearLengthFactor", "seasonMode", "lightWeatherFrequency", "heavyShare", "windStrength", "gustStrength", "precipDensity",
                "fogBrightness", "veilStrength", "blizzardVisibilityM", "skyDimming", "lightInMediumStrength", "auroraAmbientTint",
                "weatherAudioVolume", "coverStrength", "coverDrift", "forecastUncertainty", "simSeed"
            };
            foreach (string key in TierRows.SetKeys) Assert.IsFalse(taste.Contains(key), key + " is taste");
            foreach (SettingDef def in AtmospheresSettings.Definitions)
            {
                if (def.Kind == SettingKind.Bool) CollectionAssert.DoesNotContain(TierRows.SetKeys, def.Key, def.Key + " is a switch");
            }
        }

        // --- A/B: Low against High changes cost rows only ---

        [TestMethod]
        public void TierLowAgainstHighChangesOnlyTheBudgetRows()
        {
            AtmospheresSettings.ResetToDefaults();
            // The player's taste, set to values that are not the defaults.
            AtmospheresSettings.SetNumber(AtmospheresSettings.Keys.LightWeatherFrequency, 1.7);
            AtmospheresSettings.SetNumber(AtmospheresSettings.Keys.WindStrength, 0.4);
            AtmospheresSettings.SetNumber(AtmospheresSettings.Keys.VeilStrength, 0.5);
            AtmospheresSettings.SetNumber(AtmospheresSettings.Keys.WeatherEffects, 1.0);
            QualityProfile.Apply(QualityTier.Low);
            Dictionary<string, string> low = Dump();
            QualityProfile.Apply(QualityTier.High);
            Dictionary<string, string> high = Dump();

            List<string> changed = Differences(low, high);
            CollectionAssert.AreEquivalent(new List<string>(TierRows.SetKeys).FindAll(k => k != AtmospheresSettings.Keys.VolumeResolution), changed,
                "with the gate closed the resolution is 1 for both");
            Assert.AreEqual("1.7", low[AtmospheresSettings.Keys.LightWeatherFrequency]);
            Assert.AreEqual("0.4", high[AtmospheresSettings.Keys.WindStrength]);

            VolumeResolutionGate.Passed = true;
            QualityProfile.Apply(QualityTier.Low);
            low = Dump();
            QualityProfile.Apply(QualityTier.High);
            high = Dump();
            CollectionAssert.AreEquivalent(new List<string>(TierRows.SetKeys), Differences(low, high), "with the gate open the resolution moves too");
        }

        // --- Reset ---

        [TestMethod]
        public void ResetPutsEveryKeyToItsDefaultAndThenWritesTheTiersRow()
        {
            QualityProfile.HostTier = () => QualityTier.High;
            // Every setting moved away from its default.
            foreach (SettingDef def in AtmospheresSettings.Definitions)
            {
                if (def.Kind == SettingKind.Text) AtmospheresSettings.SetText(def.Key, "changed");
                else AtmospheresSettings.SetNumber(def.Key, def.Default == def.Min ? def.Max : def.Min);
            }
            Assert.AreEqual(QualityTier.High, QualityProfile.Reset());

            var set = new HashSet<string>(TierRows.SetKeys, StringComparer.Ordinal);
            foreach (SettingDef def in AtmospheresSettings.Definitions)
            {
                if (set.Contains(def.Key)) continue;
                if (def.Kind == SettingKind.Text) Assert.AreEqual(def.DefaultText, AtmospheresSettings.Text(def.Key), def.Key);
                else Assert.AreEqual(def.Default, AtmospheresSettings.Number(def.Key), 0.0, def.Key + " is not its default after Reset");
            }
            TierRow row = TierRows.For(QualityTier.High);
            Assert.AreEqual(row.VolumeSteps, AtmospheresSettings.VolumeSteps);
            Assert.AreEqual(row.LightCount, AtmospheresSettings.LightCount);
            Assert.AreEqual(row.SimGrid, AtmospheresSettings.SimGrid);
            Assert.AreEqual(row.OrbitMapWidth, AtmospheresSettings.OrbitMapWidth);
            Assert.AreEqual(1f, AtmospheresSettings.VolumeResolution);
            Assert.AreEqual(QualityTier.Auto, AtmospheresSettings.QualityTier, "the tier setting itself goes back to Auto");
        }

        [TestMethod]
        public void ResetOnACardUnderTenGigabytesWritesTheLowRows()
        {
            QualityProfile.HostTier = () => QualityTier.Ultra;
            QualityProfile.VramSource = () => 8192;
            Assert.AreEqual(QualityTier.Low, QualityProfile.Reset());
            Assert.AreEqual(16, AtmospheresSettings.VolumeSteps);
            Assert.AreEqual(2, AtmospheresSettings.LightCount);
            Assert.AreEqual(1024, AtmospheresSettings.OrbitMapWidth);
        }

        [TestMethod]
        public void ResetRestoresTheForeignSettingsTheProductChangedAndOnlyThose()
        {
            int eve = 0, parallax = 0;
            ForeignChanges.Record("eve.layerCap", () => eve++);
            ForeignChanges.Record("eve.layerCap", () => eve += 10);
            Assert.AreEqual(1, ForeignChanges.Count, "a second note for the same setting keeps the first way");
            QualityProfile.Reset();
            Assert.AreEqual(1, eve, "the way back ran once");
            Assert.AreEqual(0, parallax, "nothing was recorded for Parallax, so Reset leaves it alone");
            Assert.AreEqual(0, ForeignChanges.Count);
            QualityProfile.Reset();
            Assert.AreEqual(1, eve, "a second Reset has nothing to restore");
        }

        [TestMethod]
        public void AWayBackThatThrowsDoesNotKeepTheOthers()
        {
            int ran = 0;
            ForeignChanges.Record("a", () => { throw new InvalidOperationException("gone"); });
            ForeignChanges.Record("b", () => ran++);
            Assert.AreEqual(1, ForeignChanges.RestoreAll());
            Assert.AreEqual(1, ran);
        }

        // --- The marker ---

        [TestMethod]
        public void TheTierIsWrittenAtTheFirstStartAndWhenItChanges()
        {
            Assert.IsTrue(QualityProfile.NeedsApply(QualityTier.Medium, null), "no marker: the first start");
            Assert.IsFalse(QualityProfile.NeedsApply(QualityTier.Medium, QualityTier.Medium), "the same tier leaves the player's values alone");
            Assert.IsTrue(QualityProfile.NeedsApply(QualityTier.High, QualityTier.Medium));
        }

        [TestMethod]
        public void TheMarkerReadsBackAndADamagedOneIsNone()
        {
            var node = new ConfigNode();
            node.AddValue(QualityProfileModule.MarkerKey, "High");
            Assert.AreEqual(QualityTier.High, QualityProfileModule.ParseMarker(node));
            node = new ConfigNode();
            node.AddValue(QualityProfileModule.MarkerKey, "banana");
            Assert.IsNull(QualityProfileModule.ParseMarker(node));
            node = new ConfigNode();
            node.AddValue(QualityProfileModule.MarkerKey, "Auto");
            Assert.IsNull(QualityProfileModule.ParseMarker(node), "Auto is never a tier in force");
            Assert.IsNull(QualityProfileModule.ParseMarker(new ConfigNode()));
            Assert.IsNull(QualityProfileModule.ParseMarker(null));
        }

        // --- weatherCompose Auto ---

        [TestMethod]
        public void ComposeAutoIsUnverifiedForEveryPairWithoutRecordedMetrics()
        {
            ComposeEvidence.Clear();
            try
            {
                foreach (UpscalerKind kind in new[] { UpscalerKind.Dlss, UpscalerKind.Fsr, UpscalerKind.Xess })
                {
                    foreach (string vendor in new[] { "NVIDIA", "AMD", "Intel" })
                    {
                        Assert.IsFalse(ComposeEvidence.IsVerified(vendor, kind));
                        StringAssert.Contains(ComposeEvidence.AutoLine(vendor, kind, true), "unverified");
                        StringAssert.Contains(ComposeEvidence.AutoLine(vendor, kind, true), "scene");
                    }
                }
                ComposeEvidence.Record("NVIDIA", UpscalerKind.Dlss);
                Assert.IsTrue(ComposeEvidence.IsVerified("nvidia", UpscalerKind.Dlss));
                Assert.IsFalse(ComposeEvidence.AutoLine("NVIDIA", UpscalerKind.Dlss, true).Contains("unverified"));
                Assert.IsFalse(ComposeEvidence.IsVerified("NVIDIA", UpscalerKind.Fsr));
                StringAssert.Contains(ComposeEvidence.AutoLine("NVIDIA", UpscalerKind.None, true), "no upscaler");
                StringAssert.Contains(ComposeEvidence.AutoLine("NVIDIA", UpscalerKind.Dlss, false), "host does not answer");
            }
            finally
            {
                ComposeEvidence.Clear();
            }
        }
    }
}
