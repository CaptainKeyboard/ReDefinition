using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // -t09DrawnCover (T09ProbeDrawn.cs, T09DrawnCover.cs): the K-T2 probe chooses its time and heading by what the clouds draw in the views, not by the field's column cover at the
    // site (wolken-m1-fakten.md H). The fake world's clock scene: candidates every hour from 1000800 (hour 2 of the Kerbin day), the sun 45 degrees up at hours 1 and 2 of each day
    // (the narrow band), the column cover 0.2 at hours 2 and 5 and 0.8 otherwise (CoverByHour). The views look east (ground, inside, above) and west (sky3).
    public partial class T09WalkThroughTests
    {
        private static int HourOf(double ut)
        {
            int n = (int)Math.Round(ut / 3600.0);
            return ((n % 6) + 6) % 6;
        }

        // The cover of a layer east of the site (a clear lane where `lane`) and west of it.
        private static Func<int, double, double, double, double> Lane(Func<double, bool> laneAt, double cloud)
        {
            return (l, lat, lon, ut) => laneAt(ut) && lon > -74.5577 ? 0.0 : cloud;
        }

        private Run StartDrawn(string[] variants, string[] extraArgs, Action<Run> tweak)
        {
            return Start(variants, x =>
            {
                var args = x.Probe.CommandLineOverride.ToList();
                args.AddRange(extraArgs);
                x.Probe.CommandLineOverride = args.ToArray();
                if (tweak != null) tweak(x);
            });
        }

        [TestMethod]
        public void DrawnCover_ATimeWithAClearLaneInViewIsRejectedAndOneWithCloudsInViewIsChosen()
        {
            // Hour 2 (the first candidate, sun 45 degrees, column cover 0.2) has a clear lane east of the site, where three views look; hour 1 has its layers at 0.35 everywhere.
            // The forecast misses the lane (it has no meso pattern): the maps the pass draws must reject the time.
            Run r = StartDrawn(new[] { "plain" }, new[] { "-t09DrawnCover", "-t09DrawnHeadings", "0" }, x =>
            {
                x.World.OwnCoverAt = Lane(ut => HourOf(ut) == 2, 0.35);
                x.World.ForecastCoverAt = (l, lat, lon, ut) => 0.35;
            });
            Drive(r, 0.1);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("T09 drawn: ut 1000800 heading +0") && l.Contains("(rejected: own ground 0.00 below 0.50")), "the lane is rejected\n" + Dump(r));
            Assert.AreEqual(1000800.0 + 5 * 3600.0, Number(r, "T09.ut"), 1e-6, "the next time with the sun in the band and clouds in view");
            Assert.AreEqual(1.0, Number(r, "T09.drawn.ok"));
            Assert.AreEqual(0.0, Number(r, "T09.heading_offset_deg"));
            // The search stops after three qualifying times at the latest (every confirmation costs a clock jump and a map settle).
            Assert.IsTrue(Number(r, "T09.drawn.passes") <= 3.0 && Number(r, "T09.drawn.confirmed") <= 4.0, Dump(r));
            foreach (string view in new[] { "ground", "inside", "above", "sky3" })
            {
                double cover = Number(r, "T09.drawn." + view + ".cover");
                Assert.IsTrue(cover >= 0.5 && cover <= 1.0, view + " cover " + cover);
                Assert.IsTrue(Number(r, "T09.drawn." + view + ".layer_fraction") > 0.3, view + " meets the layers");
                // The forecast at the chosen time and heading is a row beside the estimate (here equal: the forecast has the layers at 0.35 everywhere; its grid is coarser).
                Assert.AreEqual(cover, Number(r, "T09.drawn." + view + ".forecast"), 0.1, view + " forecast");
            }
            // After the pictures of the own variant, the measured share beside the estimate.
            Assert.AreEqual(1.0, Number(r, "T09.view.ground.picture_cloud_share", "plain"), 1e-9);
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("T09 picture share: ground plain: measured 1.00 of the picture changed by the clouds, the drawn estimate ")), Dump(r));
        }

        [TestMethod]
        public void DrawnCover_TheViewsTurnToTheHeadingWhereTheyShowCloudAndASecondStartReusesTimeAndHeading()
        {
            // Hour 2: clear east of the site, 0.35 west; every other hour clear. Looking east (offset 0), north or south (half of each fan in the lane) the three required views
            // show too little; turned by 180 degrees they look west into the clouds.
            Func<int, double, double, double, double> cover = (l, lat, lon, ut) => HourOf(ut) == 2 && lon <= -74.5577 ? 0.35 : 0.0;
            Run r = StartDrawn(new[] { "plain" }, new[] { "-t09DrawnCover", "-t09DrawnViews", "ground,inside,above" }, x => x.World.OwnCoverAt = cover);
            Drive(r, 0.1);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.AreEqual(1000800.0, Number(r, "T09.ut"), 1e-6);
            Assert.AreEqual(180.0, Number(r, "T09.heading_offset_deg"));
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("T09 drawn: ut 1000800 heading +0") && l.Contains("rejected")), Dump(r));
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("T09 drawn: ut 1000800 heading +90") && l.Contains("rejected")), Dump(r));
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("T09 drawn: ut 1000800 heading +180") && l.Contains("qualifies")), Dump(r));
            // The views were placed looking west (the sky view, not required, looks east into the lane).
            foreach (string view in new[] { "ground", "inside", "above" })
            {
                double az = Number(r, "T09.view." + view + ".azimuth_deg", "plain");
                Assert.IsTrue(Math.Abs(((az - 270.0 + 540.0) % 360.0) - 180.0) < 15.0, view + " azimuth " + az);
            }
            Assert.AreEqual(0.0, Number(r, "T09.drawn.sky3.cover"), 1e-9);

            // The own start of the pair: the time and the heading through the arguments; the estimates are read and checked there.
            Run again = StartDrawn(new[] { "plain" }, new[] { "-t09DrawnCover", "-t09DrawnViews", "ground,inside,above", "-t09Ut", "1000800", "-t09HeadingOffset", "180" },
                                   x => x.World.OwnCoverAt = cover);
            Drive(again, 0.1);
            Assert.IsTrue(again.Probe.Passed, Dump(again));
            Assert.AreEqual(1000800.0, Number(again, "T09.ut"), 1e-6);
            Assert.AreEqual(180.0, Number(again, "T09.heading_offset_deg"));
            Assert.IsTrue(Number(again, "T09.drawn.ground.cover") >= 0.5);

            // The same reuse with the heading the choice rejected fails by name, before any view.
            Run wrong = StartDrawn(new[] { "plain" }, new[] { "-t09DrawnCover", "-t09DrawnViews", "ground,inside,above", "-t09Ut", "1000800", "-t09HeadingOffset", "0" },
                                   x => x.World.OwnCoverAt = cover);
            Drive(wrong, 0.1);
            AssertNamedFailure(wrong, "the views show cloud at the chosen time and heading (drawn cover)");
        }

        [TestMethod]
        public void DrawnCover_WithTvcBothSidesMustShowCloudInTheSameViews()
        {
            // TVC as shipped: our layers at 0.35 everywhere; EVE's own layers clear at hour 2 and at 0.6 at hour 1. The first candidate (hour 2) has our clouds but not TVC's.
            Run r = StartDrawn(new[] { "off" }, new[] { "-t09TvcAsShipped", "-t09TvcCover", "-t09DrawnCover", "-t09DrawnHeadings", "0" }, x =>
            {
                x.World.OwnCoverAt = (l, lat, lon, ut) => 0.35;
                x.World.EveCoverAt = (l, lat, lon, ut) => HourOf(ut) == 2 ? 0.0 : 0.6;
            });
            Drive(r, 0.1);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("T09 drawn: ut 1000800 heading +0") && l.Contains("(rejected: TVC ground 0.00 below 0.50")), Dump(r));
            Assert.AreEqual(1000800.0 + 5 * 3600.0, Number(r, "T09.ut"), 1e-6);
            foreach (string view in new[] { "ground", "inside", "above", "sky3" })
            {
                Assert.IsTrue(Number(r, "T09.drawn." + view + ".tvc_cover") >= 0.5, view + " TVC");
                Assert.IsTrue(Number(r, "T09.drawn." + view + ".cover") >= 0.5, view + " own");
            }
            // The off variant (TVC as shipped) waits for EVE's renderer and its own layers after the source went back to None, on the condition.
            int off = r.Log.FindIndex(l => l == "Probe: variant off on"), ready = r.Log.FindIndex(l => l.StartsWith("T09 maps (off): EVE's renderer ready with 2 layers of its own"));
            Assert.IsTrue(off >= 0 && ready > off && r.Log.FindIndex(l => l.StartsWith("T09 view begin: ")) > ready, Dump(r));
            // The off variant (TVC as shipped) has no baseline for the picture's share and says so.
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("T09 picture share: ground off: not measured")), Dump(r));
        }

        [TestMethod]
        public void DrawnCover_NoTimeWithCloudsInViewFailsNamedBeforeAnyView()
        {
            Run r = StartDrawn(new[] { "plain" }, new[] { "-t09DrawnCover" }, x => x.World.OwnCoverAt = (l, lat, lon, ut) => 0.05);
            Drive(r, 0.1);
            AssertNamedFailure(r, "no universal time and heading has the clouds drawn in every required view (-t09DrawnCover: own at least 0.50, layer share at least 0.50; 8 times confirmed");
            Assert.AreEqual(0, r.Log.Count(l => l.StartsWith("T09 view begin")));
            // The failure names the best three confirmed times with their numbers, not only the last rejection.
            string failure = r.Probe.Failures.First(f => f.Contains("no universal time and heading"));
            Assert.AreEqual(3, failure.Split('|').Length, failure);
            Assert.IsTrue(failure.Contains("the best: ut ") && failure.Contains(": own ground "), failure);
        }

        [TestMethod]
        public void DrawnCover_AViewWithTooFewRaysOnALayerDoesNotPassOnItsSkyRays()
        {
            // The ground view has 7 of its 9 rows on a layer (0.78): a minimum share of 0.9 rejects it whatever its cover, and the failure says why.
            Run r = StartDrawn(new[] { "plain" }, new[] { "-t09DrawnCover", "-t09DrawnHeadings", "0", "-t09DrawnMinLayerShare", "0.9" }, null);
            Drive(r, 0.1);
            AssertNamedFailure(r, "layer share at least 0.90");
            Assert.IsTrue(r.Probe.Failures.Any(f => f.Contains("own ground layer share 0.78 below 0.90")), r.Joined);
        }

        [TestMethod]
        public void MapsThatNeverSettleFailTheFirstOwnVariantBeforeAnyView()
        {
            Run r = Start(new[] { "plain" }, x => x.World.FieldLagFrames = 10000000);
            Drive(r, 0.1);
            AssertNamedFailure(r, "the maps did not settle for the first own variant (plain) within 20 s: the layer maps are not made for the clock's time yet");
            Assert.AreEqual(0, r.Log.Count(l => l.StartsWith("T09 view begin")));
        }

        [TestMethod]
        public void DrawnCover_TheGeometryFollowsTheRaysToTheLayers()
        {
            const double R = 600000.0;
            // Straight up from 200 m to a layer at 2000 m: the foot of the crossing is the camera's.
            Assert.AreEqual(0.0, T09DrawnCover.CrossingM(R, 200.0, 2000.0, 90.0), 1e-6);
            // 45 degrees up: the height difference away, within 1 percent (the curve of the shell adds metres over 2 km).
            Assert.AreEqual(1800.0, T09DrawnCover.CrossingM(R, 200.0, 2000.0, 45.0), 18.0);
            // Below the horizon from 200 m the ray meets the sea before a layer above; looking down from 8000 m it meets a layer at 2000 m.
            Assert.IsTrue(double.IsNaN(T09DrawnCover.CrossingM(R, 200.0, 2000.0, -10.0)));
            // (on the sphere the shell below falls away by x^2 / 2R, about 140 m at 13 km: the ray meets it a little farther than in a plane, within 3 percent)
            double down = T09DrawnCover.CrossingM(R, 8000.0, 2000.0, -25.0), flat = 6000.0 / Math.Tan(25.0 * Math.PI / 180.0);
            Assert.IsTrue(down > flat && down < 1.03 * flat, "down " + down + ", flat " + flat);
            // The rays of a 60 degree frame centred on east at 18 degrees up span the frame.
            var rays = T09DrawnCover.Rays(90.0, 18.0, 60.0, 16.0 / 9.0, 7, 9);
            Assert.AreEqual(63, rays.Count);
            Assert.IsTrue(rays.Min(x => x.ElevationDeg) < -5.0 && rays.Max(x => x.ElevationDeg) > 40.0);
            Assert.IsTrue(rays.All(x => x.AzimuthDeg > 30.0 && x.AzimuthDeg < 150.0));
            // A layer of cover 0.5 everywhere is half of the sky; two of them three quarters where a ray meets both.
            var one = new[] { new T09DrawnLayer { MidM = 2000.0 } };
            var two = new[] { new T09DrawnLayer { MidM = 2000.0 }, new T09DrawnLayer { MidM = 5000.0 } };
            var up = T09DrawnCover.Rays(0.0, 60.0, 30.0, 1.0, 3, 3);
            Assert.AreEqual(0.5, T09DrawnCover.Of(0.0, 0.0, 200.0, R, up, one, (l, a, b) => 0.5).Cover, 1e-9);
            Assert.AreEqual(0.75, T09DrawnCover.Of(0.0, 0.0, 200.0, R, up, two, (l, a, b) => 0.5).Cover, 1e-9);
            Assert.AreEqual(1.0, T09DrawnCover.Of(0.0, 0.0, 200.0, R, up, two, (l, a, b) => 0.5).LayerFraction, 1e-9);
            // A camera inside a layer (between its bottom and top) reads it at its own place with every ray, also the rays that look down or along the layer.
            var band = new[] { new T09DrawnLayer { MidM = 4600.0, BottomM = 4000.0, TopM = 5200.0 } };
            var level = T09DrawnCover.Rays(90.0, 0.0, 60.0, 16.0 / 9.0, 7, 9);
            int places = 0;
            var inside = T09DrawnCover.Of(0.0, 0.0, 4550.0, R, level, band, (l, a, b) => { places += Math.Abs(a) < 1e-9 && Math.Abs(b) < 1e-9 ? 1 : 0; return 0.6; });
            Assert.AreEqual(1.0, inside.LayerFraction, 1e-9);
            Assert.AreEqual(0.6, inside.Cover, 1e-9);
            Assert.AreEqual(63, places, "every ray read the layer at the camera's place");
            // Rays that all meet the sea and no layer: nothing counts.
            var sea = T09DrawnCover.Of(0.0, 0.0, 200.0, R, T09DrawnCover.Rays(0.0, -60.0, 30.0, 1.0, 3, 3), one, (l, a, b) => 1.0);
            Assert.AreEqual(0.0, sea.LayerFraction, 1e-9);
            Assert.IsTrue(double.IsNaN(sea.Cover));
        }
    }
}
