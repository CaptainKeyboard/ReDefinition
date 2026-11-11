using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests
{
    // The pure part of the cell's picture for EVE's 2D cloud layer: the swirl, the widths on the equirectangular picture, the smallest size.
    [TestClass]
    public class EveSwirlTests
    {
        private const double Kerbin = 600000;
        private const int W = EveSwirlMath.Width, H = EveSwirlMath.Height;

        private static EveCoverageMath.Blob BlobAt(double latDegrees, double lonDegrees, double radiusM, double peak, ulong seed = 7)
        {
            double la = latDegrees * Math.PI / 180, lo = lonDegrees * Math.PI / 180;
            return new EveCoverageMath.Blob
            {
                X = Math.Cos(la) * Math.Sin(lo), Y = Math.Sin(la), Z = Math.Cos(la) * Math.Cos(lo),
                RadiusM = radiusM, Peak = peak, Seed = seed
            };
        }

        private static byte[] Picture()
        {
            var rgba = new byte[W * H * 4];
            EveSwirlMath.Fill(rgba);
            return rgba;
        }

        private static int Alpha(byte[] rgba, int col, int row)
        {
            return rgba[4 * (row * W + col) + 3];
        }

        private static int Opaque(byte[] rgba, int minAlpha)
        {
            int n = 0;
            for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] >= minAlpha) n++;
            return n;
        }

        [TestMethod]
        public void TheCentreIsClosedAndTheRimFadesToNothingBeyondTheReturnedRectangle()
        {
            byte[] rgba = Picture();
            EveCoverageMath.Blob blob = BlobAt(55, 20, 15000, 0.9);
            EveCoverageMath.Rect rect = EveSwirlMath.Paint(rgba, blob, Kerbin, 0);
            Assert.IsFalse(rect.IsEmpty);
            double u, v;
            EveCoverageMath.Uv(blob.X, blob.Y, blob.Z, out u, out v);
            int col = (int)(u * W), row = (int)(v * H);
            Assert.IsTrue(Alpha(rgba, col, row) >= 0.9 * 255 * 0.82, "the core is dense: " + Alpha(rgba, col, row));
            long outside = 0;
            for (int r = 0; r < H; r++)
                for (int c = 0; c < W; c++)
                    if (Alpha(rgba, c, r) != 0 && !rect.Contains2(c, r, W)) outside++;
            Assert.AreEqual(0, outside, "nothing is painted outside the rectangle");
        }

        [TestMethod]
        public void ARowOfACellAtSixtyNorthIsTwiceAsWideInTexelsAsAtTheEquator()
        {
            byte[] atEquator = Picture(), atSixty = Picture();
            EveCoverageMath.Blob a = BlobAt(0, 0, 40000, 1.0), b = BlobAt(60, 0, 40000, 1.0);
            EveCoverageMath.Rect ra = EveSwirlMath.Paint(atEquator, a, Kerbin, 0), rb = EveSwirlMath.Paint(atSixty, b, Kerbin, 0);
            double ratio = (double)rb.Cols / ra.Cols;
            Assert.IsTrue(ratio > 1.7 && ratio < 2.3, "the rectangle is 1/cos(60) wider: " + ratio);
            // The widest row of the opaque part, in texels.
            double widthEquator = WidestRow(atEquator, ra), widthSixty = WidestRow(atSixty, rb);
            Assert.IsTrue(widthSixty / widthEquator > 1.6 && widthSixty / widthEquator < 2.4, "the cell is round on the planet, so wider in texels: " + widthSixty / widthEquator);
            // And as high in rows as at the equator (rows are a constant angle).
            Assert.AreEqual(ra.Rows, rb.Rows, 2, "the same number of rows");
        }

        private static double WidestRow(byte[] rgba, EveCoverageMath.Rect rect)
        {
            int best = 0;
            for (int r = 0; r < rect.Rows; r++)
            {
                int n = 0;
                for (int k = 0; k < rect.Cols; k++) if (Alpha(rgba, (rect.Col0 + k) % W, rect.Row0 + r) > 60) n++;
                if (n > best) best = n;
            }
            return best;
        }

        [TestMethod]
        public void ACellSmallerThanTwoAndAHalfTexelsIsPaintedThatLarge()
        {
            double texel = EveSwirlMath.TexelMetres(Kerbin);
            Assert.AreEqual(Math.PI * Kerbin / H, texel, 1e-9);
            Assert.AreEqual(EveSwirlMath.MinRadiusTexels * texel, EveSwirlMath.DisplayRadiusM(1000, Kerbin), 1e-9, "a 1 km cell is enlarged");
            Assert.AreEqual(20000.0, EveSwirlMath.DisplayRadiusM(20000, Kerbin), 1e-9, "a large cell is not");
            // Kerbin's smallest cell (6 km) is above the minimum; a 1 km one is not, and still shows.
            byte[] small = Picture(), same = Picture();
            EveSwirlMath.Paint(small, BlobAt(40, 100, 1000, 1.0), Kerbin, 0);
            EveSwirlMath.Paint(same, BlobAt(40, 100, EveSwirlMath.DisplayRadiusM(1000, Kerbin), 1.0), Kerbin, 0);
            CollectionAssert.AreEqual(small, same, "a small cell is the cell of the smallest size");
            Assert.IsTrue(Opaque(small, 100) >= 8, "the enlarged cell covers several texels: " + Opaque(small, 100));
        }

        [TestMethod]
        public void TheSpiralHasArmsAndAClosedCore()
        {
            double min = 2, max = -1;
            for (int i = 0; i < 360; i++)
            {
                double opacity, grey;
                EveSwirlMath.Pattern(0.7, i * Math.PI / 180, 0, out opacity, out grey);
                min = Math.Min(min, opacity);
                max = Math.Max(max, opacity);
            }
            Assert.IsTrue(max - min > 0.3, "the arms and the gaps between them differ: " + min + " to " + max);
            Assert.IsTrue(min >= EveSwirlMath.GapLevel * 0.9 && max <= 1.0001, "the gap is not empty and nothing is above 1");
            for (int i = 0; i < 360; i += 15)
            {
                double opacity, grey;
                EveSwirlMath.Pattern(0.05, i * Math.PI / 180, 0, out opacity, out grey);
                Assert.IsTrue(opacity > 0.9, "the core is closed in every direction: " + opacity);
            }
            double o1, g1, o2, g2;
            EveSwirlMath.Pattern(0.3, 0.0, 0, out o1, out g1);
            EveSwirlMath.Pattern(0.6, 0.0, 0, out o2, out g2);
            Assert.IsTrue(g1 >= EveSwirlMath.GapGrey && g2 >= EveSwirlMath.GapGrey && g1 <= EveSwirlMath.ArmGrey && g2 <= EveSwirlMath.ArmGrey, "greys between the two");
        }

        [TestMethod]
        public void ThePictureIsNotRotationallySymmetric()
        {
            byte[] rgba = Picture();
            EveCoverageMath.Blob blob = BlobAt(0, 0, 25000, 1.0);
            EveSwirlMath.Paint(rgba, blob, Kerbin, 0);
            // Sample a ring at 0.7 of the radius around the centre (u = 0.5, v = 0.5): a ring of constant opacity would be a disc, not a swirl.
            double ringTexels = 0.7 * 25000 / EveSwirlMath.TexelMetres(Kerbin);
            int lo = 255, hi = 0;
            for (int i = 0; i < 360; i += 5)
            {
                double a = i * Math.PI / 180;
                int col = (int)Math.Round(W / 2 + ringTexels * Math.Cos(a)), row = (int)Math.Round(H / 2 - ringTexels * Math.Sin(a));
                int alpha = Alpha(rgba, col, row);
                lo = Math.Min(lo, alpha);
                hi = Math.Max(hi, alpha);
            }
            Assert.IsTrue(hi - lo > 50, "the ring has arms and gaps: " + lo + " to " + hi);
        }

        [TestMethod]
        public void PaintingTwiceGivesTheSamePictureAndClearingTheRectangleGivesTheBackgroundAgain()
        {
            byte[] a = Picture(), b = Picture(), background = Picture();
            EveCoverageMath.Blob blob = BlobAt(-45, 100, 12000, 0.8, 99);
            EveCoverageMath.Rect ra = EveSwirlMath.Paint(a, blob, Kerbin, 0);
            EveSwirlMath.Paint(b, blob, Kerbin, 0);
            CollectionAssert.AreEqual(a, b, "deterministic");
            CollectionAssert.AreNotEqual(a, background);
            EveSwirlMath.Clear(a, ra);
            CollectionAssert.AreEqual(background, a, "the background colour with no opacity");
            Assert.AreEqual(0, background[3]);
            Assert.AreEqual(EveSwirlMath.BackgroundGrey, background[0]);
        }

        [TestMethod]
        public void ACellOnTheDateLineWrapsAndOneAtThePoleFillsTheRows()
        {
            byte[] rgba = Picture();
            EveCoverageMath.Rect line = EveSwirlMath.Paint(rgba, BlobAt(10, 180, 30000, 1.0), Kerbin, 0);
            Assert.IsTrue(Alpha(rgba, 0, H / 2 - (int)(10.0 / 180 * H)) > 0 && Alpha(rgba, W - 1, H / 2 - (int)(10.0 / 180 * H)) > 0, "both sides of the edge of the picture");
            Assert.IsTrue(line.Cols < W, "not the full width");
            byte[] pole = Picture();
            EveCoverageMath.Rect rect = EveSwirlMath.Paint(pole, BlobAt(89.5, 0, 25000, 1.0), Kerbin, 0);
            Assert.AreEqual(W, rect.Cols, "a cell at the pole reaches around it");
            Assert.AreEqual(0, rect.Row0);
        }

        [TestMethod]
        public void TheOverlapKeepsTheMoreOpaqueTexelAndANoPeakCellPaintsNothing()
        {
            byte[] rgba = Picture();
            EveSwirlMath.Paint(rgba, BlobAt(30, 30, 15000, 0.9, 1), Kerbin, 0);
            byte[] strong = (byte[])rgba.Clone();
            EveSwirlMath.Paint(rgba, BlobAt(30, 30, 15000, 0.2, 1), Kerbin, 0);
            CollectionAssert.AreEqual(strong, rgba, "a weak cell on a strong one changes nothing");
            Assert.IsTrue(EveSwirlMath.Paint(Picture(), BlobAt(30, 30, 15000, 0.0), Kerbin, 0).IsEmpty);
            Assert.IsTrue(EveSwirlMath.Paint(Picture(), BlobAt(30, 30, 0, 1.0), Kerbin, 0).IsEmpty);
        }

        [TestMethod]
        public void TheRectangleOfAnotherSizeIsTheSameShapeAsTheCoverageMapsAtItsSize()
        {
            EveCoverageMath.Rect one = EveCoverageMath.RectAround(0.3, 0.5, 0.8, 0.05);
            EveCoverageMath.Rect two = EveCoverageMath.RectAround(0.3, 0.5, 0.8, 0.05, EveCoverageMath.Width, EveCoverageMath.Height);
            Assert.AreEqual(one.Col0, two.Col0);
            Assert.AreEqual(one.Cols, two.Cols);
            Assert.AreEqual(one.Row0, two.Row0);
            Assert.AreEqual(one.Rows, two.Rows);
            EveCoverageMath.Rect half = EveCoverageMath.RectAround(0.3, 0.5, 0.8, 0.05, W, H);
            Assert.IsTrue(half.Cols < one.Cols && half.Rows < one.Rows, "a picture of half the size needs fewer texels");
        }

        [TestMethod]
        public void TheRangeIsLocalNearTheGroundAndTheWholePlanetFromOrbitAndInTheMapView()
        {
            Assert.AreEqual(EveCellLayer.RangeM, EveCellLayer.RangeFor(0, Kerbin, false), 1e-9);
            Assert.AreEqual(EveCellLayer.RangeM, EveCellLayer.RangeFor(EveCellLayer.WideFromAltitudeM - 1, Kerbin, false), 1e-9);
            Assert.AreEqual(Math.PI * Kerbin, EveCellLayer.RangeFor(EveCellLayer.WideFromAltitudeM, Kerbin, false), 1e-9);
            Assert.AreEqual(Math.PI * Kerbin, EveCellLayer.RangeFor(1000, Kerbin, true), 1e-9, "the map view from anywhere");
            Assert.IsTrue(EveCellLayer.WideFromAltitudeM < 200000 && EveCellLayer.VolumeLiveBelowM < 260000, "inside EVE's fade, which starts at 200 km and ends at 260 km");
        }

    }

    internal static class SwirlRectExtensions
    {
        // Whether a texel is inside a rectangle of a picture of that width (EveCoverageMath.Rect.Contains is for the coverage map's).
        internal static bool Contains2(this EveCoverageMath.Rect rect, int col, int row, int width)
        {
            if (row < rect.Row0 || row >= rect.Row0 + rect.Rows) return false;
            if (rect.Cols >= width) return true;
            int d = col - rect.Col0;
            if (d < 0) d += width;
            return d < rect.Cols;
        }
    }
}
