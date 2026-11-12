using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.C25;

namespace ReDefinition.Tests
{
    // The height build off the main thread (K-T2 start 42: 440 to 519 ms on the main thread). The maps of the job, of the one-shot driver and of
    // the build as it was before the job (a copy of that code below) are the same bytes; the main thread's share is small even when the worker is
    // slow; a cancelled or failed build leaves nothing behind.
    [TestClass]
    public class HeightJobTests
    {
        private const int W = 512, H = 256;
        private const double R = 600000.0;
        private const double Ut = 1234567.0;
        private static readonly uint BodySeed = 99u;

        // The field with its thread safety turned off, so that the job reads it on the caller's thread in slices.
        private sealed class MainThreadField : IWeatherField
        {
            private readonly IWeatherField inner;
            internal int Calls;
            internal MainThreadField(IWeatherField inner) { this.inner = inner; }
            public bool Sample(int b, double lat, double lon, double alt, double ut, out WeatherSample s) { Calls++; return inner.Sample(b, lat, lon, alt, ut, out s); }
            public bool GetMap(int b, WeatherMapKind k, double ut, WeatherMap t) { Calls++; return inner.GetMap(b, k, ut, t); }
            public bool FillWindow(int b, double a, double c, double r, double ut, WeatherWindow w) { return inner.FillWindow(b, a, c, r, ut, w); }
            public void Advance(double ms) { inner.Advance(ms); }
            public long Version { get { return inner.Version; } }
            public long DefinitionId { get { return inner.DefinitionId; } }
            public bool ThreadSafe { get { return false; } }
            public double StepSeconds { get { return inner.StepSeconds; } }
            public FieldReadiness Readiness { get { return inner.Readiness; } }
        }

        private sealed class ThrowingField : IWeatherField
        {
            public bool Sample(int b, double lat, double lon, double alt, double ut, out WeatherSample s) { throw new InvalidOperationException("boom"); }
            public bool GetMap(int b, WeatherMapKind k, double ut, WeatherMap t) { throw new InvalidOperationException("boom"); }
            public bool FillWindow(int b, double a, double c, double r, double ut, WeatherWindow w) { return false; }
            public void Advance(double ms) { }
            public long Version { get { return 0; } }
            public long DefinitionId { get { return 0; } }
            public bool ThreadSafe { get { return true; } }
            public double StepSeconds { get { return 0.0; } }
            public FieldReadiness Readiness { get { return FieldReadiness.Ready; } }
        }

        // Four layers of the kinds a body has: two weather layers, a thin high one, and a fog layer that has no height variation.
        private static LayerStackSource Stack()
        {
            var source = new LayerStackSource(FieldFixture.BodyIndex, R, true);
            LayerKind[] kinds = { LayerKind.Weather, LayerKind.Weather, LayerKind.Cheap, LayerKind.Fog };
            // The stack is sorted by the inner altitude: the fog layer is the highest here so that layer 3 is the one without maps.
            float[] inner = { 1000f, 3500f, 6500f, 9500f };
            for (int l = 0; l < kinds.Length; l++)
                source.Add(new CloudLayerDef("l" + l, FieldFixture.BodyIndex, inner[l], inner[l] + 2000f, new[] { CloudTypeDef.Slab("s", 0f, 1f) }, 0.01f, kinds[l]), null, null);
            return source;
        }

        // The build as it was before the job: per layer a fresh reading of the three maps and the updraft, and the builder without a cache.
        private static HeightMaps[] Reference(LayerStackSource source, IWeatherField field, HeightTuning body)
        {
            var result = new HeightMaps[source.LayerCount];
            for (int l = 0; l < source.LayerCount; l++)
            {
                CloudLayerDef layer = source.LayerAt(l);
                HeightTuning t = body.ForLayer(layer.Kind);
                if (!t.Any) continue;
                uint seed = (BodySeed * 0x9E3779B1u + (uint)(l + 1) * 0x85EBCA6Bu) & 0xFFFFFFu;
                t.Seed = seed;
                double altitude = layer.InnerAltitudeM + t.UpdraftSampleShare * layer.ThicknessM;
                result[l] = HeightMapBuilder.Build(OldRead(field, source.BodyIndex, Ut, W, H, altitude), t, source.BodyRadiusM, seed);
            }
            return result;
        }

