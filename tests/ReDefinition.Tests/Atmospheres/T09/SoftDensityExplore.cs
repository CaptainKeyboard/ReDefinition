using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // E40 soft density: the measurement run behind the "closed share per map value" table of .local/plans/atmospheres/E40-soft-density-result.md. Columns through a layer with the
    // soft water content (CloudWater) on the product's noise lookup and detail; per map value the share of columns that are closed (optical depth >= 0.92), that show any cloud
    // (>= 0.05) and the median optical depth of the closed ones. It runs only when SOFT_REPORT names the output file (minutes of CPU).
    [TestClass]
    public class SoftDensityExplore
    {
        private static NoiseVolumeSet volumes;
        private static NoiseVolumeSet Volumes { get { return volumes ?? (volumes = NoiseVolumeSet.Generate(NoiseSizes.ForLevel(1), 1u)); } }
        private static readonly double[] Cs = { 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9 };

        // The optical depth of a vertical column with the soft water content at the horizontal place (hx, hy).
        internal static double ColumnTau(CloudLayerDef layer, LayerTypeTable table, NoiseVolumeSet set, NoiseLayerParams prm, CloudWaterNumbers k, double closed, double hx, double hy,
                                         double c, double typeValue, int steps = 48)
        {
            const double R = 600000.0;
            double eb, et;
            table.InterpolatedHeights(typeValue, out eb, out et);
            if (!(et > eb)) return 0.0;
            int ia, ib;
            double f;
            table.Neighbours(typeValue, out ia, out ib, out f);
            float b0, t0, e0, b1, t1, e1;
            table.Info(ia, out b0, out t0, out e0);
            table.Info(ib, out b1, out t1, out e1);
            double erosion = e0 + (e1 - e0) * f, threshold = CloudWater.Threshold(c, 0.0, closed, k);
            if (CloudWater.Empty(threshold, closed, k)) return 0.0;
            double span = (et - eb) * layer.ThicknessM, dz = span / steps, tau = 0.0;
            for (int q = 0; q < steps; q++)
            {
                double h = (q + 0.5) / steps, share = eb + (et - eb) * h;
                double rr = R + layer.InnerAltitudeM + share * layer.ThicknessM, zz = Math.Sqrt(Math.Max(1.0, rr * rr - hx * hx - hy * hy));
                double qx, qy, qz;
                CloudShapeModel.LookupPoint(ref prm, hx, hy, zz, out qx, out qy, out qz);
                double u = CloudWater.Content(CloudShapeModel.ShapeAt(set, ref prm, qx, qy, qz), threshold, closed, k);
                if (u <= 0.0) continue;
                if (u < 1.0 && erosion > 0.0)
                {
                    double d = CloudShapeModel.DetailAt(set, ref prm, qx, qy, qz, hx / rr, hy / rr, zz / rr);
                    double d2 = CloudShapeModel.DetailAt(set, ref prm, qx, qy, qz, hx / rr, hy / rr, zz / rr, NoiseModel.FineDetailFrequency);
                    d += (d2 - d) * NoiseModel.FineDetailWeight;
                    u = CloudWater.Erode(u, d, erosion);
                }
                tau += CloudWater.Density(layer.EffectiveSigmaPerM, u, table.Profile(typeValue, share), h, k) * dz;
            }
            return tau;
        }

        // The water content at the outline of a layer's type.
        internal static double Closed(CloudLayerDef layer, LayerTypeTable table, double typeValue, CloudWaterNumbers k, double detailMean)
        {
            double eb, et;
            table.InterpolatedHeights(typeValue, out eb, out et);
            int ia, ib;
            double f;
            table.Neighbours(typeValue, out ia, out ib, out f);
            float b0, t0, e0, b1, t1, e1;
            table.Info(ia, out b0, out t0, out e0);
            table.Info(ib, out b1, out t1, out e1);
            double mean = CloudWater.ColumnMean(h => table.Profile(typeValue, eb + (et - eb) * h), k);
            return CloudWater.Closed(layer.EffectiveSigmaPerM * (et - eb) * layer.ThicknessM * mean, e0 + (e1 - e0) * f, detailMean, k);
        }

        private static string Row(string label, CloudLayerDef layer, int index, double typeValue, CloudWaterNumbers k, int sites, int cells)
        {
            var table = new LayerTypeTable(layer);
            double closed = Closed(layer, table, typeValue, k, Volumes.DetailMean);
            var line = new StringBuilder(label.PadRight(44) + " closed-content " + closed.ToString("0.000") + " | ");
            double err = 0.0;
            var any = new StringBuilder();
            var med = new StringBuilder();
            foreach (double c in Cs)
            {
                int hit = 0, some = 0, n = 0;
                var taus = new System.Collections.Generic.List<double>();
                for (int s = 0; s < sites; s++)
                {
                    NoiseLayerParams prm = CoverMeaningExplore.ParamsOf(layer, index);
                    CoverMeaningModel.Place(ref prm, s);
                    for (int j = 0; j < cells; j++)
                        for (int i = 0; i < cells; i++)
                        {
                            double tau = ColumnTau(layer, table, Volumes, prm, k, closed, (i - cells / 2) * prm.ScaleM * 6.0, (j - cells / 2) * prm.ScaleM * 6.0, c, typeValue);
                            n++;
                            if (tau >= 0.05) some++;
                            if (tau >= CloudWater.ClosedTau) { hit++; taus.Add(tau); }
                        }
                }
                double share = hit / (double)n;
                err += Math.Abs(share - c);
                line.Append(share.ToString("0.00") + " ");
                any.Append((some / (double)n).ToString("0.00") + " ");
                taus.Sort();
                med.Append((taus.Count > 0 ? taus[taus.Count / 2] : 0.0).ToString("0.0") + " ");
            }
            return line + "| mean|err| " + (err / Cs.Length).ToString("0.000") + " | any " + any + "| median tau closed " + med;
        }

        [TestMethod, TestCategory("Explore")]
        public void ClosedSharePerMapValue()
        {
            string path = Environment.GetEnvironmentVariable("SOFT_REPORT");
            if (string.IsNullOrEmpty(path)) return;
            CloudLayerDef[] layers = { CloudLookFixtures.Rain(), CloudLookFixtures.Cumulus(), CloudLookFixtures.Cumulonimbus() };
            double[][] types = { new[] { 0.5 }, new[] { 0.0, 0.25, 0.75 }, new[] { 0.229 } };
            var sets = new[]
            {
                new CloudWaterNumbers { Width = 0.3, Power = 2.0, BaseGain = 0.3, RiseShare = 0.5 },
                new CloudWaterNumbers { Width = 0.5, Power = 2.0, BaseGain = 0.3, RiseShare = 0.5 },
                new CloudWaterNumbers { Width = 0.5, Power = 3.0, BaseGain = 0.3, RiseShare = 0.5 },
                new CloudWaterNumbers { Width = 1.0, Power = 2.0, BaseGain = 0.3, RiseShare = 0.5 },
            };
            File.AppendAllText(path, "SOFT WATER CONTENT, closed share of columns (tau >= 0.92) at c = " + string.Join(" ", Array.ConvertAll(Cs, x => x.ToString("0.0"))) + Environment.NewLine);
            for (int li = 0; li < layers.Length; li++)
                foreach (double type in types[li])
                    foreach (CloudWaterNumbers k in sets)
                        File.AppendAllText(path, Row(layers[li].Name + " type " + type.ToString("0.000") + " w " + k.Width.ToString("0.0") + " k " + k.Power.ToString("0"), layers[li], li, type, k, 30, 12)
                                                 + Environment.NewLine);
        }
    }
}
