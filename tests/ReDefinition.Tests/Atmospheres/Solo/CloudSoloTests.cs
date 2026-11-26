using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Tests.Atmospheres.T09;

namespace ReDefinition.Tests.Atmospheres.Solo
{
    // The solo mode of the cloud pass (E40 SOLO): the cell the forced map can be, the layer mask of the solo key, and the plan that turns a layer and a type into the tuning keys the
    // pass, the viewer's control and the K-S1 probe all use.
    [TestClass]
    public class CloudSoloTests
    {
        private const double Radius = 600000.0;

        private static CloudLayerDef[] Stack()
        {
            return new[] { CloudLookFixtures.Rain(), CloudLookFixtures.Cumulus(), CloudLookFixtures.Cumulonimbus(), CloudLookFixtures.Cirrus() };
        }

        private static Dictionary<string, float> Apply(IEnumerable<KeyValuePair<string, float>> overrides, Dictionary<string, float> into = null)
        {
            var table = into ?? new Dictionary<string, float>();
            foreach (KeyValuePair<string, float> pair in overrides)
            {
                if (float.IsNaN(pair.Value)) table.Remove(pair.Key);
                else table[pair.Key] = pair.Value;
            }
            return table;
        }

        // --- The cell ---

        [TestMethod]
        public void TheCellIsAFlatCoreThatFallsToNothing()
        {
            Assert.AreEqual(0.9, CloudSoloCell.ValueAt(0.9, 0.0, 12.0), 1e-12, "the strength at the centre");
            // The Gaussian times 1.4 is above 1 up to 12 x sqrt(ln 1.4) = 6.96 km: a flat core.
            Assert.AreEqual(0.9, CloudSoloCell.ValueAt(0.9, 6.5, 12.0), 1e-12, "inside the core");
            Assert.IsTrue(CloudSoloCell.ValueAt(0.9, 7.5, 12.0) < 0.9, "outside it the value falls");
            Assert.IsTrue(CloudSoloCell.ValueAt(0.9, 30.0, 12.0) < 0.01, "30 km out the map is empty");
            Assert.AreEqual(0.0, CloudSoloCell.ValueAt(0.0, 0.0, 12.0), 1e-12, "a strength of 0 draws nothing");
            Assert.AreEqual(0.7, CloudSoloCell.ValueAt(0.7, 500.0, 0.0), 1e-12, "no cell: the constant map");
            double last = double.MaxValue;
            for (double d = 0.0; d <= 60.0; d += 3.0)
            {
                double v = CloudSoloCell.ValueAt(1.0, d, 12.0);
                Assert.IsTrue(v <= last + 1e-12, "the value never rises with the distance");
                last = v;
            }
        }

        [TestMethod]
        public void FillPutsOneCellAtTheCentreAndNothingElseOnAMapOfTheGeneratorsSize()
        {
            const int w = 512, h = 256;
            var bytes = new byte[w * h];
            var values = new float[w * h];
            CloudSoloCell.Fill(bytes, values, w, h, Radius, 10.0, 40.0, 12.0, 1.0);
            int peak = 0, nonZero = 0;
            double sumLat = 0.0, sumLon = 0.0, sumWeight = 0.0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    byte b = bytes[y * w + x];
                    Assert.AreEqual(b / 255f, values[y * w + x], 1e-6f, "bytes and values agree");
                    if (b == 255) peak++;
                    if (b > 0)
                    {
                        nonZero++;
                        sumLat += b * (0.5 - (y + 0.5) / h) * 180.0;
                        sumLon += b * (((x + 0.5) / w - 0.5) * 360.0);
                        sumWeight += b;
                    }
                }
            Assert.IsTrue(peak >= 1, "the core reaches the strength");
            Assert.IsTrue(nonZero < 200, "the cell is a few texels, not a map: " + nonZero);
            Assert.AreEqual(10.0, sumLat / sumWeight, 0.8, "the cell stands at the latitude asked for");
            Assert.AreEqual(40.0, sumLon / sumWeight, 0.8, "and at the longitude");
            // The weight is a texel's width at the equator: a quarter of a degree.
            Assert.AreEqual(0, bytes[0], "the corner of the map is empty");
        }