        private static HeightInputs OldRead(IWeatherField field, int bodyIndex, double ut, int width, int height, double altitude)
        {
            var input = new HeightInputs { Width = width, Height = height };
            input.Coverage = OldFetch(field, bodyIndex, WeatherMapKind.Coverage, ut, width, height);
            input.Humidity = OldFetch(field, bodyIndex, WeatherMapKind.Humidity, ut, width, height);
            input.TemperatureK = OldFetch(field, bodyIndex, WeatherMapKind.SurfaceTemperature, ut, width, height);
            var coarse = new WeatherMap();
            coarse.Resize(64, 32);
            bool any = false;
            for (int y = 0; y < 32; y++)
            {
                double lat = 90.0 - (y + 0.5) / 32 * 180.0;
                for (int x = 0; x < 64; x++)
                {
                    double lon = (x + 0.5) / 64 * 360.0 - 180.0;
                    WeatherSample s;
                    if (field.Sample(bodyIndex, lat, lon, altitude, ut, out s) && !float.IsNaN(s.WindWorld.z))
                    {
                        coarse.Values[y * 64 + x] = s.WindWorld.z;
                        any = true;
                    }
                }
            }
            if (any)
            {
                input.Updraft = new float[width * height];
                for (int y = 0; y < height; y++)
                {
                    double lat = 90.0 - (y + 0.5) / height * 180.0;
                    for (int x = 0; x < width; x++) input.Updraft[y * width + x] = coarse.Sample(lat, (x + 0.5) / width * 360.0 - 180.0);
                }
            }
            return input;
        }

        private static float[] OldFetch(IWeatherField field, int bodyIndex, WeatherMapKind kind, double ut, int width, int height)
        {
            var map = new WeatherMap();
            map.Resize(width, height);
            map.Kind = kind;
            return field.GetMap(bodyIndex, kind, ut, map) && map.Values.Length == width * height ? map.Values : null;
        }

        private static void AssertSameMaps(HeightMaps[] expected, LayerStackSource actual, string what)
        {
            for (int l = 0; l < expected.Length; l++)
            {
                HeightMaps got = actual.Heights.MapsOf(l);
                if (expected[l] == null)
                {
                    Assert.IsNull(got, what + ": layer " + l + " has no maps");
                    continue;
                }
                Assert.IsNotNull(got, what + ": layer " + l);
                CollectionAssert.AreEqual(expected[l].Bytes, got.Bytes, what + ": layer " + l + " bytes");
                Assert.AreEqual(expected[l].RootRangeM, got.RootRangeM);
                Assert.AreEqual(expected[l].TopRangeM, got.TopRangeM);
                Assert.AreEqual(expected[l].VariationRangeM, got.VariationRangeM);
            }
        }

        private static HeightResult Wait(HeightJob job, IWeatherField field, double pumpBudgetMs = 0.8)
        {
            HeightResult result;
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < 120)
            {
                job.Pump(pumpBudgetMs);
                HeightTake take = job.TryTake(out result);
                Assert.AreNotEqual(HeightTake.Failed, take, job.LastError);
                if (take == HeightTake.Ready) return result;
                Thread.Sleep(1);
            }
            Assert.Fail("the build did not finish");
            return null;
        }

