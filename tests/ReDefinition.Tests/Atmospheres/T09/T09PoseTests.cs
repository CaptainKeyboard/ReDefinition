using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The pose of K-T2's views (start 42): the pictures did not show the logged poses because the vessel had left its pose during the dwell (it climbed from 4.6 km
    // to 23 km in 25 s, shed its stages on the way and was a dot afterwards), and orbit_edge could not be aimed at 300 km (the camera's AUTO mode is ORBITAL above the
    // atmosphere, its aim lags its commands, and the old per-axis loop learnt one sign per axis). Each cause is injected into the fake world; the probe has to fail the view
    // named, not take the picture, and aim in every camera the fake can be.
    public partial class T09WalkThroughTests
    {
        private static void AssertNoPictureOfView(Run r, string view)
        {
            Assert.IsFalse(r.World.Saved.Any(n => n.Contains("-" + view + "-")), "no picture of " + view + " was saved: " + string.Join(", ", r.World.Saved.ToArray()));
        }

        // --- The aim at orbit_edge ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void TheHorizonDipIs48DegreesOnKerbinAt300KmAnd17OnTheEarth()
        {
            Assert.AreEqual(48.19, T09Probe.HorizonDipDeg(600000.0, 300000.0), 0.01);
            Assert.AreEqual(17.3, T09Probe.HorizonDipDeg(6371000.0, 300000.0), 0.1);
            Assert.AreEqual(0.0, T09Probe.HorizonDipDeg(600000.0, 0.0), 1e-9);
        }

        private void AssertOrbitEdgePlaced(Run r)
        {
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("T09 view begin: orbit_edge off")), "orbit_edge was measured\n" + Dump(r));
            Assert.AreEqual(-48.19, Number(r, "T09.view.orbit_edge.elevation_deg", "off"), 3.0);
            Assert.AreEqual(90.0, Number(r, "T09.view.orbit_edge.azimuth_deg", "off"), 15.0);
        }

        [TestMethod]
        public void OrbitEdgeIsAimedByACameraThatLagsItsCommands()
        {
            Run r = Start(new[] { "off" }, x => x.World.LagS = 0.8);
            Drive(r);
            AssertOrbitEdgePlaced(r);
        }

        [TestMethod]
        public void OrbitEdgeIsAimedByACameraWhoseAxesAreInverted()
        {
            Run r = Start(new[] { "off" }, x => { x.World.PitchSign = -1.0; x.World.HeadingSign = -1.0; });
            Drive(r);
            AssertOrbitEdgePlaced(r);
        }

        [TestMethod]
        public void OrbitEdgeIsAimedInTheOrbitalFrameWhenTheGameIgnoresTheFreeMode()
        {
            // AUTO resolves to ORBITAL above the atmosphere: the axes are rotated against the horizon (at the equator the heading axis is horizontal), the frame turns
            // against the horizon, the camera lags, and the pitch is inverted.
            Run r = Start(new[] { "off" }, x =>
            {
                x.World.FreeModeWorks = false;
                x.World.OrbitalMixDeg = 70.0;
                x.World.RotationDriftDegPerS = 0.0167;
                x.World.LagS = 0.5;
                x.World.PitchSign = -1.0;
            });
            Drive(r);
            AssertOrbitEdgePlaced(r);
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("T09 view placed: orbit_edge") && l.Contains("mode AUTO (ORBITAL)")), "the camera mode is in the placement line");
        }

        [TestMethod]
        public void TheFreeModeIsRequestedBeforeTheAimAndNamedInThePlacementLine()
        {
            Run r = Start(new[] { "off" });
            Drive(r);
            AssertOrbitEdgePlaced(r);
            Assert.IsTrue(r.World.FreeMode, "the probe asked for the FREE mode");
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("T09 view placed: orbit_edge") && l.Contains("mode FREE")), "the mode is in the placement line");
        }

        [TestMethod]
        public void AnAimThatCannotBeReachedFailsNamedWithTheAimItHasAndTheOtherViewsAreMeasured()
        {
            // The game clamps the pitch to 40 degrees: the limb's 48.2 degrees cannot be reached; the other views are within the clamp.
            Run r = Start(new[] { "off" }, x => x.World.PitchLimitDeg = 40.0);
            Drive(r);
            AssertNamedFailure(r, "T09.pose_placed_orbit_edge_off");
            Assert.IsTrue(r.Probe.Failures.Any(f => f.Contains("aim: ") && f.Contains("40.0")), "the failure names the aim the camera has: " + r.Joined);
            Assert.IsFalse(r.Log.Any(l => l.StartsWith("T09 view begin: orbit_edge")), "no view was measured at the wrong aim");
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("T09 view begin: sky3 off")), "the views after it are still measured");
        }

        [TestMethod]
        public void ACameraThatTakesNoCommandIsNamedInThePlacementFailure()
        {
            Run r = Start(new[] { "plain" }, x => x.World.CameraStuck = true);
            Drive(r);
            AssertNamedFailure(r, "T09.pose_placed_ground_plain");
            Assert.IsTrue(r.Probe.Failures.Any(f => f.Contains("did not take the heading command")), r.Joined);
        }

        // --- The vessel that leaves its pose --------------------------------------------------------------------------------------------

        [TestMethod]
        public void AVesselThatClimbsAwayAfterThePlacementFailsTheViewNamedAndNoPictureIsTaken()
        {
            // Start 42: the placement check was ok, and 6 s later the vessel climbed at hundreds of m/s.
            Run r = Start(new[] { "plain" }, x => x.World.AltitudeDrift = s => s > 6.0 ? 150.0 * (s - 6.0) : 0.0);
            Drive(r);
            AssertNamedFailure(r, "T09.pose_held_ground_plain");
            Assert.IsTrue(r.Probe.Failures.Any(f => f.StartsWith("T09.pose_held_ground_plain (altitude ") && f.Contains("over the terrain) against 270") && f.Contains("of the dwell")), r.Joined);
            Assert.AreEqual(1, r.Probe.Failures.Count(f => f.StartsWith("T09.pose_held_ground_plain")), "named once per view");
            Assert.AreEqual(0, r.World.Saved.Count, "no picture of a pose that is not the view's");
            Assert.IsTrue(Values(r, "T09.warning.pose_mismatch").Count > 0, "a warning row names the skipped pictures");
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("Probe: warning pose_mismatch")));
        }

        [TestMethod]
        public void AVesselThatDriftsSidewaysIsToleratedWithin100MetresAndFailsBeyond()
        {
            // 2 m/s: 80 m in 40 s since the placement: held.
            Run slow = Start(new[] { "plain" }, x => x.World.HorizontalDriftMPerS = 2.0);
            Drive(slow);
            Assert.IsTrue(slow.Probe.Passed, Dump(slow));
            Assert.IsTrue(slow.World.Saved.Count > 0, "pictures were taken");

            Run fast = Start(new[] { "plain" }, x => x.World.HorizontalDriftMPerS = 12.0);
            Drive(fast);
            AssertNamedFailure(fast, "T09.pose_held_ground_plain");
            Assert.IsTrue(fast.Probe.Failures.Any(f => f.Contains("m from the point it is held at")), fast.Joined);
            Assert.AreEqual(0, fast.World.Saved.Count);
        }

        [TestMethod]
        public void AVesselThatIsADotBecauseItShedItsStagesFailsTheCameraDistanceNamed()
        {
            // Start 42: the camera stayed at its distance, the vessel was a pod after its stack had broken off, and the pictures showed a dot. The camera 1.2 km from
            // the vessel at a view asked for at 100 m is such a pose.
            Run r = Start(new[] { "plain" }, x => x.World.CameraDistanceFactor = s => 12.0);
            Drive(r);
            AssertNamedFailure(r, "T09.pose_held_ground_plain");
            Assert.IsTrue(r.Probe.Failures.Any(f => f.Contains("m from the vessel against 100")), r.Joined);
            Assert.AreEqual(0, r.World.Saved.Count);
        }

        [TestMethod]
        public void APoseThatMovesWhenTheReadStartsFailsThePictureNamedAndItIsNotSaved()
        {
            Run r = Start(new[] { "off" }, x => x.World.AltitudeJumpOnCaptureBegin = 3000.0);
            Drive(r);
            AssertNamedFailure(r, "T09.pose_picture_t09-own1707-ground-off-1707x960-natural");
            Assert.AreEqual(0, r.Probe.Failures.Count(f => f.StartsWith("T09.pose_held_")), "the dwell itself was fine: " + r.Joined);
            Assert.AreEqual(0, r.World.Saved.Count, "no picture of another place than its name says");
            Assert.IsTrue(Values(r, "T09.pic.t09-own1707-ground-off-1707x960-natural.alt_m").Count > 0, "the pose rows of the picture are written");
        }

        [TestMethod]
        public void APoseThatMovesWhileTheReadIsUnderWayFailsThePictureNamed()
        {
            Run r = Start(new[] { "off" }, x => x.World.AltitudeJumpOnCaptureTake = 3000.0);
            Drive(r);
            AssertNamedFailure(r, "T09.pose_picture_t09-own1707-ground-off-1707x960-natural");
            Assert.AreEqual(0, r.World.Saved.Count);
        }

        [TestMethod]
        public void AnAltitudeAskedAsAboveTheTerrainButTakenAsSeaLevelIsNamedWithTheHeightOverTheTerrain()
        {
            // The vessel at the terrain-relative height where the sea-level one was meant (the datum of the 4.6 km 'inside' view).
            Run r = Start(new[] { "plain" }, x => x.World.PlaceResult = (call, wanted) => wanted - x.World.Terrain);
            Drive(r);
            AssertNamedFailure(r, "T09.pose_placed_ground_plain");
            Assert.IsTrue(r.Probe.Failures.Any(f => f.Contains("m over the terrain, camera")), "the placement line gives both datums: " + r.Joined);
        }

        // --- What a healthy start writes ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void EveryPictureOfAHealthyStartCarriesItsPoseRowsAndTheHoldRowsAreWritten()
        {
            Run r = Start(new[] { "off", "plain" });
            Drive(r);
            Assert.IsTrue(r.Probe.Passed, Dump(r));
            foreach (string saved in r.World.Saved)
            {
                string name = saved;
                foreach (string suffix in new[] { "alt_m", "alt_agl_m", "cam_alt_m", "cam_agl_m", "elev_deg", "heading_deg", "cam_dist_m", "drift_m" })
                {
                    Assert.IsTrue(Values(r, "T09.pic." + name + "." + suffix).Count > 0, "row for " + name + " " + suffix);
                }
            }
            string ground = Full("ground", "plain", "natural");
            Assert.AreEqual(270.0, Number(r, "T09.pic." + ground + ".alt_m"), 1.0);
            Assert.AreEqual(200.0, Number(r, "T09.pic." + ground + ".alt_agl_m"), 1.0);
            Assert.AreEqual(18.0, Number(r, "T09.pic." + ground + ".elev_deg"), 3.0);
            Assert.AreEqual(90.0, Number(r, "T09.pic." + ground + ".heading_deg"), 15.0);
            Assert.AreEqual(100.0, Number(r, "T09.pic." + ground + ".cam_dist_m"), 1.0);
            Assert.AreEqual(0.0, Number(r, "T09.pic." + ground + ".drift_m"), 1.0);
            Assert.IsTrue(Values(r, "T09.view.ground.hold_corrections", "plain").Count > 0);
            Assert.IsTrue(Values(r, "T09.view.orbit_edge.hold_worst_drift_m", "plain").Count > 0);
            Assert.IsTrue(r.Log.Any(l => l.StartsWith("Probe: check T09.pose_held_orbit_edge_plain: ok")), "a held check per view passes by name");
        }
    }
}
