using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The numeric readout of series K-T2d (T09Readout): the horizon row from the camera's pitch, the three regions, the percentiles, the deck against the baseline and the
    // ratios, on pictures whose answer is known by construction.
    [TestClass]
    public class T09ReadoutTests
    {
        private const int W = 80, H = 60;

        private static Color[] Picture(Func<double, double, Color> pixel)
        {
            var raw = new Color[W * H];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++) raw[y * W + x] = pixel((x + 0.5) / W, (y + 0.5) / H);
            }
            return raw;
        }

        private static Color Grey(float v) { return new Color(v, v, v, 1f); }

        [TestMethod]
        public void TheHorizonIsAtTheMiddleForALevelCameraAtTheSurfaceAndMovesDownWhenLookingUp()
        {
            Assert.AreEqual(0.5, T09Readout.HorizonV(0.0, 60.0, 0.0, 600000.0), 1e-9);
            // 18 degrees up from 200 m on Kerbin: the horizon is 1.48 + 18 degrees below the axis; tan(19.48) / (2 tan 30) = 0.3063.
            Assert.AreEqual(0.5 - 0.3063, T09Readout.HorizonV(18.0, 60.0, 200.0, 600000.0), 0.002);
            // Looking down at 8.3 km: the dip is 9.47 degrees, the camera points 25 degrees down, so the horizon is 15.53 degrees above the axis.
            Assert.AreEqual(0.5 + Math.Tan(15.53 * Math.PI / 180.0) / (2.0 * Math.Tan(30.0 * Math.PI / 180.0)), T09Readout.HorizonV(-25.0, 60.0, 8300.0, 600000.0), 0.002);
            Assert.IsTrue(T09Readout.HorizonV(80.0, 60.0, 200.0, 600000.0) < 0.0, "a camera looking almost straight up has no horizon in the picture");
        }

        [TestMethod]
        public void TheRegionsAreSkyAboveAndGroundBelowTheHorizonWithoutTheVessel()
        {
            Color[] raw = Picture((u, v) => v > 0.5 ? Grey(0.4f) : Grey(0.8f));
            // The vessel: a bright block at the centre that must not count.
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    if (T09Readout.InVessel((x + 0.5) / W, (y + 0.5) / H)) raw[y * W + x] = Grey(9f);
                }
            }
            T09Readout.Reading r = T09Readout.Measure(raw, W, H, 0.5, null);
            Assert.IsTrue(r.Sky.Count > 100 && r.Ground.Count > 100, r.Sky.Count + " / " + r.Ground.Count);
            Assert.AreEqual(0.4, r.Sky.Median, 1e-5);
            Assert.AreEqual(0.4, r.Sky.P90, 1e-5, "the vessel's 9.0 is not in the sky region");
            Assert.AreEqual(0.8, r.Ground.Median, 1e-5);
            Assert.AreEqual(0.4, r.Sky.R, 1e-5);
            Assert.AreEqual(9f, r.Peak, 1e-5, "the peak of the picture still sees the vessel");
            Assert.AreEqual(0, r.Deck.Count, "no baseline, no deck");
            Assert.AreEqual(0.0, r.DeckFraction);
        }

        [TestMethod]
        public void PercentilesFollowARamp()
        {
            Color[] raw = Picture((u, v) => Grey((float)u));
            T09Readout.Reading r = T09Readout.Measure(raw, W, H, -1.0, null);
            Assert.AreEqual(0, r.Ground.Count, "a horizon below the picture leaves no ground");
            Assert.IsTrue(r.Sky.Count > 100);
            Assert.AreEqual(0.1, r.Sky.P10, 0.03);
            Assert.AreEqual(0.5, r.Sky.Median, 0.03);
            Assert.AreEqual(0.9, r.Sky.P90, 0.03);
        }

        [TestMethod]
        public void TheDeckIsWhereTheFrameDiffersFromTheBaselineAndTheRatiosUseTheBaseline()
        {
            Color[] off = Picture((u, v) => v > 0.5 ? Grey(0.5f) : Grey(0.7f));
            // The pass paints a dark deck over the left third of the picture (sky and sea alike) and leaves the rest.
            Color[] on = Picture((u, v) => u < 0.33 ? Grey(0.05f) : (v > 0.5 ? Grey(0.5f) : Grey(0.7f)));
            T09Readout.Reading baseline = T09Readout.Measure(off, W, H, 0.5, null);
            T09Readout.Reading r = T09Readout.Measure(on, W, H, 0.5, baseline);
            Assert.IsTrue(r.Deck.Count > 100);
            Assert.AreEqual(0.05, r.Deck.Median, 1e-5);
            Assert.AreEqual(0.33, r.DeckFraction, 0.06);
            Assert.AreEqual(0.1, T09Readout.Ratio(r.Deck.Median, r.Deck.Count, baseline.Sky.Median, baseline.Sky.Count), 1e-4, "deck over the off sky");
            Assert.AreEqual(0.05 / 0.7, T09Readout.Ratio(r.Deck.Median, r.Deck.Count, baseline.Ground.Median, baseline.Ground.Count), 1e-4, "deck over the off sea, the white reference");
            Assert.AreEqual(0.5, r.Sky.Median, 1e-5, "the sky region's median is the unpainted part (two thirds of it)");
            // The same picture as its own baseline has no deck.
            Assert.AreEqual(0, T09Readout.Measure(off, W, H, 0.5, baseline).Deck.Count);
        }

        [TestMethod]
        public void AMaskReplacesTheDeckTest()
        {
            Color[] raw = Picture((u, v) => new Color(0.3f, 0.5f, 0.1f, 1f));
            var mask = new bool[W * H];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W / 2; x++) mask[y * W + x] = true;
            }
            T09Readout.Reading r = T09Readout.Measure(raw, W, H, 0.5, null, mask);
            Assert.IsTrue(r.Deck.Count > 100);
            Assert.AreEqual(0.3, r.Deck.R, 1e-5);
            Assert.AreEqual(0.5, r.Deck.G, 1e-5);
            Assert.AreEqual(0.1, r.Deck.B, 1e-5);
        }

        [TestMethod]
        public void ClippedValuesAreCounted()
        {
            Color[] raw = Picture((u, v) => u < 0.5 ? Grey(1.7f) : Grey(0.2f));
            T09Readout.Reading r = T09Readout.Measure(raw, W, H, 0.0, null);
            Assert.AreEqual(0.5, r.Sky.Clipped, 0.05);
            Assert.AreEqual(1.7f, r.Peak, 1e-5);
        }

        [TestMethod]
        public void RatiosAreNaNWhereTheReferenceIsEmptyOrZero()
        {
            Assert.IsTrue(double.IsNaN(T09Readout.Ratio(0.2f, 10, 0f, 10)));
            Assert.IsTrue(double.IsNaN(T09Readout.Ratio(0.2f, 0, 0.5f, 10)));
            Assert.IsTrue(double.IsNaN(T09Readout.Ratio(0.2f, 10, 0.5f, 0)));
            Assert.AreEqual(0.4, T09Readout.Ratio(0.2f, 10, 0.5f, 10), 1e-6);
        }

        [TestMethod]
        public void AWrongSizeThrows()
        {
            try
            {
                T09Readout.Measure(new Color[10], W, H, 0.5, null);
                Assert.Fail("a picture that does not match its size is refused");
            }
            catch (ArgumentException) { }
        }
    }
}
