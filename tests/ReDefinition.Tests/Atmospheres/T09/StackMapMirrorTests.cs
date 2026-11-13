using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.C25;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // K-T2d start 48: layer<l>_map_cover read 1.0 and layer<l>_map_type 0.0 in every layer at both covers. The stack the own cloud pass draws from is built without maps
    // (CloudBridgeStack.Build), the GPU maps come from the module's fetch, and nothing handed the stack a copy, so every CPU reader (the probe, the sun dimming, the shadow
    // field) saw a fully covered sky of the first type. StackMapMirror hands the stack the fetched maps.
    [TestClass]
    public class StackMapMirrorTests
    {
        private const int Body = FieldFixture.BodyIndex;
        private const double Ut = 3.0e6;

        private static LayerStackSource Stack()
        {
            var source = new LayerStackSource(Body, 600000.0, true);
            // Heights of the rain deck as the Kerbin file has them (a deck of 2100 m from 0 m).
            source.Add(new CloudLayerDef("rain", 0, 0f, 2100f, new[] { CloudTypeDef.Slab("s", 0f, 1f) }, 0.045f), null, null);
            return source;
        }

        [TestMethod]
        public void AStackWithoutMapsReadsFullCoverAndTheFirstTypeWhateverTheFieldHolds()
        {
            GeneratorField field = FieldFixture.Field();
            var layer = new WeatherMap();
            Assert.IsTrue(field.GetLayerCoverage(Body, Ut, 1050.0, layer));
            // The field's layer map is not full everywhere ...
            float min = 1f;
            foreach (float v in layer.Values) min = Math.Min(min, v);
            Assert.IsTrue(min < 0.5f, "the layer map of the rain deck has gaps: min " + min);
            // ... and the stack, never given it, reads 1.0 and type 0 at every place: the K-T2d start 48 rows.
            LayerStackSource stack = Stack();
            for (int lat = -60; lat <= 60; lat += 30)
                for (int lon = -180; lon < 180; lon += 60)
                {
                    float cover, type;
                    stack.SampleMaps(0, lat, lon, out cover, out type);
                    Assert.AreEqual(1f, cover);
                    Assert.AreEqual(0f, type);
                }
        }

        [TestMethod]
        public void TheMirrorGivesTheStackTheCoverageOfTheLayerAndTheTypeTheGpuReads()
        {
            GeneratorField field = FieldFixture.Field();
            var layer = new WeatherMap();
            var shared = new WeatherMap();
            var type = new WeatherMap();
            Assert.IsTrue(field.GetLayerCoverage(Body, Ut, 1050.0, layer));
            Assert.IsTrue(field.GetMap(Body, WeatherMapKind.Coverage, Ut, shared));
            Assert.IsTrue(field.GetMap(Body, WeatherMapKind.Precipitation, Ut, type));
            LayerStackSource stack = Stack();
            var mirror = new StackMapMirror();
            long version = stack.Version;
            mirror.Set(stack, 0, layer, type);
            Assert.AreEqual(version + 1, stack.Version, "the first set attaches the maps");
            int differing = 0, below = 0;
            for (int lat = -75; lat <= 75; lat += 15)
                for (int lon = -180; lon < 180; lon += 20)
                {
                    float cover, t;
                    stack.SampleMaps(0, lat, lon, out cover, out t);
                    Assert.AreEqual(layer.Sample(lat, lon), cover, 1e-6f);
                    Assert.AreEqual(type.Sample(lat, lon), t, 1e-6f);
                    if (cover < 0.95f) below++;
                    // The cover of the sky at 3 km (what the series picks) is a different quantity from the map of the deck at its mid height.
                    if (Math.Abs(shared.Sample(lat, lon) - cover) > 0.05f) differing++;
                }
            Assert.IsTrue(below > 10, "places where the deck is not full: " + below);
            Assert.IsTrue(differing > 10, "the deck's map and the shared cover differ at many places: " + differing);
        }

        [TestMethod]
        public void ALaterFetchRefreshesTheCopyInPlaceWithoutMovingTheStacksVersion()
        {
            var first = new WeatherMap();
            first.Resize(8, 4);
            for (int i = 0; i < first.Values.Length; i++) first.Values[i] = 0.25f;
            var type = new WeatherMap();
            type.Resize(8, 4);
            LayerStackSource stack = Stack();
            var mirror = new StackMapMirror();
            mirror.Set(stack, 0, first, type);
            long version = stack.Version;
            for (int i = 0; i < first.Values.Length; i++) first.Values[i] = 0.75f;
            mirror.Set(stack, 0, first, type);
            float cover, t;
            stack.SampleMaps(0, 10.0, 20.0, out cover, out t);
            Assert.AreEqual(0.75f, cover, 1e-6f);
            Assert.AreEqual(version, stack.Version, "refilling does not make the pass upload its frame values again");
            // The source map may change under the mirror (the module refills it): the copy keeps what it was given.
            for (int i = 0; i < first.Values.Length; i++) first.Values[i] = 0f;
            stack.SampleMaps(0, 10.0, 20.0, out cover, out t);
            Assert.AreEqual(0.75f, cover, 1e-6f);
            // A new stack gets the maps again.
            LayerStackSource other = Stack();
            mirror.Set(other, 0, first, type);
            other.SampleMaps(0, 10.0, 20.0, out cover, out t);
            Assert.AreEqual(0f, cover, 1e-6f);
            // A layer the stack does not have is ignored.
            mirror.Set(other, 3, first, type);
        }
    }
}
