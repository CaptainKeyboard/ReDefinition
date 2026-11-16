using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // CLOUDR1 P2 and P3: the heap volume (a cascade of heaps on heaps, rank-equalised, with its mips and its cavity channel) and the CPU twin that reads it. The golden numbers are made by
    // tools/weather_metrics/noise_model.py (gen_heap_levels, heap_value; tools/tests/test_heap_model.py checks the same numbers there).
    [TestClass]
    public class HeapVolumeTests
    {
        private static HeapVolumeSet small, medium;

        private static HeapVolumeSet Small { get { return small ?? (small = HeapVolumeSet.Generate(16, 1u)); } }
        private static HeapVolumeSet Medium { get { return medium ?? (medium = HeapVolumeSet.Generate(NoiseSizes.ForLevel(1).Shape, 1u)); } }

        private static ulong Fnv1a(byte[] data)
        {
            ulong h = 0xCBF29CE484222325UL;
            unchecked
            {
                foreach (byte b in data) h = (h ^ b) * 0x100000001B3UL;
            }
            return h;
        }

        // The cascade has the octaves a level can resolve: 2, 4, 8, 16 cells at 64 voxels (the finest cell 4 voxels wide, one noise scale), one fewer for every halving.
        [TestMethod]
        public void TheOctavesFollowTheLevelSize()
        {
            CollectionAssert.AreEqual(new[] { 2, 4, 8, 16 }, HeapModel.Octaves(64));
            CollectionAssert.AreEqual(new[] { 2, 4, 8, 16, 32 }, HeapModel.Octaves(128));
            CollectionAssert.AreEqual(new[] { 2, 4, 8 }, HeapModel.Octaves(32));
            CollectionAssert.AreEqual(new[] { 2 }, HeapModel.Octaves(8));
            CollectionAssert.AreEqual(new[] { 2 }, HeapModel.Octaves(2), "at least the first octave");
            Assert.AreEqual(0, HeapModel.PresetOf("none"));
            Assert.AreEqual(1, HeapModel.PresetOf("Billow"));
            Assert.AreEqual(2, HeapModel.PresetOf("rough"));
            Assert.AreEqual(0, HeapModel.PresetOf("nonsense"));
        }

        // The heap field's values are uniform (rank-equalised) at level 0 and at every analytic mip level, so a coverage threshold at 0.5 covers half the sky at any level.
        [TestMethod]
        public void TheFieldIsRankUniformAtEveryAnalyticLevel()
        {
            HeapVolumeSet set = Medium;
            Assert.AreEqual(NoiseVolumeSet.MipLevels(set.N), set.Levels.Length);
            for (int m = 0; m < 4; m++)
            {
                byte[] level = set.Levels[m];
                int count = level.Length / 4;
                for (int channel = 0; channel < 2; channel++)
                {
                    var bins = new int[8];
                    double sum = 0.0;
                    for (int i = 0; i < count; i++)
                    {
                        byte v = level[i * 4 + channel];
                        bins[v >> 5]++;
                        sum += v;
                    }
                    for (int b = 0; b < 8; b++)
                        Assert.AreEqual(0.125, bins[b] / (double)count, 0.012, "level " + m + " preset " + (channel + 1) + " bin " + b);
                    Assert.AreEqual(127.5, sum / count, 1.5, "level " + m + " preset " + (channel + 1) + " mean");
                }
            }
        }

        [TestMethod]
        public void TheGenerationIsDeterministicAndMatchesTheNumPyTwin()
        {
            HeapVolumeSet again = HeapVolumeSet.Generate(16, 1u);
            for (int m = 0; m < Small.Levels.Length; m++) CollectionAssert.AreEqual(Small.Levels[m], again.Levels[m], "level " + m);
            Assert.AreEqual(5, Small.Levels.Length);
            Assert.AreEqual(0xC8EA9DAD82F6101DUL, Fnv1a(Small.Levels[0]), "level 0 hash (noise_model.py gen_heap_levels)");
            Assert.AreEqual(0x83D897480C7869F4UL, Fnv1a(Small.Levels[1]), "level 1 hash");
            byte[] l0 = Small.Levels[0];
            // voxel (x 7, y 5, z 3): index (3 * 16 + 5) * 16 + 7
            int a = ((3 * 16 + 5) * 16 + 7) * 4;
            CollectionAssert.AreEqual(new byte[] { 164, 180, 255, 255 }, new[] { l0[a], l0[a + 1], l0[a + 2], l0[a + 3] });
            int b = ((10 * 16 + 2) * 16 + 13) * 4;
            CollectionAssert.AreEqual(new byte[] { 64, 176, 210, 255 }, new[] { l0[b], l0[b + 1], l0[b + 2], l0[b + 3] });
            int c = ((15 * 16 + 15) * 16 + 15) * 4;
            CollectionAssert.AreEqual(new byte[] { 61, 59, 224, 210 }, new[] { l0[c], l0[c + 1], l0[c + 2], l0[c + 3] });
        }

        // The CPU twin's read equals the NumPy twin's at the same lookup points, with and without a mip level (the shader's tex3Dlod with trilinear filtering).
        [TestMethod]
        public void TheCpuReadMatchesTheNumPyReadAtSamplePoints()
        {
            double[][] points = { new[] { 1200.3, -800.7, 3400.1 }, new[] { -5000.0, 2500.5, 17.9 }, new[] { 333.3, 444.4, 555.5 } };
            double[,,] want =
            {
                { { 0.247501074448, 0.734921386667, 0.944623093238 }, { 0.37436337646, 0.808261021176, 0.851748392242 } },
                { { 0.573980043834, 0.476274898824, 0.847826347389 }, { 0.493901937328, 0.670728404706, 0.526840985866 } }
            };
            NoiseLayerParams prm = NoiseLayerParams.Default(250.0);
            for (int preset = 1; preset <= 2; preset++)
                for (int l = 0; l < 2; l++)
                {
                    // a smoothing of 2^1.5 / 2 noise scales asks for the mip level 1.5 of a 16-voxel volume (2 voxels per noise scale: the heap's period is 8 scales); the last analytic level
                    // (8 voxels, level 1) is the coarsest the heap is read at, so the read is at level 1
                    prm.SmoothingScales = l == 0 ? 0.0 : Math.Pow(2.0, 1.5) / 2.0;
                    for (int p = 0; p < 3; p++)
                    {
                        double cavity;
                        double got = CloudShapeModel.HeapAt(Small, ref prm, preset, points[p][0], points[p][1], points[p][2], 0.0, out cavity);
                        Assert.AreEqual(want[preset - 1, l, p], got, 1e-9, "preset " + preset + " lod " + l + " point " + p);
                        Assert.IsTrue(cavity >= 0.0 && cavity <= 1.0);
                    }
                }
        }

        // The march's spacing raises the mip level exactly as the shader does: log2(foot / scale) + log2(N / 16), never below the smoothing's.
        [TestMethod]
        public void TheFootprintRaisesTheMipLevelLikeTheShader()
        {
            NoiseLayerParams prm = NoiseLayerParams.Default(250.0);
            prm.HeapFootLod = true;
            double cav;
            double foot = 250.0 * 2.0;   // two noise scales at N = 64 over a period of 8 scales: 2 * 64 / 8 = 16 voxels -> level 4
            double viaFoot = CloudShapeModel.HeapAt(Medium, ref prm, 1, 1234.0, 987.0, 456.0, foot, out cav);
            prm.HeapFootLod = false;
            prm.SmoothingScales = foot / 250.0;
            double viaLod = CloudShapeModel.HeapAt(Medium, ref prm, 1, 1234.0, 987.0, 456.0, 0.0, out cav);
            Assert.AreEqual(viaLod, viaFoot, 1e-12);
            prm.SmoothingScales = 5.0 * 8.0 / Medium.N * 4.0;   // 2.5 scales = 20 voxels: coarser than the footprint
            prm.HeapFootLod = true;
            double smoothWins = CloudShapeModel.HeapAt(Medium, ref prm, 1, 1234.0, 987.0, 456.0, foot, out cav);
            prm.HeapFootLod = false;
            Assert.AreEqual(CloudShapeModel.HeapAt(Medium, ref prm, 1, 1234.0, 987.0, 456.0, 0.0, out cav), smoothWins, 1e-12, "the coarser of the two wins");
        }

        // The cover share per type: with the heap the share of the sky a map value covers is the one of the shape volume, within 0.01 (both are rank-uniform), on the voxels themselves.
        // The shape and the heap share one texture (the pass has no sampler register left): red is the shape volume's own byte at every level, green and blue the two heap presets, alpha the
        // cavity of the first.
        [TestMethod]
        public void TheShapeAndTheHeapShareOneTextureWithTheShapeInRed()
        {
            NoiseVolumeSet shape = NoiseVolumeSet.Generate(NoiseSizes.ForLevel(0), 1u);
            HeapVolumeSet heap = HeapVolumeSet.Generate(shape.ShapeN, 1u);
            byte[][] merged = HeapVolumes.MergeWithShape(shape.Shape, shape.ShapeMips, heap);
            Assert.AreEqual(heap.Levels.Length, merged.Length);
            for (int m = 0; m < merged.Length; m++)
            {
                byte[] s = m == 0 ? shape.Shape : shape.ShapeMips[m - 1];
                Assert.AreEqual(s.Length * 4, merged[m].Length);
                for (int i = 0; i < s.Length; i += 7)
                {
                    Assert.AreEqual(s[i], merged[m][i * 4]);
                    Assert.AreEqual(heap.Levels[m][i * 4], merged[m][i * 4 + 1]);
                    Assert.AreEqual(heap.Levels[m][i * 4 + 1], merged[m][i * 4 + 2]);
                    Assert.AreEqual(heap.Levels[m][i * 4 + 2], merged[m][i * 4 + 3]);
                }
            }
        }

        // The heap is never read below its last analytic level (8 voxels): the coarser levels are box means, not rank-uniform, and the far view's mean takes over there.
        [TestMethod]
        public void TheHeapIsNotReadBelowItsLastAnalyticLevel()
        {
            NoiseLayerParams prm = NoiseLayerParams.Default(250.0);
            double cav;
            prm.SmoothingScales = 100.0;
            double coarse = CloudShapeModel.HeapAt(Medium, ref prm, 1, 1234.0, 987.0, 456.0, 0.0, out cav);
            prm.SmoothingScales = 2.0;   // 2 scales over a period of 8 at 64 voxels = 16 voxels -> level 4... capped at level 3 (8 voxels)
            double capped = CloudShapeModel.HeapAt(Medium, ref prm, 1, 1234.0, 987.0, 456.0, 0.0, out cav);
            prm.SmoothingScales = 1.0;   // 8 voxels -> level 3
            double level3 = CloudShapeModel.HeapAt(Medium, ref prm, 1, 1234.0, 987.0, 456.0, 0.0, out cav);
            Assert.AreEqual(level3, coarse, 1e-12);
            Assert.AreEqual(level3, capped, 1e-12);
        }

        // The heap lookup takes the untiling displacement's radial part out and keeps HeapModel.WarpShare of the rest: the heap read is not stretched into columns.
        [TestMethod]
        public void TheHeapLookupKeepsOnlyAShareOfTheHorizontalDisplacement()
        {
            NoiseLayerParams prm = NoiseLayerParams.Default(250.0);
            prm.ReferenceRadiusM = 601000.0;
            double[] p = { 601500.0, 4000.0, -2500.0 };
            double qx, qy, qz, dx, dy, dz, hx, hy, hz;
            CloudShapeModel.LookupPoint(ref prm, p[0], p[1], p[2], out qx, out qy, out qz, out dx, out dy, out dz);
            Assert.IsTrue(Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz) > 10.0, "the warp displaces the point");
            CloudShapeModel.HeapLookup(ref prm, p[0], p[1], p[2], qx, qy, qz, dx, dy, dz, out hx, out hy, out hz);
            double r = Math.Sqrt(p[0] * p[0] + p[1] * p[1] + p[2] * p[2]), ux = p[0] / r, uy = p[1] / r, uz = p[2] / r;
            // The point without the warp is q - d; the heap's point differs from it by a vector with no radial part and WarpShare of d's horizontal part.
            double ex = hx - (qx - dx), ey = hy - (qy - dy), ez = hz - (qz - dz);
            Assert.AreEqual(0.0, ex * ux + ey * uy + ez * uz, 1e-6, "no radial part");
            double along = dx * ux + dy * uy + dz * uz;
            Assert.AreEqual(HeapModel.WarpShare * (dx - ux * along), ex, 1e-9);
            Assert.AreEqual(HeapModel.WarpShare * (dy - uy * along), ey, 1e-9);
            Assert.AreEqual(HeapModel.WarpShare * (dz - uz * along), ez, 1e-9);
            // Without the warp the lookup is the plain point.
            prm.Untile = false;
            CloudShapeModel.LookupPoint(ref prm, p[0], p[1], p[2], out qx, out qy, out qz, out dx, out dy, out dz);
            Assert.AreEqual(0.0, dx + dy + dz, 0.0);
            CloudShapeModel.HeapLookup(ref prm, p[0], p[1], p[2], qx, qy, qz, dx, dy, dz, out hx, out hy, out hz);
            Assert.AreEqual(qx, hx, 1e-9);
            Assert.AreEqual(qy, hy, 1e-9);
            Assert.AreEqual(qz, hz, 1e-9);
        }

        [TestMethod]
        public void TheCoverShareMatchesTheShapeVolumeWithinOnePercent()
        {
            NoiseVolumeSet shape = NoiseVolumeSet.Generate(NoiseSizes.ForLevel(1), 1u);
            int n = Medium.N, count = n * n * n;
            foreach (double cov in new[] { 0.15, 0.35, 0.5, 0.65, 0.85 })
                foreach (double edge in new[] { 0.15, 0.5 })
                    for (int preset = 1; preset <= 2; preset++)
                    {
                        double heapMean = 0.0, shapeMean = 0.0;
                        for (int i = 0; i < count; i++)
                        {
                            heapMean += ShareOf(Medium.Levels[0][i * 4 + preset - 1] / 255.0, cov, edge);
                            shapeMean += ShareOf(shape.Shape[i] / 255.0, cov, edge);
                        }
                        Assert.AreEqual(shapeMean / count, heapMean / count, 0.01, "cover " + cov + " edge " + edge + " preset " + preset);
                    }
            // The same through the CPU twin's Share (lookup warp, trilinear read) at scattered points.
            NoiseLayerParams prm = NoiseLayerParams.Default(500.0);
            NoiseLayerParams heapPrm = prm;
            heapPrm.HeapPreset = 1;
            shape.Heap = Medium;
            double a = 0.0, b = 0.0;
            const int samples = 40000;
            for (int i = 0; i < samples; i++)
            {
                double x = (i * 7919.0) % 600000.0, y = (i * 104729.0) % 500000.0, z = 600000.0 + (i * 1237.0) % 50000.0;
                a += CloudShapeModel.Share(shape, ref prm, x, y, z, 0.5, 0.5, 0.15, 1.0, 0.0, false);
                b += CloudShapeModel.Share(shape, ref heapPrm, x, y, z, 0.5, 0.5, 0.15, 1.0, 0.0, false);
            }
            Assert.AreEqual(a / samples, b / samples, 0.03, "scattered points (finite sample, the lookup is smooth)");
        }


        // cloud.heap.iso: with a layer's vertical scale below 1 the heap is read at the lattice's true vertical scale (the shape volume's compression is taken back), so the heaps are not
        // stretched into columns; without a vertical scale nothing changes, and at the layer's reference radius the compression is the identity.
        [TestMethod]
        public void TheIsotropicHeapReadTakesTheVerticalScaleBack()
        {
            NoiseVolumeSet set = NoiseVolumeSet.Generate(NoiseSizes.ForLevel(1), 1u);
            set.Heap = Medium;
            double r0 = 600000.0, ref0 = r0 + 1000.0;
            NoiseLayerParams flat = NoiseLayerParams.Default(250.0);
            flat.Untile = false;
            flat.HeapPreset = 1;
            flat.ReferenceRadiusM = ref0;
            NoiseLayerParams vertical = flat;
            vertical.SphereScale = 0.35;
            NoiseLayerParams verticalIso = vertical;
            verticalIso.HeapIso = true;
            double differ = 0.0;
            for (int i = 0; i < 200; i++)
            {
                double y = i % 7 == 0 ? 0.0 : i * 37.0, z = i % 7 == 0 ? 0.0 : i * 11.0, x = ref0 + (i % 7) * 400.0;
                double a = CloudShapeModel.Share(set, ref flat, x, y, z, 0.5, 0.5, 0.15, 1.0, 0.0, false);
                flat.HeapIso = true;
                Assert.AreEqual(a, CloudShapeModel.Share(set, ref flat, x, y, z, 0.5, 0.5, 0.15, 1.0, 0.0, false), 1e-12, "no vertical scale: no change");
                flat.HeapIso = false;
                double plain = CloudShapeModel.Share(set, ref vertical, x, y, z, 0.5, 0.5, 0.15, 1.0, 0.0, false), iso = CloudShapeModel.Share(set, ref verticalIso, x, y, z, 0.5, 0.5, 0.15, 1.0, 0.0, false);
                if (i % 7 == 0) Assert.AreEqual(plain, iso, 1e-6, "at the reference radius the compression is the identity");
                differ += Math.Abs(plain - iso);
            }
            Assert.IsTrue(differ > 1.0, "off the reference radius the isotropic read differs from the compressed one: " + differ);
        }


        // cloud.heap.detail: the erosion detail's octaves fall off as 2^(-H k), continuing the heap's cascade; 0 is the compiled volume.
        [TestMethod]
        public void TheDetailCascadeWeightsFollowTheHurstExponentAndDefaultToTheCompiledOnes()
        {
            Assert.AreSame(NoiseModel.DetailWorleyWeights, NoiseVolumes.DetailWeights(0.0));
            double[] w = NoiseVolumes.DetailWeights(0.65);
            Assert.AreEqual(NoiseModel.DetailWorleyCells.Length, w.Length);
            Assert.AreEqual(1.0, w[0], 0.0);
            Assert.AreEqual(Math.Pow(2.0, -0.65), w[1] / w[0], 1e-12);
            Assert.AreEqual(Math.Pow(2.0, -1.3), w[2] / w[0], 1e-12);
            byte[] compiled = NoiseVolumes.Detail(16, 2u), cascade = NoiseVolumes.Detail(16, 2u, 0.65);
            CollectionAssert.AreEqual(compiled, NoiseVolumes.Detail(16, 2u, 0.0));
            CollectionAssert.AreNotEqual(compiled, cascade);
            double mean = 0.0;
            foreach (byte b in cascade) mean += b / 255.0;
            Assert.IsTrue(mean / cascade.Length > 0.2 && mean / cascade.Length < 0.8, "mean " + mean / cascade.Length);
        }

        private static double ShareOf(double n, double cov, double edge)
        {
            double x = (cov + (n - 0.5) + 0.5 - 1.0) / edge + 0.5;
            return x <= 0.0 ? 0.0 : x >= 1.0 ? 1.0 : x;
        }

        // The cavity channel is 1 where the field is not below its blur and drops in the creases: low cavity sits where the field is low.
        [TestMethod]
        public void TheCavityIsLowInTheCreases()
        {
            byte[] level = Medium.Levels[0];
            int count = level.Length / 4;
            double sumField = 0.0, sumLowField = 0.0;
            int low = 0, ones = 0;
            for (int i = 0; i < count; i++)
            {
                double f = level[i * 4] / 255.0, c = level[i * 4 + 2] / 255.0;
                sumField += f;
                if (c < 0.5) { sumLowField += f; low++; }
                if (c >= 0.999) ones++;
            }
            Assert.IsTrue(low > count / 200, "there are creases: " + low);
            Assert.IsTrue(ones > count / 2, "most voxels are not in a crease");
            Assert.IsTrue(sumLowField / low < 0.75 * (sumField / count), "the creases are low parts of the field: " + sumLowField / low);
        }

        // Self-similarity: the field's roughness follows the cascade. The mean absolute difference over a lag of one voxel shrinks when the finest octave is left out (the next level).
        [TestMethod]
        public void TheFinestOctaveIsDroppedWhereTheLevelCannotResolveIt()
        {
            double d0 = LagDifference(Medium.Levels[0], Medium.N, 1), d1 = LagDifference(Medium.Levels[1], Medium.N / 2, 1);
            Assert.IsTrue(d1 > d0, "per voxel the coarser level changes more (a voxel is twice as wide): " + d0 + " " + d1);
            // At the same world lag (two voxels at level 0, one at level 1) the level with one octave fewer changes about as much as the full one: the dropped octave was below that lag.
            double w0 = LagDifference(Medium.Levels[0], Medium.N, 2);
            Assert.AreEqual(w0, d1, 0.35 * w0);
        }

        private static double LagDifference(byte[] level, int n, int lag)
        {
            double sum = 0.0;
            int count = 0;
            for (int z = 0; z < n; z++)
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        sum += Math.Abs(level[((z * n + y) * n + x) * 4] - level[((z * n + y) * n + (x + lag) % n) * 4]);
                        count++;
                    }
            return sum / count / 255.0;
        }
    }
}
