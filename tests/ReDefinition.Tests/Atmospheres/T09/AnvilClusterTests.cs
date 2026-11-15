using System;
using System.Collections.Generic;
using AtmoSim.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // CB3: a storm is a cluster of updraft cells. The cells of a cellList layer that lie within anvilClusterScale x the reach of a stronger cell's anvil are part of that cell's storm:
    // the storm has one source (its strongest cell) and one sheet, whose radius follows the area of all its updrafts. The source density then follows from the generator and the body.
    [TestClass]
    public class AnvilClusterTests
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

        private static AnvilMap.Report Run(CloudLayerDef layer, IList<ConvectiveCell> cells, out byte[] cover, out byte[] type)
        {
            cover = new byte[W * H];
            type = new byte[W * H];
            AnvilMap.PaintCells(layer, cells, Inputs(), new AnvilMap.CellScratch(), cover, type);
            var report = new AnvilMap.Report();
            AnvilMap.ApplyCells(layer, cells, cover, type, Inputs(), report);
            return report;
        }

        private static CloudLayerDef Layer(float clusterScale)
        {
            CloudLayerDef cb = CloudLookFixtures.Cumulonimbus();
            cb.AnvilClusterScale = clusterScale;
            return cb;
        }

        // The anvil's reach beyond the tower of a lone strong cell (m).
        private static double LoneReach(out double towerR)
        {
            byte[] c, t;
            AnvilMap.Report r = Run(Layer(1f), new[] { Cell(256.0, 0.0, 4700.0, 1.0, 1.0) }, out c, out t);
            towerR = r.TowerRadiusM[0];
            return r.ReachM[0];
        }

        [TestMethod]
        public void TheKeyDefaultsToTheDerivedValue()
        {
            Assert.AreEqual(1f, CloudLookFixtures.Cumulonimbus().AnvilClusterScale);
        }

        [TestMethod]
        public void ACellWithinOneReachOfAStrongerCellJoinsItsStormAndWidensTheSheet()
        {
            double towerR;
            double lone = LoneReach(out towerR);
            Assert.IsTrue(lone > TexelM, "the reach spans a texel: " + lone);
            // 0.9 reach east of the strong cell
            double dTexels = 0.9 * lone / TexelM;
            ConvectiveCell a = Cell(256.0, 0.0, 4700.0, 1.0, 1.0), b = Cell(256.0 + dTexels, 0.0, 4700.0, 0.9, 1.0);
            byte[] c1, t1, c0, t0;
            AnvilMap.Report one = Run(Layer(1f), new[] { a, b }, out c1, out t1), none = Run(Layer(0f), new[] { a, b }, out c0, out t0);
            Assert.AreEqual(2, none.Strength.Count, "scale 0: every cell spreads its own anvil");
            Assert.AreEqual(1, one.Strength.Count, "scale 1: one storm, one source");
            Assert.AreEqual(2, one.Cells);
            Assert.AreEqual(1, one.Absorbed);
            Assert.AreEqual(2, one.Members[0]);
            Assert.AreEqual(1.0, one.Strength[0], 1e-9, "the source is the strongest cell");
            Assert.IsTrue(one.ReachM[0] > lone * 1.2, "the storm's sheet follows the summed area of the updrafts: " + one.ReachM[0] + " against " + lone);
            Assert.IsTrue(one.ReachM[0] < lone * Math.Sqrt(2.0) + 1.0, "no more than two equal updrafts: " + one.ReachM[0]);
            // both towers are drawn, the storm's one sheet covers them both
            Assert.IsTrue(c1[(H / 2) * W + 256] > 0 && c1[(H / 2) * W + 256 + (int)Math.Round(dTexels)] > 0);
        }

        [TestMethod]
        public void CellsFartherApartThanTheReachAreSeparateStormsUntilTheScaleWidensIt()
        {
            double towerR;
            double lone = LoneReach(out towerR);
            double dTexels = 1.5 * lone / TexelM;
            ConvectiveCell a = Cell(256.0, 0.0, 4700.0, 1.0, 1.0), b = Cell(256.0 + dTexels, 0.0, 4700.0, 0.9, 1.0);
            byte[] c, t;
            Assert.AreEqual(2, Run(Layer(1f), new[] { a, b }, out c, out t).Strength.Count, "1.5 reaches apart at scale 1: two storms");
            Assert.AreEqual(1, Run(Layer(2f), new[] { a, b }, out c, out t).Strength.Count, "at scale 2 they are one");
            Assert.AreEqual(2, Run(Layer(1f), new[] { a, Cell(256.0 + 40.0 * lone / TexelM, 0.0, 4700.0, 0.9, 1.0) }, out c, out t).Strength.Count, "far apart: two");
        }

        [TestMethod]
        public void TheStormsDoNotDependOnTheOrderOfTheList()
        {
            double towerR;
            double lone = LoneReach(out towerR);
            var cells = new List<ConvectiveCell>();
            var rng = new Random(7);
            for (int i = 0; i < 40; i++)
                cells.Add(Cell(200.0 + rng.NextDouble() * 12.0 * lone / TexelM, -8.0 + rng.NextDouble() * 12.0 * lone / TexelM, 3400.0 + 2600.0 * rng.NextDouble(), 0.5 + 0.5 * rng.NextDouble(), 1.0));
            var reversed = new List<ConvectiveCell>(cells);
            reversed.Reverse();
            byte[] c1, t1, c2, t2;
            AnvilMap.Report r1 = Run(Layer(1f), cells, out c1, out t1), r2 = Run(Layer(1f), reversed, out c2, out t2);
            Assert.AreEqual(r1.Strength.Count, r2.Strength.Count);
            Assert.AreEqual(r1.Absorbed, r2.Absorbed);
            CollectionAssert.AreEqual(c1, c2);
            CollectionAssert.AreEqual(t1, t2);
            Assert.IsTrue(r1.Absorbed > 0 && r1.Strength.Count > 1, "the group is some storms, not one and not forty: " + r1.Strength.Count + " sources, " + r1.Absorbed + " joined");
        }

        // A row of cells one reach apart does not chain into one storm: a cell joins a source's storm, not the storm of a cell that joined.
        [TestMethod]
        public void ARowOfCellsDoesNotChainIntoOneStorm()
        {
            double towerR;
            double lone = LoneReach(out towerR);
            var cells = new List<ConvectiveCell>();
            for (int i = 0; i < 20; i++) cells.Add(Cell(100.0 + i * 0.8 * lone / TexelM, 0.0, 4700.0, 1.0 - 0.01 * i, 1.0));
            byte[] c, t;
            AnvilMap.Report r = Run(Layer(1f), cells, out c, out t);
            Console.WriteLine("a row of 20 cells at 0.8 reach: " + r.Strength.Count + " storms");
            Assert.IsTrue(r.Strength.Count >= 5 && r.Strength.Count < 20, "a few storms along the row: " + r.Strength.Count);
        }

        // The game's cumulonimbus layer at the game clock with the storms of the cell list (exploratory numbers for the owner; asserts only that clustering cannot add sources).
        [TestMethod]
        public void TheGameCellsAtTheGameClockGiveTheSourceDensityByTheScale()
        {
            GeneratorField field = CbGeneratorMeasure.Kerbin(664216370, true);
            WeatherGenerator g = field.StateOf(ReDefinition.Tests.Atmospheres.C25.FieldFixture.BodyIndex).Generator;
            float[] bands = field.StateOf(ReDefinition.Tests.Atmospheres.C25.FieldFixture.BodyIndex).Bands;
            double areaMkm2 = 4.0 * Math.PI * R * R / 1e12;
            int previous = int.MaxValue;
            foreach (double ut in new[] { 1686294.0, 1686294.0 + 21600.0, 1686294.0 + 90000.0 })
            {
                var cells = new List<ConvectiveCell>();
                g.Cells(ut, cells);
                for (int i = 0; i < cells.Count; i++) { ConvectiveCell c = cells[i]; c.RainLevel = PrecipScale.LevelFromRate((float)c.RainMmH, bands); cells[i] = c; }
                previous = int.MaxValue;
                foreach (float scale in new[] { 0f, 1f, 2f, 3f, 5f })
                {
                    byte[] cover, type;
                    AnvilMap.Report r = Run(Layer(scale), cells, out cover, out type);
                    int anvilByte = (int)Math.Round(AnvilMap.AnvilTypeIndex(Layer(scale)) / (double)(Layer(scale).Types.Count - 1) * 255.0), anvilTexels = 0, big = 0;
                    foreach (byte b in type) if (b == anvilByte) anvilTexels++;
                    foreach (int m in r.Members) if (m > 1) big++;
                    Console.WriteLine("ut " + ut + " scale " + scale + ": cells " + cells.Count + ", with a tower " + r.Cells + ", storms " + r.Strength.Count + " (" + (r.Strength.Count / areaMkm2).ToString("0.0") + " per million km2), joined " + r.Absorbed
                        + ", storms of several cells " + big + ", anvil texels " + (100.0 * anvilTexels / (W * H)).ToString("0.0") + " % of the map");
                    Assert.IsTrue(r.Strength.Count <= previous, "a wider scale never adds storms");
                    previous = r.Strength.Count;
                }
            }
        }
    }
}
