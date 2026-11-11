using System;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Atmospheres.Audio;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.D33
{
    // The callback of the stream clip: finite and bounded output, the output stage, the cost of a block and ten minutes without a collection.
    [TestClass]
    public class StreamTests
    {
        private const int Rate = 48000;

        private static WeatherAudioStream Make(out SharedAudioState shared, uint seed = 5u)
        {
            shared = new SharedAudioState();
            return new WeatherAudioStream(shared, new Mixer(Rate, seed), Rate);
        }

        private static void Blizzard(SharedAudioState shared)
        {
            var w = new WeatherAudioInputs { Air = true, Type = PrecipType.Snow, Level01 = 0.95f, MeanWindMs = 22f, SnowCover01 = 0.6f, CellCloseness = 0.8f, CellPan = 0.3f };
            AudioSwitches sw = AudioSwitches.Defaults();
            sw.DistantStorm = true;
            AudioParamsMapper.Apply(w, new ListenerState(), sw, GameVolumes.Full(), EveSoundPolicyTable.Decide(EveSoundPolicy.Auto, false), 1f, ref shared.Params);
        }

        [TestMethod]
        public void TheCallbackIsDeterministicFiniteAndBounded()
        {
            SharedAudioState a, b;
            WeatherAudioStream sa = Make(out a), sb = Make(out b);
            Blizzard(a);
            Blizzard(b);
            var ba = new float[2048];
            var bb = new float[2048];
            for (int i = 0; i < 200; i++)
            {
                sa.Fill(ba, 2);
                sb.Fill(bb, 2);
                for (int k = 0; k < ba.Length; k++)
                {
                    Assert.AreEqual(ba[k], bb[k]);
                    Assert.IsTrue(!float.IsNaN(ba[k]) && Math.Abs(ba[k]) <= Mixer.Ceiling + 1e-3f);
                }
            }
        }

        [TestMethod]
        public void TheBufferSizeDoesNotChangeTheSound()
        {
            SharedAudioState a, b;
            WeatherAudioStream sa = Make(out a), sb = Make(out b);
            Blizzard(a);
            Blizzard(b);
            var one = new float[2 * 4096];
            var two = new float[2 * 4096];
            sa.Fill(one, 2);
            var half = new float[2 * 1024];
            for (int i = 0; i < 4; i++)
            {
                sb.Fill(half, 2);
                Array.Copy(half, 0, two, i * half.Length, half.Length);
            }
            for (int k = 0; k < one.Length; k++) Assert.AreEqual(one[k], two[k], 1e-6f);
        }

        [TestMethod]
        public void TheOutputStageLowersTheMapViewAndTheHelmet()
        {
            double Rms(float mapBlend, float helmetBlend)
            {
                SharedAudioState s;
                WeatherAudioStream st = Make(out s);
                Blizzard(s);
                s.MapBlend = mapBlend;
                s.HelmetBlend = helmetBlend;
                var buf = new float[2048];
                double sum = 0;
                long n = 0;
                for (int i = 0; i < 400; i++)
                {
                    st.Fill(buf, 2);
                    if (i < 150) continue;
                    for (int k = 0; k < buf.Length; k++) { sum += buf[k] * buf[k]; n++; }
                }
                return 10 * Math.Log10(sum / n);
            }
            double open = Rms(0, 0);
            double helmet = Rms(0, 1);
            double map = Rms(1, 0);
            Assert.IsTrue(open - helmet > 1.0 && open - helmet < 6.0, "helmet " + (open - helmet));
            Assert.IsTrue(open - map > 10.0 && open - map < 20.0, "map " + (open - map));
        }

        [TestMethod]
        public void AZeroVolumeIsSilenceAndTheCountersRun()
        {
            SharedAudioState s;
            WeatherAudioStream st = Make(out s);
            Blizzard(s);
            s.Params.volume = 0f;
            var buf = new float[2048];
            for (int i = 0; i < 200; i++) st.Fill(buf, 2);
            double peak = 0;
            for (int i = 0; i < 20; i++)
            {
                st.Fill(buf, 2);
                foreach (float v in buf) peak = Math.Max(peak, Math.Abs(v));
            }
            Assert.IsTrue(peak < 1e-4, "peak " + peak);
            Assert.AreEqual(220, s.Blocks);
            Assert.AreEqual(1024, s.LastFrames);
            Assert.IsTrue(s.Running);
        }

        [TestMethod]
        public void OneBlockCostsWithinTheCpuLimitOfThisBuildOnAverage()
        {
            SharedAudioState s;
            WeatherAudioStream st = Make(out s);
            Blizzard(s);
            s.MapBlend = 1f;
            s.HelmetBlend = 1f;
            var buf = new float[2048];
            for (int i = 0; i < 300; i++) st.Fill(buf, 2);
            // Reset the counters after the warm-up.
            s.Blocks = 0;
            s.TicksTotal = 0;
            s.Underruns = 0;
            for (int i = 0; i < 2000; i++) st.Fill(buf, 2);
            double blockMs = WeatherAudioStream.MeanBlockMs(s);
            double durationMs = 1000.0 * 1024 / Rate;
            Console.WriteLine("block " + blockMs.ToString("F4") + " ms of " + durationMs.ToString("F2") + " ms: " + (100 * blockMs / durationMs).ToString("F2") + " % (every layer, map and helmet stage on)");
            // The usual case: the output stage idle.
            s.MapBlend = 0f;
            s.HelmetBlend = 0f;
            for (int i = 0; i < 300; i++) st.Fill(buf, 2);
            s.Blocks = 0;
            s.TicksTotal = 0;
            for (int i = 0; i < 2000; i++) st.Fill(buf, 2);
            Console.WriteLine("block " + WeatherAudioStream.MeanBlockMs(s).ToString("F4") + " ms: " + (100 * WeatherAudioStream.MeanBlockMs(s) / durationMs).ToString("F2") + " % (output stage idle)");
            Assert.AreEqual(0, s.Underruns);
            // The limit of the build that runs (CpuGate): 1 % of one core for Release, the plan's fail line of 5 % for Debug. This is
            // the .NET Framework CLR; the game runs Mono, where the series row D33.cpu_budget_used measures the same thing.
            double pct = 100 * blockMs / durationMs;
            Assert.IsTrue(CpuGate.BudgetUsed(pct, CpuGate.IsReleaseBuild) <= 1.0,
                "the worst layer set takes " + pct + " % of one core, the limit is " + CpuGate.LimitPct(CpuGate.IsReleaseBuild) + " %");
        }

        [TestMethod]
        public void TenMinutesOfAudioMakeNoCollection()
        {
            SharedAudioState s;
            WeatherAudioStream st = Make(out s);
            Blizzard(s);
            var buf = new float[2048];
            for (int i = 0; i < 200; i++) st.Fill(buf, 2);
            var rng = new Random(3);
            int gen0 = GC.CollectionCount(0);
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            long blocks = 10L * 60 * Rate / 1024;
            for (long i = 0; i < blocks; i++)
            {
                // The main thread moves the parameters as in play.
                if ((i & 63) == 0)
                {
                    s.Params.windSpeed = 10f + (float)rng.NextDouble() * 20f;
                    s.Params.gust = (float)Math.Sin(i * 0.01);
                    s.Params.interior = (i & 1024) == 0 ? 0f : 1f;
                    s.MapBlend = (i & 2048) == 0 ? 0f : 1f;
                }
                st.Fill(buf, 2);
            }
            // Read before any assertion: the assertions allocate themselves.
            long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
            int collections = GC.CollectionCount(0) - gen0;
            Console.WriteLine("METRIC soak 10 min stream: gen0 collections " + collections + ", bytes allocated " + allocated);
            Assert.AreEqual(0L, allocated, "bytes allocated by the callback");
            Assert.AreEqual(0, collections, "a collection ran during ten minutes of audio");
        }
    }
}
