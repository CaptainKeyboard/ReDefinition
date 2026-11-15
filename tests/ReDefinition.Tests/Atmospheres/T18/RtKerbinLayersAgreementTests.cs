using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.T09;

namespace ReDefinition.Tests.Atmospheres.T18
{
    // The agreement of the ray-side pack with the cloud shadow on Kerbin's four real layers (soft water content, type tables with profiles, base and top
    // shares, optionally the height variation): what the in-game rows T18.scan*_ and T18.shadow_diff_* judge. The slab scenes of the other T18 tests have no
    // noise amount, so none of them reads the soft density.
    //
    // The figures are the difference of the transmittance T = exp(-tau) of the sun's light, |T_ray - T_reference|: the difference in how dark the ground is. The
    // pack's slab-in-shell model (a layer's column depth spread evenly over the shell's chord) differs from the field's march by a few percent of tau at 45 degrees
    // and by tens of percent at a grazing sun, where the chord through a shell that is several times thicker than the cloud crosses many texels; a relative figure
    // of T or tau is large where T is already near 0 and invisible there.
    [TestClass]
    public class RtKerbinLayersAgreementTests
    {
        private static WeatherMap MapOf(Func<double, double, double> f)
        {
            var map = new WeatherMap();
            map.Resize(512, 256);
            for (int j = 0; j < 256; j++)
                for (int i = 0; i < 512; i++)
                {
                    double lat, lon;
                    RtCloudPackLayout.TexelLatLon(i, j, 512, 256, out lat, out lon);
                    map.Values[j * 512 + i] = (float)Math.Max(0.0, Math.Min(1.0, f(lat, lon)));
                }
            return map;
        }

        // Kerbin's four layers with noise maps whose mean cover is moved by `offset` (the map value at which the share is one half, per layer, plus the offset), at the
        // cfg's shadow strength (1).
        internal static LayerStackSource Kerbin(bool heights, double offset, int cell = 10)
        {
            var source = new LayerStackSource(1, PackScenes.Kerbin, true);
            CloudLayerDef[] defs = { CloudLookFixtures.Rain(), CloudLookFixtures.Cumulus(), CloudLookFixtures.Cumulonimbus(), CloudLookFixtures.Cirrus() };
            for (int i = 0; i < defs.Length; i++)
            {
                Func<double, double, double> nz = PackScenes.Noise((uint)(3 + i), cell);
                double centre = (i == 0 ? 0.4375 : i == 3 ? 0.6 : 0.375) + offset;
                WeatherMap cover = MapOf((lat, lon) => centre + 0.3 * (nz(lat, lon) - 0.5) * 2.0);
                WeatherMap type = MapOf(PackScenes.Noise((uint)(30 + i), Math.Max(2, cell + 4)));
                int at = source.Add(defs[i], cover, type);
                if (!heights) continue;
                var tuning = new HeightTuning();
                var maps = new HeightMaps(128, 64, tuning.RootRangeM, tuning.TopRangeM, tuning.VariationRangeM);
                Func<double, double, double> offsets = PackScenes.Noise((uint)(40 + i), 6);
                for (int j = 0; j < 64; j++)
                    for (int k = 0; k < 128; k++)
                    {
                        double lat = 90.0 - (j + 0.5) * 180.0 / 64, lon = -180.0 + (k + 0.5) * 360.0 / 128;
                        for (int c = 0; c < 3; c++) maps.Bytes[(j * 128 + k) * 3 + c] = (byte)Math.Max(0, Math.Min(255, (int)(offsets(lat, lon + 7 * c) * 255.0)));
                    }
                source.SetHeights(at, tuning, maps, 1u);
            }
            return source;
        }

        // The game's maps are not smooth: start 66 (the first game start of the pack in the rays) measured p90 0.49 and a worst place of 0.98 at 45 degrees where the
        // scenes above (cells of ten texels) gave 0.02 to 0.08. Two things were wrong and are caught here, by cells of two to four texels (15 to 30 km on Kerbin):
        //  - the pack capped a column's depth at 64, and a cumulonimbus column's full depth is over 100 (the ray multiplies it by a small soft-edge share): the cloud
        //    read 5 to 10 times too thin (field 3.6, pack 0.39 at the worst place);
        //  - the ray spread a column over the whole shell, which is 4.7 to 12.8 km thick against a cloud of 1 to 3 km, and so averaged a band as wide as the shell
        //    into every shadow.
        // Before the fixes this scene gives p90 0.35 and worst 0.65 at the soft offset and p90 0.20 with the height variation; now p90 is at most 0.07.
        [TestMethod]
        public void FineCellsAgreeWithTheShadowAsTheGameMapsNeedIt()
        {
            foreach (int cell in new[] { 2, 3, 4 })
                foreach (bool heights in new[] { false, true })
                    foreach (double offset in new[] { -0.2, -0.3 })
                    {
                        LayerStackSource stack = Kerbin(heights, offset, cell);
                        LayerTypeTable[] tables = stack.Tables();
                        RtCloudPack pack = RtCloudPackDriver.BakeNow(stack, tables, RtCloudPackSettings.From(true, 0, (key, value) => value));
                        RtAgreementScan scan = RtPackAgreement.Scan(stack, tables, pack, 0.0, 0.0, 4.0, 0.2, 45.0);
                        string what = "cell " + cell + " heights " + heights + " offset " + offset + ": n " + scan.Places + " median " + scan.Median.ToString("0.000") + " p90 "
                                      + scan.P90.ToString("0.000") + " worst " + scan.Worst.ToString("0.000") + " ratio " + scan.TauRatioMedian.ToString("0.000");
                        Console.WriteLine(what);
                        Assert.IsTrue(scan.Places > 500, "places " + what);
                        Assert.IsTrue(scan.P90 <= 0.09, "p90 " + what);
                        Assert.IsTrue(scan.Worst <= 0.5, "worst " + what);
                        Assert.IsTrue(Math.Abs(scan.TauRatioMedian - 1.0) <= 0.12, "tau ratio " + what);
                    }
        }