        [TestMethod]
        public void TheCellWrapsAcrossTheDateLineAndKeepsItsDistances()
        {
            const int w = 512, h = 256;
            var bytes = new byte[w * h];
            CloudSoloCell.Fill(bytes, null, w, h, Radius, 0.0, 179.9, 12.0, 1.0);
            int left = 0, right = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < 20; x++) left += bytes[y * w + x] > 0 ? 1 : 0;
                for (int x = w - 20; x < w; x++) right += bytes[y * w + x] > 0 ? 1 : 0;
            }
            Assert.IsTrue(left > 0 && right > 0, "the cell shows on both edges of the map: " + left + " / " + right);
            // 0.2 degrees of longitude at 5 degrees of latitude on a sphere of 600 km: 600 km x cos 5 x 0.2 degrees in radians = 2.087 km, across the date line too.
            Assert.AreEqual(2.0866, CloudSoloCell.DistanceKm(5.0, 179.9, 5.0, -179.9, Radius), 0.001);
        }

        [TestMethod]
        public void WithoutACellTheFillIsTheConstantMapAsBefore()
        {
            var bytes = new byte[16 * 8];
            var values = new float[16 * 8];
            CloudSoloCell.Fill(bytes, values, 16, 8, Radius, 0.0, 0.0, 0.0, 0.6);
            Assert.IsTrue(bytes.All(b => b == 153), "0.6 is byte 153 everywhere");
            Assert.IsTrue(values.All(v => Math.Abs(v - 0.6f) < 0.003f));
        }

        // --- The mask ---

        [TestMethod]
        public void TheSoloKeyIsTheOneBasedIndexOfTheOnlyLayerDrawn()
        {
            Assert.AreEqual(0, CloudLookTuning.SoloMaskOf(0f), "0: every layer");
            Assert.AreEqual(1, CloudLookTuning.SoloMaskOf(1f));
            Assert.AreEqual(2, CloudLookTuning.SoloMaskOf(2f));
            Assert.AreEqual(8, CloudLookTuning.SoloMaskOf(4f));
            Assert.AreEqual(0, CloudLookTuning.SoloMaskOf(5f), "no fifth layer: every layer, not a mask that draws none");
            Assert.AreEqual(0, CloudLookTuning.SoloMaskOf(-1f));
            Assert.AreEqual(0, CloudLookTuning.SoloMaskOf(float.NaN));
            Assert.AreEqual(4, CloudLookTuning.SoloMaskOf(3.2f));
        }

        [TestMethod]
        public void AProbesMaskWinsOverTheSoloLayer()
        {
            var f = new LayerFrame();
            Assert.AreEqual(0, f.DrawnMask, "no mask, no solo: every layer");
            f.SoloMask = 4;
            Assert.AreEqual(4, f.DrawnMask, "the solo layer alone");
            f.LayerMask = 1 << 20;
            Assert.AreEqual(1 << 20, f.DrawnMask, "the probe's baseline mask draws no layer whatever the solo says");
            f.LayerMask = 0;
            Assert.AreEqual(4, f.DrawnMask);
        }

        // --- The plan ---

        [TestMethod]
        public void ASoloSelectionForcesOneLayersMapAndTypeAndEmptiesTheRest()
        {
            CloudLayerDef[] stack = Stack();
            var s = new SoloSelection { Layer = 1, Type = 1, Strength = 0.5f };
            Dictionary<string, float> keys = Apply(CloudSoloPlan.Overrides(stack, s));
            Assert.AreEqual(2f, keys[CloudLookTuning.TuneSoloLayer], "layer 1 is the key's 2");
            Assert.AreEqual(0f, keys[CloudLookTuning.TuneCoverCombine], "the forced maps stay what they say");
            Assert.AreEqual(0.5f, keys[CloudLookTuning.TuneMapOverride + "Kerbin-cumulus"]);
            Assert.AreEqual(0.25f, keys[CloudLookTuning.TuneTypeOverride + "Kerbin-cumulus"], "humilis is the second of five types: 0.25");
            foreach (string other in new[] { "Kerbin-weather-rain", "Kerbin-cumulonimbus", "Kerbin-cirrus" })
            {
                Assert.AreEqual(0f, keys[CloudLookTuning.TuneMapOverride + other], other + " has an empty map");
                Assert.IsFalse(keys.ContainsKey(CloudLookTuning.TuneTypeOverride + other), other + " has no forced type");
            }
            foreach (CloudLayerDef l in stack) Assert.AreEqual(0f, keys[CloudLookTuning.TuneLowStratiformRule + l.Name], l.Name + ": the low stratiform rule is off, a constant map draws everywhere");
            Assert.IsFalse(keys.ContainsKey(CloudLookTuning.TuneCellKm), "a uniform field has no cell");
            // The no-solo selection takes every key back.
            Apply(CloudSoloPlan.Overrides(stack, new SoloSelection { Layer = -1, Type = -1 }), keys);
            Assert.AreEqual(0, keys.Count, "nothing is left: " + string.Join(", ", keys.Keys.ToArray()));
        }

        [TestMethod]
        public void ACellSelectionAddsTheCellKeysAndTheTypeOfTheTower()
        {
            CloudLayerDef[] stack = Stack();
            Assert.IsTrue(CloudSoloPlan.IsCellType(stack[2], 1), "incus feeds an anvil");
            Assert.IsFalse(CloudSoloPlan.IsCellType(stack[2], 0));
            var s = new SoloSelection { Layer = 2, Type = 1, Strength = 1f, Cell = true, CellKm = 12.0, CellLatDeg = -0.1, CellLonDeg = -74.0 };
            Dictionary<string, float> keys = Apply(CloudSoloPlan.Overrides(stack, s));
            Assert.AreEqual(3f, keys[CloudLookTuning.TuneSoloLayer]);
            Assert.AreEqual(0.5f, keys[CloudLookTuning.TuneTypeOverride + "Kerbin-cumulonimbus"], "incus is the middle of three types");
            Assert.AreEqual(12f, keys[CloudLookTuning.TuneCellKm]);
            Assert.AreEqual(-0.1f, keys[CloudLookTuning.TuneCellLat], 1e-6f);
            Assert.AreEqual(-74f, keys[CloudLookTuning.TuneCellLon], 1e-6f);
            Assert.AreEqual(1f, keys[CloudLookTuning.TuneMapOverride + "Kerbin-cumulonimbus"]);
            Assert.AreEqual(0f, keys[CloudLookTuning.TuneMapOverride + "Kerbin-cumulus"]);
        }

        [TestMethod]
        public void TheSelectionIsReadBackFromTheKeysInForce()
        {
            CloudLayerDef[] stack = Stack();
            Func<Dictionary<string, float>, Func<string, float, float>> reader = table => (key, d) => table.ContainsKey(key) ? table[key] : d;
            var none = CloudSoloPlan.Read(stack, reader(new Dictionary<string, float>()));
            Assert.IsFalse(none.On, "no key: no solo");
            for (int layer = 0; layer < stack.Length; layer++)
                for (int type = 0; type < stack[layer].Types.Count; type++)
                {
                    var want = new SoloSelection { Layer = layer, Type = type, Strength = 0.35f };
                    SoloSelection got = CloudSoloPlan.Read(stack, reader(Apply(CloudSoloPlan.Overrides(stack, want))));
                    Assert.IsTrue(got.On, CloudSoloPlan.Label(stack[layer], type));
                    Assert.AreEqual(layer, got.Layer);
                    Assert.AreEqual(type, got.Type, CloudSoloPlan.Label(stack[layer], type));
                    Assert.AreEqual(0.35f, got.Strength, 1e-6f);
                    Assert.IsFalse(got.Cell);
                }
            var cell = new SoloSelection { Layer = 2, Type = 1, Strength = 0.9f, Cell = true, CellKm = 12.0, CellLatDeg = 1.5, CellLonDeg = 20.0 };
            SoloSelection back = CloudSoloPlan.Read(stack, reader(Apply(CloudSoloPlan.Overrides(stack, cell))));
            Assert.IsTrue(back.Cell);
            Assert.AreEqual(12.0, back.CellKm, 1e-5);
            Assert.AreEqual(1.5, back.CellLatDeg, 1e-5);
            Assert.AreEqual(20.0, back.CellLonDeg, 1e-5);
        }

        [TestMethod]
        public void ADefaultStrengthIsTheCoverATypicalFieldHas()
        {
            CloudLayerDef[] stack = Stack();
            Func<int, string, float> of = (layer, part) => CloudSoloPlan.DefaultStrength(stack[layer], stack[layer].Types.Select((t, i) => new { t, i }).First(x => x.t.Name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0).i);
            Assert.AreEqual(1f, of(0, "Fog"), "a low stratiform layer is closed");
            Assert.AreEqual(1f, of(0, "Nimbostratus"));
            Assert.AreEqual(0.8f, of(1, "Stratocumulus"));
            Assert.AreEqual(0.5f, of(1, "humilis"));
            Assert.AreEqual(0.5f, of(1, "mediocris"));
            Assert.AreEqual(0.4f, of(1, "congestus"));
            Assert.AreEqual(0.6f, of(1, "Altocumulus"));
            Assert.AreEqual(1f, of(2, "capillatus"), "a cell at its strongest");
            Assert.AreEqual(0.5f, of(3, "Cirrus"));
        }

        [TestMethod]
        public void TheTypeValueFollowsTheTablesAxisBothWays()
        {
            CloudLayerDef cumulus = CloudLookFixtures.Cumulus();
            Assert.AreEqual(0f, CloudSoloPlan.TypeValue(cumulus, 0));
            Assert.AreEqual(0.25f, CloudSoloPlan.TypeValue(cumulus, 1));
            Assert.AreEqual(1f, CloudSoloPlan.TypeValue(cumulus, 4));
            for (int t = 0; t < cumulus.Types.Count; t++) Assert.AreEqual(t, CloudSoloPlan.TypeAt(cumulus, CloudSoloPlan.TypeValue(cumulus, t)));
            Assert.AreEqual(-1, CloudSoloPlan.TypeAt(cumulus, -1f), "no override is no type");
            Assert.AreEqual("Kerbin-cumulus / Cumulus humilis", CloudSoloPlan.Label(cumulus, 1));
        }

        // A store in memory, as the viewer's tests have it.
        private sealed class MemoryStore : ReDefinition.Atmospheres.Viewer.IOptionStore
        {
            internal readonly Dictionary<string, double> SettingValues = new Dictionary<string, double>();
            internal readonly Dictionary<string, float> OverrideValues = new Dictionary<string, float>();

            public double Setting(string key) { double v; return SettingValues.TryGetValue(key, out v) ? v : 0.0; }
            public bool SetSetting(string key, double value) { SettingValues[key] = value; return true; }
            public bool TryOverride(string key, out float value) { return OverrideValues.TryGetValue(key, out value); }
            public void SetOverride(string key, float value) { OverrideValues[key] = value; }
            public void ClearOverride(string key) { OverrideValues.Remove(key); }
            public float Effective(string key, float compiledDefault) { float v; return OverrideValues.TryGetValue(key, out v) ? v : compiledDefault; }
        }

        [TestMethod]
        public void TheSoloStateIsInTheKeysSoTheABFlipAndThePresetsCarryIt()
        {
            CloudLayerDef[] stack = Stack();
            var store = new MemoryStore();
            var settingKeys = new string[0];
            List<string> keyNames = ReDefinition.Atmospheres.Viewer.CloudKeyRegistry.Expand(stack.Select(l => l.Name)).Select(k => k.Key).ToList();
            // A: the cumulonimbus cell. B: the field's own weather (no solo). Capture A, flip to B, flip back.
            var cell = new SoloSelection { Layer = 2, Type = 1, Strength = 1f, Cell = true, CellKm = 12.0, CellLatDeg = -0.1, CellLonDeg = -74.0 };
            foreach (KeyValuePair<string, float> pair in CloudSoloPlan.Overrides(stack, cell)) if (!float.IsNaN(pair.Value)) store.SetOverride(pair.Key, pair.Value);
            var flip = new ReDefinition.Atmospheres.Viewer.OptionFlip();
            flip.StoreA(ReDefinition.Atmospheres.Viewer.OptionSet.Capture(store, settingKeys, keyNames));
            foreach (KeyValuePair<string, float> pair in CloudSoloPlan.Overrides(stack, new SoloSelection { Layer = -1, Type = -1 })) store.ClearOverride(pair.Key);
            Assert.IsFalse(CloudSoloPlan.Read(stack, store.Effective).On, "B: the field's own weather");
            Assert.IsTrue(flip.Flip(store, settingKeys, keyNames));
            SoloSelection a = CloudSoloPlan.Read(stack, store.Effective);
            Assert.IsTrue(a.On && a.Cell && a.Layer == 2 && a.Type == 1, "A is the cell again after the flip");
            Assert.AreEqual(-74.0, a.CellLonDeg, 1e-5);
            Assert.IsTrue(flip.Flip(store, settingKeys, keyNames));
            Assert.IsFalse(CloudSoloPlan.Read(stack, store.Effective).On, "and B again after the second flip");
            Assert.AreEqual(0, store.OverrideValues.Count, "nothing of the solo mode is left in B");
            // The preset's file form: the overrides survive a node and back.
            foreach (KeyValuePair<string, float> pair in CloudSoloPlan.Overrides(stack, cell)) if (!float.IsNaN(pair.Value)) store.SetOverride(pair.Key, pair.Value);
            var set = ReDefinition.Atmospheres.Viewer.OptionSet.FromNode(ReDefinition.Atmospheres.Viewer.OptionSet.Capture(store, settingKeys, keyNames).ToNode());
            foreach (string k in keyNames) store.ClearOverride(k);
            set.ApplyTo(store, keyNames);
            SoloSelection loaded = CloudSoloPlan.Read(stack, store.Effective);
            Assert.IsTrue(loaded.On && loaded.Cell && loaded.Layer == 2, "the preset loads the cell");
        }

        [TestMethod]
        public void TheButtonsNameATypeWithoutTheGenusItsLayerShares()
        {
            CloudLayerDef cumulus = CloudLookFixtures.Cumulus(), cb = CloudLookFixtures.Cumulonimbus(), rain = CloudLookFixtures.Rain();
            Assert.AreEqual("Humilis", CloudSoloPlan.ShortLabel(cumulus, 1));
            Assert.AreEqual("Stratocumulus", CloudSoloPlan.ShortLabel(cumulus, 0));
            Assert.AreEqual("Capillatus", CloudSoloPlan.ShortLabel(cb, 1));
            Assert.AreEqual("Incus", CloudSoloPlan.ShortLabel(cb, 2), "the anvil sheet, WMO's supplementary feature, keeps its name");
            Assert.AreEqual("Nimbostratus", CloudSoloPlan.ShortLabel(rain, 2));
        }

        [TestMethod]
        public void TheNewKeysAreInTheViewersRegistryWithTheDefaultsThatChangeNothing()
        {
            foreach (string key in CloudSoloPlan.GlobalKeys())
                Assert.IsNotNull(ReDefinition.Atmospheres.Viewer.CloudKeyRegistry.Find(key), key);
            Assert.AreEqual(0f, ReDefinition.Atmospheres.Viewer.CloudKeyRegistry.Find(CloudLookTuning.TuneSoloLayer).Default, "no solo layer by default");
            Assert.AreEqual(0f, ReDefinition.Atmospheres.Viewer.CloudKeyRegistry.Find(CloudLookTuning.TuneCellKm).Default, "no cell by default");
        }
    }
}