        [TestMethod]
        public void TheMapsOfTheOneShotDriverAndOfTheJobAreTheBytesOfTheBuildBeforeTheJob()
        {
            GeneratorField field = FieldFixture.Field();
            var body = new HeightTuning();
            LayerStackSource reference = Stack();
            HeightMaps[] expected = Reference(reference, field, body);
            Assert.IsNotNull(expected[0]);
            Assert.IsNull(expected[3], "the fog layer has no height variation");

            LayerStackSource byDriver = Stack();
            HeightDriver.Update(byDriver, field, body, Ut, BodySeed, W, H);
            AssertSameMaps(expected, byDriver, "driver");

            // On the worker, the field thread safe: the job reads it there.
            LayerStackSource viaJob = Stack();
            var job = new HeightJob { Cache = new HeightNoiseCache(16) };
            Assert.IsTrue(job.Begin(HeightPlan.Make(viaJob, body, Ut, BodySeed, W, H), field, viaJob));
            Wait(job, field).ApplyTo(viaJob);
            AssertSameMaps(expected, viaJob, "job, worker reads");

            // The field not thread safe: the main thread reads it in slices, the worker builds.
            LayerStackSource sliced = Stack();
            var mainField = new MainThreadField(field);
            Assert.IsTrue(job.Begin(HeightPlan.Make(sliced, body, Ut, BodySeed, W, H), mainField, sliced));
            Wait(job, mainField).ApplyTo(sliced);
            AssertSameMaps(expected, sliced, "job, main thread reads");
            Assert.IsTrue(mainField.Calls > 0);
        }

        [TestMethod]
        public void TheGpuBytesAreTheTexelsOfTheMapsWithAnOpaqueAlpha()
        {
            LayerStackSource s = Stack();
            HeightDriver.Update(s, FieldFixture.Field(), new HeightTuning(), Ut, BodySeed, W, H);
            HeightMaps m = s.Heights.MapsOf(0);
            byte[] rgba = m.MakeRgba();
            Assert.AreSame(rgba, m.MakeRgba());
            Assert.AreEqual(W * H * 4, rgba.Length);
            for (int i = 0; i < W * H; i += 97)
            {
                Assert.AreEqual(m.Bytes[i * 3], rgba[i * 4]);
                Assert.AreEqual(m.Bytes[i * 3 + 1], rgba[i * 4 + 1]);
                Assert.AreEqual(m.Bytes[i * 3 + 2], rgba[i * 4 + 2]);
                Assert.AreEqual(255, rgba[i * 4 + 3]);
            }
        }

        [TestMethod]
        public void TheNoiseIsMadeOncePerSeedAndTheSecondBuildReadsItFromTheCache()
        {
            GeneratorField field = FieldFixture.Field();
            var cache = new HeightNoiseCache(16);
            var job = new HeightJob { Cache = cache };
            var body = new HeightTuning();
            for (int round = 0; round < 2; round++)
            {
                LayerStackSource s = Stack();
                job.Begin(HeightPlan.Make(s, body, Ut + round * 60.0, BodySeed, W, H), field, s);
                Wait(job, field).ApplyTo(s);
                // Three layers vary (the fog layer does not): the free variation of each, and the top jitter of the two that take an updraft.
                Assert.AreEqual(5, cache.Made, "round " + round);
            }
            Assert.AreEqual(5, cache.Count);
        }

        [TestMethod]
        public void TheCacheKeepsAtMostItsCapacityAndTheOldestGoesFirst()
        {
            var cache = new HeightNoiseCache(2);
            float[] a = cache.Get(16, 8, R, 12000.0, 1u);
            cache.Get(16, 8, R, 12000.0, 2u);
            Assert.AreSame(a, cache.Get(16, 8, R, 12000.0, 1u));
            cache.Get(16, 8, R, 12000.0, 3u);
            Assert.AreEqual(2, cache.Count);
            Assert.AreNotSame(a, cache.Get(16, 8, R, 12000.0, 1u), "seed 1 was the oldest and was dropped");
        }

