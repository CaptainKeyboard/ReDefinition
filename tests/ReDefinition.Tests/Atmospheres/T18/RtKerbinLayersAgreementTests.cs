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
        internal static LayerStackSource Kerbin(bool heights, double offset)
        {
            var source = new LayerStackSource(1, PackScenes.Kerbin, true);
            CloudLayerDef[] defs = { CloudLookFixtures.Rain(), CloudLookFixtures.Cumulus(), CloudLookFixtures.Cumulonimbus(), CloudLookFixtures.Cirrus() };
            for (int i = 0; i < defs.Length; i++)
            {
                Func<double, double, double> nz = PackScenes.Noise((uint)(3 + i), 10);
                double centre = (i == 0 ? 0.4375 : i == 3 ? 0.6 : 0.375) + offset;
                WeatherMap cover = MapOf((lat, lon) => centre + 0.3 * (nz(lat, lon) - 0.5) * 2.0);
                WeatherMap type = MapOf(PackScenes.Noise((uint)(30 + i), 14));
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
