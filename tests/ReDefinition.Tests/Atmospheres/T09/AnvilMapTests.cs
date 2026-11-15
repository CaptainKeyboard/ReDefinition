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
            AnvilMap.Apply(layer, cover, type, new AnvilInputs { Width = W, Height = H, RadiusM = radiusM, ShearRadS = shear }, report);
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
                                  + ", east extent " + Extent(cp, cb, 600000.0, 1).ToString("0") + " m");            }
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

        // E40 CB2: the tower under the anvil is the cell's updraft core, as wide as the column under the sheet is deep, not the whole cell (a cell 30 km across stood as a flat-walled box
        // as wide as itself): a broad strong cell keeps one tower texel at its strongest point, the rest of its area is the sheet; towerScale is the body's multiplier on the core.
        [TestMethod]
        public void TheTowerIsTheUpdraftCoreNotTheWholeCell()
        {
            CloudLayerDef cb = CloudLookFixtures.Cumulonimbus();
            double depth = AnvilMap.SheetBaseShare(cb) * cb.ThicknessM;
            Assert.AreEqual(0.5 * depth, AnvilMap.CoreRadiusM(cb), 1e-6, "a core as wide as it is deep");
            Assert.IsTrue(depth > 5000.0 && depth < 9000.0, "the column under the sheet: " + depth + " m");
            byte anvil = (byte)Math.Round(AnvilMap.AnvilTypeIndex(cb) / (double)(cb.Types.Count - 1) * 255.0);
            foreach (double scale in new[] { 1.0, 3.0 })
            {
                cb.TowerScale = (float)scale;
                byte[] cover, type;
                AnvilMap.Report r = Run(cb, 600000.0, 30.0, 1.0, 0.0, out cover, out type);
                int towers = 0, sheet = 0;
                double outline = AnvilMap.MedianRaw(cb) * 255.0;
                for (int i = 0; i < cover.Length; i++)
                {
                    if (cover[i] < outline) continue;
                    if (type[i] == anvil) sheet++;
                    else towers++;
                }
                double texelArea = Math.Pow(2.0 * Math.PI * 600000.0 / W, 2.0);
                double coreArea = Math.PI * Math.Pow(AnvilMap.CoreRadiusM(cb), 2.0);
                Console.WriteLine("towerScale " + scale + ": cores " + r.Cores + ", tower texels " + towers + ", sheet texels " + sheet + ", core area " + (coreArea / texelArea).ToString("0.0") + " texels");
                Assert.AreEqual(1, r.Cores, "one cell, one core");
                Assert.IsTrue(sheet > 4 * towers, "the cell's area is its sheet: " + sheet + " against " + towers);
                Assert.IsTrue(towers >= 1 && towers <= Math.Max(1.0, 2.0 * coreArea / texelArea), "the tower texels follow the core's area: " + towers);
                Assert.AreEqual(AnvilMap.CoreRadiusM(cb), r.TowerRadiusM[0], 1e-6, "the outflow law takes the core (the cell is wider than it)");
            }
        }

        // E40 CB2: the rim of a cell (map values under the outline of a source, types between calvus and incus) under its sheet is the sheet too: no texel of a calvus-incus mix stays
        // beside the sheet within its end (on the bilinear type axis it would open an incus ring around the sheet's edge, a wide box around the core).
        [TestMethod]
        public void NoTowerTypeStaysBesideTheSheetOfAStrongCell()
        {
            CloudLayerDef cb = CloudLookFixtures.Cumulonimbus();
            byte[] cover, type;
            Run(cb, 600000.0, 12.0, 1.0, 0.0, out cover, out type);
            byte anvil = (byte)Math.Round(AnvilMap.AnvilTypeIndex(cb) / (double)(cb.Types.Count - 1) * 255.0);
            int towers = 0, rim = 0;
            for (int y = H / 2 - 6; y <= H / 2 + 6; y++)
                for (int x = W / 2 - 6; x <= W / 2 + 6; x++)
                {
                    int i = y * W + x;
                    if (type[i] == anvil || type[i] == 0) continue;
                    // the visible rim: a column with the noise's middle value is thin cloud from a map value of about 0.375 (the soft edge's 0.03 share)
                    if (cover[i] >= 0.375 * 255.0) { if (type[i] == (byte)Math.Round(cb.RainType * 255.0)) towers++; else rim++; }
                }
            Assert.AreEqual(0, rim, "visible rim texels of a tower type beside the sheet");
            Assert.IsTrue(towers >= 1, "the core stays a tower");
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
