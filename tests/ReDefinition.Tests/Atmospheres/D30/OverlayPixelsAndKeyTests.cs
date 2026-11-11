using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres.Forecast;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.D30
{
    // D30 review: the pixel comparison of the overlay (on against off) and the window key's bridge to ReDefinition.Api.Keys.
    [TestClass]
    public class OverlayPixelsAndKeyTests
    {
        private const int W = 320, Hh = 240;

        // A ring of 28 points around (cx, cy), the way the overlay builds one.
        private static List<P2> RingPoints(float cx, float cy, float radius)
        {
            var points = new List<P2>();
            for (int i = 0; i <= 28; i++)
            {
                double a = i * 2.0 * Math.PI / 28.0;
                points.Add(new P2(cx + (float)(radius * Math.Cos(a)), cy + (float)(radius * Math.Sin(a))));
            }
            return points;
        }

        // Paints the ring as the overlay does: a 2 pixel wide line between neighbouring points.
        private static void Draw(byte[] rgb, IList<P2> points)
        {
            for (int p = 1; p < points.Count; p++)
            {
                const int steps = 40;
                for (int s = 0; s <= steps; s++)
                {
                    float t = s / (float)steps;
                    int x = (int)Math.Round(points[p - 1].X + (points[p].X - points[p - 1].X) * t);
                    int y = (int)Math.Round(points[p - 1].Y + (points[p].Y - points[p - 1].Y) * t);
                    for (int dy = 0; dy < 2; dy++)
                    {
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= W || yy >= Hh) continue;
                            int i = (yy * W + xx) * 3;
                            rgb[i] = 255;
                            rgb[i + 1] = 40;
                            rgb[i + 2] = 40;
                        }
                    }
                }
            }
        }

        private static byte[] Background()
        {
            var rgb = new byte[W * Hh * 3];
            for (int i = 0; i < rgb.Length; i++) rgb[i] = 60;
            return rgb;
        }

        [TestMethod]
        public void RingsDrawnWhereTheSceneSaysHitEveryOutlinePoint()
        {
            List<P2> ring = RingPoints(160f, 120f, 50f);
            byte[] off = Background(), on = Background();
            Draw(on, ring);
            OverlayPixelResult result = OverlayPixels.Measure(on, off, W, Hh, new List<IList<P2>> { ring });
            Console.WriteLine("D30 pixels: " + result.Hits + " of " + result.Samples + " points, " + result.ChangedPixels + " changed pixels");
            Assert.AreEqual(29, result.Samples);
            Assert.AreEqual(100.0, result.HitPct, 1e-9);
            Assert.IsTrue(result.ChangedPixels > 200);
        }

        [TestMethod]
        public void ARingDrawnAtAnotherPlaceThanTheSceneSaysIsCaughtWhichTheSameFunctionComparisonCouldNot()
        {
            List<P2> said = RingPoints(160f, 120f, 50f);
            List<P2> drawn = RingPoints(172f, 120f, 50f);
            byte[] off = Background(), on = Background();
            Draw(on, drawn);
            OverlayPixelResult shifted = OverlayPixels.Measure(on, off, W, Hh, new List<IList<P2>> { said });
            Console.WriteLine("D30 pixels, shifted by 12 px: hit share " + shifted.HitPct.ToString("0.0") + " %");
            Assert.IsTrue(shifted.HitPct < 85.0, "A canvas shifted by 12 pixels is below the row's 85 %: " + shifted.HitPct);

            // A canvas at the wrong scale (the ring half the size).
            byte[] small = Background();
            Draw(small, RingPoints(160f, 120f, 25f));
            OverlayPixelResult scaled = OverlayPixels.Measure(small, off, W, Hh, new List<IList<P2>> { said });
            Assert.IsTrue(scaled.HitPct < 85.0, "A canvas at half the scale is below the row's 85 %: " + scaled.HitPct);
        }

        [TestMethod]
        public void WithTheOverlayOffNothingHitsAndPointsOffTheScreenAreNotCounted()
        {
            List<P2> ring = RingPoints(10f, 10f, 50f);
            byte[] same = Background();
            OverlayPixelResult none = OverlayPixels.Measure(same, Background(), W, Hh, new List<IList<P2>> { ring });
            Assert.AreEqual(0, none.Hits);
            Assert.AreEqual(0, none.ChangedPixels);
            Assert.IsTrue(none.Samples > 0 && none.Samples < 29, "Points left of and below the screen are not samples: " + none.Samples);
            Assert.AreEqual(0.0, none.HitPct, 1e-9);
        }

        [TestMethod]
        public void TheOverlayPositionCheckAgainstAHandWrittenExpectationFailsForAShiftedProjector()
        {
            // The same expectation as the ring-position test of OverlayTests, with a projector whose origin is 10 pixels off: 1.7 % of 600.
            var projector = new PlaneProjector { OriginX = 410f };
            ForecastTrack[] tracks = { D30Fixtures.Track(1, 0.0, 10.0, -60.0, 0.1, 2.0, 60, 150000f, 0.7f) };
            var scene = new OverlayScene();
            OverlayBuilder.Build(scene, tracks, projector, 1, D30Fixtures.RadiusM, 3 * D30Fixtures.H, 24.0, null);
            double ex = 400.0 + (-60.0 + 2.0 * 3.0) * 8.0, ey = 300.0 + (10.0 + 0.1 * 3.0) * 8.0;
            double error = Math.Sqrt((scene.Rings[0].Centre.X - ex) * (scene.Rings[0].Centre.X - ex) + (scene.Rings[0].Centre.Y - ey) * (scene.Rings[0].Centre.Y - ey));
            Assert.IsTrue(error / 600.0 > 0.01, "A 10 pixel shift is more than 1 % of the screen: " + error / 600.0);
        }

        // A stand-in for ReDefinition.Api.Keys.
        internal static class FakeKeys
        {
            internal static string Asked;
            internal static bool Down;

            public static bool Pressed(string key)
            {
                Asked = key;
                return Down;
            }
        }

        internal static class ThrowingKeys
        {
            public static bool Pressed(string key)
            {
                throw new InvalidOperationException("binding gone");
            }
        }

        [TestMethod]
        public void TheKeyBridgeAsksTheMainModsInterfaceForTheRegisteredBindingAndTogglesOnThePress()
        {
            var key = new ForecastKey(typeof(FakeKeys));
            Assert.IsTrue(key.Available);
            int toggles = 0;
            FakeKeys.Down = false;
            Assert.IsFalse(key.Poll(() => toggles++));
            Assert.AreEqual("atmospheres.forecastWindow", FakeKeys.Asked);
            FakeKeys.Down = true;
            Assert.IsTrue(key.Poll(() => toggles++));
            Assert.AreEqual(1, toggles);
        }

        [TestMethod]
        public void WithoutTheMainModTheKeyBridgeIsSilentAndAFailingBindingSwitchesItOff()
        {
            var absent = new ForecastKey((Type)null);
            Assert.IsFalse(absent.Available);
            Assert.IsFalse(absent.Pressed());
            int toggles = 0;
            Assert.IsFalse(absent.Poll(() => toggles++));
            Assert.AreEqual(0, toggles);
            var wrong = new ForecastKey(typeof(string));
            Assert.IsFalse(wrong.Available, "A type without Pressed(string) is not the interface.");
            var logged = new List<string>();
            var throwing = new ForecastKey(typeof(ThrowingKeys), logged.Add);
            Assert.IsTrue(throwing.Available);
            Assert.IsFalse(throwing.Pressed());
            Assert.AreEqual(1, logged.Count);
            Assert.IsFalse(throwing.Available, "After a failure the bridge stays off.");
        }
    }
}