        [TestMethod]
        public void ADeepColumnKeepsItsFullDepthSoThatASmallShareStillShadesRight()
        {
            LayerStackSource stack = Kerbin(false, -0.2, 3);
            RtCloudPack pack = RtCloudPackDriver.BakeNow(stack, stack.Tables(), RtCloudPackSettings.From(true, 0, (key, value) => value));
            float deepest = 0f;
            for (int i = 1; i < pack.Data.Length; i += RtCloudPackLayout.Channels) deepest = Math.Max(deepest, pack.Data[i]);
            Assert.IsTrue(deepest > 100f, "the cumulonimbus column's full depth is kept (" + deepest + "), not capped at 64");
            // Where the soft edge's share is small the product is the field's: the vertical depth over a place by the pack against the shadow field's.
            var field = new CloudShadowField(stack, stack.Tables()) { StepsPerSpan = 64, DiscSamples = 1 };
            double worst = 0.0;
            int judged = 0;
            for (double lat = -3.0; lat <= 3.0; lat += 0.25)
                for (double lon = -3.0; lon <= 3.0; lon += 0.25)
                {
                    double reference = field.VerticalOpticalDepth(lat, lon, 0.0), sum = 0.0;
                    if (reference < 0.05) continue;
                    for (int l = 0; l < pack.LayerCount; l++) sum += pack.ColumnTau(l, lat, lon);
                    judged++;
                    worst = Math.Max(worst, Math.Abs(Math.Log(Math.Max(1e-6, sum)) - Math.Log(reference)));
                }
            Assert.IsTrue(judged > 100, "places " + judged);
            Assert.IsTrue(worst < 0.7, "the pack's vertical depth is within a factor of two of the field's everywhere (worst ln ratio " + worst + ")");
        }

        [TestMethod]
        public void ThePackHoldsTheCloudsBaseAndTopAsTheTypeTableHasThem()
        {
            LayerStackSource stack = Kerbin(false, -0.2, 10);
            RtCloudPack pack = RtCloudPackDriver.BakeNow(stack, stack.Tables(), RtCloudPackSettings.From(true, 0, (key, value) => value));
            // Layer 1 is the cumulus: its type table's base and top change with the type value, and the shell is the widest of them.
            LayerTypeTable table = stack.Tables()[1];
            CloudLayerDef layer = stack.LayerAt(1);
            RtCloudLayerEntry entry = pack.Entries[1];
            int judged = 0;
            for (double lat = -3.0; lat <= 3.0; lat += 0.5)
                for (double lon = -3.0; lon <= 3.0; lon += 0.5)
                {
                    double tau, baseFraction, topFraction;
                    pack.Column(1, lat, lon, out tau, out baseFraction, out topFraction);
                    if (tau < 0.2) continue;
                    float cover, type;
                    stack.SampleMaps(1, lat, lon, out cover, out type);
                    double b, t;
                    table.InterpolatedHeights(type, out b, out t);
                    double baseM = layer.InnerAltitudeM + b * layer.ThicknessM, topM = layer.InnerAltitudeM + t * layer.ThicknessM;
                    double packBase = entry.InnerRadiusM - pack.BodyRadiusM + baseFraction * entry.ThicknessM, packTop = entry.InnerRadiusM - pack.BodyRadiusM + topFraction * entry.ThicknessM;
                    // Smooth maps: the type moves a little between the texels the filter mixes, so a few hundred m.
                    Assert.AreEqual(baseM, packBase, 400.0, "base at " + lat + ", " + lon);
                    Assert.AreEqual(topM, packTop, 600.0, "top at " + lat + ", " + lon);
                    judged++;
                }
            Assert.IsTrue(judged > 30, "places " + judged);
        }

