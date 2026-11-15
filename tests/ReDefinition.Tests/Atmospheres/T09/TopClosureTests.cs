using System;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The top of a cloud is where its threshold closes the column, never a plane at the type's top (E40 CB step 0). The layer shader's density at a point is
    // sigma x share(map - lift(h) x headroom, noise) x profile(h); the share is the coverage threshold moved by the shape noise, the profile only scales it. If any column
    // still passes the threshold at the type's top, the profile's last tenth cuts it there: a flat lid at one altitude over every strong column (the slabs of game start 60/61).
    // The columns here are the product's (CloudShapeModel with the layer's vertical scale and the type's smoothing, the layer's effective numbers at the default softness).
    [TestClass]
    public class TopClosureTests
    {
        private static NoiseVolumeSet volumes;
        private static NoiseVolumeSet Volumes { get { return volumes ?? (volumes = NoiseVolumeSet.Generate(NoiseSizes.ForLevel(1), 1u)); } }
        private const double R = 600000.0;
        private const int N = 24, Steps = 48, Sites = 2;

        private static CloudLayerDef[] Layers()
        {
            return new[] { CloudLookFixtures.Rain(), CloudLookFixtures.Cumulus(), CloudLookFixtures.Cumulonimbus(), CloudLookFixtures.Cirrus() };
        }

        // The coverage share of the shader at a height share h of the type over the place (hx, hy): the map value lowered by the type's lift (map units).
        private static double ShareAt(CloudLayerDef layer, LayerTypeTable table, NoiseLayerParams prm, double hx, double hy, double map, double typeValue, double h)
        {
            double eb, et;
            table.InterpolatedHeights(typeValue, out eb, out et);
            double share = eb + (et - eb) * h;
            double rr = R + layer.InnerAltitudeM + share * layer.ThicknessM, zz = Math.Sqrt(Math.Max(1.0, rr * rr - hx * hx - hy * hy));
            double lift = table.LiftOf(typeValue, share);
            return CloudShapeModel.Share(Volumes, ref prm, hx, hy, zz, layer.ClearSkyMap((float)map) - lift, layer.EffectiveCoverage, layer.EffectiveEdgeSoftness,
                                         layer.EffectiveNoiseAmount, 0.0, false, 0.0);
        }

        private static NoiseLayerParams ParamsOf(CloudLayerDef layer, LayerTypeTable table, int index, int site, double typeValue)
        {
            NoiseLayerParams prm = CoverMeaningExplore.ParamsOf(layer, index);
            if (layer.VerticalScale > 0f) prm.SphereScale = layer.VerticalScale;
            prm.ReferenceRadiusM = R + 0.5 * (layer.InnerAltitudeM + layer.OuterAltitudeM);
            prm.ShapeLod = CloudShapeModel.SmoothingLod(table.SmoothingOf(typeValue), Volumes.ShapeN);
            CoverMeaningModel.Place(ref prm, site);
            return prm;
        }

        // Per type and map value: the share of the columns with cloud anywhere whose body (share >= 0.5) still stands at 98 % of the type's height, the cut; and the share of the
        // cloudy columns whose top (the highest height with body) lies within the top tenth of the type's height, the plateau (a lid a little below the type's top is still a lid).
        private static double CutShare(CloudLayerDef layer, LayerTypeTable table, int index, double typeValue, double map, out int cloudy)
        {
            double plateau;
            return CutShare(layer, table, index, typeValue, map, out cloudy, out plateau);
        }

        internal static double CutShare(CloudLayerDef layer, LayerTypeTable table, int index, double typeValue, double map, out int cloudy, out double plateau)
        {
            int cut = 0, high = 0;
            cloudy = 0;
            for (int s = 0; s < Sites; s++)
            {
                NoiseLayerParams prm = ParamsOf(layer, table, index, s, typeValue);
                for (int j = 0; j < N; j++)
                    for (int i = 0; i < N; i++)
                    {
                        double hx = (i - N / 2) * prm.ScaleM * 0.7, hy = (j - N / 2) * prm.ScaleM * 0.7;
                        int top = -1;
                        for (int k = Steps - 1; k >= 0 && top < 0; k--)
                            if (ShareAt(layer, table, prm, hx, hy, map, typeValue, (k + 0.5) / Steps) >= 0.5) top = k;
                        if (top < 0) continue;
                        cloudy++;
                        if ((top + 0.5) / Steps >= 0.9) high++;
                        if (ShareAt(layer, table, prm, hx, hy, map, typeValue, 0.98) >= 0.5) cut++;
                    }
            }
            plateau = cloudy == 0 ? 0.0 : high / (double)cloudy;
            return cloudy == 0 ? 0.0 : cut / (double)cloudy;
        }

        [TestMethod]
        public void NoColumnOfAnyTypeStandsAtItsTypesTop()
        {
            CloudSoftDensity.DetailMean = Volumes.DetailMean;
            var report = new StringBuilder();
            bool bad = false;
            CloudLayerDef[] layers = Layers();
            for (int li = 0; li < layers.Length; li++)
            {
                CloudLayerDef layer = layers[li];
                var table = new LayerTypeTable(layer);
                for (int t = 0; t < layer.Types.Count; t++)
                {
                    double typeValue = layer.Types.Count > 1 ? t / (double)(layer.Types.Count - 1) : 0.0;
                    report.Append(layer.Types[t].Name.PadRight(22) + " headroom " + LayerTypeTable.LiftHeadroom(layer).ToString("0.000") + ":");
                    foreach (double map in new[] { 0.5, 0.8, 1.0 })
                    {
                        int cloudy;
                        double plateau;
                        double cut = CutShare(layer, table, li + 1, typeValue, map, out cloudy, out plateau);
                        report.Append("  map " + map.ToString("0.0") + " cut " + cut.ToString("0.00") + " top tenth " + plateau.ToString("0.00") + " of " + cloudy);
                        if (cut > 0.0) bad = true;
                    }
                    report.AppendLine();
                }
            }
            Console.WriteLine(report);
            Assert.IsFalse(bad, "columns stand at their type's top (a flat lid):\n" + report);
        }

        // The density of every type goes to 0 at its top (the profile's last sample), and nothing is above the table's top: the march's interval (TightAltitudes) holds every height
        // with density.
        [TestMethod]
        public void TheDensityEndsAtTheTopAndTheMarchIntervalHoldsIt()
        {
            foreach (CloudLayerDef layer in Layers())
            {
                var table = new LayerTypeTable(layer);
                float inner, outer;
                layer.TightAltitudes(out inner, out outer);
                for (int t = 0; t < layer.Types.Count; t++)
                {
                    Assert.IsTrue(table.ProfileAt(t, 1.0) <= 0.01f, layer.Types[t].Name + " profile at the top " + table.ProfileAt(t, 1.0));
                    double typeValue = layer.Types.Count > 1 ? t / (double)(layer.Types.Count - 1) : 0.0;
                    for (double alt = outer + 1.0; alt < layer.OuterAltitudeM + 3000.0; alt += 100.0)
                        Assert.AreEqual(0.0, LayerDensity.Extinction(layer, table, alt, 1.0, typeValue), layer.Types[t].Name + " above the march interval at " + alt);
                }
            }
        }
    }
}
