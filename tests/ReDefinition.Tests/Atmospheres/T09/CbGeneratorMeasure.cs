using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AtmoSim.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.B13;
using ReDefinition.Tests.Atmospheres.C25;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // CBGEN: measures the generator's convective cells over the globe (exploratory; runs only when its environment variable names a file):
    //   CBGEN_OUT   MeasureCells: the area under cells by bucket phase, local hour, season and instability (M1 to M3)
    //   CBGEN_OUT5  MeasureTheCbLayer: the cumulonimbus layer as the game builds it and the anvil sources it gives (M5), at the game clock and over a year
    //   CBGEN_STATIONARY=0  the round-4 draw (BodyParams.StationaryCells false)
    // The numbers are in .local/plans/atmospheres/E40-cb-generator-result.md.
    [TestClass]
    public class CbGeneratorMeasure
    {
        private sealed class Acc
        {
            internal double Area, A15, A30, A60, Max, SumCs, Present;
            internal void Add(double wgt, double cs)
            {
                Area += wgt; if (cs > 0.0) Present += wgt; if (cs > 0.15) A15 += wgt; if (cs > 0.3) A30 += wgt; if (cs > 0.6) A60 += wgt; if (cs > Max) Max = cs;
            }
            internal string Text()
            {
                if (Area <= 0.0) return "-";
                return "any " + (100.0 * Present / Area).ToString("0.00") + " | >.15 " + (100.0 * A15 / Area).ToString("0.00") + " | >.3 " + (100.0 * A30 / Area).ToString("0.00") + " | >.6 " + (100.0 * A60 / Area).ToString("0.000") + " % (max " + Max.ToString("0.00") + ")";
            }
        }

        [TestMethod]
        [TestCategory("Explore")]
        public void MeasureCells()
        {
            string outPath = Environment.GetEnvironmentVariable("CBGEN_OUT");
            if (string.IsNullOrEmpty(outPath)) return;
            var sb = new StringBuilder();
            GeneratorField field = Kerbin(1, Environment.GetEnvironmentVariable("CBGEN_STATIONARY") != "0");
            WeatherGenerator g = field.StateOf(FieldFixture.BodyIndex).Generator;
            double year = g.Body.YearS, day = g.Body.RotationPeriodS, bucket = WeatherGenerator.CellHourBucketS;
            const int nx = 192, ny = 96;
            double d2r = Math.PI / 180.0;

            // M1: share by phase inside the 3 h bucket (24 buckets spread over the year, 12 phases each).
            var byPhase = new Acc[12];
            for (int i = 0; i < 12; i++) byPhase[i] = new Acc();
            // M2: tropics (|lat| < 25) by local solar hour (3 h bins) and by phase of the year of the hemisphere's summer.
            var byHour = new Acc[8];
            for (int i = 0; i < 8; i++) byHour[i] = new Acc();
            var bySeason = new Acc[4];
            for (int i = 0; i < 4; i++) bySeason[i] = new Acc();
            // M3: cs against the instability proxy (tropics, all times).
            var byInst = new Acc[10];
            for (int i = 0; i < 10; i++) byInst[i] = new Acc();
            var byInstAfternoon = new Acc[10];
            for (int i = 0; i < 10; i++) byInstAfternoon[i] = new Acc();
            for (int b = 0; b < 24; b++)
            {
                double bucketStart = Math.Floor((b / 24.0 * year + 0.37 * day * b) / bucket) * bucket;
                for (int p = 0; p < 12; p++)
                {
                    double ut = bucketStart + (p + 0.5) / 12.0 * bucket;
                    for (int y = 0; y < ny; y++)
                    {
                        double lat = (90.0 - (y + 0.5) / ny * 180.0) * d2r;
                        double wgt = Math.Cos(lat);
                        for (int x = 0; x < nx; x++)
                        {
                            double lon = ((x + 0.5) / nx * 360.0 - 180.0) * d2r;
                            Weather w;
                            g.Sample(lat, lon, 0.0, ut, out w);
                            byPhase[p].Add(wgt, w.CellStrength);
                            if (Math.Abs(lat) < 25.0 * d2r)
                            {
                                // local solar hour from the sub-solar longitude (the generator's own: SunLon = -rate * ut)
                                double sunLon = SunLonOf(g, ut);
                                double hourAng = DMath.WrapPi(lon - sunLon); // 0 at local noon, +pi at midnight
                                double hour = 12.0 + hourAng / Math.PI * 12.0;
                                if (hour >= 24.0) hour -= 24.0; if (hour < 0.0) hour += 24.0;
                                byHour[(int)(hour / 3.0)].Add(wgt, w.CellStrength);
                                double phase = ut / year - Math.Floor(ut / year);
                                bySeason[(int)(phase * 4.0) % 4].Add(wgt, w.CellStrength);
                                int ib = (int)Math.Min(9, Math.Floor(w.Instability * 10.0));
                                byInst[ib].Add(wgt, w.CellStrength);
                                if (hour >= 12.0 && hour < 18.0) byInstAfternoon[ib].Add(wgt, w.CellStrength);
                            }
                        }
                    }
                }
            }
            sb.AppendLine("M1 by phase inside the 3 h bucket (12 phases, globe, 24 buckets):");
            for (int i = 0; i < 12; i++) sb.AppendLine("  phase " + (i / 12.0).ToString("0.00") + ": " + byPhase[i].Text());
            sb.AppendLine("M2 tropics |lat|<25 by local solar hour (0 = midnight, noon in the 12-15 bin):");
            for (int i = 0; i < 8; i++) sb.AppendLine("  " + (3 * i) + "-" + (3 * i + 3) + " h: " + byHour[i].Text());
            sb.AppendLine("M2b tropics by quarter of the year (phase 0 = northern midwinter):");
            for (int i = 0; i < 4; i++) sb.AppendLine("  quarter " + i + ": " + bySeason[i].Text());
            sb.AppendLine("M3 tropics by instability bin (all times) and afternoon (12-18 h):");
            for (int i = 0; i < 10; i++) sb.AppendLine("  inst " + (i / 10.0).ToString("0.0") + ": all " + byInst[i].Text() + "   |  afternoon " + byInstAfternoon[i].Text() + "  area share " + (100.0 * byInst[i].Area / SumArea(byInst)).ToString("0.0") + " %");
            File.WriteAllText(outPath, sb.ToString());
        }

        // M5: the Cb layer as the game builds it (game seed, shipped cfg, low stratiform rule with its rainType scale, meso stage, the combination of all layers to the field's cover) and the
        // anvil's sources and strengths (AnvilMap.Report).
        [TestMethod]
        [TestCategory("Explore")]
        public void MeasureTheCbLayer()
        {
            string outPath = Environment.GetEnvironmentVariable("CBGEN_OUT5");
            if (string.IsNullOrEmpty(outPath)) return;
            var sb = new StringBuilder();
            double bucket = WeatherGenerator.CellHourBucketS;
            LayerWindOptions.SteeringOverride = LayerSteeringMode.MapsOnly;
            try
            {
                GeneratorField kf = Kerbin(664216370, Environment.GetEnvironmentVariable("CBGEN_STATIONARY") != "0");
                CloudLayerDef[] layers = TypeMixFixture.ShippedLayers();
                int cbIndex = -1;
                for (int l = 0; l < layers.Length; l++) if (layers[l].Name.Contains("cumulonimbus")) cbIndex = l;
                CloudLayerDef cb = layers[cbIndex];
                var uts = new List<double> { 1686294.0 };
                for (int k = 0; k < 12; k++) uts.Add(1684800.0 + (k + 0.5) / 12.0 * bucket);
                for (int k = 1; k <= 16; k++) uts.Add(1686294.0 + k * 4.7 * 21549.425);
                sb.AppendLine("M5 the Cb layer, game seed, shipped cfg, combined: per UT: sources, strength max, > 0.3, > 0.6, texels over the outline");
                foreach (double ut in uts)
                {
                    TypeMixFixture.Shared s = TypeMixFixture.Maps(kf, FieldFixture.BodyIndex, ut);
                    var combined = new byte[layers.Length][];
                    var types = new byte[layers.Length][];
                    var rules = new WeatherLowStratiformRule[layers.Length];
                    for (int li = 0; li < layers.Length; li++)
                    {
                        byte[] cover, type;
                        TypeMixFixture.Layer(kf, FieldFixture.BodyIndex, s, layers[li], true, out cover, out type);
                        rules[li] = layers[li].LowStratiform;
                        if (layers[li].LowStratiform != null && layers[li].RainType < 1f)
                        {
                            type = (byte[])type.Clone();
                            for (int i = 0; i < type.Length; i++) type[i] = (byte)Math.Round(type[i] * layers[li].RainType);
                        }
                        combined[li] = (byte[])cover.Clone();
                        types[li] = type;
                    }
                    CloudLookTuning.MeetTheField(combined, layers.Length, s.Cover.Bytes, CloudLookTuning.CoverCombine, rules, s.Precip.Values);
                    byte[] c = combined[cbIndex];
                    var rep = new AnvilMap.Report();
                    int over = 0;
                    double outline = AnvilMap.MedianRaw(cb) * 255.0;
                    for (int i = 0; i < c.Length; i++) if (c[i] > outline) over++;
                    AnvilMap.Apply(cb, (byte[])c.Clone(), (byte[])types[cbIndex].Clone(), new AnvilInputs { Width = GeneratorMaps.Width, Height = GeneratorMaps.Height, RadiusM = 600000.0, ShearRadS = 0.0, UpdraftMs = kf.CellUpdraftMsOf(FieldFixture.BodyIndex) }, rep);
                    int n = rep.Strength.Count, s3 = 0, s6 = 0; double mx = 0;
                    foreach (double v in rep.Strength) { if (v > mx) mx = v; if (v > 0.3) s3++; if (v > 0.6) s6++; }
                    sb.AppendLine("  ut " + ut.ToString("0") + " phase " + ((ut % bucket) / bucket).ToString("0.00") + ": sources " + n + ", max " + mx.ToString("0.000") + ", >0.3: " + s3 + ", >0.6: " + s6 + ", texels over outline " + over);
                }
            }
            finally
            {
                LayerWindOptions.SteeringOverride = null;
            }
            File.WriteAllText(outPath, sb.ToString());
        }

        // Kerbin as the game builds it (SkyMapsExplore.KerbinField) with the round-5 switch set.
        internal static GeneratorField Kerbin(int seed, bool stationary)
        {
            BodyFacts facts = FieldFixture.HomeFacts("Kerbin");
            BodyClimate parsed = ReDefinition.Tests.Atmospheres.T10.CloudLayerSchemaTests.ParseKerbin().Climate;
            var climate = new BodyClimate("Kerbin", FieldFixture.BodyIndex, 600000.0, FieldFixture.HomeYear, 21549.425, BodyStatus.Full, seed,
                parsed.Seasons, parsed.Temperature, parsed.Precip, parsed.Covers, parsed.Ice, parsed.Generator, parsed.Clouds, parsed.Look, parsed.Sound, parsed.Wind, parsed.Gameplay,
                parsed.Aurora, parsed.LightMedium, parsed.Frost, parsed.Forecast);
            var field = new GeneratorField();
            field.Say = s => { };
            field.Maps.Synchronous = true;
            BodyParams p = FieldFixture.Params(climate, facts, "Kerbin");
            p.StationaryCells = stationary;
            field.SetState(FieldFixture.State(climate, p));
            return field;
        }

        private static double SumArea(Acc[] a)
        {
            double s = 0; foreach (Acc x in a) s += x.Area; return s;
        }

        // The sub-solar longitude of the generator (its frame: SunLon = WrapPi(-rate * ut), rate = 2 pi / day) through a probe: where the local noon is.
        private static double SunLonOf(WeatherGenerator g, double ut)
        {
            double rate = g.Body.Omega - 2.0 * Math.PI / g.Body.YearS;
            return DMath.WrapPi(-rate * ut);
        }
    }
}
