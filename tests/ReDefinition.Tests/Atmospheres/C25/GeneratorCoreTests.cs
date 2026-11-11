using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using AtmoSim.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ReDefinition.Tests.Atmospheres.C25
{
    // The generator core inside the game's assembly against the values recorded from the offline proof's runtime (tests/data/generator, made
    // by tools/sync_generator_core.py). The assembly compiles the source of tools/atmosphere-sim/AtmoSim.Core as it is; these tests say that it
    // gives the proof's numbers on this runtime too, bit for bit, and that it is deterministic across threads, order and instances.
    [TestClass]
    public class GeneratorCoreTests
    {
        private static string golden;

        private static string Golden
        {
            get
            {
                if (golden == null) golden = File.ReadAllText(RepoRoot.Locate("tests", "data", "generator", "golden.json"));
                return golden;
            }
        }

        private static readonly double[] Uts = { 0.0, 1.0e6, 7.5e6, 2.0e7, 3.1e7 };

        [TestMethod]
        public void TheGoldenValuesAreRecordedFromTheSourceThatIsCompiled()
        {
            string folder = RepoRoot.Locate("tools", "atmosphere-sim", "AtmoSim.Core");
            var files = new List<string>();
            foreach (string f in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories))
            {
                string rel = f.Substring(folder.Length + 1).Replace('\\', '/');
                if (rel.StartsWith("obj/") || rel.StartsWith("bin/")) continue;
                files.Add(rel);
            }
            files.Sort(StringComparer.Ordinal);
            var all = new StringBuilder();
            foreach (string rel in files) all.Append(rel).Append('\n').Append(File.ReadAllText(Path.Combine(folder, rel)).Replace("\r\n", "\n")).Append('\n');
            string hash = BitConverter.ToString(SHA256.Create().ComputeHash(new UTF8Encoding(false).GetBytes(all.ToString()))).Replace("-", "");
            Assert.AreEqual(DumpReader.Text(Golden, "sourceHash"), hash, "the core changed since the golden values were recorded: run tools/sync_generator_core.py");
        }

        [TestMethod]
        public void TheCsprojCompilesEveryFileOfTheCoreByAPattern()
        {
            string csproj = File.ReadAllText(RepoRoot.Locate("src", "Atmospheres", "ReDefinition.Atmospheres.csproj"));
            StringAssert.Contains(csproj, @"tools\atmosphere-sim\AtmoSim.Core\**\*.cs");
            // The assembly holds the core's types.
            Assert.AreEqual("ReDefinition.Atmospheres", typeof(WeatherGenerator).Assembly.GetName().Name);
            Assert.AreEqual("ReDefinition.Atmospheres", typeof(SurfaceSite).Assembly.GetName().Name);
        }

        [TestMethod]
        public void TheProofsTwentyThousandQueriesGiveTheProofsHash()
        {
            byte[] bin = File.ReadAllBytes(RepoRoot.Locate("tests", "data", "generator", "proof-lat.bin"));
            CoreProbe.Q[] q = CoreProbe.Queries(20000, 77, 3.0e7);
            for (int i = 0; i < q.Length; i++) q[i].Lat = BitConverter.Int64BitsToDouble(BitConverter.ToInt64(bin, 8 * i));
            ulong h = CoreProbe.HashQueries(CoreProbe.ProofGenerator(1), q, null, 0);
            Console.WriteLine("golden hash reproduced under " + Environment.Version + ": " + h.ToString("X16"));
            Assert.AreEqual(DumpReader.Text(Golden, "proofHash"), h.ToString("X16"));
        }

        [TestMethod]
        public void TheRoundOneGeneratorStillGivesItsHashAndTheSignedOneItsOwn()
        {
            byte[] bin = File.ReadAllBytes(RepoRoot.Locate("tests", "data", "generator", "proof-lat.bin"));
            CoreProbe.Q[] q = CoreProbe.Queries(20000, 77, 3.0e7);
            for (int i = 0; i < q.Length; i++) q[i].Lat = BitConverter.Int64BitsToDouble(BitConverter.ToInt64(bin, 8 * i));
            ulong round1 = CoreProbe.HashQueries(new WeatherGenerator(BodyParams.KerbinLike().Round1(), 1), q, null, 0);
            Assert.AreEqual("3A3A34F462C55CAC", round1.ToString("X16"), "round 1");
            Assert.AreEqual(DumpReader.Text(Golden, "proofHashRound1"), round1.ToString("X16"));
            ulong signed = CoreProbe.HashQueries(new WeatherGenerator(BodyParams.KerbinLike().Signed(), 1), q, null, 0);
            Assert.AreEqual("237126757692E6D9", signed.ToString("X16"), "signed");
        }

        [TestMethod]
        public void TheSameHoldsForPositionsMadeWithoutThePlatformsAsin()
        {
            CoreProbe.Q[] q = CoreProbe.Queries(20000, 77, 3.0e7);
            var r = new Rng(77);
            for (int i = 0; i < q.Length; i++)
            {
                q[i].Lat = DMath.Asin(2.0 * r.Uniform() - 1.0);
                r.Uniform();
                r.Uniform();
                r.Uniform();
            }
            ulong h = CoreProbe.HashQueries(CoreProbe.ProofGenerator(1), q, null, 0);
            Assert.AreEqual(DumpReader.Text(Golden, "proofHashPortable"), h.ToString("X16"));
        }

        [TestMethod]
        public void TheMapHashIsTheProofs()
        {
            WeatherGenerator g = CoreProbe.ProofGenerator(1);
            ulong h = 0xCBF29CE484222325UL;
            Weather w;
            for (int j = 0; j < 64; j++)
            {
                for (int i = 0; i < 128; i++)
                {
                    g.Sample((90.0 - (j + 0.5) * 180.0 / 64) * Math.PI / 180.0, (-180.0 + (i + 0.5) * 360.0 / 128) * Math.PI / 180.0, 0.0, 4.0e6, out w);
                    h = WeatherGenerator.HashOf(ref w, h);
                }
            }
            Assert.AreEqual(DumpReader.Text(Golden, "mapHash"), h.ToString("X16"));
        }

        [TestMethod]
        public void ThreeSeedsAtTwentyPointsAndFiveTimesEqualTheRecordedValuesBitForBit()
        {
            var pr = new Rng(5);
            var lat = new double[20]; var lon = new double[20]; var alt = new double[20];
            for (int i = 0; i < 20; i++)
            {
                lat[i] = DMath.Asin(2.0 * pr.Uniform() - 1.0);
                lon[i] = (2.0 * pr.Uniform() - 1.0) * Math.PI;
                alt[i] = pr.Uniform() * 9000.0;
            }
            int compared = 0;
            for (int seed = 1; seed <= 3; seed++)
            {
                string[] expected = DumpReader.Strings(Golden, "values_seed" + seed);
                Assert.AreEqual(5 * 20 * 9, expected.Length);
                var g = new WeatherGenerator(BodyParams.KerbinLike(), (ulong)seed);
                int at = 0;
                Weather w;
                foreach (double t in Uts)
                {
                    for (int i = 0; i < 20; i++)
                    {
                        g.Sample(lat[i], lon[i], alt[i], t, out w);
                        foreach (double v in new[] { w.TempK, w.Coverage, w.PrecipMmH, w.WindEast, w.WindNorth, w.CloudWater, w.CellStrength, w.Humidity, (double)(int)w.Type })
                        {
                            Assert.AreEqual(expected[at], BitConverter.DoubleToInt64Bits(v).ToString("X16"), "seed " + seed + " value " + at);
                            at++;
                            compared++;
                        }
                    }
                }
            }
            Assert.AreEqual(3 * 5 * 20 * 9, compared);
        }

        [TestMethod]
        public void TheSurfaceLayerEqualsTheRecordedValues()
        {
            string[] expected = DumpReader.Strings(Golden, "surface");
            var g = CoreProbe.ProofGenerator(1);
            var sites = new[] { new[] { 1.0, 0.3 }, new[] { -0.9, 2.0 }, new[] { 0.2, -1.0 } };
            int at = 0;
            foreach (double[] site in sites)
            {
                var s = new SurfaceSite(g, site[0], site[1]);
                foreach (double t in new[] { 3.0e6, 6.0e6, 9.0e6 })
                {
                    SurfaceState st = s.Evaluate(t);
                    foreach (double v in new[] { st.SnowSweMm, st.IceThicknessM, st.IceFraction, st.Wet01, st.MixedLayerK })
                    {
                        Assert.AreEqual(expected[at], BitConverter.DoubleToInt64Bits(v).ToString("X16"), "surface value " + at);
                        at++;
                    }
                }
            }
        }

        [TestMethod]
        public void TheResultIsTheSameAcrossThreadsOrderAndInstances()
        {
            CoreProbe.Q[] q = CoreProbe.Queries(20000, 77, 3.0e7);
            WeatherGenerator g1 = CoreProbe.ProofGenerator(1);
            ulong first = CoreProbe.HashQueries(g1, q, null, 0);
            Assert.AreEqual(first, CoreProbe.HashQueries(g1, q, null, 0), "second run");
            Assert.AreEqual(first, CoreProbe.HashQueries(CoreProbe.ProofGenerator(1), q, null, 0), "second instance");
            var hashes = new ulong[4];
            var threads = new Thread[4];
            for (int t = 0; t < 4; t++)
            {
                int tt = t;
                threads[t] = new Thread(() => hashes[tt] = CoreProbe.HashQueries(g1, q, null, 0));
                threads[t].Start();
            }
            foreach (Thread t in threads) t.Join();
            foreach (ulong h in hashes) Assert.AreEqual(first, h, "four threads");
            var order = Enumerable.Range(0, q.Length).ToArray();
            var rr = new Rng(99);
            for (int i = order.Length - 1; i > 0; i--)
            {
                int k = (int)(rr.Uniform() * (i + 1));
                int tmp = order[i]; order[i] = order[k]; order[k] = tmp;
            }
            Assert.AreEqual(first, CoreProbe.HashQueries(g1, q, order, 0), "shuffled order");
            Assert.AreNotEqual(first, CoreProbe.HashQueries(CoreProbe.ProofGenerator(2), q, null, 0), "another seed differs");
        }

        [TestMethod]
        public void ARewindAndAJumpLeaveTheWeatherIdenticalAtEqualUt()
        {
            CoreProbe.Q[] q = CoreProbe.Queries(5000, 77, 3.0e7);
            WeatherGenerator g = CoreProbe.ProofGenerator(1);
            ulong direct = CoreProbe.HashQueries(g, q, null, -172800.0);
            CoreProbe.HashQueries(g, q, null, 864000.0);
            CoreProbe.HashQueries(g, q, null, 3.15e7);
            Assert.AreEqual(direct, CoreProbe.HashQueries(g, q, null, -172800.0), "UT - 2 days after a jump forward of 10 days and a year");
            Assert.AreNotEqual(direct, CoreProbe.HashQueries(g, q, null, 0.0), "the weather moves");
        }

        [TestMethod]
        public void ASampleAllocatesNothingInSteadyState()
        {
            WeatherGenerator g = CoreProbe.ProofGenerator(1);
            CoreProbe.Q[] q = CoreProbe.Queries(2000, 5, 3.0e7);
            Weather w;
            for (int i = 0; i < q.Length; i++) g.Sample(q[i].Lat, q[i].Lon, q[i].Alt, q[i].Ut, out w);
            AppDomain.MonitoringIsEnabled = true;
            long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            for (int i = 0; i < q.Length; i++) g.Sample(q[i].Lat, q[i].Lon, q[i].Alt, q[i].Ut + (i / 256) * 3600.0, out w);
            long used = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before;
            Assert.IsTrue(used < 4096, "allocated " + used + " bytes in 2000 samples (monitoring counts the whole domain)");
        }
    }
}
