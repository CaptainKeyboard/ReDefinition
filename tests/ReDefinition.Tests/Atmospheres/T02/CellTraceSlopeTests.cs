using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // A4 (owner audit 2026-11-17): the march steps outside a single cloud's body by its depth over a bound on the depth's slope (CloudCells.cginc CellBodyGap). The billows' share of that bound is
    // measured here, not chosen: the steepest change per noise scale of the first octave (the body's rank, the shape noise's share at the noise's mean with a unit edge and the full swing, on the
    // heap preset billow as the cumulus reads it) and of the finer octaves (0.37 and 0.137 of the heap period, read straight). The constants in CloudCells (CloudCells.TraceRankSlope,
    // TraceFineSlope) must hold what the volumes give, with a margin.
    [TestClass]
    public class CellTraceSlopeTests
    {
        private const double ScaleM = 250.0;

        private static NoiseVolumeSet SetFor(int level, uint seed)
        {
            NoiseVolumeSet set = NoiseVolumeSet.Generate(NoiseSizes.ForLevel(level), seed);
            set.Heap = HeapVolumeSet.Generate(set.ShapeN, seed + LayerNoise.HeapSeedOffset);
            return set;
        }

        private static NoiseLayerParams Params(uint seed)
        {
            NoiseLayerParams p = NoiseLayerParams.Default(ScaleM);
            p.Seed = NoiseSeeds.LayerSeed(seed, 0);
            double x, y, z;
            NoiseSeeds.BaseOffset(seed, 0, out x, out y, out z);
            p.OffsetX = x;
            p.OffsetY = y;
            p.OffsetZ = z;
            p.HeapPreset = HeapModel.PresetOf("billow");
            p.HeapWarpShare = HeapModel.WarpShare;
            p.Blend = true;
            return p;
        }

        // The steepest change of the body's rank per noise scale along straight lines (finite steps of a 64th of a scale; the rank is the share CloudShadowShapeNoise.BodyShare reads).
        private static double RankSlope(NoiseVolumeSet set, NoiseLayerParams prm, int lines, int steps, int seed)
        {
            var random = new Random(seed);
            const double coverage = 0.5;
            double ds = ScaleM / 64.0, worst = 0.0;
            for (int l = 0; l < lines; l++)
            {
                double x = 600000.0 + random.NextDouble() * 40000.0, y = random.NextDouble() * 40000.0, z = random.NextDouble() * 3000.0;
                double dx = random.NextDouble() - 0.5, dy = random.NextDouble() - 0.5, dz = random.NextDouble() - 0.5;
                double n = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                dx /= n; dy /= n; dz /= n;
                double last = CloudShapeModel.Share(set, ref prm, x, y, z, 1.0 - coverage, coverage, 1.0, 1.0, 0.0, false);
                for (int i = 1; i <= steps; i++)
                {
                    double v = CloudShapeModel.Share(set, ref prm, x + dx * ds * i, y + dy * ds * i, z + dz * ds * i, 1.0 - coverage, coverage, 1.0, 1.0, 0.0, false);
                    worst = Math.Max(worst, Math.Abs(v - last) / ds * ScaleM);
                    last = v;
                }
            }
            return worst;
        }

        // The steepest change per noise scale of the finer octaves 0.37 f1 + 0.137 f2 (CellFineBillows): each is the heap field read trilinearly at its own period, whose slope along any line is at most
        // sqrt(3) times the largest step between neighbouring voxels per voxel; a voxel is (heap period / N) of the field, the field's period 0.37 or 0.137 of the heap period, so each octave weighted
        // by its own share of the period gives the same bound and the two together twice it.
        private static double FineSlope(HeapVolumeSet heap)
        {
            byte[] v = heap.Levels[0];
            int n = heap.N, worst = 0;
            for (int z = 0; z < n; z++)
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        int a = v[4 * ((z * n + y) * n + x)];
                        worst = Math.Max(worst, Math.Abs(a - v[4 * ((z * n + y) * n + (x + 1) % n)]));
                        worst = Math.Max(worst, Math.Abs(a - v[4 * ((z * n + (y + 1) % n) * n + x)]));
                        worst = Math.Max(worst, Math.Abs(a - v[4 * ((((z + 1) % n) * n + y) * n + x)]));
                    }
            double perVoxel = worst / 255.0;
            double voxelsPerScale = n / HeapModel.PeriodScales;
            return 2.0 * Math.Sqrt(3.0) * perVoxel * voxelsPerScale;
        }

        [TestMethod]
        public void TheTraceSlopesHoldWhatTheVolumesGive()
        {
            double rank = 0.0, fine = 0.0;
            foreach (int level in new[] { 1, 2 })
                foreach (uint seed in new[] { 1u, 7u })
                {
                    NoiseVolumeSet set = SetFor(level, seed);
                    rank = Math.Max(rank, RankSlope(set, Params(seed), 60, 1500, (int)seed * 31 + level));
                    fine = Math.Max(fine, FineSlope(set.Heap));
                }
            Console.WriteLine("measured: rank slope " + rank.ToString("0.00") + " per scale, fine slope " + fine.ToString("0.00") + " per scale");
            Assert.IsTrue(CloudCells.TraceRankSlope >= 1.25 * rank, "the rank's bound " + CloudCells.TraceRankSlope + " holds 1.25 x the measured " + rank.ToString("0.00"));
            Assert.IsTrue(CloudCells.TraceFineSlope >= fine, "the fine octaves' bound " + CloudCells.TraceFineSlope + " holds the exact " + fine.ToString("0.00"));
        }
    }
}