        [TestMethod]
        public void ABandShareIsTheShareOfThePathInsideTheCloud()
        {
            Assert.AreEqual(1.0, RtCloudRayMath.BandShare(0.2, 0.4, 0.1, 0.5), 1e-12, "the path is inside");
            Assert.AreEqual(0.0, RtCloudRayMath.BandShare(0.6, 0.8, 0.1, 0.5), 1e-12, "the path is above");
            Assert.AreEqual(0.5, RtCloudRayMath.BandShare(0.4, 0.6, 0.1, 0.5), 1e-12, "half of the path");
            Assert.AreEqual(0.5, RtCloudRayMath.BandShare(0.6, 0.4, 0.1, 0.5), 1e-12, "the path descends");
            Assert.AreEqual(1.0, RtCloudRayMath.BandShare(0.3, 0.3, 0.1, 0.5), 1e-12, "a level path inside");
            Assert.AreEqual(0.0, RtCloudRayMath.BandShare(0.7, 0.7, 0.1, 0.5), 1e-12, "a level path outside");
        }

        [TestMethod]
        public void TheWorstPlacesNameWhatBothSidesRead()
        {
            LayerStackSource stack = Kerbin(true, -0.2, 2);
            LayerTypeTable[] tables = stack.Tables();
            RtCloudPack pack = RtCloudPackDriver.BakeNow(stack, tables, RtCloudPackSettings.From(true, 0, (key, value) => value));
            System.Collections.Generic.List<string> lines = RtPackAgreement.WorstPlaces(stack, tables, pack, 0.0, 0.0, 4.0, 0.2, 45.0, 3);
            foreach (string line in lines) Console.WriteLine(line);
            Assert.AreEqual(3 * pack.LayerCount, lines.Count, "a line per layer for each of the three places");
            StringAssert.Contains(lines[0], "tau field");
            StringAssert.Contains(lines[0], "band m");
        }

        [TestMethod]
        public void TheRealKerbinLayersAgreeWithTheShadowInTransmittance()
        {
            int judged = 0;
            foreach (bool heights in new[] { false, true })
                foreach (double offset in new[] { -0.1, -0.15, -0.2, -0.3 })
                {
                    LayerStackSource stack = Kerbin(heights, offset);
                    LayerTypeTable[] tables = stack.Tables();
                    RtCloudPack pack = RtCloudPackDriver.BakeNow(stack, tables, RtCloudPackSettings.From(true, 0, (key, value) => value));
                    foreach (double elevation in new[] { 45.0, 6.0 })
                    {
                        RtAgreementScan scan = RtPackAgreement.Scan(stack, tables, pack, 0.0, 0.0, 4.0, 0.2, elevation);
                        Console.WriteLine("heights " + heights + " offset " + offset + " sun " + elevation + ": n " + scan.Places + " |dT| median " + scan.Median.ToString("0.000")
                                          + " p90 " + scan.P90.ToString("0.000") + " worst " + scan.Worst.ToString("0.000") + " tau ratio " + scan.TauRatioMedian.ToString("0.000")
                                          + " (n " + scan.RatioPlaces + ")");
                        if (scan.Places < 100) continue;
                        judged++;
                        // [proposed] from the first runs of these scenes (45 degrees: p90 0.02 to 0.05, worst 0.18; 6 degrees: p90 up to 0.31, worst 0.84; the ratio 0.97
                        // to 1.01 at 45 degrees and 0.73 to 1.0 at 6, where the shell's chord averages the column over many more texels than the cloud's own band holds).
                        // 0.07 of T at 45 degrees is a shadow a few percent too dark or too light; at a grazing sun the shadow edge of a thin cloud moves by kilometres.
                        bool high = elevation > 40.0;
                        Assert.IsTrue(scan.P90 <= (high ? 0.09 : 0.65), elevation + " degrees: p90 |dT| " + scan.P90);
                        // The worst place at a grazing sun reaches 0.92 (a thin cloud's shadow edge displaced by kilometres): no bound but 1.
                        if (high) Assert.IsTrue(scan.Worst <= 0.25, elevation + " degrees: worst |dT| " + scan.Worst);
                        // The scale: a factor of two (a layer twice or not at all, a column depth read at the wrong scale) is 2.0 or 0.5. With the height variation the
                        // shells are several times the cloud's thickness and the grazing sun's median ratio is 2 to 3.4: no bound there.
                        if (scan.RatioPlaces >= 30 && (high || !heights))
                            Assert.IsTrue(high ? Math.Abs(scan.TauRatioMedian - 1.0) <= 0.12 : scan.TauRatioMedian > 0.6 && scan.TauRatioMedian < 1.6, "tau ratio " + scan.TauRatioMedian);
                    }
                }
            Assert.IsTrue(judged >= 4, "scenes with enough places: " + judged);
        }
    }
}