        [TestMethod]
        public void WhileTheWorkerIsSlowTheMainThreadOnlyPaysForStartingAndPolling()
        {
            GeneratorField field = FieldFixture.Field();
            LayerStackSource s = Stack();
            var release = new ManualResetEvent(false);
            var job = new HeightJob { Cache = new HeightNoiseCache(16) };
            // A worker that takes 3 s before it starts (a slow machine, a busy pool).
            job.Schedule = work => new Thread(() => { release.WaitOne(); Thread.Sleep(3000); work(); }) { IsBackground = true }.Start();
            var watch = Stopwatch.StartNew();
            Assert.IsTrue(job.Begin(HeightPlan.Make(s, new HeightTuning(), Ut, BodySeed, W, H), field, s));
            double worst = watch.Elapsed.TotalMilliseconds;
            HeightResult result;
            int polls = 0;
            release.Set();
            watch.Restart();
            while (watch.ElapsedMilliseconds < 400)
            {
                long t0 = Stopwatch.GetTimestamp();
                job.Pump(0.8);
                HeightTake take = job.TryTake(out result);
                worst = Math.Max(worst, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                Assert.AreEqual(HeightTake.None, take, "the worker is still sleeping");
                polls++;
                Thread.Sleep(1);
            }
            Assert.IsTrue(worst < 25.0, "the worst main-thread call was " + worst.ToString("0.0") + " ms");
            Assert.IsTrue(job.Busy);
            Wait(job, field).ApplyTo(s);
            Assert.IsFalse(job.Busy);
            Assert.IsNotNull(s.Heights.MapsOf(0));
        }

        [TestMethod]
        public void AFieldThatIsNotThreadSafeIsReadInSlicesWithinTheBudget()
        {
            GeneratorField real = FieldFixture.Field();
            // The test field renders its maps inline on the first read (the game's worker does that elsewhere): read once before the budget is measured.
            real.GetMap(FieldFixture.BodyIndex, WeatherMapKind.Coverage, Ut, new WeatherMap());
            var field = new MainThreadField(real);
            LayerStackSource s = Stack();
            var job = new HeightJob { Cache = new HeightNoiseCache(16) };
            var queued = new List<Action>();
            job.Schedule = queued.Add;
            job.Begin(HeightPlan.Make(s, new HeightTuning(), Ut, BodySeed, W, H), field, s);
            int pumps = 0;
            double worst = 0.0;
            while (queued.Count == 0 && pumps < 100000)
            {
                long t0 = Stopwatch.GetTimestamp();
                job.Pump(0.5);
                worst = Math.Max(worst, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                pumps++;
            }
            Assert.AreEqual(1, queued.Count, "the build is handed to the worker once the reading is complete");
            Assert.IsTrue(pumps > 5, "the reading took " + pumps + " pumps: it was sliced");
            Assert.IsTrue(worst < 20.0, "the worst pump took " + worst.ToString("0.0") + " ms for a budget of 0.5 ms");
            queued[0]();
            HeightResult result;
            Assert.AreEqual(HeightTake.Ready, job.TryTake(out result));
            Assert.AreEqual(0, queued.Count - 1);
        }

        [TestMethod]
        public void ACancelledBuildIsNeverHandedOutAndTheJobIsFreeAtOnce()
        {
            GeneratorField field = FieldFixture.Field();
            LayerStackSource s = Stack();
            var queued = new List<Action>();
            var job = new HeightJob { Cache = new HeightNoiseCache(16), Schedule = queued.Add };
            Assert.IsTrue(job.Begin(HeightPlan.Make(s, new HeightTuning(), Ut, BodySeed, W, H), field, s));
            Assert.IsFalse(job.Begin(HeightPlan.Make(s, new HeightTuning(), Ut, BodySeed, W, H), field, s), "one build at a time");
            job.Cancel();
            Assert.IsFalse(job.Busy);
            // The next build starts at once, and the first one's worker, running late, leaves nothing.
            Assert.IsTrue(job.Begin(HeightPlan.Make(s, new HeightTuning(), Ut, BodySeed, W, H), field, s));
            Assert.AreEqual(2, queued.Count);
            queued[0]();
            HeightResult result;
            Assert.AreEqual(HeightTake.None, job.TryTake(out result), "the dropped build's worker did not publish");
            Assert.IsNull(result);
            queued[1]();
            Assert.AreEqual(HeightTake.Ready, job.TryTake(out result));
            Assert.IsNotNull(result.Maps[0]);
            Assert.AreEqual(1, job.Finished);
            Assert.AreEqual(1, job.Dropped);
        }

        [TestMethod]
        public void ABuildThatIsCancelledWhileItRunsStopsBetweenLayersAndPublishesNothing()
        {
            GeneratorField field = FieldFixture.Field();
            LayerStackSource s = Stack();
            var queued = new List<Action>();
            var job = new HeightJob { Cache = new HeightNoiseCache(16), Schedule = queued.Add };
            job.Begin(HeightPlan.Make(s, new HeightTuning(), Ut, BodySeed, W, H), field, s);
            job.Cancel();
            var watch = Stopwatch.StartNew();
            queued[0]();
            Assert.IsTrue(watch.ElapsedMilliseconds < 2000);
            // A build already running stops between two layers once it is cancelled.
            var plan = HeightPlan.Make(s, new HeightTuning(), Ut, BodySeed, W, H);
            HeightGather gather = plan.Gather(field);
            gather.Step(0.0);
            int asked = 0;
            Assert.IsNull(plan.Build(gather, new HeightNoiseCache(16), () => ++asked > 1));
            Assert.AreEqual(2, asked, "the second layer was not built");
            HeightResult r;
            Assert.AreEqual(HeightTake.None, job.TryTake(out r));
            Assert.IsNull(s.Heights.MapsOf(0), "nothing was applied");
        }

        [TestMethod]
        public void AResultIsAppliedOnlyToTheStackItWasPlannedFor()
        {
            GeneratorField field = FieldFixture.Field();
            LayerStackSource planned = Stack(), replaced = Stack();
            var job = new HeightJob { Cache = new HeightNoiseCache(16) };
            job.Begin(HeightPlan.Make(planned, new HeightTuning(), Ut, BodySeed, W, H), field, planned);
            HeightResult result = Wait(job, field);
            Assert.AreSame(planned, result.Source);
            Assert.AreNotSame(replaced, result.Source);
            Assert.IsNull(replaced.Heights.MapsOf(0));
        }

        [TestMethod]
        public void AFailureOnTheWorkerIsReportedAndFreesTheJob()
        {
            LayerStackSource s = Stack();
            var job = new HeightJob { Cache = new HeightNoiseCache(16) };
            Assert.IsTrue(job.Begin(HeightPlan.Make(s, new HeightTuning(), Ut, BodySeed, W, H), new ThrowingField(), s));
            HeightResult r = null;
            HeightTake take = HeightTake.None;
            var watch = Stopwatch.StartNew();
            while (take == HeightTake.None && watch.Elapsed.TotalSeconds < 30)
            {
                take = job.TryTake(out r);
                Thread.Sleep(1);
            }
            Assert.AreEqual(HeightTake.Failed, take);
            StringAssert.Contains(job.LastError, "boom");
            Assert.IsFalse(job.Busy);
            Assert.IsNull(s.Heights.MapsOf(0));
        }

        [TestMethod]
        public void WithoutAFieldTheMapsAreTheNeutralOnesOfTheOldDriver()
        {
            LayerStackSource a = Stack(), b = Stack();
            var body = new HeightTuning();
            HeightDriver.Update(a, null, body, Ut, BodySeed, W, H);
            HeightMaps[] expected = new HeightMaps[a.LayerCount];
            for (int l = 0; l < a.LayerCount; l++)
            {
                HeightTuning t = body.ForLayer(a.LayerAt(l).Kind);
                if (!t.Any) continue;
                uint seed = (BodySeed * 0x9E3779B1u + (uint)(l + 1) * 0x85EBCA6Bu) & 0xFFFFFFu;
                t.Seed = seed;
                expected[l] = HeightMapBuilder.Build(new HeightInputs { Width = W, Height = H }, t, R, seed);
            }
            AssertSameMaps(expected, a, "driver, no field");
        }

        [TestMethod]
        public void PollingAnIdleJobAllocatesNothing()
        {
            var job = new HeightJob();
            HeightResult r;
            job.Pump(0.8);
            job.TryTake(out r);
            AppDomain.MonitoringIsEnabled = true;
            long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            for (int i = 0; i < 10000; i++)
            {
                job.Pump(0.8);
                job.TryTake(out r);
            }
            long grown = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before;
            Assert.IsTrue(grown < 4096, "allocated " + grown + " bytes");
        }
    }
}
