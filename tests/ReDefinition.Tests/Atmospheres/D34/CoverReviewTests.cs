using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Raytracing;

namespace ReDefinition.Tests.Atmospheres.D34
{
    // Package D34, the independent review's findings: the shell gate and the ray-traced scene's tag (R1, R3), the screen-space region
    // maths (R1), the tyre width and the track runs (R5, R4), the shared memory of the tracks (R10) and the paint budget (R9).
    [TestClass]
    public class CoverReviewTests
    {
        // ---- R1: the gate on foreign shaders, and the method switch ---------------------------------------------------------------------

        [TestMethod]
        public void AShellIsBuiltOnlyOverStockLikeShadersAndNeverOverTheFiveForeignOnesOfRecon()
        {
            foreach (string stock in new[] { "KSP/Diffuse", "KSP/Bumped Specular", "KSP/Emissive/Bumped Specular", "Standard", "Legacy Shaders/Diffuse", "Deferred/Standard" })
                Assert.IsTrue(VehicleCoverGate.ShellAccepts(stock), stock);
            // The candidates RECON P8 found in the frame: none looked like the stock material, so none gets a shell.
            foreach (string foreign in new[] { "Custom/_Standard", "MechJeb/DiffuseAmbient", "Parallax Specular (Alpha)", "RPM/JSILabel", "Shader Forge/Monolith", "", null })
                Assert.IsFalse(VehicleCoverGate.ShellAccepts(foreign), foreign ?? "(null)");
        }

        [TestMethod]
        public void TheMethodSwitchHasTwoValuesAndTheShellIsTheDefault()
        {
            CollectionAssert.AreEqual(new[] { "Shell", "ScreenSpace" }, Enum.GetNames(typeof(VehicleCoverMethod)));
            Assert.AreEqual((double)(int)VehicleCoverMethod.Shell, AtmospheresSettings.Definition("vehicleCoverMethod").Default);
            Assert.AreEqual("needs-ground-cover", AtmospheresSettings.Definition("vehicleCoverMethod").GreyReason);
        }

        // ---- R3: the ray-traced scene leaves the shell out ----------------------------------------------------------------------------

        [TestMethod]
        public void TheRayTracedSceneLeavesTheShellShaderOutByTheNameBothAssembliesUse()
        {
            Assert.AreEqual(VehicleCover.ShaderName, VehicleCoverGate.ShellShaderName);
            Assert.AreEqual(VehicleCoverGate.ShellShaderName, RtFilter.CoverShellShader, "the contract name is the same in both assemblies");
            Assert.IsTrue(RtFilter.LeftOut(VehicleCoverGate.ShellShaderName));
            Assert.IsTrue(RtFilter.LeftOut("Custom/DiscardAll"), "Parallax's discarding material is still left out");
            foreach (string name in new[] { "KSP/Diffuse", "Standard", "ReDefinition/AtmospheresShellScreen", "", null })
                Assert.IsFalse(RtFilter.LeftOut(name), name ?? "(null)");
            Assert.IsTrue(VehicleCoverGate.RayTracingLeavesOut(VehicleCover.ShaderName));
            Assert.IsFalse(VehicleCoverGate.RayTracingLeavesOut("KSP/Diffuse"));
        }

        // ---- R1: the region maths of the screen-space pass --------------------------------------------------------------------------------

        [TestMethod]
        public void TheFloorOfARegionIsTheLowestPointAlongUpPlusAMargin()
        {
            // A box of half sizes 4, 0.5 and 2 with up along y: its lowest point is 0.5 below the middle.
            CoverScreenMath.Region r = CoverScreenMath.RegionOf(0, 0, 0, 4f, 0.5f, 2f, 0f, 1f, 0f, 0.7f);
            Assert.AreEqual(-0.5f + CoverScreenMath.FloorMarginM, r.FloorM, 1e-6f);
            Assert.AreEqual(Math.Sqrt(16 + 0.25 + 4), r.Radius, 1e-5);
            Assert.AreEqual(0.7f, r.Amount, 0f);
            // Turned so that up lies along x, the same box reaches 4 below.
            Assert.AreEqual(-4f + CoverScreenMath.FloorMarginM, CoverScreenMath.RegionOf(0, 0, 0, 4f, 0.5f, 2f, 1f, 0f, 0f, 1f).FloorM, 1e-6f);
        }

