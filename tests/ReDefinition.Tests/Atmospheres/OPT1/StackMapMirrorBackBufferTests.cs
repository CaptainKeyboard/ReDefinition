using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.OPT1
{
    // The map round's worker writes only back buffers (StackMapMirror.CopyBack); the main thread swaps the stack's maps for them (AttachBack) and a type map the worker
    // refills is first replaced by a copy of its own (OwnTypeMaps).
    [TestClass]
    public class StackMapMirrorBackBufferTests
    {
        private static LayerStackSource Stack()
        {
            var source = new LayerStackSource(0, 600000.0, true);
            source.Add(new CloudLayerDef("rain", 0, 0f, 2100f, new[] { CloudTypeDef.Slab("s", 0f, 1f) }, 0.045f), null, null);
            return source;
        }

        private static WeatherMap Filled(float value)
        {
            var m = new WeatherMap();
            m.Resize(8, 4);
            for (int i = 0; i < m.Values.Length; i++) m.Values[i] = value;
            return m;
        }

        private static void Read(LayerStackSource stack, out float cover, out float type)
        {
            stack.SampleMaps(0, 10.0, 20.0, out cover, out type);
        }

        [TestMethod]
        public void TheWorkersCopyIsInvisibleToTheStackUntilTheSwap()
        {
            LayerStackSource stack = Stack();
            var mirror = new StackMapMirror();
            mirror.Set(stack, 0, Filled(0.25f), Filled(0.5f));
            long version = stack.Version;
            WeatherMap newCover = Filled(0.75f), newType = Filled(0.125f);
            mirror.CopyBack(0, newCover, newType);
            float cover, type;
            Read(stack, out cover, out type);
            Assert.AreEqual(0.25f, cover, 1e-6f, "the stack still reads the old coverage while the worker has copied");
            Assert.AreEqual(0.5f, type, 1e-6f);
            mirror.AttachBack(stack, 0);
            Read(stack, out cover, out type);
            Assert.AreEqual(0.75f, cover, 1e-6f);
            Assert.AreEqual(0.125f, type, 1e-6f);
            Assert.AreEqual(version, stack.Version, "a swap does not move the version");
        }

        [TestMethod]
        public void TheSourcesMayChangeAfterTheCopy()
        {
            LayerStackSource stack = Stack();
            var mirror = new StackMapMirror();
            WeatherMap c = Filled(0.4f), t = Filled(0.6f);
            mirror.CopyBack(0, c, t);
            mirror.AttachBack(stack, 0);
            for (int i = 0; i < c.Values.Length; i++) { c.Values[i] = 0f; t.Values[i] = 0f; }
            float cover, type;
            Read(stack, out cover, out type);
            Assert.AreEqual(0.4f, cover, 1e-6f);
            Assert.AreEqual(0.6f, type, 1e-6f);
        }

        [TestMethod]
        public void TheFirstAttachGivesTheStackItsMapsAndLaterRoundsNeverWriteTheMapsItReads()
        {
            LayerStackSource stack = Stack();
            var mirror = new StackMapMirror();
            long version = stack.Version;
            mirror.CopyBack(0, Filled(0.1f), Filled(0.2f));
            mirror.AttachBack(stack, 0);
            Assert.AreEqual(version + 1, stack.Version, "the first set of maps moves the version");
            long after = stack.Version;
            float cover, type;
            // Round after round: the worker's copy leaves what the stack reads alone, the swap shows it, the version stays.
            for (int round = 1; round <= 5; round++)
            {
                float c = 0.1f + 0.1f * round, t = 0.2f + 0.1f * round;
                float readC, readT;
                Read(stack, out readC, out readT);
                mirror.CopyBack(0, Filled(c), Filled(t));
                Read(stack, out cover, out type);
                Assert.AreEqual(readC, cover, 1e-6f, "round " + round + ": the copy changed the stack's coverage");
                Assert.AreEqual(readT, type, 1e-6f, "round " + round + ": the copy changed the stack's type");
                mirror.AttachBack(stack, 0);
                Read(stack, out cover, out type);
                Assert.AreEqual(c, cover, 1e-6f);
                Assert.AreEqual(t, type, 1e-6f);
            }
            Assert.AreEqual(after, stack.Version);
        }

        [TestMethod]
        public void ASharedTypeMapTheWorkerRefillsIsReplacedByACopyFirst()
        {
            LayerStackSource stack = Stack();
            var mirror = new StackMapMirror();
            WeatherMap shared = Filled(0.5f);
            mirror.Set(stack, 0, Filled(0.25f), shared);
            long version = stack.Version;
            mirror.OwnTypeMaps(stack);
            // The worker refills the shared map in place; the stack must not see it.
            for (int i = 0; i < shared.Values.Length; i++) shared.Values[i] = 0.9f;
            float cover, type;
            Read(stack, out cover, out type);
            Assert.AreEqual(0.5f, type, 1e-6f, "the type map the stack reads is its own copy");
            Assert.AreEqual(0.25f, cover, 1e-6f);
            Assert.AreEqual(version, stack.Version);
            // A second call changes nothing, and the next swap works.
            mirror.OwnTypeMaps(stack);
            mirror.CopyBack(0, Filled(0.3f), shared);
            mirror.AttachBack(stack, 0);
            Read(stack, out cover, out type);
            Assert.AreEqual(0.3f, cover, 1e-6f);
            Assert.AreEqual(0.9f, type, 1e-6f);
            Assert.AreEqual(version, stack.Version);
        }
    }
}
