using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The measurement run behind the tables of the design note (E40-cover-meaning-design.md): prints the closed share per mapping, layer, edge, sigma, type and elevation band to
    // the file named by COVER_REPORT. Ignored in the suite (minutes of CPU); take the attribute off to rerun it.
    [TestClass, Ignore("measurement run for the design note; remove the attribute to print the tables (COVER_REPORT names the file)")]
    public class CoverMeaningExplore
    {
        private static NoiseVolumeSet volumes;
        private static NoiseVolumeSet Volumes { get { return volumes ?? (volumes = NoiseVolumeSet.Generate(NoiseSizes.ForLevel(1), 1u)); } }
        private static readonly string ReportPath = Environment.GetEnvironmentVariable("COVER_REPORT");

        private static void Report(string line)
        {
            Console.WriteLine(line);
            if (!string.IsNullOrEmpty(ReportPath)) File.AppendAllText(ReportPath, line + Environment.NewLine);
        }

        internal static NoiseLayerParams ParamsOf(CloudLayerDef layer, int index, double scaleM = 0.0)
        {
            double bx, by, bz;
            NoiseSeeds.BaseOffset(1u, index, out bx, out by, out bz);
            var prm = new NoiseLayerParams { ScaleM = scaleM > 0 ? scaleM : layer.EffectiveNoiseScaleM, Untile = true, Curl = true, OffsetX = bx, OffsetY = by, OffsetZ = bz, Seed = NoiseSeeds.LayerSeed(1u, index), SphereScale = 1.0, ReferenceRadiusM = 600000.0 };
            if (ColumnConstant)
            {
                prm.SphereScale = 1e-6;
                prm.ReferenceRadiusM = 600000.0 + 0.5 * (layer.InnerAltitudeM + layer.OuterAltitudeM);
            }
            return prm;
        }

        internal static bool ColumnConstant;

        private static double Mean(CoverMapping m, CloudLayerDef layer, int index, CoverNumbers k, double c, double type, int sites, bool eroded, out double sd, double scaleM = 0.0, int cells = 16)
        {
            var table = new LayerTypeTable(layer);
            NoiseLayerParams prm = ParamsOf(layer, index, scaleM);
            if (ColumnConstant)
            {
                double eb, et;
                table.InterpolatedHeights(type, out eb, out et);
                prm.SphereScale = 1e-6;
                prm.ReferenceRadiusM = 600000.0 + layer.InnerAltitudeM + 0.5 * (eb + et) * layer.ThicknessM;
            }
            double sum = 0, sum2 = 0;
            for (int s = 0; s < sites; s++)
            {
                double v = CoverMeaningModel.SiteClosedShare(m, layer, table, Volumes, prm, k, c, type, s, cells, prm.ScaleM * 6.0, eroded);
                sum += v;
                sum2 += v * v;
            }
            double mean = sum / sites;
            sd = Math.Sqrt(Math.Max(0, sum2 / sites - mean * mean));
            return mean;
        }

        private static string Row(string label, CoverMapping m, CloudLayerDef layer, int index, CoverNumbers k, double[] cs, double type, int sites, bool eroded, double scaleM = 0.0)
        {
            var line = new StringBuilder(label.PadRight(26));
            double err = 0, maxErr = 0;
            foreach (double c in cs)
            {
                double sd;
                double v = Mean(m, layer, index, k, c, type, sites, eroded, out sd, scaleM);
                line.Append(v.ToString("0.00") + " ");
                err += Math.Abs(v - c);
                maxErr = Math.Max(maxErr, Math.Abs(v - c));
            }
            line.Append(" | mean|err| " + (err / cs.Length).ToString("0.000") + " max " + maxErr.ToString("0.000"));
            return line.ToString();
        }

        private static readonly double[] Cs = { 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9 };

        private static CloudLayerDef[] Layers { get { return new[] { CloudLookFixtures.Rain(), CloudLookFixtures.Cumulus(), CloudLookFixtures.Cumulonimbus(), CloudLookFixtures.Cirrus() }; } }
        private static readonly double[] Types = { 0.5, 0.0, 0.229, 0.0 };

        [TestMethod, TestCategory("Explore")]
        public void ColumnConstantLookup()
        {
            ColumnConstant = true;
            try
            {
                CloudLayerDef[] layers = Layers;
                Report("COLUMN-CONSTANT shape lookup (one noise value per column), closed share of columns, c = " + string.Join(" ", Array.ConvertAll(Cs, x => x.ToString("0.0"))));
                for (int li = 0; li < 3; li++)
                {
                    CloudLayerDef layer = layers[li];
                    CoverNumbers k = CoverNumbers.Of(layer);
                    k.Cdf = CoverCdf.Of(Volumes, ParamsOf(layer, li), 32, 200000, 11);
                    Report("LAYER " + layer.Name + " type " + Types[li]);
                    foreach (CoverMapping m in new[] { CoverMapping.Current, CoverMapping.MeaningRank, CoverMapping.AmountShare })
                        Report(Row(m.ToString(), m, layer, li, k, Cs, Types[li], 40, true));
                }
                CloudLayerDef l1 = layers[1];
                double[] cs = { 0.3, 0.5, 0.7 };
                foreach (double type in new[] { 0.0, 0.25, 0.75, 1.0 })
                {
                    CoverNumbers k = CoverNumbers.Of(l1);
                    k.Cdf = CoverCdf.Of(Volumes, ParamsOf(l1, 1), 32, 100000, 11);
                    Report(Row("L1 type " + type.ToString("0.00") + " MeaningRank", CoverMapping.MeaningRank, l1, 1, k, cs, type, 24, true));
                }
                foreach (double edge in new[] { 0.05, 0.15, 0.3, 0.5 })
                {
                    CoverNumbers k = CoverNumbers.Of(l1);
                    k.Edge = edge;
                    k.Cdf = CoverCdf.Of(Volumes, ParamsOf(l1, 1), 32, 100000, 11);
                    Report(Row("L1 edge " + edge.ToString("0.00") + " MeaningRank", CoverMapping.MeaningRank, l1, 1, k, cs, 0.0, 24, true));
                }
                foreach (double sigma in new[] { 0.005, 0.02, 0.2 })
                {
                    CoverNumbers k = CoverNumbers.Of(l1);
                    k.Sigma = sigma;
                    k.Cdf = CoverCdf.Of(Volumes, ParamsOf(l1, 1), 32, 100000, 11);
                    Report(Row("L1 sigma " + sigma.ToString("0.000") + " MeaningRank", CoverMapping.MeaningRank, l1, 1, k, cs, 0.0, 24, true));
                }
            }
            finally { ColumnConstant = false; }
        }

        [TestMethod, TestCategory("Explore")]
        public void FrameBands()
        {
            CloudLayerDef l1 = CloudLookFixtures.Cumulus();
            var table = new LayerTypeTable(l1);
            Report("FRAME BANDS L1 type 0 (264 m), closed share by elevation band; zenith band = pitch 80 fov 20");
            double[,] bands = { { 0, 5 }, { 5, 10 }, { 10, 20 }, { 20, 50 } };
            foreach (double c in new[] { 0.3, 0.5, 0.7 })
                foreach (CoverMapping m in new[] { CoverMapping.Current, CoverMapping.MeaningRank })
                {
                    CoverNumbers k = CoverNumbers.Of(l1);
                    k.Cdf = CoverCdf.Of(Volumes, ParamsOf(l1, 1), 32, 100000, 11);
                    var sums = new double[5];
                    const int sites = 16;
                    for (int s = 0; s < sites; s++)
                    {
                        NoiseLayerParams prm = ParamsOf(l1, 1);
                        CoverMeaningModel.Place(ref prm, s);
                        for (int b = 0; b < 4; b++)
                            sums[b] += CoverMeaningModel.FrameClosedShare(m, l1, table, Volumes, prm, k, c, 0.0, CloudCoverOracle.Frame.Ground(64, 36, 0.0), 0.92, bands[b, 0], bands[b, 1]);
                        CloudCoverOracle.Frame up = CloudCoverOracle.Frame.Ground(24, 24, 0.0);
                        up.PitchDeg = 80.0;
                        up.FovVDeg = 20.0;
                        sums[4] += CoverMeaningModel.FrameClosedShare(m, l1, table, Volumes, prm, k, c, 0.0, up);
                    }
                    Report("c " + c.ToString("0.0") + " " + m.ToString().PadRight(12) + " 0-5 " + (sums[0] / sites).ToString("0.00") + "  5-10 " + (sums[1] / sites).ToString("0.00") + "  10-20 " + (sums[2] / sites).ToString("0.00") + "  20-50 " + (sums[3] / sites).ToString("0.00") + "  zenith " + (sums[4] / sites).ToString("0.00"));
                }
        }

        // The four Kerbin layers at one place: the union of their closed columns against the field's cover (the low pick 0.473: layer maps 0.406 / 0.665 / 0.110 / 0.501), with the maps as
        // they are and scaled by CloudLookTuning.CombineFactor (the rule coverCombine builds, which emptied the sky under the old mapping).
        [TestMethod, TestCategory("Explore")]
        public void StackUnion()
        {
            CloudLayerDef[] layers = Layers;
            double[] maps = { 0.4056, 0.665, 0.110, 0.501 };
            double cover = 0.473;
            var m = new float[] { (float)maps[0], (float)maps[1], (float)maps[2], (float)maps[3] };
            float factor = CloudLookTuning.CombineFactor(m, 4, (float)cover);
            Report("STACK union at the low pick: field cover " + cover + ", maps " + string.Join(" ", maps) + ", combine factor " + factor.ToString("0.000"));
            foreach (CoverMapping mp in new[] { CoverMapping.Current, CoverMapping.MeaningRank })
                foreach (double scale in new[] { 1.0, factor })
                {
                    double sumUnion = 0;
                    var perLayer = new double[4];
                    const int sites = 24, cells = 14;
                    for (int s = 0; s < sites; s++)
                    {
                        var closed = new bool[4][];
                        for (int li = 0; li < 4; li++)
                        {
                            closed[li] = new bool[cells * cells];
                            CloudLayerDef layer = layers[li];
                            var table = new LayerTypeTable(layer);
                            CoverNumbers k = CoverNumbers.Of(layer);
                            k.Cdf = CoverCdf.Of(Volumes, ParamsOf(layer, li), 32, 60000, 11);
                            NoiseLayerParams prm = ParamsOf(layer, li);
                            CoverMeaningModel.Place(ref prm, s);
                            double spacing = 12000.0;
                            for (int j = 0; j < cells; j++)
                                for (int i = 0; i < cells; i++)
                                    closed[li][j * cells + i] = CoverMeaningModel.ColumnTau(mp, layer, table, Volumes, prm, k, (i - cells / 2) * spacing, (j - cells / 2) * spacing, maps[li] * scale, Types[li], true) >= 0.92;
                        }
                        int any = 0;
                        for (int q = 0; q < cells * cells; q++)
                        {
                            bool u = false;
                            for (int li = 0; li < 4; li++)
                            {
                                if (closed[li][q]) { perLayer[li] += 1.0 / (cells * cells * sites); u = true; }
                            }
                            if (u) any++;
                        }
                        sumUnion += any / (double)(cells * cells);
                    }
                    Report(mp.ToString().PadRight(12) + " maps x " + scale.ToString("0.000") + ": union closed " + (sumUnion / sites).ToString("0.000") + " per layer " + string.Join(" ", Array.ConvertAll(perLayer, x => x.ToString("0.00"))));
                }
        }

        // Where the closed share of the cumulonimbus layer parts from c: the rank of the shape value at the columns' own places against the rank table of the global sample.
        [TestMethod, TestCategory("Explore")]
        public void Slice()
        {
            CloudLayerDef l2 = CloudLookFixtures.Cumulonimbus();
            foreach (bool constant in new[] { false, true })
            {
                ColumnConstant = constant;
                foreach (uint seed in new uint[] { 1u, 2u, 3u })
                {
                    NoiseVolumeSet set = NoiseVolumeSet.Generate(NoiseSizes.ForLevel(1), seed);
                    NoiseLayerParams prm0 = ParamsOf(l2, 2);
                    CoverCdf global = CoverCdf.Of(set, prm0, 32, 100000, 11);
                    int n = 0, a50 = 0, a90 = 0;
                    double sum = 0;
                    for (int site = 0; site < 60; site++)
                    {
                        NoiseLayerParams prm = prm0;
                        CoverMeaningModel.Place(ref prm, site);
                        for (int j = 0; j < 14; j++)
                            for (int i = 0; i < 14; i++)
                            {
                                double hx = (i - 7) * prm.ScaleM * 6.0, hy = (j - 7) * prm.ScaleM * 6.0;
                                double zz = Math.Sqrt(Math.Max(1.0, Math.Pow(600000.0 + 6000.0, 2) - hx * hx - hy * hy));
                                double qx, qy, qz;
                                CloudShapeModel.LookupPoint(ref prm, hx, hy, zz, out qx, out qy, out qz);
                                double u = global.Rank(CloudShapeModel.ShapeAt(set, ref prm, qx, qy, qz));
                                n++;
                                sum += u;
                                if (u >= 0.5) a50++;
                                if (u >= 0.9) a90++;
                            }
                    }
                    Report("SLICE constant=" + constant + " seed " + seed + ": mean rank " + (sum / n).ToString("0.000") + ", P(u>=0.5) " + (a50 / (double)n).ToString("0.000") + ", P(u>=0.9) " + (a90 / (double)n).ToString("0.000"));
                }
            }
            ColumnConstant = false;
        }

        [TestMethod, TestCategory("Explore")]
        public void Cdf()
        {
            CloudLayerDef[] layers = Layers;
            for (int li = 0; li < layers.Length; li++)
            {
                NoiseLayerParams prm = ParamsOf(layers[li], li);
                CoverCdf cdf = CoverCdf.Of(Volumes, prm, 20, 200000, 11);
                var sb = new StringBuilder("CDF " + layers[li].Name + " scale " + prm.ScaleM + " quantiles at 0.05 steps: ");
                for (int i = 0; i < cdf.Quantile.Length; i++) sb.Append(cdf.Quantile[i].ToString("0.000") + " ");
                double dev = 0;
                for (int i = 0; i < cdf.Quantile.Length; i++) dev = Math.Max(dev, Math.Abs(cdf.Quantile[i] - i / 20.0));
                sb.Append(" max |q - rank| " + dev.ToString("0.000"));
                Report(sb.ToString());
            }
            // The same table of the one volume at another scale.
            foreach (double s in new[] { 500.0, 2000.0, 8000.0, 15000.0 })
            {
                NoiseLayerParams prm = ParamsOf(layers[1], 1, s);
                CoverCdf cdf = CoverCdf.Of(Volumes, prm, 20, 200000, 11);
                var sb = new StringBuilder("CDF scale " + s + ": ");
                foreach (int i in new[] { 1, 2, 4, 10, 16, 18, 19 }) sb.Append((i / 20.0).ToString("0.00") + "->" + cdf.Quantile[i].ToString("0.000") + " ");
                Report(sb.ToString());
            }
        }

        [TestMethod, TestCategory("Explore")]
        public void Mappings()
        {
            CloudLayerDef[] layers = Layers;
            Report("closed share (tau >= 0.92, vertical columns, 40 sites x 16x16 columns), c = " + string.Join(" ", Array.ConvertAll(Cs, x => x.ToString("0.0"))));
            for (int li = 0; li < layers.Length; li++)
            {
                CloudLayerDef layer = layers[li];
                CoverNumbers k = CoverNumbers.Of(layer);
                k.Cdf = CoverCdf.Of(Volumes, ParamsOf(layer, li), 32, 200000, 11);
                Report("LAYER " + layer.Name + " type " + Types[li] + " sigma " + k.Sigma.ToString("0.0000") + " edge " + k.Edge.ToString("0.00") + " amount " + k.Amount.ToString("0.00") + " coverage " + k.Coverage.ToString("0.00") + " scale " + layer.EffectiveNoiseScaleM);
                foreach (CoverMapping m in new[] { CoverMapping.Current, CoverMapping.Meaning, CoverMapping.MeaningRank, CoverMapping.AmountShare })
                    Report(Row(m.ToString(), m, layer, li, k, Cs, Types[li], 40, true));
            }
        }

        [TestMethod, TestCategory("Explore")]
        public void Dependence()
        {
            CloudLayerDef l1 = CloudLookFixtures.Cumulus();
            double[] cs = { 0.3, 0.5, 0.7 };
            Report("DEPENDENCE on L1 (columns, 24 sites), c = 0.3 0.5 0.7");
            foreach (double edge in new[] { 0.05, 0.15, 0.3, 0.5 })
                foreach (CoverMapping m in new[] { CoverMapping.Current, CoverMapping.MeaningRank })
                {
                    CoverNumbers k = CoverNumbers.Of(l1);
                    k.Edge = edge;
                    k.Cdf = CoverCdf.Of(Volumes, ParamsOf(l1, 1), 32, 100000, 11);
                    Report(Row("edge " + edge.ToString("0.00") + " " + m, m, l1, 1, k, cs, 0.0, 24, true));
                }
            foreach (double sigma in new[] { 0.005, 0.02, 0.05, 0.2 })
                foreach (CoverMapping m in new[] { CoverMapping.Current, CoverMapping.MeaningRank })
                {
                    CoverNumbers k = CoverNumbers.Of(l1);
                    k.Sigma = sigma;
                    k.Cdf = CoverCdf.Of(Volumes, ParamsOf(l1, 1), 32, 100000, 11);
                    Report(Row("sigma " + sigma.ToString("0.000") + " " + m, m, l1, 1, k, cs, 0.0, 24, true));
                }
            foreach (double type in new[] { 0.0, 0.25, 0.75, 1.0 })
                foreach (CoverMapping m in new[] { CoverMapping.Current, CoverMapping.MeaningRank })
                {
                    CoverNumbers k = CoverNumbers.Of(l1);
                    k.Cdf = CoverCdf.Of(Volumes, ParamsOf(l1, 1), 32, 100000, 11);
                    Report(Row("type " + type.ToString("0.00") + " " + m, m, l1, 1, k, cs, type, 24, true));
                }
            foreach (double amount in new[] { 0.35, 0.7, 1.0 })
                foreach (double scale in new[] { 500.0, 2500.0, 5000.0 })
                {
                    CoverNumbers k = CoverNumbers.Of(l1);
                    k.Amount = amount;
                    Report(Row("CURRENT amount " + amount.ToString("0.00") + " scale " + scale, CoverMapping.Current, l1, 1, k, cs, 0.0, 24, true, scale));
                }
            foreach (double amount in new[] { 0.3, 0.6, 1.0 })
            {
                CoverNumbers k = CoverNumbers.Of(l1);
                k.Amount = amount;
                k.Cdf = CoverCdf.Of(Volumes, ParamsOf(l1, 1), 32, 100000, 11);
                Report(Row("AmountShare amount " + amount.ToString("0.00"), CoverMapping.AmountShare, l1, 1, k, cs, 0.0, 24, true));
            }
        }

        [TestMethod, TestCategory("Explore")]
        public void Frame()
        {
            CloudLayerDef l1 = CloudLookFixtures.Cumulus();
            var table = new LayerTypeTable(l1);
            Report("FRAME (ground 264 m, 18 deg up, 60 deg fov, 64x36, 24 sites) L1 type 0: closed share above the horizon");
            foreach (double c in new[] { 0.3, 0.5, 0.515, 0.665, 0.8 })
                foreach (CoverMapping m in new[] { CoverMapping.Current, CoverMapping.MeaningRank })
                {
                    CoverNumbers k = CoverNumbers.Of(l1);
                    k.Cdf = CoverCdf.Of(Volumes, ParamsOf(l1, 1), 32, 100000, 11);
                    double sum = 0;
                    const int sites = 24;
                    for (int s = 0; s < sites; s++)
                    {
                        NoiseLayerParams prm = ParamsOf(l1, 1);
                        CoverMeaningModel.Place(ref prm, s);
                        sum +=CoverMeaningModel.FrameClosedShare(m, l1, table, Volumes, prm, k, c, 0.0, CloudCoverOracle.Frame.Ground(64, 36, 0.0));
                    }
                    Report("c " + c.ToString("0.000") + " " + m + " frame closed " + (sum / sites).ToString("0.000"));
                }
        }
    }
}