        [TestMethod]
        public void ThePointsInsideARegionAreAboveTheFloorAndWithinTheRadius()
        {
            CoverScreenMath.Region r = CoverScreenMath.RegionOf(10, 20, 30, 4f, 0.5f, 2f, 0f, 1f, 0f, 1f);
            Assert.IsTrue(CoverScreenMath.Inside(r, 10, 20.4f, 30, 0, 1, 0), "the roof");
            Assert.IsFalse(CoverScreenMath.Inside(r, 10, 19.0f, 30, 0, 1, 0), "the ground under the vessel is below the floor");
            Assert.IsFalse(CoverScreenMath.Inside(r, 10, 20.4f, 40, 0, 1, 0), "beyond the radius");
        }

        [TestMethod]
        public void TheNearestFourVesselsWithCoverAreSelectedNearestFirst()
        {
            var all = new CoverScreenMath.Region[7];
            float[] distances = { 120f, 30f, 500f, 60f, 10f, 200f, 90f };
            float[] amounts = { 0.5f, 0.5f, 0.9f, 0.0f, 0.4f, 0.6f, 0.3f };
            for (int i = 0; i < all.Length; i++) all[i] = CoverScreenMath.RegionOf(distances[i], 0, 0, 1f, 1f, 1f, 0f, 1f, 0f, amounts[i]);
            var into = new CoverScreenMath.Region[CoverScreenMath.MaxVessels];
            int n = CoverScreenMath.Select(all, all.Length, 0, 0, 0, 0.02f, into);
            Assert.AreEqual(4, n);
            // 500 m is beyond NearMetres (400); 60 m has no cover; of the rest the four nearest are 10, 30, 90, 120.
            Assert.AreEqual(10f, into[0].X, 0f);
            Assert.AreEqual(30f, into[1].X, 0f);
            Assert.AreEqual(90f, into[2].X, 0f);
            Assert.AreEqual(120f, into[3].X, 0f);
            Assert.AreEqual(0, CoverScreenMath.Select(all, 0, 0, 0, 0, 0.02f, into));
        }

        // ---- R5, R4: the tyre's width and the runs across a track ------------------------------------------------------------------------

        [TestMethod]
        public void TheMarkIsHalfTheSmallestDimensionOfTheWheelMeshAndNeverNarrowerThanAHalfTexel()
        {
            Assert.AreEqual(0.25f, TrackWidth.HalfWidthM(1.0f, 1.0f, 0.4f, 0.1f), 1e-6f, "a 0.4 m tyre: half of it is 0.2, under the floor of half a texel (0.25)");
            Assert.AreEqual(0.5f * 0.9f, TrackWidth.HalfWidthM(1.4f, 0.9f, 1.4f, 0.25f), 1e-6f, "a wide tyre of 0.9 m marks 0.9 m");
            Assert.AreEqual(0.25f, TrackWidth.HalfWidthM(0f, float.NaN, -1f, 0.25f), 0f, "no usable dimension: the tuning value stands");
        }

        [TestMethod]
        public void ARoverLeavesTwoRunsAcrossItsHeadingEachAsWideAsTheStamp()
        {
            const double radius = 600000.0;
            var tiles = new TrackTiles(radius);
            double lat = 10.0, lon = 20.0;
            double metresPerDegLat = radius * Math.PI / 180.0;
            double metresPerDegLon = metresPerDegLat * Math.Cos(lat * Math.PI / 180.0);
            // Two tracks 2 m apart, driving north: a line of stamps every 0.25 m for 20 m, each 0.75 m half width... use 0.5 m.
            for (double north = 0; north <= 20.0; north += 0.25)
                foreach (double east in new[] { -1.0, 1.0 })
                    Assert.IsTrue(tiles.Stamp(lat + north / metresPerDegLat, lon + east / metresPerDegLon, 0.5, 100.0, 5000.0, 1.0));
            double meanRun;
            // Across the heading (north is bearing 0): the line runs east-west through the middle of the trail.
            int runs = tiles.RunsAcross(lat + 10.0 / metresPerDegLat, lon, 0.0, 3.0, 100.0, out meanRun);
            Assert.AreEqual(2, runs, "two tracks");
            Assert.AreEqual(1.0, meanRun, 0.35, "each as wide as the stamp (1 m), within a texel: " + meanRun);
            // Far from the trail there is none.
            Assert.AreEqual(0, tiles.RunsAcross(lat + 10.0 / metresPerDegLat, lon + 20.0 / metresPerDegLon, 0.0, 3.0, 100.0, out meanRun));
            Assert.AreEqual(0.0, meanRun, 0.0);
        }

