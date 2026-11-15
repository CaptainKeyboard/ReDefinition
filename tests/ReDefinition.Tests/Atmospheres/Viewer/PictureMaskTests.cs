using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres.Viewer;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.Viewer
{
    // The solo probe's measure of a picture against its baseline, on known pictures: the share of the frame the cloud fills, how far it reaches, the vessel's box left out.
    [TestClass]
    public class PictureMaskTests
    {
        private const int W = 200, H = 100;

        private static Color32[] Frame(Color32 colour)
        {
            var pixels = new Color32[W * H];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = colour;
            return pixels;
        }

        private static void Paint(Color32[] pixels, int x0, int x1, int y0, int y1, Color32 colour)
        {
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++) pixels[y * W + x] = colour;
        }

        private static readonly Color32 Sky = new Color32(90, 140, 220, 255), Cloud = new Color32(230, 230, 235, 255);

        [TestMethod]
        public void AnEmptyFrameFillsNothingAndAFullOneEverything()
        {
            MaskReading none = PictureMask.Measure(Frame(Sky), Frame(Sky), W, H);
            Assert.AreEqual(0.0, none.Share, 1e-12);
            Assert.AreEqual(0.0, none.RowSpan, 1e-12);
            MaskReading all = PictureMask.Measure(Frame(Cloud), Frame(Sky), W, H);
            Assert.AreEqual(1.0, all.Share, 1e-12);
            Assert.AreEqual(1.0, all.RowSpan, 0.011, "from the first row to the last");
            Assert.AreEqual(1.0, all.ColumnSpan, 0.006);
        }

        [TestMethod]
        public void ABandAcrossTheTopOfTheFrameFillsItsShareAndReachesItsRows()
        {
            Color32[] picture = Frame(Sky);
            // The top 30 rows (rows count from the bottom: 70 to 99) over the whole width.
            Paint(picture, 0, W, 70, 100, Cloud);
            MaskReading m = PictureMask.Measure(picture, Frame(Sky), W, H);
            Assert.AreEqual(6000.0 / (W * H - 48 * 40), m.Share, 0.003, "the share of the frame without the vessel's box (48 x 40 pixels), the band is above the box");
            Assert.AreEqual(0.30, m.RowSpan, 0.011);
            Assert.AreEqual(0.70, m.RowLow, 0.011, "from the bottom");
            Assert.AreEqual(1.00, m.RowHigh, 0.011);
        }

        [TestMethod]
        public void ADifferenceInsideTheVesselsBoxIsNotCounted()
        {
            Color32[] picture = Frame(Sky);
            Paint(picture, 90, 110, 45, 55, Cloud);
            MaskReading m = PictureMask.Measure(picture, Frame(Sky), W, H);
            Assert.AreEqual(0.0, m.Share, 1e-12);
            Assert.IsTrue(m.Counted < W * H, "the box is left out of the count");
        }

        [TestMethod]
        public void AChangeUnderTheThresholdIsNoiseAndOneOverItIsCloud()
        {
            Color32[] faint = Frame(new Color32(94, 140, 220, 255));
            Assert.AreEqual(0.0, PictureMask.Measure(faint, Frame(Sky), W, H).Share, 1e-12, "4 of 255 is 0.016, under 0.03");
            Color32[] clear = Frame(new Color32(100, 140, 220, 255));
            Assert.AreEqual(1.0, PictureMask.Measure(clear, Frame(Sky), W, H).Share, 1e-12, "10 of 255 is 0.04, over it");
        }

        [TestMethod]
        public void RowsBelowTheHorizonAreNotCountedAsCloud()
        {
            Color32[] picture = Frame(Sky);
            // A shadow on the ground in the lower 40 rows (the horizon row 0.4) and a cloud in the upper 20.
            Paint(picture, 0, W, 0, 40, new Color32(40, 60, 30, 255));
            Paint(picture, 0, W, 80, 100, Cloud);
            MaskReading all = PictureMask.Measure(picture, Frame(Sky), W, H);
            MaskReading sky = PictureMask.Measure(picture, Frame(Sky), W, H, 0.41);
            Assert.AreEqual(0.0, sky.RowLow - 0.8, 0.011, "the reach begins at the cloud, not at the ground");
            Assert.AreEqual(1.0, sky.RowHigh, 0.011);
            Assert.IsTrue(all.RowSpan > 0.9, "counted with the ground the reach is the whole frame: " + all.RowSpan);
            Assert.IsTrue(sky.RowSpan < 0.25, "without it the reach is the cloud's: " + sky.RowSpan);
        }

        [TestMethod]
        public void TheHorizonRowFollowsTheAimAndTheHeight()
        {
            Assert.AreEqual(0.5 - 0.5 * Math.Tan(1.8 * Math.PI / 180.0) / Math.Tan(30.0 * Math.PI / 180.0), PictureMask.HorizonRow(0.0, 60.0, 300.0, 600000.0), 0.006, "level camera: the horizon is just below the centre");
            Assert.IsTrue(PictureMask.HorizonRow(15.0, 60.0, 200.0, 600000.0) < PictureMask.HorizonRow(5.0, 60.0, 200.0, 600000.0), "looking higher puts the horizon lower in the frame");
        }

        [TestMethod]
        public void ASparseSpeckleDoesNotWidenTheReach()
        {
            Color32[] picture = Frame(Sky);
            picture[10 * W + 5] = Cloud;
            picture[90 * W + 150] = Cloud;
            MaskReading m = PictureMask.Measure(picture, Frame(Sky), W, H);
            Assert.AreEqual(0.0, m.RowSpan, 1e-12, "two pixels are under 2 percent of any row");
        }

        [TestMethod]
        public void PicturesThatDoNotMatchReadAsNothing()
        {
            Assert.IsFalse(PictureMask.Measure(Frame(Sky), new Color32[10], W, H).Valid);
            Assert.IsFalse(PictureMask.Measure(null, Frame(Sky), W, H).Valid);
        }
    }
}
