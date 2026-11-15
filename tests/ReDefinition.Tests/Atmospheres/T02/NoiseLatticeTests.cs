using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // The lattice table of the untiling warp (E40 C4, NoiseLattice): a table read, with the shader's quintic fade and the texture filter's blend, is the vector value noise of the integer hash it
    // replaces, inside the window the table follows (exact over the float corner values, to the precision of the texture's format over the half floats or bytes the GPU holds); the window moves
    // in steps and a moved window is the one a fresh table would hold; the shader's switch and the tuning key that sets it are the ones the code binds.
    [TestClass]
    public class NoiseLatticeTests
    {
        private static double Next(ref uint state)
        {
            state = NoiseHash.LowBias32(state + 0x9E3779B9u);
            return NoiseHash.Unit(state);
        }

        private static double WorstWarpError(float[] table, int layer, uint seed, double cx, double cy, double cz, int points)
        {
            uint s = 7u;
            int reach = NoiseLattice.N / 2 - NoiseLattice.Hysteresis - 1;
            double worst = 0.0;
            for (int i = 0; i < points; i++)
            {
                double x = cx + (Next(ref s) * 2.0 - 1.0) * reach, y = cy + (Next(ref s) * 2.0 - 1.0) * reach, z = cz + (Next(ref s) * 2.0 - 1.0) * reach;
                double vx, vy, vz;
                CloudShapeModel.ValueVec(x, y, z, seed, out vx, out vy, out vz);
                worst = Math.Max(worst, Math.Abs(NoiseLattice.Read(table, layer, 0, x, y, z) * 2.0 - 1.0 - vx));
                worst = Math.Max(worst, Math.Abs(NoiseLattice.Read(table, layer, 1, x, y, z) * 2.0 - 1.0 - vy));
                worst = Math.Max(worst, Math.Abs(NoiseLattice.Read(table, layer, 2, x, y, z) * 2.0 - 1.0 - vz));
            }
            return worst;
        }

        [TestMethod]
        public void AWarpTableReadIsTheVectorValueNoiseInsideTheWindow()
        {
            var lattice = new NoiseLattice();
            uint seed = CloudShapeModel.WarpSeed(NoiseSeeds.LayerSeed(1u, 2));
            lattice.FollowWarp(2, seed, 140.3, -77.9, 5.5);
            double worst = WorstWarpError(NoiseLattice.Floats(lattice.Halves, false), 2, seed, 140.3, -77.9, 5.5, 400);
            Assert.IsTrue(worst < 0.0006, "over the half floats the read is the hash's noise to " + worst);
        }

        [TestMethod]
        public void TheGpuFormatsHoldTheCornerValuesToTheirPrecision()
        {
            var lattice = new NoiseLattice();
            uint seed = 9u;
            lattice.FollowWarp(0, seed, 0.5, 0.5, 0.5);
            double worstByte = WorstWarpError(NoiseLattice.Floats(lattice.Halves, true), 0, seed, 0.5, 0.5, 0.5, 400);
            double worstHalf = WorstWarpError(NoiseLattice.Floats(lattice.Halves, false), 0, seed, 0.5, 0.5, 0.5, 400);
            Assert.IsTrue(worstByte < 0.0045, "bytes: " + worstByte);
            Assert.IsTrue(worstHalf < 0.0006, "halves: " + worstHalf);
            Assert.AreEqual(0f, NoiseLattice.FromHalf(NoiseLattice.ToHalf(0f)));
            foreach (float v in new[] { 1e-5f, 0.001f, 0.25f, 0.5f, 0.7071f, 0.9995f })
                Assert.AreEqual(v, NoiseLattice.FromHalf(NoiseLattice.ToHalf(v)), v * 0.0006f + 1e-7f, "the half of " + v);
        }

        [TestMethod]
        public void TheWindowMovesInStepsAndAMovedWindowIsTheOneAFreshTableHolds()
        {
            uint seed = 4242u;
            var moved = new NoiseLattice();
            Assert.IsTrue(moved.FollowWarp(0, seed, 10.2, 20.7, -30.1));
            int oy = moved.WarpOrigin(0, 1), oz = moved.WarpOrigin(0, 2);
            Assert.IsFalse(moved.FollowWarp(0, seed, 10.2 + NoiseLattice.Hysteresis, 20.7 - NoiseLattice.Hysteresis, -30.1), "within the hysteresis the window stays");
            Assert.IsTrue(moved.FollowWarp(0, seed, 10.2 + NoiseLattice.Hysteresis + 2, 20.7 + 31.0, -30.1 - 5.0), "past it the window moves");
            Assert.AreNotEqual(oy, moved.WarpOrigin(0, 1));
            var fresh = new NoiseLattice();
            fresh.FollowWarp(0, seed, 10.2 + NoiseLattice.Hysteresis + 2, 20.7 + 31.0, -30.1 - 5.0);
            CollectionAssert.AreEqual(fresh.Halves, moved.Halves);
            Assert.AreEqual(oz - 5, moved.WarpOrigin(0, 2));
            Assert.IsTrue(moved.FollowWarp(0, seed + 1u, 10.2 + NoiseLattice.Hysteresis + 2, 20.7 + 31.0, -30.1 - 5.0), "a new seed writes the window again");
        }

        [TestMethod]
        public void TheCopiedSliceClosesEverySlabAndTheLayersDoNotShareTexels()
        {
            var lattice = new NoiseLattice();
            for (int l = 0; l < NoiseLattice.Layers; l++) lattice.FollowWarp(l, 100u + (uint)l, 3.0, 4.0, 5.0);
            for (int slab = 0; slab < NoiseLattice.Layers; slab++)
                for (int i = 0; i < NoiseLattice.N * NoiseLattice.N * 4; i++)
                    Assert.AreEqual(lattice.Halves[NoiseLattice.Index(slab, 0, 0, 0, 0) + i], lattice.Halves[NoiseLattice.Index(slab, NoiseLattice.N, 0, 0, 0) + i]);
            Assert.AreNotEqual(lattice.Halves[NoiseLattice.Index(0, 3, 4, 5, 0)], lattice.Halves[NoiseLattice.Index(1, 3, 4, 5, 0)]);
            Assert.AreEqual(NoiseLattice.Depth, NoiseLattice.Layers * NoiseLattice.SlabDepth);
        }

        [TestMethod]
        public void TheShaderReadsTheTableWhereTheFrameSaysAndTheKeyIsTheOneTheCodeReads()
        {
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            string noise = File.ReadAllText(Path.Combine(root, "unity", "Assets", "ReDefinitionAtmospheres", "Shaders", "ReDefinitionAtmospheresNoise.cginc"));
            StringAssert.Contains(noise, "sampler3D _NoiseLattice;");
            StringAssert.Contains(noise, "float4 _NoiseL;");
            StringAssert.Contains(noise, "[branch] if (_NoiseP.w > 0.5) wv = NoiseLatticeAt(q / (_NoiseQ.y * scale), li).rgb * 2.0 - 1.0;");
            StringAssert.Contains(noise, "else wv = NoiseValueVec(q / (_NoiseQ.y * scale), (uint)_NoiseOffset[li].w + 4001u);");
            string code = File.ReadAllText(Path.Combine(root, "src", "Atmospheres", "Render", "Layers", "Noise", "LayerNoise.cs"));
            StringAssert.Contains(code, "Shader.PropertyToID(\"_NoiseLattice\")");
            StringAssert.Contains(code, "Shader.PropertyToID(\"_NoiseL\")");
            Assert.AreEqual("layers.warpTable", OwnCloudOptions.TuneWarpTable);

            var frame = new LayerFrame();
            Tuning.ClearOverrides();
            Tuning.Instance.BeginFrame(9400001);
            OwnCloudOptions.Apply(frame, key => AtmospheresSettings.Definition(key).Default);
            Assert.AreEqual(OwnCloudOptions.WarpTableDefault, frame.WarpTable);
            Assert.AreEqual(0, OwnCloudOptions.WarpTableDefault, "the warp is hashed in the product until the table's window steps are off the main thread");
            Tuning.SetOverride(OwnCloudOptions.TuneWarpTable, 1f);
            try
            {
                Tuning.Instance.BeginFrame(9400002);
                OwnCloudOptions.Apply(frame, key => AtmospheresSettings.Definition(key).Default);
                Assert.AreEqual(1, frame.WarpTable, "layers.warpTable 1 reads the half-float table");
            }
            finally
            {
                Tuning.ClearOverrides();
                Tuning.Instance.BeginFrame(9400003);
            }
        }
    }
}
