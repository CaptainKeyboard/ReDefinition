using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests
{
    // A source of one hard-edged patch of cloud on a body so large that its curvature is invisible on the scale of the test (the
    // reference renderer's flat planet, tools/weather_metrics/parity_ref.py FLAT_RADIUS): the layer is the shell between two altitudes
    // and the coverage map is 1 inside a square of the given half width around the map frame's (lat 0, lon 0) and 0 outside.
    // The local frame is east = +y of the map frame, north = +z, up = +x.
    internal sealed class PatchSource : ICloudLayerSource
    {
        private readonly List<CloudLayerDef> layers = new List<CloudLayerDef>();
        private readonly List<Func<double, double, double>> patches = new List<Func<double, double, double>>();

        internal PatchSource(double radiusM, bool hasSurface = true)
        {
            BodyRadiusM = radiusM;
            HasSurface = hasSurface;
        }

        public int BodyIndex { get { return 1; } }
        public double BodyRadiusM { get; }
        public bool HasSurface { get; }
        public int LayerCount { get { return layers.Count; } }
        public long Version { get { return 1; } }
        public CloudLayerDef LayerAt(int index) { return layers[index]; }

        // A slab of vertical optical depth tau between two altitudes, covering a square of half width halfM (metres east and north of the
        // origin) or, with a cover function of (east m, north m), wherever it returns 1.
        internal PatchSource AddSlab(double baseM, double topM, double tau, double halfM, double centreEastM = 0.0, double centreNorthM = 0.0)
        {
            return AddSlab(baseM, topM, tau, (e, n) => Math.Abs(e - centreEastM) < halfM && Math.Abs(n - centreNorthM) < halfM ? 1.0 : 0.0);
        }

        internal PatchSource AddSlab(double baseM, double topM, double tau, Func<double, double, double> cover)
        {
            var layer = new CloudLayerDef("slab", 1, (float)baseM, (float)topM, new[] { CloudTypeDef.Slab("s", 0f, 1f) }, (float)(tau / (topM - baseM)));
            // A coverage of 0.5 puts the threshold at map 0.5: a map of 1 is cloud, a map of 0 is clear; the edge is as hard as the
            // formula allows.
            layer.Coverage = 0.5f;
            layer.EdgeSoftness = 1e-4f;
            layers.Add(layer);
            patches.Add(cover);
            return this;
        }

        public void SampleMaps(int layerIndex, double latDeg, double lonDeg, out float coverage, out float type)
        {
            double east = BodyRadiusM * lonDeg * Math.PI / 180.0 * Math.Cos(latDeg * Math.PI / 180.0);
            double north = BodyRadiusM * latDeg * Math.PI / 180.0;
            coverage = (float)patches[layerIndex](east, north);
            type = 0f;
        }
    }

    internal static class ShadowScenes
    {
        internal const double Flat = 2.0e9;

        internal static CloudShadowField Field(PatchSource s, int disc, int steps = 16)
        {
            var tables = new LayerTypeTable[s.LayerCount];
            for (int i = 0; i < tables.Length; i++) tables[i] = new LayerTypeTable(s.LayerAt(i));
            return new CloudShadowField(s, tables) { DiscSamples = disc, StepsPerSpan = steps };
        }

        // The sun in the east at an elevation: local (east, north, up) = (cos e, 0, sin e) as a map-frame vector (up, east, north).
        internal static void SunEast(double elevationDeg, out double sx, out double sy, out double sz)
        {
            double e = elevationDeg * Math.PI / 180.0;
            sx = Math.Sin(e);
            sy = Math.Cos(e);
            sz = 0.0;
        }

        // The map-frame position of a local point (east, north, height above the sea level).
        internal static void At(double east, double north, double height, out double x, out double y, out double z)
        {
            x = Flat + height;
            y = east;
            z = north;
        }

        // A slab of 20 m thickness and the tau given, its square 1000 m wide, at height h.
        internal static PatchSource Slab(double tau, double h = 2000.0, double half = 500.0)
        {
            return new PatchSource(Flat).AddSlab(h - 10.0, h + 10.0, tau, half);
        }

        internal static double Transmission(CloudShadowField f, double east, double north, double height, double sx, double sy, double sz)
        {
            double x, y, z;
            At(east, north, height, out x, out y, out z);
            return f.DirectTransmission(x, y, z, sx, sy, sz);
        }
    }

    // The cloud shadow's pure maths and its CPU field (package T04), against the analytic expectations of tests/data/parity/checks.json.
    [TestClass]
    public class CloudShadowMathTests
    {
        [TestMethod]
        public void TheDirectTransmissionIsBeerLambert()
        {
            Assert.AreEqual(1.0, CloudShadowMath.DirectTransmission(0.0), 0.0);
            Assert.AreEqual(Math.Exp(-0.5), CloudShadowMath.DirectTransmission(0.5), 1e-12);
            // A thin cloud of tau 0.5 leaves about 60 % of the direct light.
            Assert.AreEqual(0.6, CloudShadowMath.DirectTransmission(0.5), 0.01);
            Assert.AreEqual(1.0, CloudShadowMath.DirectTransmission(-3.0), 0.0);
        }

        [TestMethod]
        public void TheDisplacementIsHeightOverTanOfTheElevation()
        {
            Assert.AreEqual(2000.0, CloudShadowMath.Displacement(2000.0, Math.PI / 4.0), 1e-9);
            Assert.AreEqual(2000.0 / Math.Tan(20.0 * Math.PI / 180.0), CloudShadowMath.Displacement(2000.0, 20.0 * Math.PI / 180.0), 1e-9);
        }

        [TestMethod]
        public void ThePenumbraWidthIsTheSunDiscOverSinSquared()
        {
            // Overhead: h x 9.31 mrad.
            Assert.AreEqual(2000.0 * 9.31e-3, CloudShadowMath.PenumbraWidth(2000.0, Math.PI / 2.0), 1e-9);
            // 45 degrees: twice that.
            Assert.AreEqual(2.0 * 2000.0 * 9.31e-3, CloudShadowMath.PenumbraWidth(2000.0, Math.PI / 4.0), 1e-9);
        }

        [TestMethod]
        public void TheTwoStreamTransmissionFallsWithTheOpticalDepthAndNeverBelowTheDirectBeam()
        {
            double g = CloudShadowMath.Asymmetry(0.85, -0.3, 0.92);
            Assert.AreEqual(0.758, g, 1e-9);
            double previous = 1.0;
            foreach (double tau in new[] { 0.1, 0.5, 1.0, 5.0, 20.0 })
            {
                double t = CloudShadowMath.TwoStreamTransmission(tau, g);
                Assert.IsTrue(t < previous);
                previous = t;
                double total = CloudShadowMath.TotalTransmission(tau / Math.Sin(Math.PI / 4.0), tau);
                Assert.IsTrue(total >= CloudShadowMath.DirectTransmission(tau / Math.Sin(Math.PI / 4.0)));
                Assert.IsTrue(total <= 1.0);
            }
            Assert.AreEqual(1.0, CloudShadowMath.TwoStreamTransmission(0.0, g), 0.0);
        }

        [TestMethod]
        public void TheFittedTransmissionFollowsTheReferenceRenderersSunOnlyRunWithinThreePercent()
        {
            // tools/weather_metrics/parity_ref.py, environment light 0, 150 ground samples, slab 200 m thick, sun 45 degrees.
            double[] taus = { 0.1, 1.0, 5.0, 20.0 };
            double[] reference = { 0.9825, 0.9566, 0.6689, 0.1785 };
            for (int i = 0; i < taus.Length; i++)
                Assert.AreEqual(reference[i], CloudShadowMath.FittedTransmission(taus[i]), 0.03 * reference[i], "tau " + taus[i]);
            Assert.AreEqual(1.0, CloudShadowMath.FittedTransmission(0.0), 0.0);
        }

        [TestMethod]
        public void TheDiscDirectionsAreUnitAndSpreadOverTheSunsAngularRadius()
        {
            double sx = 0.3, sy = 0.5, sz = Math.Sqrt(1 - 0.34);
            double worst = 0.0;
            for (int i = 0; i < 64; i++)
            {
                double dx, dy, dz;
                CloudShadowMath.DiscDirection(sx, sy, sz, i, 64, 0.0, out dx, out dy, out dz);
                Assert.AreEqual(1.0, Math.Sqrt(dx * dx + dy * dy + dz * dz), 1e-12);
                worst = Math.Max(worst, Math.Acos(Math.Min(1.0, dx * sx + dy * sy + dz * sz)));
            }
            Assert.IsTrue(worst < 0.5 * CloudShadowMath.SunAngularDiameter);
            Assert.IsTrue(worst > 0.45 * CloudShadowMath.SunAngularDiameter);
            double ax, ay, az;
            CloudShadowMath.DiscDirection(sx, sy, sz, 0, 1, 0.0, out ax, out ay, out az);
            Assert.AreEqual(sx, ax, 0.0);
        }

        [TestMethod]
        public void AFlatSlabGivesTheShadowAtHeightOverTanElevationWithinOnePercent()
        {
            foreach (double e in new[] { 20.0, 45.0, 70.0 })
            {
                double sx, sy, sz;
                ShadowScenes.SunEast(e, out sx, out sy, out sz);
                CloudShadowField f = ShadowScenes.Field(ShadowScenes.Slab(8.0), 1);
                double expected = 2000.0 / Math.Tan(e * Math.PI / 180.0);
                double cx = Centroid(f, sx, sy, sz, expected);
                double shift = -cx;
                Assert.AreEqual(expected, shift, 0.01 * expected, "e = " + e);
            }
        }

        // The centroid east of the darkness (1 - transmission) on a grid like the reference's.
        internal static double Centroid(CloudShadowField f, double sx, double sy, double sz, double shift)
        {
            double num = 0.0, den = 0.0;
            for (double x = -1500.0 - shift; x <= 1500.0; x += 8.0)
                for (double y = -800.0; y <= 800.0; y += 100.0)
                {
                    double dark = 1.0 - ShadowScenes.Transmission(f, x, y, 0.0, sx, sy, sz);
                    num += dark * x;
                    den += dark;
                }
            return num / den;
        }

        [TestMethod]
        public void TheShadowFactorFollowsBeerLambertAlongTheSunPath()
        {
            double sx, sy, sz;
            ShadowScenes.SunEast(45.0, out sx, out sy, out sz);
            foreach (double tau in new[] { 0.1, 1.0, 5.0, 20.0 })
            {
                CloudShadowField f = ShadowScenes.Field(ShadowScenes.Slab(tau), 1);
                double got = ShadowScenes.Transmission(f, -2000.0, 0.0, 0.0, sx, sy, sz);
                double want = Math.Exp(-tau / Math.Sin(Math.PI / 4.0));
                Assert.AreEqual(want, got, 0.1 * want, "tau " + tau);
            }
        }

        [TestMethod]
        public void TheEdgeOfAHardSlabHasThePenumbraOfTheSunDisc()
        {
            double sx, sy, sz;
            ShadowScenes.SunEast(45.0, out sx, out sy, out sz);
            CloudShadowField f = ShadowScenes.Field(ShadowScenes.Slab(8.0), 64);
            double expectedFull = CloudShadowMath.PenumbraWidth(2000.0, Math.PI / 4.0);
            double edge = 500.0 - 2000.0;
            double span = expectedFull * 3.0;
            int n = 257;
            var xs = new double[n];
            var ys = new double[n];
            for (int i = 0; i < n; i++)
            {
                xs[i] = edge - span + 2.0 * span * i / (n - 1);
                ys[i] = ShadowScenes.Transmission(f, xs[i], 0.0, 0.0, sx, sy, sz);
            }
            double width = Width1090(xs, ys);
            // The 10 to 90 % width of a uniform disc's edge is 0.687 of its full width.
            Assert.AreEqual(0.687 * expectedFull, width, 0.25 * 0.687 * expectedFull + 2.0);
        }

        // The distance between the 10 % and 90 % levels of a monotone profile (rising or falling), by linear interpolation.
        internal static double Width1090(double[] x, double[] y)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (double v in y) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
            return Math.Abs(Cross(x, y, lo + 0.9 * (hi - lo)) - Cross(x, y, lo + 0.1 * (hi - lo)));
        }

        private static double Cross(double[] x, double[] y, double level)
        {
            for (int i = 1; i < x.Length; i++)
            {
                if ((y[i - 1] - level) * (y[i] - level) <= 0.0 && y[i] != y[i - 1])
                    return x[i - 1] + (x[i] - x[i - 1]) * (level - y[i - 1]) / (y[i] - y[i - 1]);
            }
            return double.NaN;
        }

        [TestMethod]
        public void ATerrainPointAVesselAndAKerbalInTheSameShadowTakeTheSameFactor()
        {
            double sx, sy, sz;
            ShadowScenes.SunEast(45.0, out sx, out sy, out sz);
            CloudShadowField f = ShadowScenes.Field(ShadowScenes.Slab(1.0), 24);
            // The shadow of the 1 km patch at 2000 m and 45 degrees lies from -2500 to -1500: the centre is -2000.
            double terrain = ShadowScenes.Transmission(f, -2000.0, 0.0, 0.0, sx, sy, sz);
            double vessel = ShadowScenes.Transmission(f, -2000.0, 0.0, 6.0, sx, sy, sz);
            double kerbal = ShadowScenes.Transmission(f, -2000.0, 0.0, 1.8, sx, sy, sz);
            Assert.AreEqual(Math.Exp(-1.0 / Math.Sin(Math.PI / 4.0)), terrain, 0.01);
            Assert.AreEqual(terrain, vessel, 0.03 * terrain);
            Assert.AreEqual(terrain, kerbal, 0.03 * terrain);
        }

        [TestMethod]
        public void TheVerticalOpticalDepthIsTheSumOfTheCloudsAboveTheAltitude()
        {
            var s = new PatchSource(600000.0).AddSlab(1500.0, 1900.0, 2.0, (e, n) => 1.0).AddSlab(3000.0, 3400.0, 3.0, (e, n) => 1.0);
            CloudShadowField f = ShadowScenes.Field(s, 1, 64);
            Assert.AreEqual(5.0, f.VerticalOpticalDepth(10.0, 20.0, 0.0), 5e-3);
            Assert.AreEqual(3.0, f.VerticalOpticalDepth(10.0, 20.0, 2500.0), 5e-3);
            Assert.AreEqual(1.5, f.VerticalOpticalDepth(10.0, 20.0, 3200.0), 5e-3);
            Assert.AreEqual(0.0, f.VerticalOpticalDepth(10.0, 20.0, 4000.0), 1e-9);
        }

        [TestMethod]
        public void TheExactSunTransmissionEqualsTheCommonDefinitionForAPlaneParallelCloud()
        {
            var s = new PatchSource(600000.0).AddSlab(1500.0, 1900.0, 2.0, (e, n) => 1.0);
            CloudShadowField f = ShadowScenes.Field(s, 1, 64);
            OwnCloudOpticalDepth.Bind(f);
            try
            {
                double lat = 10.0, lon = 20.0;
                double la = lat * Math.PI / 180.0, lo = lon * Math.PI / 180.0;
                double ux = Math.Cos(la) * Math.Cos(lo), uy = Math.Cos(la) * Math.Sin(lo), uz = Math.Sin(la);
                // A sun 35 degrees above the local horizon, towards the east of the place.
                double ex = -Math.Sin(lo), ey = Math.Cos(lo);
                double e = 35.0 * Math.PI / 180.0;
                double sx = ux * Math.Sin(e) + ex * Math.Cos(e), sy = uy * Math.Sin(e) + ey * Math.Cos(e), sz = uz * Math.Sin(e);
                float exact = OwnCloudOpticalDepth.SunTransmissionAt(lat, lon, 0.0, sx, sy, sz);
                float tau = OwnCloudOpticalDepth.Read(lat, lon, 0.0, 0.0);
                float common = SunTransmission.From(tau, (float)e);
                Assert.AreEqual(2.0f, tau, 5e-3f);
                // The curvature of a 600 km body shortens the slant path through a shell at 1.5 to 1.9 km by about 0.6 %, which is 2 % of the
                // transmission at a slant optical depth of 3.5: within 3 %, and the exact value is the higher one.
                Assert.AreEqual(common, exact, 0.03f * common);
                Assert.IsTrue(exact > common);
            }
            finally { OwnCloudOpticalDepth.Bind(null); }
        }

        [TestMethod]
        public void TheHookIsFilledOnlyWhenNoOtherWriterHoldsIt()
        {
            Func<double, double, double, double, float> saved = WeatherHooks.CloudOpticalDepth;
            try
            {
                WeatherHooks.CloudOpticalDepth = null;
                Assert.IsTrue(OwnCloudHook.Attach());
                Assert.AreSame(OwnCloudOpticalDepth.Reader, WeatherHooks.CloudOpticalDepth);
                Assert.AreEqual(0f, WeatherHooks.CloudOpticalDepth(0, 0, 0, 0), 0f, "unbound: no cloud");
                OwnCloudHook.Detach();
                Assert.IsNull(WeatherHooks.CloudOpticalDepth);
                Func<double, double, double, double, float> host = (a, b, c, d) => 7f;
                WeatherHooks.CloudOpticalDepth = host;
                Assert.IsFalse(OwnCloudHook.Attach());
                Assert.AreSame(host, WeatherHooks.CloudOpticalDepth);
                OwnCloudHook.Detach();
                Assert.AreSame(host, WeatherHooks.CloudOpticalDepth, "Detach leaves a foreign writer's hook alone");
            }
            finally { WeatherHooks.CloudOpticalDepth = saved; OwnCloudOpticalDepth.Bind(null); }
        }

        [TestMethod]
        public void ALayersShadowStrengthScalesItsOpticalDepthOnTheSunPath()
        {
            var s = ShadowScenes.Slab(2.0);
            s.LayerAt(0).ShadowStrength = 0.5f;
            CloudShadowField f = ShadowScenes.Field(s, 1);
            double sx, sy, sz;
            ShadowScenes.SunEast(90.0, out sx, out sy, out sz);
            Assert.AreEqual(Math.Exp(-1.0), ShadowScenes.Transmission(f, 0.0, 0.0, 0.0, sx, sy, sz), 1e-3);
            s.LayerAt(0).ShadowStrength = 0f;
            Assert.AreEqual(1.0, ShadowScenes.Transmission(f, 0.0, 0.0, 0.0, sx, sy, sz), 1e-12);
        }

        [TestMethod]
        public void AReceiverAboveTheCloudSeesNoShadowAndASunBelowTheHorizonIsCutAtThePlanet()
        {
            var s = ShadowScenes.Slab(4.0);
            CloudShadowField f = ShadowScenes.Field(s, 1);
            double sx, sy, sz;
            ShadowScenes.SunEast(45.0, out sx, out sy, out sz);
            // A receiver at 3000 m, above the 2000 m patch: nothing above it.
            Assert.AreEqual(1.0, ShadowScenes.Transmission(f, 0.0, 0.0, 3000.0, sx, sy, sz), 1e-12);
            // A sun below the horizon: the ray meets the planet at once, no cloud counts.
            ShadowScenes.SunEast(-10.0, out sx, out sy, out sz);
            Assert.AreEqual(1.0, ShadowScenes.Transmission(f, 0.0, 0.0, 0.0, sx, sy, sz), 1e-12);
        }

        [TestMethod]
        public void TheShadowOnAKerbinSizedBodyFollowsTheDisplacementToTheCurvature()
        {
            // On a 600 km body the 2000 m shift at 45 degrees is a few hundredths of a degree of arc: the centroid stays within 1.5 %.
            var s = new PatchSource(600000.0).AddSlab(1990.0, 2010.0, 8.0, 500.0);
            CloudShadowField f = ShadowScenes.Field(s, 1);
            double e = Math.PI / 4.0;
            double sx = Math.Sin(e), sy = Math.Cos(e), sz = 0.0;
            double num = 0.0, den = 0.0;
            for (double x = -3500.0; x <= 1500.0; x += 8.0)
                for (double y = -800.0; y <= 800.0; y += 100.0)
                {
                    double la = y / 600000.0, lo = x / 600000.0;
                    double r = 600000.0;
                    double px = r * Math.Cos(la) * Math.Cos(lo), py = r * Math.Cos(la) * Math.Sin(lo), pz = r * Math.Sin(la);
                    // The sun direction is given in the local frame at the origin: up = +x, east = +y; at the receiver it is turned by the
                    // longitude, so rotate about z by lo.
                    double ex = Math.Cos(lo) * sx - Math.Sin(lo) * sy, ey = Math.Sin(lo) * sx + Math.Cos(lo) * sy;
                    double dark = 1.0 - f.DirectTransmission(px, py, pz, ex, ey, sz);
                    num += dark * x;
                    den += dark;
                }
            Assert.AreEqual(-2000.0, num / den, 0.015 * 2000.0);
        }

        // ---------------------------------------------------------------------------------------------------------------------------
        // The captures of the 'ours' column of tools/weather_metrics/parity_run.py table (one folder per check id with meta.json). Written
        // only when ATMO_T04_CAPTURE names a folder.
        // ---------------------------------------------------------------------------------------------------------------------------
        [TestMethod]
        public void WriteTheOursColumnOfTheParityTable()
        {
            string folder = Environment.GetEnvironmentVariable("ATMO_T04_CAPTURE");
            if (string.IsNullOrEmpty(folder)) return;
            double asym = CloudShadowMath.Asymmetry(0.85, -0.3, 0.92);

            // R11-shadow-displacement and the profile of the reference's grid at 45 degrees.
            {
                double sx, sy, sz;
                ShadowScenes.SunEast(45.0, out sx, out sy, out sz);
                CloudShadowField f = ShadowScenes.Field(ShadowScenes.Slab(8.0), 24);
                double shift = 2000.0;
                var xs = new List<double>();
                for (double x = -1500.0 - shift; x <= 1500.0; x += 8.0) xs.Add(x);
                var row = new List<double>();
                double num = 0.0, den = 0.0;
                foreach (double x in xs)
                {
                    for (double y = -800.0; y <= 800.0; y += 100.0)
                    {
                        double t = ShadowScenes.Transmission(f, x, y, 0.0, sx, sy, sz);
                        num += (1.0 - t) * x;
                        den += 1.0 - t;
                        if (y == 0.0) row.Add(t);
                    }
                }
                Write(folder, "R11-shadow-displacement", Json("shift_m", -num / den, "height_m", 2000.0, "elevation_deg", 45.0, "x_m", xs, "profile", row, "pixel_m", 8.0));
            }
            // R11-shadow-penumbra.
            {
                double sx, sy, sz;
                ShadowScenes.SunEast(45.0, out sx, out sy, out sz);
                CloudShadowField f = ShadowScenes.Field(ShadowScenes.Slab(8.0), 64);
                double full = CloudShadowMath.PenumbraWidth(2000.0, Math.PI / 4.0);
                double edge = 500.0 - 2000.0, span = Math.Max(full * 3.0, 40.0);
                var xs = new List<double>();
                var ys = new List<double>();
                for (int i = 0; i < 257; i++)
                {
                    double x = edge - span + 2.0 * span * i / 256.0;
                    xs.Add(x);
                    ys.Add(ShadowScenes.Transmission(f, x, 0.0, 0.0, sx, sy, sz));
                }
                Write(folder, "R11-shadow-penumbra", Json("x_m", xs, "profile", ys, "pixel_m", xs[1] - xs[0], "height_m", 2000.0, "elevation_deg", 45.0));
            }
            // R11-shadow-darkness: tau 4 at 45 degrees, the core.
            {
                double sx, sy, sz;
                ShadowScenes.SunEast(45.0, out sx, out sy, out sz);
                CloudShadowField f = ShadowScenes.Field(ShadowScenes.Slab(4.0), 1);
                double shadow = ShadowScenes.Transmission(f, -2000.0, 0.0, 0.0, sx, sy, sz);
                Write(folder, "R11-shadow-darkness", Json("shadow", shadow, "lit", 1.0, "tau_sun", 4.0 / Math.Sin(Math.PI / 4.0)));
            }
            // R11-shadow-receivers.
            {
                double sx, sy, sz;
                ShadowScenes.SunEast(45.0, out sx, out sy, out sz);
                CloudShadowField f = ShadowScenes.Field(ShadowScenes.Slab(1.0), 24);
                Write(folder, "R11-shadow-receivers", "{\"factors\": {\"terrain\": " + Num(ShadowScenes.Transmission(f, -2000.0, 0.0, 0.0, sx, sy, sz))
                    + ", \"vessel\": " + Num(ShadowScenes.Transmission(f, -2000.0, 0.0, 6.0, sx, sy, sz))
                    + ", \"kerbal\": " + Num(ShadowScenes.Transmission(f, -2000.0, 0.0, 1.8, sx, sy, sz)) + "}}");
            }
            // R12-shadow-vs-tau: the sun's irradiance under a slab over the open ground's, direct beam plus the diffuse floor.
            {
                var taus = new List<double> { 0.1, 1.0, 5.0, 20.0 };
                var factors = new List<double>();
                foreach (double tau in taus)
                    factors.Add(CloudShadowMath.TotalTransmission(tau / Math.Sin(Math.PI / 4.0), tau));
                Write(folder, "R12-shadow-vs-tau", Json("taus", taus, "factors", factors));
            }
            // R13-ocean-land: a patch on the sea (0 m) and a patch on land (60 m) in the middle of a wide shadow.
            {
                double sx, sy, sz;
                ShadowScenes.SunEast(45.0, out sx, out sy, out sz);
                CloudShadowField f = ShadowScenes.Field(ShadowScenes.Slab(1.0, 2000.0, 1500.0), 24);
                double water = Mean(f, -2000.0, 0.0, 0.0, sx, sy, sz), land = Mean(f, -2000.0, 0.0, 60.0, sx, sy, sz);
                Write(folder, "R13-ocean-land", Json("water", water, "land", land));
            }
            // R13-disc-shadow: a cloud 4 degrees wide at (10 N, 20 E), the sun 5 degrees above the local horizon from the east; the
            // shadow centre on the planet against the analytic place.
            {
                double lat0 = 10.0, lon0 = 20.0, h = 2000.0, r = 600000.0;
                var s = new PatchSource(r).AddSlab(h - 10.0, h + 10.0, 8.0, (east, north) =>
                {
                    double dla = north / r * 180.0 / Math.PI - lat0, dlo = east / (r * Math.Cos(lat0 * Math.PI / 180.0)) * 180.0 / Math.PI - lon0;
                    return Math.Abs(dla) < 1.0 && Math.Abs(dlo) < 1.0 ? 1.0 : 0.0;
                });
                CloudShadowField f = ShadowScenes.Field(s, 1, 24);
                double la = lat0 * Math.PI / 180.0, lo = lon0 * Math.PI / 180.0;
                double ux = Math.Cos(la) * Math.Cos(lo), uy = Math.Cos(la) * Math.Sin(lo), uz = Math.Sin(la);
                double ex = -Math.Sin(lo), ey = Math.Cos(lo);
                double e = 5.0 * Math.PI / 180.0;
                double sx = ux * Math.Sin(e) + ex * Math.Cos(e), sy = uy * Math.Sin(e) + ey * Math.Cos(e), sz = uz * Math.Sin(e);
                // The analytic place: from the cloud's centre (radius r + h) back along the sun ray to the sea-level sphere.
                double cx = ux * (r + h), cy = uy * (r + h), cz = uz * (r + h);
                double t0, t1;
                LayerGeometry.HitSphere(cx, cy, cz, -sx, -sy, -sz, r, out t0, out t1);
                double t = t0 > 0.0 ? t0 : t1;
                double tx = cx - sx * t, ty = cy - sy * t, tz = cz - sz * t;
                double tlat, tlon;
                LayerGeometry.LatLon(tx, ty, tz, out tlat, out tlon);
                // The measured place: the darkness centroid on a lat/lon grid of the surface.
                double nl = 0.0, nn = 0.0, d = 0.0;
                for (double la1 = tlat - 3.0; la1 <= tlat + 3.0; la1 += 0.1)
                    for (double lo1 = tlon - 3.0; lo1 <= tlon + 3.0; lo1 += 0.1)
                    {
                        double a = la1 * Math.PI / 180.0, b = lo1 * Math.PI / 180.0;
                        double dark = 1.0 - f.DirectTransmission(r * Math.Cos(a) * Math.Cos(b), r * Math.Cos(a) * Math.Sin(b), r * Math.Sin(a), sx, sy, sz);
                        nl += dark * la1; nn += dark * lo1; d += dark;
                    }
                Write(folder, "R13-disc-shadow", Json("measured_latlon", new List<double> { nl / d, nn / d }, "true_latlon", new List<double> { tlat, tlon }));
            }
        }

        private static double Mean(CloudShadowField f, double east, double north, double height, double sx, double sy, double sz)
        {
            double sum = 0.0;
            int n = 0;
            for (double de = -50.0; de <= 50.0; de += 10.0)
                for (double dn = -50.0; dn <= 50.0; dn += 10.0)
                {
                    sum += ShadowScenes.Transmission(f, east + de, north + dn, height, sx, sy, sz);
                    n++;
                }
            return sum / n;
        }

        private static string Num(double v)
        {
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Json(params object[] kv)
        {
            var sb = new StringBuilder("{");
            for (int i = 0; i < kv.Length; i += 2)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('"').Append((string)kv[i]).Append("\": ");
                object v = kv[i + 1];
                var list = v as List<double>;
                if (list != null)
                {
                    sb.Append('[');
                    for (int j = 0; j < list.Count; j++) sb.Append(j > 0 ? ", " : "").Append(Num(list[j]));
                    sb.Append(']');
                }
                else sb.Append(Num(Convert.ToDouble(v, CultureInfo.InvariantCulture)));
            }
            return sb.Append('}').ToString();
        }

        private static void Write(string folder, string id, string json)
        {
            string dir = Path.Combine(folder, id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "meta.json"), json);
        }
    }
}
