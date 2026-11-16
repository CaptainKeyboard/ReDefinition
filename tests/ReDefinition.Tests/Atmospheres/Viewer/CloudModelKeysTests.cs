using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Atmospheres.Viewer;

namespace ReDefinition.Tests.Atmospheres.Viewer
{
    // CLOUDR1's keys and looks in the viewer's tables (CloudModelKeys): groups, ranges, markers, the grid lever's stops and the two looks, without the game.
    [TestClass]
    public class CloudModelKeysTests
    {
        private static CloudKey Key(string key)
        {
            CloudKey k = CloudKeyRegistry.Find(key);
            Assert.IsNotNull(k, key);
            return k;
        }

        [TestMethod]
        public void EveryKeyOfTheNewModelIsListedWithAGroupARangeAndAMarker()
        {
            string[] global =
            {
                CloudLookTuning.TuneHeap, CloudLookTuning.TuneEdgePower, CloudLookTuning.TuneCavity, CloudLookTuning.TuneHeapFootLod, CloudLookTuning.TuneHeapIso, CloudLookTuning.TuneHeapWarp,
                CloudLookTuning.TuneNoiseBlend, CloudLookTuning.TuneNoiseBlendExp, CloudLookTuning.TuneNoiseBlendCell, CloudLookTuning.TuneHeapDetail,
                CloudLookTuning.TuneFirstHit, CloudLookTuning.TuneFirstHitThin, CloudLookTuning.TuneFirstHitShell, CloudLookTuning.TuneFirstHitNear
            };
            foreach (string key in global)
            {
                CloudKey k = Key(key);
                Assert.AreNotEqual(KeySection.Other, k.Section, key);
                Assert.IsFalse(k.RangeEstimated, key + ": the range is the reader's clamp");
                Assert.IsTrue(k.Min <= k.Default && k.Default <= k.Max, key + ": the default is on the slider");
            }
            foreach (string key in new[] { CloudLookTuning.TuneFirstHit, CloudLookTuning.TuneFirstHitThin, CloudLookTuning.TuneFirstHitShell, CloudLookTuning.TuneFirstHitNear })
            {
                Assert.AreEqual(KeySection.Quality, Key(key).Section, key);
                StringAssert.Contains(Key(key).Note, "experimental");
            }
            StringAssert.Contains(Key(CloudLookTuning.TuneNoiseBlend).Note, "CPU twin only");
            Assert.AreEqual(KeyMarker.AfterVolumeRebuild, Key(CloudLookTuning.TuneHeapDetail).Marker);
            Assert.AreEqual(KeyLiveness.Volume, Key(CloudLookTuning.TuneHeapDetail).Liveness);
            Assert.AreEqual("volume", CloudKeyRegistry.MarkerShort(KeyMarker.AfterVolumeRebuild));
            Assert.AreEqual(KeyMarker.Live, Key(CloudLookTuning.TuneHeap).Marker);
            Assert.AreEqual(-1f, Key(CloudLookTuning.TuneHeap).Min);
            Assert.AreEqual((float)HeapModel.PresetCount, Key(CloudLookTuning.TuneHeap).Max);
        }

        [TestMethod]
        public void ThePerLayerKeysOfTheNewModelCoverEveryLayerWithTheReadersRange()
        {
            CloudKey heap = Key(CloudLookTuning.TuneHeapLayer + "Kerbin-cumulus");
            Assert.AreEqual(-1f, heap.Default);
            Assert.AreEqual((float)HeapModel.PresetCount, heap.Max);
            Assert.IsFalse(heap.RangeEstimated);
            Assert.AreEqual(KeySection.Edge, Key(CloudLookTuning.TuneEdgePowerLayer + "Laythe-cirrus").Section);
            Assert.AreEqual(1f, Key(CloudLookTuning.TuneCavityLayer + "x").Max);
            List<CloudKey> keys = CloudKeyRegistry.Expand(new[] { "Kerbin-cumulus" });
            Assert.AreEqual(1, keys.Count(k => k.Key == CloudLookTuning.TuneCavityLayer + "Kerbin-cumulus"));
        }

