using System;
using System.Text;
using System.IO;
using AtmoSim.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Tests.Atmospheres.C25;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // CELLS: the strength of a convective cell by place and time, and the rain and cover of the fronts, for a generator with the energy term (CellInstabilityWeight 1) and without it (0).
    // Exploratory: runs only when CELLS_OUT names a file (the numbers are in .local/plans/atmospheres/E40-cells-result.md). The tolerance of the term on rain and cover is asserted
    // in CellEnergyTests.
    [TestClass]
    public class CellEnergyMeasure
    {
        internal sealed class Acc
        {
            internal double Area, A03, A06, Cells, SumCs, Rain, ConvRain, Cover, Precipitating;
            internal void Add(double wgt, ref Weather w)
            {
                Area += wgt;
                if (w.CellStrength > 0.0) { Cells += wgt; SumCs += wgt * w.CellStrength; }
                if (w.CellStrength > 0.3) A03 += wgt;
                if (w.CellStrength > 0.6) A06 += wgt;
                Rain += wgt * w.PrecipMmH;
                ConvRain += wgt * w.PrecipMmH * w.ConvectiveShare;
                Cover += wgt * w.Coverage;
                if (w.PrecipMmH > 0.04) Precipitating += wgt;
            }
            internal double P03 { get { return Area > 0.0 ? 100.0 * A03 / Area : 0.0; } }
            internal double P06 { get { return Area > 0.0 ? 100.0 * A06 / Area : 0.0; } }
            internal double MeanRain { get { return Area > 0.0 ? Rain / Area : 0.0; } }
            internal double MeanConv { get { return Area > 0.0 ? ConvRain / Area : 0.0; } }
            internal double MeanCover { get { return Area > 0.0 ? Cover / Area : 0.0; } }
            internal double MeanCs { get { return Cells > 0.0 ? SumCs / Cells : 0.0; } }
            internal double PrecipShare { get { return Area > 0.0 ? 100.0 * Precipitating / Area : 0.0; } }
            internal string Text()
            {
                if (Area <= 0.0) return "-";
                return ">.3 " + P03.ToString("0.000") + " % | >.6 " + P06.ToString("0.000") + " % | cs mean " + MeanCs.ToString("0.00") + " | rain " + MeanRain.ToString("0.0000") + " mm/h (conv " + MeanConv.ToString("0.0000")
                    + ") | cover " + MeanCover.ToString("0.0000") + " | precipitating " + PrecipShare.ToString("0.00") + " %";
            }
        }

        internal sealed class Table
        {
            internal readonly Acc Globe = new Acc(), Tropics = new Acc(), Fronts = new Acc(), MidLat = new Acc(), WarmAfternoon = new Acc(), ColdSeaNight = new Acc(), Land = new Acc(), Sea = new Acc();
            internal readonly Acc[] Bands = { new Acc(), new Acc(), new Acc(), new Acc(), new Acc() };
            internal readonly Acc[] Hours = { new Acc(), new Acc(), new Acc(), new Acc(), new Acc(), new Acc(), new Acc(), new Acc() };
        }

        internal static readonly double[] BandEdgesDeg = { 0, 10, 25, 45, 65, 90 };

        // The sub-solar longitude of the generator's frame.
        private static double SunLon(WeatherGenerator g, double ut)
        {
            return DMath.WrapPi(-(g.Body.Omega - 2.0 * Math.PI / g.Body.YearS) * ut);
        }

        internal static Table Measure(WeatherGenerator g, int nx, int ny, int buckets, int phases)
        {
            var t = new Table();
            double bucket = WeatherGenerator.CellHourBucketS, d2r = Math.PI / 180.0;
            for (int b = 0; b < buckets; b++)
            {
                for (int p = 0; p < phases; p++)
                {
                    double ut = Math.Floor((b / (double)buckets * g.Body.YearS + 0.37 * g.Body.RotationPeriodS * b) / bucket) * bucket + (p + 0.5) / phases * bucket;
                    double declination = -g.Body.ObliquityRad * Math.Cos(2.0 * Math.PI * (ut / g.Body.YearS - Math.Floor(ut / g.Body.YearS)));
                    double sunLon = SunLon(g, ut);
                    for (int y = 0; y < ny; y++)
                    {
                        double lat = (90.0 - (y + 0.5) / ny * 180.0) * d2r, latDeg = Math.Abs(lat) / d2r, wgt = Math.Cos(lat);
                        int band = 0;
                        while (band < 4 && latDeg >= BandEdgesDeg[band + 1]) band++;
                        for (int x = 0; x < nx; x++)
                        {
                            double lon = ((x + 0.5) / nx * 360.0 - 180.0) * d2r;
                            Weather w;
                            g.Sample(lat, lon, 0.0, ut, out w);
                            double hour = 12.0 + DMath.WrapPi(lon - sunLon) / Math.PI * 12.0;
                            if (hour >= 24.0) hour -= 24.0; else if (hour < 0.0) hour += 24.0;
                            t.Globe.Add(wgt, ref w);
                            t.Bands[band].Add(wgt, ref w);
                            if (w.OceanFraction >= 0.5) t.Sea.Add(wgt, ref w); else t.Land.Add(wgt, ref w);
                            if (latDeg >= 35.0 && latDeg < 70.0) t.MidLat.Add(wgt, ref w);
                            if (w.FrontKPer100Km > 0.5 * g.D.FrontRefKPer100Km) t.Fronts.Add(wgt, ref w);
                            if (latDeg < 25.0)
                            {
                                t.Tropics.Add(wgt, ref w);
                                t.Hours[(int)(hour / 3.0) % 8].Add(wgt, ref w);
                                if (lat * declination > 0.0 && hour >= 12.0 && hour < 18.0) t.WarmAfternoon.Add(wgt, ref w);
                            }
                            if (w.OceanFraction >= 0.9 && w.SurfaceTempK < 283.0 && (hour >= 21.0 || hour < 5.0)) t.ColdSeaNight.Add(wgt, ref w);
                        }
                    }
                }
            }
            return t;
        }

        internal static void Print(StringBuilder sb, string label, Table t)
        {
            sb.AppendLine("== " + label);
            sb.AppendLine("globe: " + t.Globe.Text());
            sb.AppendLine("tropics |lat|<25: " + t.Tropics.Text());
            for (int i = 0; i < 5; i++) sb.AppendLine("  |lat| " + BandEdgesDeg[i] + "-" + BandEdgesDeg[i + 1] + ": " + t.Bands[i].Text());
            sb.AppendLine("land: " + t.Land.Text());
            sb.AppendLine("sea: " + t.Sea.Text());
            for (int i = 0; i < 8; i++) sb.AppendLine("  tropics local hour " + (3 * i) + "-" + (3 * i + 3) + ": " + t.Hours[i].Text());
            sb.AppendLine("warm tropics, afternoon: " + t.WarmAfternoon.Text());
            sb.AppendLine("cold sea (T<283 K), night: " + t.ColdSeaNight.Text());
            sb.AppendLine("mid-latitudes 35-70: " + t.MidLat.Text());
            sb.AppendLine("front zone (gradient > half the reference): " + t.Fronts.Text());
        }

        internal static WeatherGenerator With(WeatherGenerator g, Action<BodyParams> set)
        {
            BodyParams p = g.Body.Clone();
            set(p);
            return new WeatherGenerator(p, g.Seed);
        }

        [TestMethod]
        [TestCategory("Explore")]
        public void MeasureTheEnergyTerm()
        {
            string outPath = Environment.GetEnvironmentVariable("CELLS_OUT");
            if (string.IsNullOrEmpty(outPath)) return;
            double full;
            if (!double.TryParse(Environment.GetEnvironmentVariable("CELLS_FULL"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out full)) full = -1.0;
            double lift;
            if (!double.TryParse(Environment.GetEnvironmentVariable("CELLS_LIFT"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out lift)) lift = -1.0;
            var sb = new StringBuilder();
            WeatherGenerator g =CbGeneratorMeasure.Kerbin(1, true).StateOf(FieldFixture.BodyIndex).Generator;
            WeatherGenerator off = With(g, p => p.CellInstabilityWeight = 0.0);
            WeatherGenerator on = With(g, p => { p.CellInstabilityWeight = 1.0; if (full > 0.0) p.CellEnergyFull = full; if (lift >= 0.0) p.CellLiftEnergyShare = lift; });
            sb.AppendLine("Kerbin, seed 1, 144 x 72, 24 buckets x 6 phases, CellEnergyFull " + on.Body.CellEnergyFull + ", lift share " + on.Body.CellLiftEnergyShare);
            Print(sb, "term off (weight 0)", Measure(off, 144, 72, 24, 6));
            Print(sb, "term on (weight 1)", Measure(on, 144, 72, 24, 6));
            File.WriteAllText(outPath, sb.ToString());
        }
    }
}
