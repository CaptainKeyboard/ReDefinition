using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres.Forecast;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.D30
{
    // D30: the overlay's projection maths against a plane projector: the rings follow the systems' positions through a warp, a ring is
    // as large on the screen as its footprint, the clutter and capacity rules, the far side of the body, the ticks and the hover label.
    [TestClass]
    public class OverlayTests
    {
        private const double H = D30Fixtures.H;
        private const double R = D30Fixtures.RadiusM;

        [TestMethod]
        public void TheGreatCircleDestinationIsRightOnTheEquatorAndOnAMeridian()
        {
            double lat, lon;
            OverlayBuilder.Destination(0.0, 0.0, 90.0, R * 10.0 * Math.PI / 180.0, R, out lat, out lon);
            Assert.AreEqual(0.0, lat, 1e-9);
            Assert.AreEqual(10.0, lon, 1e-9);
            OverlayBuilder.Destination(0.0, 170.0, 90.0, R * 20.0 * Math.PI / 180.0, R, out lat, out lon);
            Assert.AreEqual(-170.0, lon, 1e-9, "The longitude wraps.");
            OverlayBuilder.Destination(10.0, 5.0, 0.0, R * 15.0 * Math.PI / 180.0, R, out lat, out lon);
            Assert.AreEqual(25.0, lat, 1e-9);
            Assert.AreEqual(5.0, lon, 1e-9);
        }

        [TestMethod]
        public void RingsFollowTheSystemsThroughAWarpWithinOnePercentOfTheScreen()
        {
            var projector = new PlaneProjector();
            const float screenHeight = 600f;
            ForecastTrack[] tracks =
            {
                D30Fixtures.Track(1, 0.0, 10.0, -60.0, 0.1, 2.0, 60, 150000f, 0.7f),
                D30Fixtures.Track(2, 0.0, -20.0, 30.0, 0.0, -1.5, 60, 100000f, 0.5f)
            };
            var scene = new OverlayScene();
            double worst = 0.0;
            foreach (double hours in new[] { 0.0, 1.0, 3.0, 6.0, 12.0, 24.0 })
            {
                double ut = hours * H;
                OverlayBuilder.Build(scene, tracks, projector, 1, R, ut, 24.0, null);
                Assert.AreEqual(2, scene.Rings.Count, "Both systems are drawn at " + hours + " h.");
                foreach (OverlayRing ring in scene.Rings)
                {
                    ForecastTrack track = tracks.First(t => t.CellKey == ring.Key);
                    double lat, lon;
                    float radius, strength;
                    Assert.IsTrue(SystemTracker.PositionAt(track, ut, out lat, out lon, out radius, out strength));
                    float ex, ey;
                    projector.Project(1, lat, lon, out ex, out ey);
                    double error = Math.Sqrt((ring.Centre.X - ex) * (ring.Centre.X - ex) + (ring.Centre.Y - ey) * (ring.Centre.Y - ey));
                    worst = Math.Max(worst, error / screenHeight);
                }
            }
            Console.WriteLine("D30 overlay: worst ring position error " + (worst * 100.0).ToString("0.0000") + " % of the screen");
            Assert.IsTrue(worst <= 0.01, "The rings sit on the projected positions: " + worst);
            OverlayBuilder.Build(scene, tracks, projector, 1, R, 3 * H, 24.0, null);
            OverlayRing moving = scene.Rings.First(r => r.Key == 1);
            OverlayBuilder.Build(scene, tracks, projector, 1, R, 4 * H, 24.0, null);
            OverlayRing later = scene.Rings.First(r => r.Key == 1);
            Assert.AreEqual(2.0 * projector.PixelsPerDegree, later.Centre.X - moving.Centre.X, 0.01, "Two degrees of longitude an hour is 16 pixels.");
        }

        [TestMethod]
        public void ARingIsAsLargeOnTheScreenAsItsFootprintAndItsOutlineIsClosed()
        {
            var projector = new PlaneProjector();
            ForecastTrack[] tracks = { D30Fixtures.Track(1, 0.0, 0.0, 0.0, 0.0, 0.0, 4, 100000f, 0.7f) };
            var scene = new OverlayScene();
            OverlayBuilder.Build(scene, tracks, projector, 1, R, 0.0, 6.0, null);
            double expected = 100000.0 / R * 180.0 / Math.PI * projector.PixelsPerDegree;
            Assert.AreEqual(expected, scene.Rings[0].RadiusPx, 0.5);
            OverlayLine ring = scene.Lines.First(l => l.Kind == OverlayKind.Ring);
            Assert.AreEqual(29, ring.Points.Count);
            Assert.AreEqual(ring.Points[0].X, ring.Points[ring.Points.Count - 1].X, 0.01f);
            Assert.AreEqual(PrecipBand.Heavy, scene.Rings[0].Band);
        }

        [TestMethod]
        public void ZoomedOutManySystemsHideTheTinyRingsAndTheCapacityCutsTheWeakest()
        {
            var projector = new PlaneProjector { PixelsPerDegree = 0.05f };
            var many = new List<ForecastTrack>();
            for (int i = 0; i < 30; i++) many.Add(D30Fixtures.Track((ulong)(i + 1), 0.0, -60.0 + i * 4.0, -170.0 + i * 11.0, 0.0, 0.0, 4, 80000f, 0.5f));
            var scene = new OverlayScene();
            OverlayBuilder.Build(scene, many.ToArray(), projector, 1, R, 0.0, 6.0, null);
            Assert.AreEqual(0, scene.Rings.Count, "Thirty systems zoomed out: the rings under 6 pixels are left out.");
            Assert.AreEqual(30, scene.Hidden);
            OverlayBuilder.Build(scene, many.Take(10).ToArray(), projector, 1, R, 0.0, 6.0, null);
            Assert.AreEqual(10, scene.Rings.Count, "A few systems are drawn however small.");
            Assert.AreEqual(0, scene.Hidden);

            var projector2 = new PlaneProjector { PixelsPerDegree = 4f };
            var crowd = new List<ForecastTrack>();
            for (int i = 0; i < 70; i++) crowd.Add(D30Fixtures.Track((ulong)(i + 1), 0.0, -80.0 + i * 2.0, -179.0 + i * 5.0, 0.0, 0.0, 4, 300000f, 0.3f + i * 0.005f));
            OverlayBuilder.Build(scene, crowd.ToArray(), projector2, 1, R, 0.0, 6.0, null);
            Assert.AreEqual(64, scene.Rings.Count);
            Assert.AreEqual(6, scene.Hidden);
            Assert.IsFalse(scene.Rings.Any(r => r.Key <= 6), "The six weakest are the ones left out.");
        }

        [TestMethod]
        public void PlacesOnTheFarSideOfTheBodyBreakTheOutlineAndAHiddenCentreDrawsNothing()
        {
            var projector = new PlaneProjector();
            ForecastTrack[] tracks = { D30Fixtures.Track(1, 0.0, 0.0, 20.0, 0.0, 0.0, 4, 100000f, 0.7f) };
            var scene = new OverlayScene();
            projector.HiddenFromLon = 20.5;
            projector.HiddenToLon = 40.0;
            OverlayBuilder.Build(scene, tracks, projector, 1, R, 0.0, 6.0, null);
            Assert.AreEqual(1, scene.Rings.Count);
            foreach (OverlayLine line in scene.Lines.Where(l => l.Kind == OverlayKind.Ring))
            {
                foreach (P2 p in line.Points)
                {
                    double lon = (p.X - projector.OriginX) / projector.PixelsPerDegree;
                    Assert.IsFalse(lon >= 20.5 && lon <= 40.0, "No outline point on the far side: " + lon);
                }
            }
            projector.HiddenFromLon = 10.0;
            OverlayBuilder.Build(scene, tracks, projector, 1, R, 0.0, 6.0, null);
            Assert.AreEqual(0, scene.Rings.Count, "A system whose centre is hidden has no ring.");
        }

        [TestMethod]
        public void ThePathHasATickAtEveryFullHourAfterNow()
        {
            var projector = new PlaneProjector();
            ForecastTrack[] tracks = { D30Fixtures.Track(1, 0.0, 0.0, 0.0, 0.0, 1.0, 12, 100000f, 0.7f) };
            var scene = new OverlayScene();
            OverlayBuilder.Build(scene, tracks, projector, 1, R, 0.0, 24.0, null);
            Assert.AreEqual(5, scene.Lines.Count(l => l.Kind == OverlayKind.Tick), "Samples run 0 to 5.5 h: ticks at 1 to 5 h.");
            OverlayLine path = scene.Lines.First(l => l.Kind == OverlayKind.Track);
            Assert.AreEqual(12, path.Points.Count);
            Assert.IsTrue(path.Points[11].X > path.Points[0].X);
            OverlayLine tick = scene.Lines.First(l => l.Kind == OverlayKind.Tick);
            Assert.AreEqual(2, tick.Points.Count);
            Assert.AreEqual(tick.Points[0].X, tick.Points[1].X, 0.01f, "The bar stands across an eastward path.");
            Assert.AreEqual(12.0, Math.Abs(tick.Points[1].Y - tick.Points[0].Y), 0.01);
        }

        [TestMethod]
        public void HoverPicksTheRingUnderThePointerAndLabelsItWithTheTimeToTheSite()
        {
            var projector = new PlaneProjector();
            // A system moving east at 1 degree an hour that reaches a site 3 degrees east of it, with a footprint of 0.5 degrees.
            double radius = 0.5 * Math.PI / 180.0 * R;
            ForecastTrack[] tracks = { D30Fixtures.Track(1, 0.0, 0.0, 0.0, 0.0, 1.0, 24, (float)radius, 0.7f) };
            var scene = new OverlayScene();
            OverlayBuilder.Build(scene, tracks, projector, 1, R, 0.0, 12.0, null);
            OverlayRing ring = scene.Rings[0];
            Assert.AreSame(ring, scene.Pick(ring.Centre.X + 1f, ring.Centre.Y));
            Assert.IsNull(scene.Pick(ring.Centre.X + 200f, ring.Centre.Y));
            string label = OverlayBuilder.HoverLabel(ring, 0.0, 3.0, "Pad", 0.0, R);
            Console.WriteLine("D30 hover: " + label);
            Assert.AreEqual("Heavy (4), reaches Pad in 2 h 30 min", label);
            Assert.AreEqual("Heavy (4), does not reach Pad", OverlayBuilder.HoverLabel(ring, 40.0, 3.0, "Pad", 0.0, R));
            StringAssert.Contains(OverlayBuilder.HoverLabel(ring, 0.0, 0.0, "Pad", 0.0, R), "over Pad");
        }

        [TestMethod]
        public void ABodyWithoutTracksShowsTheFieldsPrecipitationMapAsDots()
        {
            var projector = new PlaneProjector();
            var map = new WeatherMap();
            map.Resize(4, 2);
            map.Values = new[] { 0f, 0.5f, 0f, 0.9f, 0f, 0f, 0.3f, 0f };
            var scene = new OverlayScene();
            OverlayBuilder.AddMapDots(scene, map, projector, 1, 4, 2, 0.2f);
            Assert.AreEqual(3, scene.Lines.Count);
            Assert.IsTrue(scene.Lines.All(l => l.Kind == OverlayKind.Dot && l.Points.Count == 1));
            Assert.IsTrue(scene.Lines.Any(l => l.Band == PrecipBand.Heavy || l.Band == PrecipBand.Blizzard));
            Assert.IsTrue(OverlayBuilder.FacesCamera(0, 0, 10, 0, 0, 10, 9.9), "The point under the camera is seen.");
            Assert.IsFalse(OverlayBuilder.FacesCamera(0, 0, -10, 0, 0, 10, 9.9), "The point on the far side is not.");
        }
    }
}