        [TestMethod]
        public void TheGridLeverIsASliderWithTheStopsOfTheLooks()
        {
            CloudKey grid = Key(OwnCloudOptions.TuneGridFine);
            Assert.AreEqual(KeySection.Quality, grid.Section);
            Assert.IsFalse(grid.RangeEstimated);
            Assert.AreEqual(0.25f, grid.Min);
            foreach (float stop in new[] { 1f, 2f, CloudModelKeys.Grid128 }) Assert.IsTrue(grid.Min <= stop && stop <= grid.Max, "stop " + stop);
            foreach (string text in new[] { "1 = 48", "2 = 96", "2.6667 = 128" }) StringAssert.Contains(grid.Note, text);
            Assert.AreEqual(128, (int)System.Math.Round(48 * CloudModelKeys.Grid128));
            Assert.AreEqual(96, (int)System.Math.Round(48 * CloudModelKeys.Grid96));
        }

        [TestMethod]
        public void TheLooksSetTheirKeysOnTheCumulusLayersAndTodayUndoesThem()
        {
            NamedKeyPreset look96 = CloudKeyRegistry.Presets.First(p => p.Name == "New Model 96 Steps");
            NamedKeyPreset look128 = CloudKeyRegistry.Presets.First(p => p.Name == "New Model 128 Steps");
            Assert.AreEqual(2f, look96.Keys[OwnCloudOptions.TuneGridFine]);
            Assert.AreEqual(CloudModelKeys.Grid128, look128.Keys[OwnCloudOptions.TuneGridFine]);
            Assert.AreEqual(0, look96.Settings.Count, "a look never touches a setting (ownClouds, cloudSource)");

            var layers = new[] { "Kerbin-weather-rain", "Kerbin-cumulus", "Kerbin-cumulonimbus", "Kerbin-cirrus" };
            OptionSet set = ViewerPresetBook.FromSlot(look96, layers);
            Assert.AreEqual(1f, set.Overrides[CloudLookTuning.TuneHeapLayer + "Kerbin-cumulus"]);
            Assert.AreEqual(0.5f, set.Overrides[CloudLookTuning.TuneEdgePowerLayer + "Kerbin-cumulus"]);
            Assert.AreEqual(0.35f, set.Overrides[CloudLookTuning.TuneCavityLayer + "Kerbin-cumulus"]);
            Assert.IsFalse(set.Overrides.ContainsKey(CloudLookTuning.TuneHeapLayer + "Kerbin-cumulonimbus"), "the cumulonimbus layer does not end with cumulus");
            Assert.AreEqual(4, set.Overrides.Count, "three layer keys and the grid");

            // The keys the viewer lists for these layers; loading a look and then Today leaves no override.
            List<string> names = CloudKeyRegistry.Expand(layers).Select(k => k.Key).ToList();
            var store = new FakeViewerRig.Store();
            set.ApplyTo(store, names);
            Assert.AreEqual(4, store.Overrides.Count);
            OptionSet today = ViewerPresetBook.BuiltIn(new[] { AtmospheresSettings.Keys.OwnClouds, AtmospheresSettings.Keys.CloudSource, AtmospheresSettings.Keys.CloudSoftness }, k => 0.5, layers)[0].Make();
            today.ApplyTo(store, names);
            Assert.AreEqual(0, store.Overrides.Count, "Today undoes every key of the new model");
            Assert.IsFalse(today.Settings.ContainsKey(AtmospheresSettings.Keys.OwnClouds));
            Assert.IsFalse(today.Settings.ContainsKey(AtmospheresSettings.Keys.CloudSource));
        }

        [TestMethod]
        public void NoCloudLooksFileRemainsAsASecondSource()
        {
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            Assert.IsFalse(System.IO.File.Exists(System.IO.Path.Combine(root, "GameData", "ReDefinition", "Atmospheres", "Clouds", "CloudLooks.cfg")));
        }
    }
}
