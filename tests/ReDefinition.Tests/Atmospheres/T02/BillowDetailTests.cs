using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // The billow detail keys (package PUFF, cloud.billow.*): neutral at their defaults, and the CPU twin of the keys that act on the erosion detail.
    [TestClass]
    public class BillowDetailTests
    {
        private static NoiseVolumeSet set;

        private static NoiseVolumeSet Set
        {
            get { return set ?? (set = NoiseVolumeSet.Generate(NoiseSizes.ForLevel(1), 1u)); }
        }

        private static NoiseLayerParams Params()
        {
            NoiseLayerParams p = NoiseLayerParams.Default(250.0);
            p.Seed = NoiseSeeds.LayerSeed(1u, 0);
            double x, y, z;
            NoiseSeeds.BaseOffset(1u, 0, out x, out y, out z);
            p.OffsetX = x;
            p.OffsetY = y;
            p.OffsetZ = z;
            return p;
        }

        // The shares of a line of edge samples (cover 0.5, soft edge 0.5: the share rises over half the noise range, so most samples are in the band the erosion acts on).
        private static double[] Shares(NoiseLayerParams p)
        {
            var v = new double[3000];
            for (int i = 0; i < v.Length; i++)
                v[i] = CloudShapeModel.Share(Set, ref p, 600000.0 + (i % 60) * 41.0, (i / 60) * 37.0, 0.0, 0.5, 0.5, 0.5, 1.0, 0.45, true);
            return v;
        }

        // What the erosion takes off the share (the share without the detail minus the share with it), at the same samples.
        private static double[] Eaten(NoiseLayerParams p)
        {
            var v = new double[3000];
            for (int i = 0; i < v.Length; i++)
            {
                double x = 600000.0 + (i % 60) * 41.0, y = (i / 60) * 37.0;
                v[i] = CloudShapeModel.Share(Set, ref p, x, y, 0.0, 0.5, 0.5, 0.5, 1.0, 0.45, false) - CloudShapeModel.Share(Set, ref p, x, y, 0.0, 0.5, 0.5, 0.5, 1.0, 0.45, true);
            }
            return v;
        }

        private static double Spread(double[] v)
        {
            double mean = 0.0, acc = 0.0;
            foreach (double x in v) mean += x;
            mean /= v.Length;
            foreach (double x in v) acc += (x - mean) * (x - mean);
            return Math.Sqrt(acc / v.Length);
        }

        [TestMethod]
        public void TheKeysAreNeutralAtTheirDefaults()
        {
            Assert.AreEqual(0f, CloudLookTuning.BillowLight);
            Assert.AreEqual(1f, CloudLookTuning.BillowContrast);
            Assert.AreEqual(0f, CloudLookTuning.BillowTop);
            Assert.AreEqual(0f, CloudLookTuning.BillowSharpen);
            Assert.AreEqual(0.4f, CloudLookTuning.BillowFine(0.4f));
            Assert.AreEqual(-1f, CloudLookTuning.BillowFine(-1f));
            Assert.AreEqual(2.37f, CloudLookTuning.BillowFineFreq(2.37f));
            Assert.AreEqual(0f, CloudLookTuning.BillowFineFreq(0f));
        }

        [TestMethod]
        public void ANeutralContrastAndAnUnsetFineOctaveGiveTheModelAsItWas()
        {
            NoiseLayerParams a = Params(), b = Params();
            b.BillowContrastDelta = 0.0;
            b.FineWeight = NoiseModel.FineDetailWeight;
            b.FineFrequency = NoiseModel.FineDetailFrequency;
            double[] x = Shares(a), y = Shares(b);
            for (int i = 0; i < x.Length; i++) Assert.AreEqual(x[i], y[i], 1e-12);
        }

        [TestMethod]
        public void MoreContrastSpreadsTheErodedShareAndLessNarrowsIt()
        {
            NoiseLayerParams p = Params(), more = Params(), less = Params();
            more.BillowContrastDelta = 1.0;
            less.BillowContrastDelta = -0.5;
            double s0 = Spread(Eaten(p)), s1 = Spread(Eaten(more)), s2 = Spread(Eaten(less));
            Assert.IsTrue(s1 > s0, "more contrast " + s1 + " against " + s0);
            Assert.IsTrue(s2 < s0, "less contrast " + s2 + " against " + s0);
        }

        // The numbers of the erosion detail at the Medium volume size: the spread of the detail value d (with and without the fine octave) and what the erosion takes off the share.
        // Runs only with PUFF_REPORT=<file> (a measurement for the package's result, not a check).
        [TestMethod]
        public void ErosionReport()
        {
            string path = Environment.GetEnvironmentVariable("PUFF_REPORT");
            if (string.IsNullOrEmpty(path)) return;
            var lines = new System.Collections.Generic.List<string>();
            foreach (double? weight in new double?[] { 0.0, 0.4, 0.8 })
            {
                NoiseLayerParams p = Params();
                p.FineWeight = weight;
                var d = new double[6000];
                for (int i = 0; i < d.Length; i++)
                {
                    double qx = 600000.0 + (i % 100) * 23.0, qy = (i / 100) * 31.0;
                    double a = CloudShapeModel.DetailAt(Set, ref p, qx, qy, 0.0, 1.0, 0.0, 0.0);
                    double b = CloudShapeModel.DetailAt(Set, ref p, qx, qy, 0.0, 1.0, 0.0, 0.0, NoiseModel.FineDetailFrequency);
                    d[i] = a + (b - a) * weight.Value;
                }
                Array.Sort(d);
                lines.Add(string.Format("fine weight {0}: d mean {1:F3} std {2:F3} p5 {3:F3} p25 {4:F3} p50 {5:F3} p75 {6:F3} p95 {7:F3}", weight, Mean(d), Spread(d), d[300], d[1500], d[3000], d[4500], d[5700]));
                for (int k = 0; k < 4; k++)
                {
                    p.BillowContrastDelta = k * 0.5;
                    lines.Add(string.Format("  contrast {0}: share eaten std {1:F3}", 1.0 + p.BillowContrastDelta, Spread(Eaten(p))));
                }
            }
            System.IO.File.WriteAllLines(path, lines);
        }

        private static double Mean(double[] v)
        {
            double s = 0.0;
            foreach (double x in v) s += x;
            return s / v.Length;
        }

        [TestMethod]
        public void AFineWeightOfZeroEqualsNoFineOctave()
        {
            NoiseLayerParams none = Params(), zero = Params();
            none.NoFineDetail = true;
            zero.FineWeight = 0.0;
            double[] a = Shares(none), b = Shares(zero);
            for (int i = 0; i < a.Length; i++) Assert.AreEqual(a[i], b[i], 1e-12);
        }

        [TestMethod]
        public void AHeavierFineOctaveChangesTheShare()
        {
            NoiseLayerParams p = Params(), heavy = Params();
            heavy.FineWeight = 0.9;
            double[] a = Shares(p), b = Shares(heavy);
            int differ = 0;
            for (int i = 0; i < a.Length; i++) if (Math.Abs(a[i] - b[i]) > 1e-6) differ++;
            Assert.IsTrue(differ > a.Length / 4, "differing samples " + differ);
        }
    }
}