        // ---- R10: the tiles of all bodies share 4 MB ----------------------------------------------------------------------------------------

        [TestMethod]
        public void TheLiveTilesOfTwoBodiesTogetherStayWithinTheCapAndTheOldestOfEitherGoesFirst()
        {
            var budget = new TrackBudget();
            var kerbin = new TrackTiles(600000.0, budget);
            var duna = new TrackTiles(320000.0, budget);
            double lat = 0.0;
            for (int i = 0; i < 40; i++) Assert.IsTrue(kerbin.Stamp(lat, i * 0.05, 0.5, 10.0, 1000.0, 1.0));
            Assert.AreEqual(40, budget.TotalLive);
            for (int i = 0; i < 40; i++) Assert.IsTrue(duna.Stamp(lat, i * 0.05, 0.5, 10.0, 1000.0, 1.0));
            Assert.IsTrue(budget.TotalLive <= TrackTiles.MaxLive, "together: " + budget.TotalLive);
            Assert.IsTrue((long)budget.TotalLive * TrackTiles.TexelsPerSide * TrackTiles.TexelsPerSide <= 4L * 1024 * 1024, "4 MB for all bodies");
            Assert.AreEqual(80, kerbin.LiveCount + kerbin.ParkedCount + duna.LiveCount + duna.ParkedCount, "nothing is lost, the rest is parked");
            Assert.IsTrue(kerbin.ParkedCount > 0 && duna.ParkedCount == 0, "Kerbin's tiles were the oldest: " + kerbin.ParkedCount + " / " + duna.ParkedCount);
            // A tile that went to the compressed form comes back with its content.
            Assert.IsTrue(kerbin.Shown(lat, 0.0, 10.0) > 0.5, "the first Kerbin tile is parked and still shows");
            duna.Leave();
            Assert.AreEqual(0, duna.LiveCount);
        }

        [TestMethod]
        public void ATrackTilesWithoutABudgetKeepsTheCapForItself()
        {
            var tiles = new TrackTiles(600000.0);
            for (int i = 0; i < 70; i++) tiles.Stamp(0.0, i * 0.05, 0.5, 10.0, 1000.0, 1.0);
            Assert.AreEqual(TrackTiles.MaxLive, tiles.LiveCount);
            Assert.AreEqual(6, tiles.ParkedCount);
        }

        // ---- R9: the paint budget on a storm-scale replay ---------------------------------------------------------------------------------------

        [TestMethod]
        public void AStormScaleRepaintFinishesInFramesAndNoFrameOverrunsItsBudgetByMoreThanOneQuad()
        {
            // 3000 quads (a storm over a broad area at the tier's draw distance), mostly 0.15 ms and some 1.2 ms (broad cells, the
            // Parallax notification on top), against the budget of 2 ms a frame.
            var costs = new List<double>();
            for (int i = 0; i < 3000; i++) costs.Add(i % 17 == 0 ? 1.2 : 0.15);
            double total = 0, worst = 0, maxQuad = 1.2;
            foreach (double c in costs) total += c;
            int cursor = 0, frames = 0;
            while (cursor < costs.Count)
            {
                double spent;
                cursor = CoverRepainter.FrameOnVirtualClock(costs, cursor, 2.0, out spent);
                worst = Math.Max(worst, spent);
                frames++;
                Assert.IsTrue(frames < 10000, "ends");
            }
            Assert.IsTrue(worst < 2.0 + maxQuad + 1e-9, "a frame overruns by less than one quad: " + worst);
            Assert.IsTrue(frames >= (int)(total / (2.0 + maxQuad)), "the work is spread over frames: " + frames + " for " + total + " ms");
            Assert.IsTrue(frames <= (int)Math.Ceiling(total / 2.0) + 1, "and nothing is held back: " + frames);
            // A budget of 1.5 ms with a quad of 1.9 ms still paints one quad per frame (progress, no stall), and the whole-millisecond
            // clock the review named would have let a frame run to 2.99 ms: the virtual clock stops at 1.5.
            double one;
            Assert.AreEqual(1, CoverRepainter.FrameOnVirtualClock(new[] { 1.9, 1.9 }, 0, 1.5, out one));
            Assert.AreEqual(1.9, one, 1e-12);
            Assert.IsFalse(CoverRepainter.WithinBudget(1.5, 1.5));
            Assert.IsTrue(CoverRepainter.WithinBudget(1.49, 1.5));
        }
    }
}
