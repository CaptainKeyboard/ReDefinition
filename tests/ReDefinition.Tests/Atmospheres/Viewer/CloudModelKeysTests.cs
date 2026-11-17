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
            Assert.AreEqual(KeySection.Light, Key(CloudLookTuning.TuneLightErosion).Section);
            Assert.AreEqual(KeyMarker.Live, Key(CloudLookTuning.TuneLightErosion).Marker);
            Assert.AreEqual(KeyLiveness.FrameHistory, Key(CloudLookTuning.TuneLightErosion).Liveness);
            Assert.AreEqual(8f, Key(CloudLookTuning.TuneLightErosion).Max);
            Assert.AreEqual(KeySection.Quality, Key(OwnCloudOptions.TuneCatmull).Section);
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
            Assert.AreEqual(1f, grid.Default);
            foreach (float stop in new[] { 0.5f, 1f, 4f / 3f }) Assert.IsTrue(grid.Min <= stop && stop <= grid.Max, "stop " + stop);
            foreach (string text in new[] { "0.5 = 48", "1.3333 = 128", "96 at Medium" }) StringAssert.Contains(grid.Note, text);
        }

        [TestMethod]
        public void TheNewModelIsOnByDefaultAndTheLooksUndoIt()
        {
            Assert.AreEqual(1f, Key(CloudLookTuning.TuneNoiseBlend).Default, "the blend hides the tiling by default");
            Assert.AreEqual(CloudLookTuning.LightErosionDefault, Key(CloudLookTuning.TuneLightErosion).Default);
            Assert.IsTrue(CloudLookTuning.LightErosionTaps >= 1f, "the first sun taps read the eroded density by default");
            Assert.IsTrue(CloudLookTuning.NoiseBlend);
            Assert.AreEqual(1f, Key(OwnCloudOptions.TuneCatmull).Default);
            Assert.AreEqual(0f, Key(CloudLookTuning.TuneFirstHit).Default, "the first-hit bisection is a measured dead end and stays off");
            Assert.AreEqual(256, (int)AtmospheresSettings.Definition(AtmospheresSettings.Keys.CloudSamples).Default);
        }

        [TestMethod]
        public void TheLooksSetTheirKeysOnTheCumulusLayersAndTodayUndoesThem()
        {
            NamedKeyPreset before = CloudKeyRegistry.Presets.First(p => p.Name == CloudModelKeys.LookBefore);
            NamedKeyPreset look96 = CloudKeyRegistry.Presets.First(p => p.Name == "96 Steps");
            NamedKeyPreset look128 = CloudKeyRegistry.Presets.First(p => p.Name == "128 Steps");
            Assert.AreEqual(0f, before.Keys[CloudLookTuning.TuneNoiseBlend]);
            Assert.AreEqual(0f, before.Keys[CloudLookTuning.TuneLightErosion]);
            Assert.AreEqual(0f, before.Keys[OwnCloudOptions.TuneCatmull]);
            Assert.AreEqual(48.0, before.Settings[AtmospheresSettings.Keys.CloudSamples]);
            Assert.AreEqual(96.0, look96.Settings[AtmospheresSettings.Keys.CloudSamples]);
            Assert.AreEqual(128.0, look128.Settings[AtmospheresSettings.Keys.CloudSamples]);
            Assert.IsFalse(before.Settings.ContainsKey(AtmospheresSettings.Keys.OwnClouds), "a look never touches ownClouds or cloudSource");
            Assert.IsFalse(before.Settings.ContainsKey(AtmospheresSettings.Keys.CloudSource));

            var layers = new[] { "Kerbin-weather-rain", "Kerbin-cumulus", "Kerbin-cumulonimbus", "Kerbin-cirrus" };
            OptionSet set = ViewerPresetBook.FromSlot(before, layers);
            Assert.AreEqual(0f, set.Overrides[CloudLookTuning.TuneHeapLayer + "Kerbin-cumulus"]);
            Assert.AreEqual(2f, set.Overrides[CloudLookTuning.TuneEdgePowerLayer + "Kerbin-cumulus"]);
            Assert.AreEqual(0f, set.Overrides[CloudLookTuning.TuneCavityLayer + "Kerbin-cumulus"]);
            Assert.IsFalse(set.Overrides.ContainsKey(CloudLookTuning.TuneHeapLayer + "Kerbin-cumulonimbus"), "the cumulonimbus layer does not end with cumulus");
            Assert.AreEqual(0f, set.Overrides[CloudLookTuning.TuneCells], "the look before has no single clouds");
            Assert.AreEqual(7, set.Overrides.Count, "three layer keys and the blend, the erosion, the Catmull and the cells key");

            // The keys the viewer lists for these layers; loading a look and then Today leaves no override.
            List<string> names = CloudKeyRegistry.Expand(layers).Select(k => k.Key).ToList();
            var store = new FakeViewerRig.Store();
            set.ApplyTo(store, names);
            Assert.AreEqual(7, store.Overrides.Count);
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
