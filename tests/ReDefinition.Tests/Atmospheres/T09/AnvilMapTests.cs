using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // E40 CB: the anvil of a cumulonimbus as map data (AnvilMap). One Gaussian cell on the equator of an equirectangular map, the shipped cumulonimbus layer (CloudLookFixtures,
    // pinned to the cfg): the anvil's reach follows the outflow law R = r sqrt(1 + w tau / d), grows with the cell's updraft, takes the body's own sizes, is carried downwind by the
    // shear, and is the same for the same input.
    [TestClass]
    public class AnvilMapTests
    {
        private const int W = 512, H = 256;
        // The updraft of a full-strength cell of the test body (Kerbin: sqrt(B g H) = 6 m/s, WeatherGenerator.CellUpdraftMs); the layer takes it from AnvilInputs.UpdraftMs.
        private const double FullCellMs = 6.0;

        [TestMethod]
        public void TheFullCellUpdraftIsTheBodysUnlessTheLayerNamesOne()
        {
            CloudLayerDef cb = CloudLookFixtures.Cumulonimbus();
            Assert.AreEqual(0f, cb.AnvilUpdraftMs, 0f, "the shipped layer names none");
            Assert.AreEqual(4.5, AnvilMap.FullUpdraftMs(cb, new AnvilInputs { UpdraftMs = 4.5 }), 0.0, "the body's");
            Assert.AreEqual(0.0, AnvilMap.FullUpdraftMs(cb, new AnvilInputs()), 0.0, "a body without a generator: no outflow");
            cb.AnvilUpdraftMs = 3f;
            Assert.AreEqual(3.0, AnvilMap.FullUpdraftMs(cb, new AnvilInputs { UpdraftMs = 4.5 }), 0.0, "the layer's number replaces the body's");
            cb.AnvilUpdraftMs = 0f;
            byte[] c1, t1, c2, t2;
            Cell(cb, 600000.0, 12.0, 1.0, out c1, out t1);
            Cell(cb, 600000.0, 12.0, 1.0, out c2, out t2);
            var weakBody = new AnvilMap.Report();
            var strongBody = new AnvilMap.Report();
            AnvilMap.Apply(cb, c1, t1, new AnvilInputs { Width = W, Height = H, RadiusM = 600000.0, UpdraftMs = 3.0 }, weakBody);
            AnvilMap.Apply(cb, c2, t2, new AnvilInputs { Width = W, Height = H, RadiusM = 600000.0, UpdraftMs = 9.0 }, strongBody);
            Assert.IsTrue(strongBody.ReachM[0] > weakBody.ReachM[0], "a body with a stronger full cell spreads a wider anvil");
        }

        // The cell: coverage from 0.3 outside to peak at the centre (flat core where the Gaussian is above 1 / 1.4), type value rainType x the same shape (the precipitation axis).
        private static void Cell(CloudLayerDef layer, double radiusM, double cellKm, double peak, out byte[] cover, out byte[] type)
        {
            cover = new byte[W * H];
            type = new byte[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    double lat = Math.PI * (0.5 - (y + 0.5) / H), lon = 2.0 * Math.PI * ((x + 0.5) / W - 0.5);
                    double ang = Math.Acos(Math.Max(-1.0, Math.Min(1.0, Math.Cos(lat) * Math.Cos(lon))));
                    double bump = Math.Min(1.0, 1.4 * Math.Exp(-Math.Pow(ang * radiusM / 1000.0 / cellKm, 2.0)));
                    cover[y * W + x] = (byte)Math.Round(Math.Min(1.0, 0.3 + (peak - 0.3) * bump) * 255.0);
                    type[y * W + x] = (byte)Math.Round(layer.RainType * bump * peak * 255.0);
                }
        }

        private static AnvilMap.Report Run(CloudLayerDef layer, double radiusM, double cellKm, double peak, double shear, out byte[] cover, out byte[] type)
        {
            Cell(layer, radiusM, cellKm, peak, out cover, out type);
            var report = new AnvilMap.Report();
            AnvilMap.Apply(layer, cover, type, new AnvilInputs { Width = W, Height = H, RadiusM = radiusM, ShearRadS = shear, UpdraftMs = FullCellMs }, report);
            return report;
        }

        // The farthest texel from the cell's centre along the equator row, east (sign 1) or west (-1), in m, whose coverage reaches the layer's outline (a column with the noise's middle value
        // is cloud there): the sheet's radius as the eye sees it, the thin edge beyond it not counted.
        private static double Extent(byte[] cover, CloudLayerDef layer, double radiusM, int sign)
        {
            double outline = AnvilMap.MedianRaw(layer) * 255.0;
            int y = H / 2, cx = W / 2;
            double far = 0.0;
            for (int o = 1; o < W / 4; o++)
                if (cover[y * W + cx + sign * o] >= outline) far = (o + 0.5) * 2.0 * Math.PI * radiusM / W;
            return far;
        }

        [TestMethod]
        public void TheOutflowLawGrowsWithTheUpdraftAndShrinksWithTheSheetsDepth()
        {
            Assert.AreEqual(1.0, AnvilMap.RadiusRatio(0.0, 3600.0, 2000.0, 1.0), 1e-12);
            Assert.AreEqual(Math.Sqrt(1.0 + 6.0 * 3600.0 / 2000.0), AnvilMap.RadiusRatio(6.0, 3600.0, 2000.0, 1.0), 1e-12);
            Assert.IsTrue(AnvilMap.RadiusRatio(6.0, 3600.0, 2000.0, 1.0) > AnvilMap.RadiusRatio(3.0, 3600.0, 2000.0, 1.0));
            Assert.IsTrue(AnvilMap.RadiusRatio(6.0, 3600.0, 1000.0, 1.0) > AnvilMap.RadiusRatio(6.0, 3600.0, 2000.0, 1.0));
            Assert.AreEqual(1.0 + 2.0 * (AnvilMap.RadiusRatio(6.0, 3600.0, 2000.0, 1.0) - 1.0), AnvilMap.RadiusRatio(6.0, 3600.0, 2000.0, 2.0), 1e-12, "the body's multiplier on the spread");
        }

        [TestMethod]
        public void TheAnvilReachesAsFarAsTheLawSaysAndAStrongerCellFarther()
        {
            CloudLayerDef cb = CloudLookFixtures.Cumulonimbus();
            Assert.IsTrue(AnvilMap.AnvilTypeIndex(cb) >= 0, "the shipped layer has an anvil type");
            Console.WriteLine("layer: coverage " + cb.EffectiveCoverage + ", amount " + cb.EffectiveNoiseAmount + ", edge " + cb.EffectiveEdgeSoftness + ", K " + cb.ClearSkyK + ", sheet depth " + AnvilMap.SheetDepthM(cb));
            foreach (double peak in new[] { 0.72, 0.86, 1.0 })
            {
                byte[] cp, tp;
                AnvilMap.Report r = Run(cb, 600000.0, 12.0, peak, 0.0, out cp, out tp);
                Console.WriteLine("cell peak " + peak + ": sources " + r.TowerRadiusM.Count + (r.TowerRadiusM.Count > 0 ? ", tower " + r.TowerRadiusM[0].ToString("0") + " m, reach "
                                  + r.ReachM[0].ToString("0") + " m, updraft " + r.UpdraftMs[0].ToString("0.0") + " m/s, strength " + r.Strength[0].ToString("0.00") : "") + ", texels " + r.Changed
                                  + ", east extent " + Extent(cp, cb, 600000.0, 1).ToString("0") + " m");
            }
            double texel = 2.0 * Math.PI * 600000.0 / W;
            // The sheet: 1.5 to 3 km deep under the tropopause (the layer's top), its base in the WMO's upper troposphere.
            double depth = AnvilMap.SheetDepthM(cb);
            Assert.IsTrue(depth >= 1500.0 && depth <= 3000.0 && cb.OuterAltitudeM - depth >= 6000.0, "sheet depth " + depth);
            byte[] cover, type;
            AnvilMap.Report strong = Run(cb, 600000.0, 12.0, 1.0, 0.0, out cover, out type);
            Assert.AreEqual(1, strong.TowerRadiusM.Count, "one cell, one source");
            double law = strong.TowerRadiusM[0] * (AnvilMap.RadiusRatio(strong.UpdraftMs[0], cb.AnvilSeconds, AnvilMap.SheetDepthM(cb), cb.AnvilScale) - 1.0);
            Assert.AreEqual(law, strong.ReachM[0], 1e-6);
            Assert.IsTrue(strong.ReachM[0] > 2.0 * strong.TowerRadiusM[0], "a strong cell's anvil reaches several tower radii: " + strong.ReachM[0] + " m against " + strong.TowerRadiusM[0] + " m");
            double east = Extent(cover, cb, 600000.0, 1), west = Extent(cover, cb, 600000.0, -1);
            // The cell's centre lies between two texel columns, so east and west differ by up to a texel; their mean is the radius.
            Assert.AreEqual(strong.TowerRadiusM[0] + strong.ReachM[0], 0.5 * (east + west), 1.5 * texel, "the drawn sheet's outline is at the law's reach");
            Assert.AreEqual(east, west, 1.5 * texel, "no shear: round");
            byte[] cw, tw;
            AnvilMap.Report weak = Run(cb, 600000.0, 12.0, 0.8, 0.0, out cw, out tw);
            Assert.IsTrue(weak.TowerRadiusM.Count == 0 || weak.ReachM[0] < strong.ReachM[0], "a weaker cell spreads less");
        }

        [TestMethod]
        public void ASecondBodyGetsItsOwnSizes()
        {
            CloudLayerDef home = CloudLookFixtures.Cumulonimbus();
            // A smaller body with a shallower troposphere (7.5 km against 10.5 km): the same layer set scaled in height, the same cell in km.
            CloudLayerDef second = CloudLookFixtures.Cumulonimbus();
            double s = 7.5 / 10.5;
            second = new CloudLayerDef(second.Name, 0, (float)(home.InnerAltitudeM * s), (float)(home.OuterAltitudeM * s), second.Types, second.SigmaPerM, second.Kind)
            { Coverage = home.Coverage, EdgeSoftness = home.EdgeSoftness, NoiseAmount = home.NoiseAmount, NoiseScaleM = home.NoiseScaleM, RainType = home.RainType, AnvilType = home.AnvilType,
              AnvilSeconds = home.AnvilSeconds, AnvilUpdraftMs = home.AnvilUpdraftMs, AnvilScale = home.AnvilScale, Deck = home.Deck, SoftnessScale = home.SoftnessScale, WaterPower = home.WaterPower };
            byte[] c1, t1, c2, t2;
            AnvilMap.Report a = Run(home, 600000.0, 12.0, 1.0, 0.0, out c1, out t1);
            AnvilMap.Report b = Run(second, 300000.0, 12.0, 1.0, 0.0, out c2, out t2);
            Assert.AreEqual(AnvilMap.SheetDepthM(home) * s, AnvilMap.SheetDepthM(second), 1e-3, "the sheet's depth is the body's");
            double lawB = b.TowerRadiusM[0] * (AnvilMap.RadiusRatio(b.UpdraftMs[0], second.AnvilSeconds, AnvilMap.SheetDepthM(second), 1.0) - 1.0);
            Assert.AreEqual(lawB, b.ReachM[0], 1e-6);
            Assert.AreNotEqual(a.ReachM[0] / a.TowerRadiusM[0], b.ReachM[0] / b.TowerRadiusM[0], "a shallower sheet spreads farther over the same tower");
            Assert.IsTrue(b.ReachM[0] / b.TowerRadiusM[0] > a.ReachM[0] / a.TowerRadiusM[0]);
        }

        [TestMethod]
        public void TheShearCarriesTheSheetDownwindAndTheSameInputGivesTheSameMap()
        {
            CloudLayerDef cb = CloudLookFixtures.Cumulonimbus();
            byte[] c1, t1, c2, t2;
            double shear = 10.0 / 600000.0;
            Run(cb, 600000.0, 12.0, 1.0, shear, out c1, out t1);
            Run(cb, 600000.0, 12.0, 1.0, shear, out c2, out t2);
            CollectionAssert.AreEqual(c1, c2);
            CollectionAssert.AreEqual(t1, t2);
            double east = Extent(c1, cb, 600000.0, 1), west = Extent(c1, cb, 600000.0, -1);
            double carry = shear * cb.AnvilSeconds * 600000.0;
            Assert.IsTrue(east > west + 0.5 * carry, "east " + east + " m, west " + west + " m, carry " + carry + " m");
        }

        [TestMethod]
        public void ATowerThatFeedsNoOutflowSpreadsNoAnvil()
        {
            CloudLayerDef cb = CloudLookFixtures.Cumulonimbus();
            byte[] cover, type;
            Cell(cb, 600000.0, 12.0, 1.0, out cover, out type);
            for (int i = 0; i < type.Length; i++) type[i] = 0;   // calvus everywhere
            byte[] before = (byte[])cover.Clone();
            Assert.IsFalse(AnvilMap.Apply(cb, cover, type, new AnvilInputs { Width = W, Height = H, RadiusM = 600000.0 }));
            CollectionAssert.AreEqual(before, cover);
        }
    }
}
