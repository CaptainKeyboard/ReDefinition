using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T18
{
    // Acceptance of package T18 on the CPU: the cloud shadow factor of a ray read from the pack (RtCloudRayMath, the law the ray shaders mirror) against the
    // cloud shadow of T04 (CloudShadowField.DirectTransmission, the raster side's definition) at five places chosen where the shadow is neither 0 nor 1:
    // under a broken field, at an edge, under a deck, behind a layer pair, at the terminator. Limit written before the first run: 10 percent of the shadow
    // factor (relative), brief T18 acceptance "Shadow agreement". The T04 field uses 16 steps a span and the sun's centre (one disc sample), as the ray
    // side has one ray. The fields are read the way the stack reads them: 512 x 256 weather maps, bilinear (PackScenes.OnMap, PackScenes.Noise).
    [TestClass]
    public class RtShadowAgreementTests
    {
        private const double Limit = 0.10;
        private const double Lat0 = 0.0;
        private static readonly StringBuilder Table = new StringBuilder();

        private struct Place
        {
            internal string Name;
            internal double Lat, Lon, Alt, Elevation, Azimuth;
        }

        private static double Direct(LatLonSource s, CloudShadowField field, Place p)
        {
            double x, y, z, sx, sy, sz;
            s.Position(p.Lat, p.Lon, p.Alt, out x, out y, out z);
            LatLonSource.SunTowards(p.Lat, p.Lon, p.Elevation, p.Azimuth, out sx, out sy, out sz);
            return field.DirectTransmission(x, y, z, sx, sy, sz);
        }

        private static double ByRay(LatLonSource s, RtCloudPack pack, Place p)
        {
            double x, y, z, sx, sy, sz;
            s.Position(p.Lat, p.Lon, p.Alt, out x, out y, out z);
            LatLonSource.SunTowards(p.Lat, p.Lon, p.Elevation, p.Azimuth, out sx, out sy, out sz);
            return RtCloudRayMath.LightTransmission(pack, x, y, z, sx, sy, sz);
        }

        // The first place on a line of longitudes whose T04 factor lies in [low, high].
        private static Place Find(LatLonSource s, CloudShadowField field, string name, double lat, double lonFrom, double lonTo, double step, double elevation,
                                  double azimuth, double low, double high)
        {
            for (double lon = lonFrom; lon <= lonTo; lon += step)
            {
                var p = new Place { Name = name, Lat = lat, Lon = lon, Alt = 0.0, Elevation = elevation, Azimuth = azimuth };
                double t = Direct(s, field, p);
                if (t >= low && t <= high) return p;
            }
            Assert.Fail("no place for " + name + " between " + lonFrom + " and " + lonTo);
            return default(Place);
        }

        private static void Report(LatLonSource s, RtCloudPack pack, CloudShadowField field, Place p)
        {
            double t04 = Direct(s, field, p), ray = ByRay(s, pack, p);
            double diff = PackScenes.Relative(ray, t04);
            Table.AppendLine(p.Name + ": lat " + p.Lat.ToString("0.0") + " lon " + p.Lon.ToString("0.00") + " alt " + p.Alt + " sun " + p.Elevation + "deg  T04 "
                             + t04.ToString("0.0000") + "  ray " + ray.ToString("0.0000") + "  diff " + (100.0 * diff).ToString("0.00") + " %");
            Assert.IsTrue(t04 > 0.02 && t04 < 0.98, p.Name + ": the place is where the shadow is neither 0 nor 1 (" + t04 + ")");
            Assert.IsTrue(diff <= Limit, p.Name + ": ray " + ray + " against T04 " + t04 + " = " + (100.0 * diff).ToString("0.0") + " percent");
        }

        [TestMethod]
        public void TheFivePlacesAgreeWithinTenPercent()
        {
            Table.Clear();
            // p1 under a broken field (value noise on the map's lattice, cells of 12 texels, 90 km on Kerbin), sun 45 degrees in the east.
            var broken = new LatLonSource(PackScenes.Kerbin).Add("broken", 1500, 3500, 3.0, PackScenes.Noise(7, 12));
            RtCloudPack brokenPack = PackScenes.Bake(broken, broken.Tables());
            CloudShadowField brokenField = broken.Field(16);
            Place p1 = Find(broken, brokenField, "p1 broken field", Lat0, 0.0, 20.0, 0.05, 45.0, 90.0, 0.3, 0.7);
            Report(broken, brokenPack, brokenField, p1);

            // p2 at an edge: a deck whose map edge is as sharp as the lattice allows (one texel from 0 to 1), the place where the shadow is half.
            var edge = new LatLonSource(PackScenes.Kerbin).Add("deck", 1500, 3500, 3.0, PackScenes.OnMap((lat, lon) => Math.Abs(lat) < 30 && Math.Abs(lon) < 30 ? 1.0 : 0.0));
            RtCloudPack edgePack = PackScenes.Bake(edge, edge.Tables());
            CloudShadowField edgeField = edge.Field(16);
            Place p2 = Find(edge, edgeField, "p2 edge", Lat0, 25.0, 33.0, 0.01, 45.0, 90.0, 0.35, 0.65);
            Report(edge, edgePack, edgeField, p2);

            // p3 under a deck of optical depth 1.2: inside the box, the shadow is the slant depth everywhere.
            var deck = new LatLonSource(PackScenes.Kerbin).Add("deck", 1500, 3500, 1.2, PackScenes.OnMap((lat, lon) => Math.Abs(lat) < 30 && Math.Abs(lon) < 30 ? 1.0 : 0.0));
            RtCloudPack deckPack = PackScenes.Bake(deck, deck.Tables());
            CloudShadowField deckField = deck.Field(16);
            Report(deck, deckPack, deckField, new Place { Name = "p3 deck", Lat = 0.0, Lon = 0.0, Alt = 0.0, Elevation = 45.0, Azimuth = 90.0 });

            // p4 behind a layer pair: the broken low layer and a high deck of strength 0.8, one sun ray through both.
            var pair = new LatLonSource(PackScenes.Kerbin).Add("low", 1500, 3500, 1.5, PackScenes.Noise(11, 12))
                                                         .Add("high", 7000, 8000, 0.5, PackScenes.OnMap((lat, lon) => Math.Abs(lat) < 60 && Math.Abs(lon) < 60 ? 1.0 : 0.0));
            pair.LayerAt(1).ShadowStrength = 0.8f;
            RtCloudPack pairPack = PackScenes.Bake(pair, pair.Tables());
            CloudShadowField pairField = pair.Field(16);
            Place p4 = Find(pair, pairField, "p4 layer pair", Lat0, 0.0, 20.0, 0.05, 45.0, 90.0, 0.2, 0.5);
            Report(pair, pairPack, pairField, p4);

            // p5 at the terminator: the broken layer with the sun 6 degrees up.
            Place p5 = Find(broken, brokenField, "p5 terminator", Lat0, 0.0, 20.0, 0.05, 6.0, 90.0, 0.25, 0.75);
            Report(broken, brokenPack, brokenField, p5);
            Console.WriteLine(Table.ToString());
        }

        [TestMethod]
        public void AScanOfBrokenSkiesAgreesAtAllSunHeights()
        {
            // Every place of a lattice where the shadow is neither 0 nor 1, two fields (cells of 12 and of 6 texels), the sun at 45 and 6 degrees: the median,
            // the 90th percentile and the worst relative difference. Criteria written before the run: p90 at most 10 percent, worst at most 15 percent.
            Table.Clear();
            foreach (int cell in new[] { 12, 6 })
            {
                var s = new LatLonSource(PackScenes.Kerbin).Add("n", 1500, 3500, 3.0, PackScenes.Noise(7, cell));
                CloudShadowField field = s.Field(16);
                RtCloudPack pack = PackScenes.Bake(s, s.Tables());
                foreach (double elevation in new[] { 45.0, 6.0 })
                {
                    var diffs = new List<double>();
                    for (double lat = -20; lat < 20; lat += 2.1)
                        for (double lon = -30; lon < 30; lon += 0.37)
                        {
                            var p = new Place { Lat = lat, Lon = lon, Alt = 0.0, Elevation = elevation, Azimuth = 90.0 };
                            double t04 = Direct(s, field, p);
                            if (t04 > 0.05 && t04 < 0.95) diffs.Add(PackScenes.Relative(ByRay(s, pack, p), t04));
                        }
                    diffs.Sort();
                    Assert.IsTrue(diffs.Count > 100, "places in the band: " + diffs.Count);
                    double median = diffs[diffs.Count / 2], p90 = diffs[(int)(diffs.Count * 0.9)], worst = diffs[diffs.Count - 1];
                    Table.AppendLine("noise cells " + cell + ", sun " + elevation + " deg: n " + diffs.Count + " median " + (100 * median).ToString("0.00") + " % p90 "
                                     + (100 * p90).ToString("0.00") + " % worst " + (100 * worst).ToString("0.00") + " %");
                    Assert.IsTrue(p90 <= 0.10, "cells " + cell + " sun " + elevation + ": p90 " + p90);
                    Assert.IsTrue(worst <= 0.15, "cells " + cell + " sun " + elevation + ": worst " + worst);
                }
            }
            Console.WriteLine(Table.ToString());
        }

        [TestMethod]
        public void ATextureOffTheMapsLatticeIsLessAccurateThanTheAlignedOne()
        {
            // The pack at the weather maps' own lattice (512 x 256) filters the coverage exactly as the maps do; 256 x 128 resamples it. Measured for the
            // record of the tuning key rt.packWidth: the aligned width is the default.
            Table.Clear();
            var s = new LatLonSource(PackScenes.Kerbin).Add("n", 1500, 3500, 3.0, PackScenes.Noise(7, 12));
            CloudShadowField field = s.Field(16);
            double[] p90 = new double[2];
            int index = 0;
            foreach (int width in new[] { 512, 256 })
            {
                RtCloudPack pack = PackScenes.Bake(s, s.Tables(), width);
                var diffs = new List<double>();
                for (double lat = -20; lat < 20; lat += 2.1)
                    for (double lon = -30; lon < 30; lon += 0.37)
                    {
                        var p = new Place { Lat = lat, Lon = lon, Alt = 0.0, Elevation = 45.0, Azimuth = 90.0 };
                        double t04 = Direct(s, field, p);
                        if (t04 > 0.05 && t04 < 0.95) diffs.Add(PackScenes.Relative(ByRay(s, pack, p), t04));
                    }
                diffs.Sort();
                p90[index++] = diffs[(int)(diffs.Count * 0.9)];
                Table.AppendLine("width " + width + ": p90 " + (100 * p90[index - 1]).ToString("0.00") + " %");
            }
            Console.WriteLine(Table.ToString());
            Assert.IsTrue(p90[0] < p90[1]);
        }

        [TestMethod]
        public void ATwoLayerSkyGivesTheShadowOfBothAndRemovingOneRemovesItsShare()
        {
            var both = new LatLonSource(PackScenes.Kerbin).Add("low", 1500, 3500, 1.0, (a, b) => 1.0).Add("high", 7000, 8000, 0.7, (a, b) => 1.0);
            RtCloudPack pack = PackScenes.Bake(both, both.Tables());
            double x, y, z, sx, sy, sz;
            both.Position(0.0, 0.0, 0.0, out x, out y, out z);
            LatLonSource.SunTowards(0.0, 0.0, 90.0, 0.0, out sx, out sy, out sz);
            double tBoth = RtCloudRayMath.LightTransmission(pack, x, y, z, sx, sy, sz);
            Assert.AreEqual(Math.Exp(-1.7), tBoth, 0.01, "the vertical sun sees the sum of the two depths");
            Assert.AreEqual(both.Field(16).DirectTransmission(x, y, z, sx, sy, sz), tBoth, 0.01);
            var one = new LatLonSource(PackScenes.Kerbin).Add("low", 1500, 3500, 1.0, (a, b) => 1.0);
            RtCloudPack onePack = PackScenes.Bake(one, one.Tables());
            Assert.AreEqual(Math.Exp(-1.0), RtCloudRayMath.LightTransmission(onePack, x, y, z, sx, sy, sz), 0.01);
        }

        [TestMethod]
        public void ARayFromInsideTheShellSeesTheShareAboveIt()
        {
            // A receiver in the middle of a 2 km thick slab sees half of its depth along the vertical (uniform density in this test).
            var s = new LatLonSource(PackScenes.Kerbin).Add("deck", 1500, 3500, 2.0, (a, b) => 1.0);
            RtCloudPack pack = PackScenes.Bake(s, s.Tables());
            double x, y, z, sx, sy, sz;
            s.Position(0.0, 0.0, 2500.0, out x, out y, out z);
            LatLonSource.SunTowards(0.0, 0.0, 90.0, 0.0, out sx, out sy, out sz);
            Assert.AreEqual(Math.Exp(-1.0), RtCloudRayMath.LightTransmission(pack, x, y, z, sx, sy, sz), 0.01);
            Assert.AreEqual(s.Field(16).DirectTransmission(x, y, z, sx, sy, sz), RtCloudRayMath.LightTransmission(pack, x, y, z, sx, sy, sz), 0.01);
        }

        [TestMethod]
        public void TheSunBelowTheHorizonIsCutAtTheBodyAndAGasGiantIsNot()
        {
            var s = new LatLonSource(PackScenes.Kerbin).Add("deck", 1500, 3500, 2.0, (a, b) => 1.0);
            RtCloudPack pack = PackScenes.Bake(s, s.Tables());
            double x, y, z, sx, sy, sz;
            s.Position(0.0, 0.0, 5.0, out x, out y, out z);
            LatLonSource.SunTowards(0.0, 0.0, -20.0, 0.0, out sx, out sy, out sz);
            Assert.AreEqual(1.0, RtCloudRayMath.LightTransmission(pack, x, y, z, sx, sy, sz), 1e-9, "the ray meets the planet at once");
            Assert.AreEqual(s.Field(16).DirectTransmission(x, y, z, sx, sy, sz), 1.0, 1e-9);
            var giant = new LatLonSource(PackScenes.Kerbin, false).Add("deck", 1500, 3500, 2.0, (a, b) => 1.0);
            RtCloudPack giantPack = PackScenes.Bake(giant, giant.Tables());
            Assert.IsTrue(RtCloudRayMath.LightTransmission(giantPack, x, y, z, sx, sy, sz) < 1.0);
        }

        [TestMethod]
        public void TheViewOpacityIsTheCloudsCoverageAlongARay()
        {
            var s = new LatLonSource(PackScenes.Kerbin).Add("low", 1500, 3500, 1.0, (a, b) => 1.0).Add("high", 7000, 8000, 0.5, (a, b) => 1.0);
            s.LayerAt(0).ShadowStrength = 0.0f;
            RtCloudPack pack = PackScenes.Bake(s, s.Tables());
            double x, y, z, sx, sy, sz;
            s.Position(0.0, 0.0, 0.0, out x, out y, out z);
            LatLonSource.SunTowards(0.0, 0.0, 90.0, 0.0, out sx, out sy, out sz);
            // The view ignores the shadow strength: both layers count.
            Assert.AreEqual(1.0 - Math.Exp(-1.5), RtCloudRayMath.ViewOpacity(pack, x, y, z, sx, sy, sz, double.MaxValue), 0.01);
            // Up to a distance that ends below the high layer: the low one only.
            Assert.AreEqual(1.0 - Math.Exp(-1.0), RtCloudRayMath.ViewOpacity(pack, x, y, z, sx, sy, sz, 5000.0), 0.01);
            Assert.AreEqual(0.0, RtCloudRayMath.ViewOpacity(pack, x, y, z, sx, sy, sz, 1000.0), 1e-9);
        }

        [TestMethod]
        public void TheCoverageShiftActsOnTheEntryWithoutANewBake()
        {
            // The runtime coverage shift (CloudCoverShift) and the look knobs reach the pack through the entry's threshold and softness: the same data, a
            // different shape, the transmission follows.
            var s = new LatLonSource(PackScenes.Kerbin).Add("n", 1500, 3500, 3.0, PackScenes.Noise(7, 12));
            RtCloudPack pack = PackScenes.Bake(s, s.Tables());
            var p = new Place { Name = "x", Lat = 0.0, Lon = 5.0, Alt = 0.0, Elevation = 45.0, Azimuth = 90.0 };
            double before = ByRay(s, pack, p);
            pack.Entries[0].Coverage = 0.8f;
            double more = ByRay(s, pack, p);
            pack.Entries[0].Coverage = 0.2f;
            double less = ByRay(s, pack, p);
            Assert.IsTrue(more <= before + 1e-9 && less >= before - 1e-9 && more < less, more + " <= " + before + " <= " + less);
        }
    }
}
