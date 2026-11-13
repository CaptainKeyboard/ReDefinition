using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T18
{
    // The ray-side pack of the own layers (package T18): the layout the consumer binds to, the bake of the vertical optical depth against the cloud shadow's
    // own vertical depth, the entries, and the driver that keeps the pack current (versions, caps, the live fields).
    [TestClass]
    public class RtCloudPackTests
    {
        // Settings that bake a small pack in one tick: a long budget.
        private static RtCloudPackSettings Quick(int width, int cap)
        {
            return new RtCloudPackSettings { Enabled = true, Width = width, LayerCap = cap, BudgetMs = 100000f, RebakeIntervalS = 1e6f };
        }

        [TestMethod]
        public void AMovedVersionRebakesOnlyAfterTheInterval()
        {
            var s = new LatLonSource(PackScenes.Kerbin).Add("a", 1000, 3000, 1.0, (a, b) => 1.0);
            double now = 100.0;
            var driver = new RtCloudPackDriver { Clock = () => now };
            RtCloudPackSettings settings = Quick(64, 0);
            settings.RebakeIntervalS = 10f;
            driver.Tick(s, s.Tables(), settings);
            Assert.AreEqual(1, driver.Stats.BakesDone, "the first pack at once");
            now += 3.0;
            driver.Tick(s, s.Tables(), settings);
            Assert.AreEqual(1, driver.Stats.BakesDone, "3 s after the last start: not yet");
            now += 8.0;
            driver.Tick(s, s.Tables(), settings);
            Assert.AreEqual(2, driver.Stats.BakesDone, "11 s after it: the bake");
            var other = new LatLonSource(PackScenes.Kerbin).Add("a", 1000, 3000, 1.0, (a, b) => 1.0);
            driver.Tick(other, other.Tables(), settings);
            Assert.AreEqual(3, driver.Stats.BakesDone, "another stack: at once, whatever the interval");
        }

        [TestMethod]
        public void AStrideOfTwoEqualsTheFullDepthWhereTheDepthIsSmooth()
        {
            var s = new LatLonSource(PackScenes.Kerbin).Add("a", 1000, 3000, 2.0, PackScenes.Noise(5, 10));
            LayerTypeTable[] tables = s.Tables();
            var one = new RtCloudTauBaker(s, tables, 1, 128, 64, 8, 1);
            var two = new RtCloudTauBaker(s, tables, 1, 128, 64, 8, 2);
            Assert.IsTrue(one.Step(int.MaxValue));
            Assert.IsTrue(two.Step(int.MaxValue));
            // The full depth is the same constant everywhere in this scene, so the column depth (shape times full depth) of the two bakes is the same at every
            // texel; the full depth itself differs only where the shape is 0 (stride 2 interpolates nodes round a cloud into texels that hold none).
            CloudLayerDef layer = s.LayerAt(0);
            double worst = 0.0;
            for (int i = 0; i < one.Data.Length; i += 2)
            {
                double a = LayerDensity.Shape(one.Data[i], layer.EffectiveCoverage, layer.EffectiveEdgeSoftness) * one.Data[i + 1];
                double b = LayerDensity.Shape(two.Data[i], layer.EffectiveCoverage, layer.EffectiveEdgeSoftness) * two.Data[i + 1];
                worst = Math.Max(worst, Math.Abs(a - b));
            }
            Assert.IsTrue(worst < 1e-4, "the column depth of the two bakes (" + worst + ")");
        }

        [TestMethod]
        public void TexelCoordinatesInvertTheTexelCentres()
        {
            const int w = 256, h = 128;
            foreach (int column in new[] { 0, 1, 100, 255 })
                foreach (int row in new[] { 0, 7, 64, 127 })
                {
                    double lat, lon, x, y;
                    RtCloudPackLayout.TexelLatLon(column, row, w, h, out lat, out lon);
                    RtCloudPackLayout.TexelCoordinates(lat, lon, w, h, out x, out y);
                    Assert.AreEqual(column, x, 1e-9);
                    Assert.AreEqual(row, y, 1e-9);
                }
            Assert.AreEqual(w * h * 3L * 2L * 4L, RtCloudPackLayout.Bytes(w, h, 3), "two floats a texel");
        }

        [TestMethod]
        public void TheLongitudeWrapsAndTheLatitudeClampsInTheFilter()
        {
            var s = new LatLonSource(PackScenes.Kerbin).Add("deck", 2000, 4000, 2.0, (lat, lon) => 1.0);
            RtCloudPack pack = PackScenes.Bake(s, s.Tables());
            // A uniform deck reads the same everywhere, across the seam at 180 and at the poles.
            double expected = 2.0;
            Assert.AreEqual(expected, pack.ColumnTau(0, 0.0, 179.9), 1e-5);
            Assert.AreEqual(expected, pack.ColumnTau(0, 0.0, -179.9), 1e-5);
            Assert.AreEqual(expected, pack.ColumnTau(0, 89.9, 10.0), 1e-5);
            Assert.AreEqual(expected, pack.ColumnTau(0, -89.9, 10.0), 1e-5);
        }

        [TestMethod]
        public void TheBakedDepthIsTheShadowsVerticalDepth()
        {
            // A broken field with a thickness profile that is not constant, against CloudShadowField.VerticalOpticalDepth from sea level at the texel centres.
            var s = new LatLonSource(PackScenes.Kerbin).Add("a", 1500, 3500, 1.4, PackScenes.Broken(6, 4), 0.3).Add("b", 6000, 6800, 0.5, PackScenes.Broken(3, 2, 20));
            RtCloudPack pack = PackScenes.Bake(s, s.Tables(), 128, 16);
            CloudShadowField field = s.Field(16);
            double worst = 0.0;
            int checkedTexels = 0;
            for (int l = 0; l < s.LayerCount; l++)
                for (int row = 3; row < 64; row += 9)
                    for (int column = 2; column < 128; column += 7)
                    {
                        double lat, lon;
                        RtCloudPackLayout.TexelLatLon(column, row, 128, 64, out lat, out lon);
                        // The vertical depth of this layer alone: the layer's own optical depth along the vertical.
                        double up = Math.PI / 180.0;
                        double ux = Math.Cos(lat * up) * Math.Cos(lon * up), uy = Math.Cos(lat * up) * Math.Sin(lon * up), uz = Math.Sin(lat * up);
                        double expected = field.LayerOpticalDepth(l, ux * PackScenes.Kerbin, uy * PackScenes.Kerbin, uz * PackScenes.Kerbin, ux, uy, uz, double.MaxValue);
                        // At a texel centre the filter returns the texel: shape of its coverage times its full-cloud depth.
                        double got = pack.ColumnTau(l, lat, lon);
                        worst = Math.Max(worst, Math.Abs(got - expected));
                        checkedTexels++;
                    }
            Assert.IsTrue(checkedTexels > 100);
            Assert.IsTrue(worst < 1e-4, "worst difference of a texel against the shadow's vertical depth " + worst);
        }

        [TestMethod]
        public void AClearColumnCostsNothingAndIsZero()
        {
            var s = new LatLonSource(PackScenes.Kerbin).Add("deck", 2000, 4000, 3.0, PackScenes.Box(-10, 10, -20, 20, 0.0));
            RtCloudPack pack = PackScenes.Bake(s, s.Tables());
            Assert.AreEqual(3.0, pack.ColumnTau(0, 0.0, 0.0), 1e-5);
            Assert.AreEqual(0.0, pack.ColumnTau(0, 40.0, 100.0), 0.0);
            Assert.AreEqual(0.0, pack.ColumnTau(0, 0.0, 120.0), 0.0);
            double cover, tauFull;
            pack.Sample(0, 40.0, 100.0, out cover, out tauFull);
            Assert.AreEqual(0.0, tauFull, 0.0, "no cloud in or next to the texel: no depth baked");
        }

        [TestMethod]
        public void ABakeInPiecesEqualsOneBake()
        {
            var s = new LatLonSource(PackScenes.Kerbin).Add("a", 1500, 3500, 1.4, PackScenes.Broken(5, 3));
            LayerTypeTable[] tables = s.Tables();
            var whole = new RtCloudTauBaker(s, tables, 1, 64, 32, 8);
            Assert.IsTrue(whole.Step(int.MaxValue));
            var pieces = new RtCloudTauBaker(s, tables, 1, 64, 32, 8);
            int calls = 0;
            while (!pieces.Step(100)) calls++;
            Assert.IsTrue(calls >= 19, "64 x 32 texels at 100 a call: " + calls);
            CollectionAssert.AreEqual(whole.Data, pieces.Data);
            Assert.AreEqual(1.0, pieces.Progress);
        }

        [TestMethod]
        public void TheLayersShiftMovesTheBakedMapNotTheTexels()
        {
            // The coverage patch is at map longitudes 0 to 10; with the layer shifted by 20 it stands at world 20 to 30. The pack holds it at map 0 to 10
            // and the ray side adds the shift: a ray under world 25 is shaded, one under world 5 is not.
            var s = new LatLonSource(PackScenes.Kerbin).Add("deck", 2000, 4000, 4.0, PackScenes.Box(-40, 40, 0, 10, 0.0));
            s.LayerAt(0).MapShiftDeg = 20f;
            RtCloudPack pack = PackScenes.Bake(s, s.Tables());
            Assert.AreEqual(4.0, pack.ColumnTau(0, 0.0, 5.0), 0.2, "the patch stands at map 0 to 10 in the pack");
            Assert.AreEqual(0.0, pack.ColumnTau(0, 0.0, 25.0), 1e-9);
            CloudShadowField field = s.Field(16);
            foreach (double world in new[] { 5.0, 25.0 })
            {
                double x, y, z, sx, sy, sz;
                s.Position(0.0, world, 0.0, out x, out y, out z);
                LatLonSource.SunTowards(0.0, world, 90.0, 0.0, out sx, out sy, out sz);
                double t04 = field.DirectTransmission(x, y, z, sx, sy, sz);
                double ray = RtCloudRayMath.LightTransmission(pack, x, y, z, sx, sy, sz);
                Assert.AreEqual(t04, ray, 0.02, "world longitude " + world);
            }
        }

        [TestMethod]
        public void EntriesCarryTheTightShellTheStrengthAndTheLiveFields()
        {
            var s = new LatLonSource(PackScenes.Kerbin).Add("low", 1000, 3000, 1.0, (a, b) => 1.0).Add("high", 7000, 8000, 0.5, (a, b) => 1.0);
            s.LayerAt(1).ShadowStrength = 0.6f;
            s.LayerAt(1).HeightDownM = 50f;
            s.LayerAt(1).HeightUpM = 300f;
            RtCloudLayerEntry low = RtCloudPackDriver.EntryFor(s, 0), high = RtCloudPackDriver.EntryFor(s, 1);
            Assert.AreEqual(PackScenes.Kerbin + 1000, low.InnerRadiusM, 1e-3);
            Assert.AreEqual(PackScenes.Kerbin + 3000, low.OuterRadiusM, 1e-3);
            Assert.AreEqual(PackScenes.Kerbin + 6950, high.InnerRadiusM, 1e-2, "the height variation widens the shell down");
            Assert.AreEqual(PackScenes.Kerbin + 8300, high.OuterRadiusM, 1e-2, "and up");
            Assert.AreEqual(0.6f, high.ShadowStrength);
            Assert.AreEqual(1f, low.ShadowStrength);
            Assert.AreEqual("high", high.Name);
            Assert.IsTrue(float.IsPositiveInfinity(low.FadeFarM), "no fade by default");
        }

        [TestMethod]
        public void TheLayerCapKeepsTheLowestLayers()
        {
            Assert.AreEqual(4, new RtCloudPackSettings { LayerCap = 0 }.LayersFor(4));
            Assert.AreEqual(2, new RtCloudPackSettings { LayerCap = 2 }.LayersFor(4));
            Assert.AreEqual(1, new RtCloudPackSettings { LayerCap = 2 }.LayersFor(1));
            Assert.AreEqual(0, new RtCloudPackSettings { LayerCap = 2 }.LayersFor(0));
            var s = new LatLonSource(PackScenes.Kerbin).Add("low", 1000, 3000, 1.0, (a, b) => 1.0).Add("mid", 4000, 5000, 1.0, (a, b) => 1.0).Add("high", 7000, 8000, 1.0, (a, b) => 1.0);
            var driver = new RtCloudPackDriver();
            var settings = Quick(64, 2);
            driver.Tick(s, s.Tables(), settings);
            Assert.IsNotNull(driver.Current);
            Assert.AreEqual(2, driver.Current.LayerCount);
            Assert.AreEqual("low", driver.Current.Entries[0].Name);
            Assert.AreEqual("mid", driver.Current.Entries[1].Name);
        }

        [TestMethod]
        public void TheDriverBakesAgainOnTheIntervalAndOnAStructuralChange()
        {
            var s = new LatLonSource(PackScenes.Kerbin).Add("a", 1000, 3000, 1.0, (a, b) => 1.0);
            double now = 0.0;
            var driver = new RtCloudPackDriver { Clock = () => now };
            var settings = Quick(64, 0);
            settings.RebakeIntervalS = 10f;
            driver.Tick(s, s.Tables(), settings);
            long first = driver.Current.Version;
            Assert.AreEqual(1, driver.Stats.BakesDone);
            driver.Tick(s, s.Tables(), settings);
            driver.Tick(s, s.Tables(), settings);
            Assert.AreEqual(1, driver.Stats.BakesDone, "no time passed, nothing baked");
            Assert.AreEqual(first, driver.Current.Version);
            now += 11.0;
            driver.Tick(s, s.Tables(), settings);
            Assert.AreEqual(2, driver.Stats.BakesDone, "the maps are refilled in place and the version does not move: the interval decides");
            Assert.IsTrue(driver.Current.Version > first);
            settings.Width = 128;
            driver.Tick(s, s.Tables(), settings);
            Assert.AreEqual(128, driver.Current.Width, "a new size, a new bake");
        }

        [TestMethod]
        public void AStackWithOneLayerLessLeavesThePackAtTheNextBake()
        {
            var four = new LatLonSource(PackScenes.Kerbin).Add("a", 1000, 3000, 1.0, (a, b) => 1.0).Add("b", 4000, 5000, 1.0, (a, b) => 1.0);
            var three = new LatLonSource(PackScenes.Kerbin).Add("a", 1000, 3000, 1.0, (a, b) => 1.0);
            var driver = new RtCloudPackDriver();
            var settings = Quick(64, 0);
            driver.Tick(four, four.Tables(), settings);
            Assert.AreEqual(2, driver.Current.LayerCount);
            driver.Tick(three, three.Tables(), settings);
            Assert.AreEqual(1, driver.Current.LayerCount);
            Assert.AreEqual("a", driver.Current.Entries[0].Name);
        }

        [TestMethod]
        public void ABakeWithATinyBudgetSpansFramesAndTheOldPackKeepsServing()
        {
            var s = new LatLonSource(PackScenes.Kerbin).Add("a", 1000, 3000, 1.0, PackScenes.Broken(4, 2));
            var driver = new RtCloudPackDriver();
            var settings = Quick(64, 0);
            driver.Tick(s, s.Tables(), settings);
            RtCloudPack old = driver.Current;
            settings.RebakeIntervalS = 0f;
            // A tick that may bake 10 texels: the pack that stands is untouched until the bake completes.
            int ticks = 0;
            while (ReferenceEquals(driver.Current, old) && ticks < 1000)
            {
                driver.Tick(s, s.Tables(), settings, 10);
                ticks++;
                if (ReferenceEquals(driver.Current, old)) Assert.IsTrue(driver.Baking);
            }
            Assert.IsTrue(ticks > 100, "64 x 32 texels at 10 a tick: " + ticks);
            Assert.AreNotSame(old, driver.Current);
            Assert.IsFalse(driver.Baking);
        }

        [TestMethod]
        public void TheLiveFieldsMoveWithoutABake()
        {
            var s = new LatLonSource(PackScenes.Kerbin).Add("a", 1000, 3000, 1.0, (a, b) => 1.0);
            var driver = new RtCloudPackDriver();
            var settings = Quick(64, 0);
            driver.Tick(s, s.Tables(), settings);
            RtCloudPack pack = driver.Current;
            long live = pack.LiveVersion;
            driver.Tick(s, s.Tables(), settings);
            Assert.AreEqual(live, pack.LiveVersion, "nothing changed");
            s.LayerAt(0).MapShiftDeg = 12.5f;
            s.LayerAt(0).ShadowStrength = 0.4f;
            driver.Tick(s, s.Tables(), settings);
            Assert.AreSame(pack, driver.Current, "no new pack");
            Assert.AreEqual(1, driver.Stats.BakesDone);
            Assert.AreEqual(12.5f, pack.Entries[0].MapShiftDeg);
            Assert.AreEqual(0.4f, pack.Entries[0].ShadowStrength);
            Assert.IsTrue(pack.LiveVersion > live);
        }

        [TestMethod]
        public void OffDropsThePackAndABodyChangeDropsItAtOnce()
        {
            var s = new LatLonSource(PackScenes.Kerbin).Add("a", 1000, 3000, 1.0, (a, b) => 1.0);
            var driver = new RtCloudPackDriver();
            var settings = Quick(64, 0);
            driver.Tick(s, s.Tables(), settings);
            Assert.IsNotNull(driver.Current);
            settings.Enabled = false;
            driver.Tick(s, s.Tables(), settings);
            Assert.IsNull(driver.Current);
            settings.Enabled = true;
            driver.Tick(s, s.Tables(), settings);
            Assert.IsNotNull(driver.Current);
            driver.Tick(null, null, settings);
            Assert.IsNull(driver.Current, "no stack, no pack");
        }

        [TestMethod]
        public void TheSourceChoiceNeverGivesTheRaysADeckTwice()
        {
            var ours = CloudShadowSwitches.Caster.Ours;
            Assert.IsTrue(RtOwnCloudsPlan.Wanted(true, true, ours, true, 3));
            Assert.IsFalse(RtOwnCloudsPlan.Wanted(false, true, ours, true, 3), "the host is the hard dependency");
            Assert.IsFalse(RtOwnCloudsPlan.Wanted(true, false, ours, true, 3), "the switch");
            Assert.IsFalse(RtOwnCloudsPlan.Wanted(true, true, ours, false, 3), "the own clouds do not draw");
            Assert.IsFalse(RtOwnCloudsPlan.Wanted(true, true, ours, true, 0), "no layer");
            Assert.IsFalse(RtOwnCloudsPlan.Wanted(true, true, CloudShadowSwitches.Caster.Eve, true, 3), "Volumetric or Both: TVC's layer is the rays' cloud");
            Assert.IsFalse(RtOwnCloudsPlan.Wanted(true, true, CloudShadowSwitches.Caster.None, true, 3));
            Assert.AreEqual(CloudShadowSwitches.Caster.Ours, CloudShadowSwitches.WhoCasts(CloudSourceKind.Ours, false));
            Assert.AreEqual(CloudShadowSwitches.Caster.Eve, CloudShadowSwitches.WhoCasts(CloudSourceKind.Both, false));
        }
    }
}
