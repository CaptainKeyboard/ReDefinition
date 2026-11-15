using System;
using System.Collections.Generic;
using AtmoSim.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // CELLS: the cumulonimbus layer drawn from the generator's list of cells (AnvilMap.PaintCells, AnvilMap.ApplyCells). One cell on the equator of an equirectangular map: the footprint is the
    // cell's cover averaged over the texel (the area fraction), not a point sample; the anvil is spread from the cell's own radius, strength and updraft.
    [TestClass]
    public class AnvilCellTests
    {
        private const int W = 512, H = 256;
        private const double R = 600000.0;
        private static readonly double TexelM = 2.0 * Math.PI * R / W;

        private static ConvectiveCell Cell(double eastTexels, double northTexels, double radiusM, double strength, double rainLevel)
        {
            return new ConvectiveCell { LatRad = northTexels * Math.PI / H, LonRad = (eastTexels / W - 0.5) * 2.0 * Math.PI, RadiusM = radiusM, Strength = strength, RainLevel = rainLevel, RainMmH = 12.0 * rainLevel };
        }

        private static AnvilInputs Inputs()
        {
            return new AnvilInputs { Width = W, Height = H, RadiusM = R, UpdraftMs = 6.0 };
        }

        private static void Paint(CloudLayerDef layer, IList<ConvectiveCell> cells, out byte[] cover, out byte[] type)
        {
            cover = new byte[W * H];
            type = new byte[W * H];
            AnvilMap.PaintCells(layer, cells, Inputs(), new AnvilMap.CellScratch(), cover, type);
        }

        // The strongest painted texel and its value for a cell at a sub-texel offset (offset in texels, 0 = the centre of the texel under the cell).
        private static int Peak(byte[] cover)
        {
            int max = 0;
            foreach (byte b in cover) if (b > max) max = b;
            return max;
        }

        // The cover a point sample of the cell would give at the texel centres (what the map had before): the strongest texel.
        private static double PointPeak(ConvectiveCell c)
        {
            double best = 0.0;
            for (int dy = -3; dy <= 3; dy++)
                for (int dx = -3; dx <= 3; dx++)
                {
                    double lat = (0.5 - ((int)Math.Floor((0.5 - c.LatRad / Math.PI) * H) + dy + 0.5) / H) * Math.PI;
                    double lon = (((int)Math.Floor((c.LonRad / (2.0 * Math.PI) + 0.5) * W) + dx + 0.5) / W - 0.5) * 2.0 * Math.PI;
                    double ex = R * Math.Cos(c.LatRad) * (lon - c.LonRad), ey = R * (lat - c.LatRad);
                    double g = Math.Exp(-(ex * ex + ey * ey) / (c.RadiusM * c.RadiusM));
                    best = Math.Max(best, 1.0 - Math.Exp(-AnvilMap.CellCoverGain * c.Strength * g));
                }
            return best;
        }

        [TestMethod]
        public void AnyCellLeavesATowerInTheMapAndItsPeakSwingsLessThanAPointSample()
        {
            CloudLayerDef cb = CloudLookFixtures.Cumulonimbus();
            foreach (double rad in new[] { 3400.0, 4700.0, 6000.0 })
            {
                double lowArea = 1e9, highArea = 0.0, lowPoint = 1e9, highPoint = 0.0;
                for (int i = 0; i < 8; i++)
                    for (int j = 0; j < 8; j++)
                    {
                        ConvectiveCell c = Cell(100.0 + i / 8.0, 10.0 + j / 8.0, rad, 1.0, 1.0);
                        byte[] cover, type;
                        Paint(cb, new[] { c }, out cover, out type);
                        double peak = Peak(cover) / 255.0, point = PointPeak(c);
                        lowArea = Math.Min(lowArea, peak); highArea = Math.Max(highArea, peak);
                        lowPoint = Math.Min(lowPoint, point); highPoint = Math.Max(highPoint, point);
                    }
                Console.WriteLine("e-fold " + rad + " m: peak cover over 64 sub-texel positions: area-averaged " + lowArea.ToString("0.00") + " to " + highArea.ToString("0.00") + ", point sample " + lowPoint.ToString("0.00") + " to " + highPoint.ToString("0.00"));
                // The cell is at Nyquist on the texel grid (an e-fold radius of 0.5 to 0.8 texel): averaging over the texel keeps the cell's area but not a constant peak; the swing is the grid's.
                // What averaging does: the smallest cells are not lost where the grid falls between their texel centres (point sample 0.19 at 3.4 km).
                Assert.IsTrue(lowArea > 0.2, "no position loses the cell: " + lowArea);
                if (rad < 4000.0) Assert.IsTrue(lowArea > lowPoint, "the smallest cell keeps a stronger weakest position than the point sample");
            }
        }

        [TestMethod]
        public void ThePaintedCoverHoldsTheCellsAreaAndIsClearElsewhere()
        {
            CloudLayerDef cb = CloudLookFixtures.Cumulonimbus();
            const double rad = 4700.0;
            ConvectiveCell c = Cell(200.37, 20.61, rad, 0.9, 0.8);
            byte[] cover, type;
            int drawn = AnvilMap.PaintCells(cb, new[] { c }, Inputs(), new AnvilMap.CellScratch(), cover = new byte[W * H], type = new byte[W * H]);
            Assert.AreEqual(1, drawn);
            // the cell's cover integrated over the map against the integral of 1 - exp(-G s exp(-r^2 / rad^2)) over the plane (numerically, polar quadrature)
            double dy = Math.PI * R / H, sum = 0.0;
            for (int y = 0; y < H; y++)
            {
                double lat = Math.PI * (0.5 - (y + 0.5) / H);
                double dx = 2.0 * Math.PI * R * Math.Cos(lat) / W;
                for (int x = 0; x < W; x++) sum += cover[y * W + x] / 255.0 * dx * dy;
            }
            double exact = 0.0;
            const int steps = 4000;
            for (int k = 0; k < steps; k++)
            {
                double r0 = 3.0 * rad * k / steps, r1 = 3.0 * rad * (k + 1) / steps, rm = 0.5 * (r0 + r1);
                exact += (1.0 - Math.Exp(-AnvilMap.CellCoverGain * c.Strength * Math.Exp(-rm * rm / (rad * rad)))) * Math.PI * (r1 * r1 - r0 * r0);
            }
            Console.WriteLine("painted area " + (sum / 1e6).ToString("0.0") + " km2, exact " + (exact / 1e6).ToString("0.0") + " km2");
            Assert.AreEqual(exact, sum, 0.06 * exact, "the map holds the cell's cover area within 6 % (byte rounding and the 4 x 4 sub-samples)");
            Assert.AreEqual(0, cover[0], "far from the cell: clear");
            int typePeak = 0;
            foreach (byte t in type) typePeak = Math.Max(typePeak, t);
            double fullType = 255.0 * cb.RainType * c.RainLevel;
            Assert.IsTrue(typePeak >= 0.6 * fullType && typePeak <= fullType + 1.0, "the type follows the rain level on the precipitation axis (rainType x level): " + typePeak + " of " + fullType);
        }

        [TestMethod]
        public void AStrongRainingCellSpreadsAnAnvilByTheLawFromItsOwnNumbers()
        {
            CloudLayerDef cb = CloudLookFixtures.Cumulonimbus();
            ConvectiveCell c = Cell(256.0, 0.0, 4700.0, 1.0, 1.0);
            byte[] cover, type;
            Paint(cb, new[] { c }, out cover, out type);
            var report = new AnvilMap.Report();
            Assert.IsTrue(AnvilMap.ApplyCells(cb, new[] { c }, cover, type, Inputs(), report), "an anvil is drawn");
            Assert.AreEqual(1, report.TowerRadiusM.Count);
            double load = AnvilMap.CellCoverGain * c.Strength, need = -Math.Log(1.0 - AnvilMap.MedianRaw(cb));
            Assert.AreEqual(c.RadiusM * Math.Sqrt(Math.Log(load / need)), report.TowerRadiusM[0], 1e-6, "the tower's radius is where the cell's own cover reaches the outline");
            Assert.AreEqual(6.0, report.UpdraftMs[0], 1e-6, "full strength, incus: the body's full-cell updraft");
            double law = report.TowerRadiusM[0] * (AnvilMap.RadiusRatio(report.UpdraftMs[0], cb.AnvilSeconds, AnvilMap.SheetDepthM(cb), cb.AnvilScale) - 1.0);
            Assert.AreEqual(law, report.ReachM[0], 1e-6);
            Console.WriteLine("tower " + report.TowerRadiusM[0].ToString("0") + " m, reach " + report.ReachM[0].ToString("0") + " m, " + report.Changed + " texels changed");
            Assert.IsTrue(report.ReachM[0] > 1.5 * report.TowerRadiusM[0], "the anvil spreads over more than one tower radius beyond the tower");
            // the sheet is where the law puts it: the farthest sheet texel east of the cell is within a texel of the sheet's end
            int anvilType = (int)Math.Round(AnvilMap.AnvilTypeIndex(cb) / (double)(cb.Types.Count - 1) * 255.0);
            int far = 0, first = -1, count = 0;
            for (int x = 0; x < W; x++)
                if (type[(H / 2 - 1) * W + x] == anvilType || type[H / 2 * W + x] == anvilType) { count++; if (first < 0) first = x; if (x >= W / 2) far = x - W / 2; }
            Console.WriteLine("anvil type " + anvilType + ": " + count + " texels on the two rows, first x " + first + ", farthest east of the centre " + far);
            double farM = far * TexelM;
            Assert.IsTrue(farM > report.TowerRadiusM[0] && farM < report.TowerRadiusM[0] + report.ReachM[0] * 1.6 + 2.0 * TexelM, "the sheet ends near the law's reach: " + farM + " m");
        }

        [TestMethod]
        public void ACellThatDoesNotRainOrIsTooWeakSpreadsNoAnvil()
        {
            CloudLayerDef cb = CloudLookFixtures.Cumulonimbus();
            // too weak: its own cover stays under the layer's outline
            ConvectiveCell weak = Cell(256.0, 0.0, 4700.0, 0.35, 1.0);
            // does not rain: calvus, no outflow
            ConvectiveCell dry = Cell(300.0, 40.0, 4700.0, 1.0, 0.0);
            byte[] cover, type;
            Paint(cb, new[] { weak, dry }, out cover, out type);
            var report = new AnvilMap.Report();
            Assert.IsFalse(AnvilMap.ApplyCells(cb, new[] { weak, dry }, cover, type, Inputs(), report));
            Assert.AreEqual(0, report.TowerRadiusM.Count, "neither is a source");
        }

        [TestMethod]
        public void TwoCellsAreTwoSourcesAndTheSameInputGivesTheSameMap()
        {
            CloudLayerDef cb = CloudLookFixtures.Cumulonimbus();
            ConvectiveCell a = Cell(120.0, 5.0, 5200.0, 1.0, 1.0), b = Cell(300.0, -30.0, 4000.0, 0.8, 0.9);
            byte[] c1, t1, c2, t2;
            Paint(cb, new[] { a, b }, out c1, out t1);
            Paint(cb, new[] { a, b }, out c2, out t2);
            var r1 = new AnvilMap.Report();
            AnvilMap.ApplyCells(cb, new[] { a, b }, c1, t1, Inputs(), r1);
            AnvilMap.ApplyCells(cb, new[] { a, b }, c2, t2, Inputs());
            Assert.AreEqual(2, r1.TowerRadiusM.Count);
            Assert.IsTrue(r1.ReachM[0] > r1.ReachM[1], "the stronger cell spreads farther");
            CollectionAssert.AreEqual(c1, c2);
            CollectionAssert.AreEqual(t1, t2);
        }

        [TestMethod]
        public void TheBodysFullCellUpdraftScalesTheSpread()
        {
            CloudLayerDef cb = CloudLookFixtures.Cumulonimbus();
            ConvectiveCell c = Cell(256.0, 0.0, 4700.0, 1.0, 1.0);
            byte[] cover, type;
            double[] reach = new double[2];
            for (int k = 0; k < 2; k++)
            {
                Paint(cb, new[] { c }, out cover, out type);
                var report = new AnvilMap.Report();
                AnvilInputs inp = Inputs();
                inp.UpdraftMs = k == 0 ? 3.0 : 9.0;
                AnvilMap.ApplyCells(cb, new[] { c }, cover, type, inp, report);
                reach[k] = report.ReachM[0];
            }
            Assert.IsTrue(reach[1] > reach[0], "a body whose full cell is stronger spreads a wider anvil");
        }

        // The game's cumulonimbus layer drawn from the generator's own cells at the game clock, next to the map it had (exploratory numbers for the owner; runs always, asserts only the basics).
        [TestMethod]
        public void TheGeneratorsCellsFeedTheCumulonimbusLayer()
        {
            GeneratorField field = CbGeneratorMeasure.Kerbin(664216370, true);
            WeatherGenerator g = field.StateOf(ReDefinition.Tests.Atmospheres.C25.FieldFixture.BodyIndex).Generator;
            CloudLayerDef cb = CloudLookFixtures.Cumulonimbus();
            double ut = 1686294.0;
            var cells = new List<ConvectiveCell>();
            g.Cells(ut, cells);
            // the rain level on the body's bands, as the map worker gives it
            float[] bands = field.StateOf(ReDefinition.Tests.Atmospheres.C25.FieldFixture.BodyIndex).Bands;
            for (int i = 0; i < cells.Count; i++) { ConvectiveCell c = cells[i]; c.RainLevel = PrecipScale.LevelFromRate((float)c.RainMmH, bands); cells[i] = c; }
            byte[] cover = new byte[W * H], type = new byte[W * H];
            AnvilInputs inp = new AnvilInputs { Width = W, Height = H, RadiusM = R, UpdraftMs = g.CellUpdraftMs };
            AnvilMap.PaintCells(cb, cells, inp, new AnvilMap.CellScratch(), cover, type);
            double outline = AnvilMap.MedianRaw(cb) * 255.0;
            int over = 0;
            foreach (byte b in cover) if (b > outline) over++;
            var report = new AnvilMap.Report();
            AnvilMap.ApplyCells(cb, cells, cover, type, inp, report);
            int s3 = 0, s6 = 0, anvilTexels = 0;
            foreach (double v in report.Strength) { if (v > 0.3) s3++; if (v > 0.6) s6++; }
            int anvilByte = (int)Math.Round(AnvilMap.AnvilTypeIndex(cb) / (double)(cb.Types.Count - 1) * 255.0);
            foreach (byte t in type) if (t == anvilByte) anvilTexels++;
            Console.WriteLine("cells " + cells.Count + ", above 0.3: " + cells.FindAll(c => c.Strength > 0.3).Count + "; texels over the outline " + over + "; anvil sources " + report.Strength.Count + " (above 0.3: " + s3 + ", above 0.6: " + s6
                + "), anvil texels " + anvilTexels + " (" + (100.0 * anvilTexels / (W * H)).ToString("0.0") + " % of the map), texels changed " + report.Changed);
            Assert.IsTrue(cells.Count > 100);
            Assert.IsTrue(over > 0 && report.Strength.Count > 0, "the layer has towers and anvils");
        }
    }
}
