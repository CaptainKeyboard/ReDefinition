using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.CELLS
{
    // The body of a cloud with a form and the cover share it is placed by (CloudCells, the C# twin of CloudCells.cginc). The placement itself is the cloud field's
    // (CloudFieldCloudTests); the first placement, a lattice of discs, went with master plan 47.U stage 1 and its tests with it.
    [TestClass]
    public class CloudCellsTests
    {
        private static CloudCells.CellRead Disc(double xBest)
        {
            CloudCells.CellRead read;
            read.XBest = xBest;
            read.RBest = 1000.0;
            read.Core = 0.5;
            read.Hv = 0.0;   // on the body's base
            read.Top = 1.0;
            return read;
        }

        [TestMethod]
        public void AnOutlinesWidthIsTheHalfEllipsoidsAtItsEquivalentHeight()
        {
            // Master plan 47.U stage 3: the half ellipsoid is as wide as w at the height sqrt(1 - w^2); the whole footprint is its base, a closed outline its top.
            Assert.AreEqual(0.0, CloudField.OutlineHeight(1.0), 1e-12);
            Assert.AreEqual(1.0, CloudField.OutlineHeight(0.0), 1e-12);
            Assert.AreEqual(0.6, CloudField.OutlineHeight(0.8), 1e-12);
            // The half ellipsoid's own curve gives the height back: a type with that curve draws the body of a type without one.
            foreach (double h in new[] { 0.0, 0.3, 0.7, 1.0 })
                Assert.AreEqual(h, CloudField.OutlineHeight(Math.Sqrt(1.0 - h * h)), 1e-12);
        }

        [TestMethod]
        public void ADecksOutlineKeepsTheWholeFootprintWhereTheHalfEllipsoidHasNarrowed()
        {
            // Half way across the body, at 0.9 of its height: outside the half ellipsoid (0.5^2 + 0.9^2 above 1), inside a deck whose outline is the whole footprint there.
            CloudCells.CellRead dome = Disc(0.5 * CloudCells.EdgeX), deck = Disc(0.5 * CloudCells.EdgeX);
            Assert.AreEqual(0, CloudField.Body(ref dome, 0.9, 1000.0, 0.0, 10.0, 0.0));
            Assert.IsFalse(dome.Body);
            Assert.AreEqual(1, CloudField.Body(ref deck, CloudField.OutlineHeight(1.0), 1000.0, 0.0, 10.0, 0.0));
            Assert.AreEqual(1.0, CloudCells.CellBody(deck, 0.5, 0.0, 0.0, 1000.0, 0.0, 10.0, 0.0), 1e-12);
        }

        [TestMethod]
        public void TheBodyIsOutsideInsideAndHalfOnTheBareSurface()
        {
            // Well outside the body (1.5 of its radius unit), at the base: nothing.
            Assert.AreEqual(0.0, CloudCells.CellBody(Disc(1.5), 0.5, 0.0, 0.0, 1000.0, 0.5, 10.0, 0.0), 1e-12);
            // At the centre, on the base, the depth is the shorter semi-axis (725 m) over a band of 10 m: fully inside.
            Assert.AreEqual(1.0, CloudCells.CellBody(Disc(0.0), 0.5, 0.0, 0.0, 1000.0, 0.5, 10.0, 0.0), 1e-12);
            // On the ellipsoid's surface (x at the horizontal semi-axis, h 0) with the bulge rank at its mean and no fine octaves: the depth is 0, the share half.
            Assert.AreEqual(0.5, CloudCells.CellBody(Disc(CloudCells.EdgeX), 0.5, 0.0, 0.0, 1000.0, 0.5, 10.0, 0.0), 1e-12);
            // A rank above the mean moves the surface out: the same point is inside.
            Assert.IsTrue(CloudCells.CellBody(Disc(CloudCells.EdgeX), 1.0, 0.0, 0.0, 1000.0, 0.5, 10.0, 0.0) > 0.5);
        }

        [TestMethod]
        public void TheCoverShareRisesFromNoneToAllWithTheMapValue()
        {
            // The mean of the layer's threshold over the uniform noise value: nothing far below the threshold, everything far above it, rising in between.
            const double coverage = 0.35, edge = 0.15, amount = 1.0;
            Assert.AreEqual(0.0, CloudCells.Share(0.0, coverage, edge, amount), 1e-12);
            Assert.AreEqual(1.0, CloudCells.Share(1.5, coverage, edge, amount), 1e-12);
            double last = 0.0;
            for (int i = 0; i <= 30; i++)
            {
                double f = CloudCells.Share(i * 0.05, coverage, edge, amount);
                Assert.IsTrue(f >= last - 1e-12 && f >= 0.0 && f <= 1.0 + 1e-12, "map value " + i * 0.05 + ": " + f.ToString("R"));
                last = f;
            }
            // Without noise the share is the threshold's own ramp.
            Assert.AreEqual(0.5, CloudCells.Share(1.0 - coverage, coverage, edge, 0.0), 1e-12);
        }
    }
}
