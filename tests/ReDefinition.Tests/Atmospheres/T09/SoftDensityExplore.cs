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

        // The optical depth of a vertical column through the PRODUCT's density: the layer's effective numbers (CloudLayerDef), the clear-sky rule, the share of CloudShapeModel with
        // the layer's exponent, the type table with the rise by height and the density scale baked in. No other mapping of this file.
        internal static double ProductColumnTau(CloudLayerDef layer, LayerTypeTable table, NoiseVolumeSet set, NoiseLayerParams prm, double hx, double hy, double map, double typeValue,
                                                int steps = 48, double liftScale = 0.0)
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
            double erosion = e0 + (e1 - e0) * f, span = (et - eb) * layer.ThicknessM, dz = span / steps, tau = 0.0;
            float clearMap = layer.ClearSkyMap((float)map);
            for (int q = 0; q < steps; q++)
            {
                double share = eb + (et - eb) * (q + 0.5) / steps;
                double rr = R + layer.InnerAltitudeM + share * layer.ThicknessM, zz = Math.Sqrt(Math.Max(1.0, rr * rr - hx * hx - hy * hy));
                // The shader's height-shaped threshold (E40 L2 D1): the map value lowered by the type's lift at this height times layers.profileShape.
                double lift = liftScale > 0.0 ? liftScale * table.LiftOf(typeValue, share) : 0.0;
                double s = CloudShapeModel.Share(set, ref prm, hx, hy, zz, clearMap - lift, layer.EffectiveCoverage, layer.EffectiveEdgeSoftness, layer.EffectiveNoiseAmount, erosion, true, dz);
                if (s <= 0.0) continue;
                s = CloudWater.Pow(s, layer.EffectiveWaterPower);
                tau += layer.EffectiveSigmaPerM * s * table.Profile(typeValue, share) * dz;
            }
            return tau;
        }

        private static double Percentile(System.Collections.Generic.List<double> sorted, double p)
        {
            return sorted.Count == 0 ? 0.0 : sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];
        }

        // The opacity table of E40 B1 (looking straight up, per layer and per type, at the map values 0.2 .. 0.8): the share of columns with any cloud (tau >= 0.05), over those the 10th,
        // 50th and 90th percentile of the optical depth and of alpha = 1 - exp(-tau), and the share of the whole sky with alpha above 0.5 and above 0.95. Runs only with SOFT_REPORT.
        [TestMethod, TestCategory("Explore")]
        public void OpacityPerTypeThroughTheProduct()
        {
            string path = Environment.GetEnvironmentVariable("SOFT_REPORT");
            if (string.IsNullOrEmpty(path)) return;
            CloudLayerDef[] layers = { CloudLookFixtures.Rain(), CloudLookFixtures.Cumulus(), CloudLookFixtures.Cumulonimbus(), CloudLookFixtures.Cirrus() };
            double[] maps = { 0.2, 0.4, 0.6, 0.8 };
            CloudSoftDensity.DetailMean = Volumes.DetailMean;
            File.AppendAllText(path, "OPACITY PER TYPE, product mapping (softness " + CloudSoftDensity.Softness.ToString("0.0") + ", detail mean " + Volumes.DetailMean.ToString("0.000")
                                     + "); per map value: any% | tau p10/p50/p90 | alpha p10/p50/p90 | sky alpha>0.5 % | sky alpha>0.95 %" + Environment.NewLine);
            for (int li = 0; li < layers.Length; li++)
            {
                CloudLayerDef layer = layers[li];
                var table = new LayerTypeTable(layer);
                File.AppendAllText(path, "LAYER " + layer.Name + " sigma " + layer.EffectiveSigmaPerM.ToString("0.0000") + " thickness " + layer.ThicknessM.ToString("0") + " closed " + layer.SoftClosed.ToString("0.000")
                                         + " K " + layer.ClearSkyK.ToString("0.000") + " power " + layer.EffectiveWaterPower.ToString("0.0") + Environment.NewLine);
                for (int t = 0; t < layer.Types.Count; t++)
                {
                    double typeValue = layer.Types.Count == 1 ? 0.0 : t / (double)(layer.Types.Count - 1);
                    double thick = (layer.Types[t].TopShare - layer.Types[t].BaseShare) * layer.ThicknessM;
                    // The centre of the full cloud: the type's whole column at full water content, through the table (profile x rise x density scale).
                    double meanProfile = 0.0;
                    for (int q = 0; q < 64; q++) meanProfile += table.Profile(typeValue, layer.Types[t].BaseShare + (layer.Types[t].TopShare - layer.Types[t].BaseShare) * (q + 0.5) / 64.0) / 64.0;
                    var line = new StringBuilder("  " + layer.Types[t].Name.PadRight(20) + " full-column tau " + (layer.EffectiveSigmaPerM * thick * meanProfile).ToString("0.0") + " :");
                    foreach (double map in maps)
                    {
                        var taus = new System.Collections.Generic.List<double>();
                        int n = 0, a50 = 0, a95 = 0;
                        for (int s = 0; s < 12; s++)
                        {
                            NoiseLayerParams prm = CoverMeaningExplore.ParamsOf(layer, li);
                            CoverMeaningModel.Place(ref prm, s);
                            for (int j = 0; j < 10; j++)
                                for (int i = 0; i < 10; i++)
                                {
                                    double tau = ProductColumnTau(layer, table, Volumes, prm, (i - 5) * prm.ScaleM * 6.0, (j - 5) * prm.ScaleM * 6.0, map, typeValue);
                                    n++;
                                    double alpha = 1.0 - Math.Exp(-tau);
                                    if (alpha > 0.5) a50++;
                                    if (alpha > 0.95) a95++;
                                    if (tau >= 0.05) taus.Add(tau);
                                }
                        }
                        taus.Sort();
                        var alphas = taus.ConvertAll(x => 1.0 - Math.Exp(-x));
                        line.Append("  map " + map.ToString("0.0") + ": " + (100.0 * taus.Count / n).ToString("0") + "% | " + Percentile(taus, 0.1).ToString("0.0") + "/" + Percentile(taus, 0.5).ToString("0.0")
                                    + "/" + Percentile(taus, 0.9).ToString("0.0") + " | " + Percentile(alphas, 0.1).ToString("0.00") + "/" + Percentile(alphas, 0.5).ToString("0.00") + "/"
                                    + Percentile(alphas, 0.9).ToString("0.00") + " | " + (100.0 * a50 / n).ToString("0") + "% | " + (100.0 * a95 / n).ToString("0") + "%");
                    }
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
        }

        // The structure of a cloud field seen from above (E40 B3): the columns of a patch of the layer on a grid, closed where the vertical optical depth is 0.92 or more (the product's
        // density). Per map value the closed share and how it is connected: the share of the closed area that is one piece (the largest 4-connected component) and the number of pieces
        // per 100 closed columns. A field of separate cells has a small largest piece and many pieces; a plate with holes is one large piece. Runs only with SOFT_REPORT.
        [TestMethod, TestCategory("Explore")]
        public void CellStructureFromAbove()
        {
            string path = Environment.GetEnvironmentVariable("SOFT_REPORT");
            if (string.IsNullOrEmpty(path)) return;
            CloudSoftDensity.DetailMean = Volumes.DetailMean;
            const int N = 56;
            double[] maps = { 0.2, 0.3, 0.4, 0.5, 0.6 };
            File.AppendAllText(path, "CELL STRUCTURE FROM ABOVE, product mapping (softness " + CloudSoftDensity.Softness.ToString("0.0") + "), " + N + " x " + N + " columns at half a noise scale, 6 sites; "
                                     + "per map value: closed share | largest piece of the closed area | pieces per 100 closed columns" + Environment.NewLine);
            foreach (double typeValue in new[] { 0.0, 0.25 })
                foreach (double scale in new[] { 500.0, 800.0, 1200.0 })
                {
                    CloudLayerDef layer = CloudLookFixtures.Cumulus();
                    layer.NoiseScaleM = (float)scale;
                    var table = new LayerTypeTable(layer);
                    var line = new StringBuilder("  " + (typeValue == 0.0 ? "Stratocumulus" : "Cumulus humilis").PadRight(16) + " noise scale " + scale.ToString("0").PadLeft(5) + " m :");
                    foreach (double map in maps)
                    {
                        double share = 0.0, largest = 0.0, pieces = 0.0;
                        int sites = 6;
                        for (int s = 0; s < sites; s++)
                        {
                            NoiseLayerParams prm = CoverMeaningExplore.ParamsOf(layer, 1);
                            CoverMeaningModel.Place(ref prm, s);
                            var closed = new bool[N * N];
                            int count = 0;
                            for (int j = 0; j < N; j++)
                                for (int i = 0; i < N; i++)
                                {
                                    double tau = ProductColumnTau(layer, table, Volumes, prm, (i - N / 2) * scale * 0.5, (j - N / 2) * scale * 0.5, map, typeValue, 32);
                                    if (tau >= CloudWater.ClosedTau) { closed[j * N + i] = true; count++; }
                                }
                            int biggest, components;
                            Components(closed, N, out biggest, out components);
                            share += count / (double)(N * N) / sites;
                            if (count > 0)
                            {
                                largest += biggest / (double)count / sites;
                                pieces += 100.0 * components / count / sites;
                            }
                        }
                        line.Append("  map " + map.ToString("0.0") + ": " + share.ToString("0.00") + " | " + largest.ToString("0.00") + " | " + pieces.ToString("0.0"));
                    }
                    File.AppendAllText(path, line + Environment.NewLine);
                }
        }

        // Whether the cloud body stands at a height share h of the type over the place (hx, hy): the shader's coverage share of the point is at least a half (the soft edge's middle),
        // with the type's lift times liftScale taken off the map value.
        private static bool BodyAt(CloudLayerDef layer, LayerTypeTable table, NoiseVolumeSet set, NoiseLayerParams prm, double hx, double hy, double map, double typeValue, double h, double liftScale)
        {
            const double R = 600000.0;
            double eb, et;
            table.InterpolatedHeights(typeValue, out eb, out et);
            double share = eb + (et - eb) * h;
            double rr = R + layer.InnerAltitudeM + share * layer.ThicknessM, zz = Math.Sqrt(Math.Max(1.0, rr * rr - hx * hx - hy * hy));
            double lift = liftScale > 0.0 ? liftScale * table.LiftOf(typeValue, share) : 0.0;
            return CloudShapeModel.Share(set, ref prm, hx, hy, zz, layer.ClearSkyMap((float)map) - lift, layer.EffectiveCoverage, layer.EffectiveEdgeSoftness, layer.EffectiveNoiseAmount, 0.0, false, 0.0) >= 0.5;
        }

        // E40 L2 D1 (the height-shaped threshold), per type of a layer at the map values 0.3 / 0.5 / 0.7 and the lift scale 0 (the column shape) and 1 (the cfg's LIFT):
        //   closed: the share of columns looking up with optical depth 0.92 or more (the cover meaning's measure, which the lift must leave near what the map says)
        //   base / top: the share of the sky where the cloud body stands at 10 percent and at 90 percent of the type's height, and over the top level the number of separate pieces
        //   per 100 body columns and the share of the body that is its largest piece (a plate with holes is one large piece, a field of heads is many small ones).
        // Runs only with SOFT_REPORT.
        [TestMethod, TestCategory("Explore")]
        public void LiftedShapePerType()
        {
            string path = Environment.GetEnvironmentVariable("SOFT_REPORT");
            if (string.IsNullOrEmpty(path)) return;
            CloudSoftDensity.DetailMean = Volumes.DetailMean;
            const int N = 40;
            File.AppendAllText(path, "HEIGHT-SHAPED THRESHOLD, product mapping (softness " + CloudSoftDensity.Softness.ToString("0.0") + "), " + N + " x " + N + " columns at half a noise scale, 4 sites; per map value:"
                                     + " closed share (lift 0 -> 1) | body at 10 percent of the height (lift 1) | body at 90 percent (lift 0 -> 1) | top level: pieces per 100 body columns, largest piece (lift 0 -> 1)" + Environment.NewLine);
            CloudLayerDef[] layers = { CloudLookFixtures.Cumulus(), CloudLookFixtures.Cumulonimbus() };
            // The vertical scale of the noise lattice (the body key sphereScale; 1: the planet-centred lattice, below 1 a feature is longer in the vertical): both are measured.
            foreach (double sphere in new[] { 1.0, 0.4 })
            for (int li = 0; li < layers.Length; li++)
            {
                CloudLayerDef layer = layers[li];
                var table = new LayerTypeTable(layer);
                for (int t = 0; t < layer.Types.Count; t++)
                {
                    double typeValue = t / (double)(layer.Types.Count - 1);
                    var line = new StringBuilder("  sphereScale " + sphere.ToString("0.0") + " " + layer.Types[t].Name.PadRight(20) + " :");
                    foreach (double map in new[] { 0.3, 0.5, 0.7 })
                    {
                        var closed = new double[2];
                        double baseShare = 0.0;
                        var top = new double[2];
                        var pieces = new double[2];
                        var largest = new double[2];
                        int sites = 4;
                        for (int s = 0; s < sites; s++)
                        {
                            NoiseLayerParams prm = CoverMeaningExplore.ParamsOf(layer, li + 1);
                            prm.SphereScale = sphere;
                            prm.ReferenceRadiusM = 600000.0 + 0.5 * (layer.InnerAltitudeM + layer.OuterAltitudeM);
                            CoverMeaningModel.Place(ref prm, s);
                            for (int variant = 0; variant < 2; variant++)
                            {
                                var topBody = new bool[N * N];
                                int count = 0, closedCount = 0, baseCount = 0;
                                for (int j = 0; j < N; j++)
                                    for (int i = 0; i < N; i++)
                                    {
                                        double hx = (i - N / 2) * prm.ScaleM * 0.5, hy = (j - N / 2) * prm.ScaleM * 0.5;
                                        if (ProductColumnTau(layer, table, Volumes, prm, hx, hy, map, typeValue, 24, variant) >= CloudWater.ClosedTau) closedCount++;
                                        if (variant == 1 && BodyAt(layer, table, Volumes, prm, hx, hy, map, typeValue, 0.1, 1.0)) baseCount++;
                                        if (BodyAt(layer, table, Volumes, prm, hx, hy, map, typeValue, 0.9, variant)) { topBody[j * N + i] = true; count++; }
                                    }
                                int biggest, components;
                                Components(topBody, N, out biggest, out components);
                                closed[variant] += closedCount / (double)(N * N) / sites;
                                top[variant] += count / (double)(N * N) / sites;
                                if (variant == 1) baseShare += baseCount / (double)(N * N) / sites;
                                if (count > 0)
                                {
                                    pieces[variant] += 100.0 * components / count / sites;
                                    largest[variant] += biggest / (double)count / sites;
                                }
                            }
                        }
                        line.Append("  map " + map.ToString("0.0") + ": " + closed[0].ToString("0.00") + "->" + closed[1].ToString("0.00") + " | " + baseShare.ToString("0.00") + " | " + top[0].ToString("0.00") + "->"
                                    + top[1].ToString("0.00") + " | " + pieces[0].ToString("0.0") + "->" + pieces[1].ToString("0.0") + ", " + largest[0].ToString("0.00") + "->" + largest[1].ToString("0.00"));
                    }
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
        }

        // The largest 4-connected piece (in cells) and the number of pieces of a boolean grid.
        private static void Components(bool[] grid, int n, out int biggest, out int components)
        {
            var seen = new bool[grid.Length];
            var stack = new System.Collections.Generic.Stack<int>();
            biggest = 0;
            components = 0;
            for (int start = 0; start < grid.Length; start++)
            {
                if (!grid[start] || seen[start]) continue;
                components++;
                int size = 0;
                stack.Push(start);
                seen[start] = true;
                while (stack.Count > 0)
                {
                    int k = stack.Pop();
                    size++;
                    int x = k % n, y = k / n;
                    if (x > 0 && grid[k - 1] && !seen[k - 1]) { seen[k - 1] = true; stack.Push(k - 1); }
                    if (x < n - 1 && grid[k + 1] && !seen[k + 1]) { seen[k + 1] = true; stack.Push(k + 1); }
                    if (y > 0 && grid[k - n] && !seen[k - n]) { seen[k - n] = true; stack.Push(k - n); }
                    if (y < n - 1 && grid[k + n] && !seen[k + n]) { seen[k + n] = true; stack.Push(k + n); }
                }
                if (size > biggest) biggest = size;
            }
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
