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

        private static Color[] PictureOf(int w, int h, Func<double, double, Color> pixel)
        {
            var raw = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++) raw[y * w + x] = pixel((x + 0.5) / w, (y + 0.5) / h);
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

        [TestMethod]
        public void TheAidTauOfAViewThatLooksDownIsReadOverTheSeaBelowTheHorizon()
        {
            const double horizon = 0.6;
            Color sky = new Color(0.337f, 0.541f, 0.754f, 1f), sea = new Color(0.1f, 0.3f, 0.5f, 1f);
            Color[] scene = Picture((u, v) => v > horizon ? sky : sea);
            // A deck of tau 2.0 over the sea only.
            Color[] aid = Picture((u, v) => v > horizon ? sky : T09Readout.AidPixel(2.0, sea));
            T09Readout.AidTauReading seaRead = T09Readout.MeasureAidTau(aid, scene, W, H, horizon, 1, true);
            T09Readout.AidTauReading skyRead = T09Readout.MeasureAidTau(aid, scene, W, H, horizon, 1, false);
            Assert.IsTrue(seaRead.Count > 0 && skyRead.Count > 0);
            Assert.AreEqual(1.0, seaRead.Median / 2.0, 0.12, "the sea region carries the deck");
            Assert.AreEqual(0.0, skyRead.Median, 1e-9, "the sky region carries none");
            Assert.IsTrue(seaRead.TopCount > 0, "the bottom rows are the 'top' rows of a view that looks down");
        }

        [TestMethod]
        public void TheHorizonLineReadsTheColourAboveAndBelowTheRowAndTheStepOfTheCloudsOwnChange()
        {
            const double horizon = 0.3;
            // Baseline: sky 0.7 above the horizon, sea 0.2 below. Frame: a cloud of +0.2 above the row, +0.05 below it.
            Color[] baseline = Picture((u, v) => Grey(v > horizon ? 0.7f : 0.2f));
            Color[] frame = Picture((u, v) => Grey(v > horizon ? 0.9f : 0.25f));
            T09Readout.HorizonLine line = T09Readout.MeasureHorizonLine(frame, baseline, W, H, horizon);
            Assert.IsTrue(line.Count > 0);
            Assert.AreEqual(0.9f, line.Above.r, 1e-5f);
            Assert.AreEqual(0.25f, line.Below.g, 1e-5f);
            Assert.AreEqual(0.2f, line.DeltaAbove.b, 1e-5f);
            Assert.AreEqual(0.05f, line.DeltaBelow.r, 1e-5f);
            Assert.AreEqual(0.15f, line.Step, 1e-5f, "the step is the difference of the changes, not of the colours");
            Assert.AreEqual(0.65f, line.StepColour, 1e-5f, "the frame's own colour steps by 0.9 - 0.25");
            Assert.IsFalse(T09Readout.MeasureHorizonLine(baseline, null, W, H, horizon).HasDelta, "the baseline itself has no change");
            Assert.AreEqual(0, T09Readout.MeasureHorizonLine(frame, baseline, W, H, 0.02).Count, "a horizon row at the picture's edge is not read");
        }

        // E40 start 52 follow-up. The horizon line reads six narrow columns in two thin bands. A cloud whose edge lies at the horizon row (deck on the sea side, clear sky above) gives a step that
        // is the cloud against the sky, not a haze step: the metric cannot tell the two apart (the cross-check of start 52 named this). And a mound that fills most of the sampled columns in a
        // band replaces the band's median.
        [TestMethod]
        public void TheHorizonLineStepCannotTellACloudEdgeAtTheRowFromAHazeStepAndAMoundInMostColumnsReplacesTheMedian()
        {
            const double horizon = 0.5;
            Color[] baseline = Picture((u, v) => Grey(v > horizon ? 0.7f : 0.4f));
            // The deck covers the sea side only: no cloud above the row, +0.25 below it.
            Color[] edge = Picture((u, v) => Grey(v > horizon ? 0.7f : 0.65f));
            T09Readout.HorizonLine line = T09Readout.MeasureHorizonLine(edge, baseline, W, H, horizon);
            Assert.AreEqual(0f, line.DeltaAbove.r, 1e-5f);
            Assert.AreEqual(0.25f, line.DeltaBelow.r, 1e-5f);
            Assert.AreEqual(0.25f, line.Step, 1e-5f, "a deck that simply ends at the row reads as a step of the cloud's own change");
            // The same deck on both sides, a mound (dark) in the columns left of 0.35 and right of 0.75 above the row, in the rows the band reads: five of the six columns are the mound's.
            Color[] mound = Picture((u, v) =>
            {
                bool inMound = (u < 0.35 || u > 0.75) && v > horizon && v < horizon + 0.05;
                return Grey(inMound ? 0.2f : (v > horizon ? 0.62f : 0.6f));
            });
            T09Readout.HorizonLine m = T09Readout.MeasureHorizonLine(mound, baseline, W, H, horizon);
            Assert.AreEqual(0.2f, m.Above.g, 1e-5f, "the mound's colour is the band's median: the line reads the mound, not the deck");
            Assert.AreEqual(0.6f, m.Below.g, 1e-5f);
        }

        [TestMethod]
        public void TheRowProfileDropsMoundRimsFromTheDeckColourAndReadsTheBaselineBesideIt()
        {
            const double horizon = 0.5;
            const int w = 200, h = 200;
            Color[] baseline = PictureOf(w, h, (u, v) => Grey(v > horizon ? 0.7f : 0.4f));
            // A deck of 0.62 below the row and 0.5 above it, a mound (0.2) in the first quarter of the columns above the row.
            Color[] frame = PictureOf(w, h, (u, v) => Grey(v <= horizon ? 0.62f : (u < 0.25 && v < horizon + 0.12) ? 0.2f : 0.5f));
            T09Readout.RowProfile profile = T09Readout.MeasureRowProfile(frame, baseline, w, h, horizon);
            Assert.AreEqual(T09Readout.ProfileBands, profile.Below.Length);
            for (int k = 0; k < T09Readout.ProfileBands; k++)
            {
                T09Readout.ProfileBand b = profile.Below[k], a = profile.Above[k];
                Assert.IsTrue(b.HasColour, "below band " + k);
                Assert.AreEqual(0.62f, b.Frame.r, 1e-5f, "below band " + k);
                Assert.AreEqual(0.4f, b.Baseline.r, 1e-5f, "the baseline's own colour over the same samples, below band " + k);
                Assert.AreEqual(0, b.Rejected, "no mound below the row");
                Assert.IsTrue(a.HasColour, "above band " + k);
                Assert.AreEqual(0.5f, a.Frame.r, 1e-5f, "the mound's samples are not in the colour, above band " + k);
                Assert.AreEqual(0.7f, a.Baseline.r, 1e-5f);
                Assert.IsTrue(a.Rejected > 0 && a.Rejected < a.Changed / 2, "mound samples rejected above band " + k + ": " + a.Rejected + " of " + a.Changed);
            }
            // The baseline itself: no change is known, every sample counts, the colour is the picture's and no baseline colour is written.
            T09Readout.RowProfile own = T09Readout.MeasureRowProfile(baseline, null, w, h, horizon);
            Assert.IsTrue(own.Below[0].HasColour && own.Below[0].Frame.r == 0.4f && own.Below[0].Baseline.a == 0f);
        }

        [TestMethod]
        public void TheRowProfileSeesADeckFadeIntoTheHazeAsFewerChangedSamplesAndASmallerDistanceToTheBaseline()
        {
            const double horizon = 0.5;
            const int w = 200, h = 200;
            Color[] baseline = PictureOf(w, h, (u, v) => Grey(0.7f));
            // The deck's colour falls from 0.7 at the row to 0.4 at 0.10 below it (a fade into the haze): the profile's distance to the baseline falls towards the row, and the bands near the row
            // hold no changed sample at all (the change there is under T09Readout.ClosedChange).
            Color[] frame = PictureOf(w, h, (u, v) => Grey(v >= horizon ? 0.7f : 0.7f - 0.3f * (float)Math.Min(1.0, (horizon - v) / 0.1)));
            T09Readout.RowProfile profile = T09Readout.MeasureRowProfile(frame, baseline, w, h, horizon);
            float previous = -1f;
            for (int k = 2; k < T09Readout.ProfileBands; k++)
            {
                T09Readout.ProfileBand b = profile.Below[k];
                Assert.IsTrue(b.HasColour, "band " + k);
                float distance = Math.Abs(b.Baseline.r - b.Frame.r);
                Assert.IsTrue(distance > previous, "the distance to the baseline grows away from the row: band " + k + " " + distance);
                previous = distance;
            }
            Assert.AreEqual(0, profile.Below[0].Changed, "the band at the row has faded into the baseline");
            Assert.IsFalse(profile.Below[0].HasColour);
            // A row near the picture's edge gives empty bands.
            Assert.AreEqual(0, T09Readout.MeasureRowProfile(frame, baseline, w, h, 0.02).Below[0].Count);
        }

        // The cross-check of start 52 read 'above.high.l0up.sky_closed_share = 9.86'; the log row is 9.8607E-05 (the exponent was lost in reading). The share is a count over the same samples
        // as the region, so it cannot leave [0, 1] whatever the region's size.
        [TestMethod]
        public void TheClosedShareStaysBetweenZeroAndOneForATinySkyRegion()
        {
            const double horizon = 0.92;
            const int w = 100, h = 100;
            Color[] baseline = PictureOf(w, h, (u, v) => Grey(v > horizon ? 0.7f : 0.3f));
            T09Readout.Reading off = T09Readout.Measure(baseline, w, h, horizon, null);
            Color[] frame = PictureOf(w, h, (u, v) => Grey(0.1f));
            T09Readout.Reading r = T09Readout.Measure(frame, w, h, horizon, off);
            Assert.IsTrue(r.Sky.Count > 0 && r.Sky.Count < 100, "a sky region of a few samples: " + r.Sky.Count);
            Assert.AreEqual(1.0, r.SkyClosedShare, 1e-9);
            Assert.IsTrue(r.GroundClosedShare >= 0.0 && r.GroundClosedShare <= 1.0);
            Color[] clear = PictureOf(w, h, (u, v) => Grey(v > horizon ? 0.7f : 0.3f));
            Assert.AreEqual(0.0, T09Readout.Measure(clear, w, h, horizon, off).SkyClosedShare, 1e-9);
        }
    }
}
