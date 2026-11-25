using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T10
{
    // Master plan section 43 step 1: the form of a cloud type as the layer pass's density draws it, on the CPU, before a game run. For each listed type of Kerbin's cfg, the map forced
    // closed and the type forced (as the probe's cloud.mapOverride / cloud.typeOverride do), it writes
    //   slice-<layer>-<type>[-pannus].pgm   a vertical slice: the density over 30 km along the surface (columns) and the layer's height (rows, top row = the layer's outer altitude);
    //   top-<layer>-<type>[-pannus].pgm     the form from above: the column's optical depth over 30 x 30 km (1 - exp(-tau) as grey);
    //   form.txt                            per type and band of its height: the share of the area the type fills there, the mean density, the top's height spread.
    // The density is the shader's LayerDensityEx in its parts the CPU twins carry: the map's clear-sky rule, the type's lift (LayerTypeTable.LiftOf), the shape noise with the type's
    // erosion (CloudShapeModel.Share, the layer's noise scale read at pc (1 + g) and its amount times (1 + b), g and b the type table's FORM channels), the layer's water exponent and the type's
    // profile (LayerTypeTable.ProfileAt) times sigma. NOT mirrored: the height maps' local base and top and their ragged boundary (the slice keeps the type's cfg heights), the
    // undulatus, the cells and the cloud field (so stratocumulus and altocumulus, which the field draws, are not judged here), the far view's mean and the light.
    // Runs only when ATMO_FORM_SLICE names a folder (the test passes without it).
    [TestClass]
    public class TypeFormSliceTool
    {
        private const int Columns = 600, TopSize = 300;
        private const double AlongM = 30000.0;

        private static readonly string[][] Types =
        {
            new[] { "Kerbin-weather-rain", "Fog" }, new[] { "Kerbin-weather-rain", "Stratus" }, new[] { "Kerbin-weather-rain", "Nimbostratus" },
            new[] { "Kerbin-cirrus", "Cirrus" }, new[] { "Kerbin-cirrus", "Cirrostratus" }
        };

        private static int frame = 8800000;

        [TestCleanup]
        public void Cleanup()
        {
            Tuning.ClearOverrides();
            Tuning.BeginFrame(++frame);
        }

        [TestMethod, TestCategory("Explore")]
        public void WriteTheFormSlicesOfTheTypes()
        {
            string folder = Environment.GetEnvironmentVariable("ATMO_FORM_SLICE");
            if (string.IsNullOrEmpty(folder)) Assert.Inconclusive("A measurement run: it runs only when ATMO_FORM_SLICE is set.");
            Directory.CreateDirectory(folder);
            NoiseVolumeSet set = NoiseVolumeSet.Generate(NoiseSizes.ForLevel(1), 1u);
            var text = new StringBuilder();
            text.AppendLine("layer type pannus | band(h) area_share mean_density | top_height_p10_p90_m");
            foreach (string[] lt in Types)
            {
                Tuning.ClearOverrides();
                Tuning.BeginFrame(++frame);
                CloudLayerDef layer = CloudLayerSchemaTests.ParseKerbin().Climate.Clouds.Layers.Single(l => l.Name == lt[0]).Build(1);
                int type = -1;
                for (int i = 0; i < layer.Types.Count; i++) if (layer.Types[i].Name == lt[1]) type = i;
                if (type < 0) continue;
                var table = new LayerTypeTable(layer);
                Write(folder, set, layer, table, type, layer.Types[type].PannusProfile != null, text);
            }
            File.WriteAllText(Path.Combine(folder, "form.txt"), text.ToString());
        }

        // The density at a point of the map frame (planet-centred, metres) and an altitude, the map closed and the type forced.
        private static double Density(NoiseVolumeSet set, CloudLayerDef layer, LayerTypeTable table, int type, double x, double y, double z, double altM)
        {
            double depth = layer.OuterAltitudeM - layer.InnerAltitudeM;
            double layerShare = (altM - layer.InnerAltitudeM) / depth;
            double typeValue = table.Count > 1 ? type / (double)(table.Count - 1) : 0.0;
            double b, t;
            table.InterpolatedHeights(typeValue, out b, out t);
            if (layerShare < b || layerShare > t || !(t > b)) return 0.0;
            double h = (layerShare - b) / (t - b);
            double lift = table.LiftOf(typeValue, layerShare);
            double cov = layer.ClearSkyMap(1.0) - lift;
            int i = (int)Math.Round(h * (LayerTypeTable.ProfileSamples - 1));
            double g = table.ErosionGainData[type * LayerTypeTable.ProfileSamples + Math.Max(0, Math.Min(LayerTypeTable.ProfileSamples - 1, i))];
            NoiseLayerParams prm = NoiseLayerParams.Default(layer.EffectiveNoiseScaleM);
            double amount = layer.EffectiveNoiseAmount * (1.0 + table.NoiseAmountOf(typeValue, h));
            double share = CloudShapeModel.Share(set, ref prm, x, y, z, cov, layer.EffectiveCoverage, layer.EffectiveEdgeSoftness, amount,
                                                 layer.Types[type].Erosion * (1.0 + g), true);
            share = CloudWater.Pow(share, layer.EffectiveWaterPowerAt(table, typeValue));
            return layer.EffectiveSigmaPerM * share * table.ProfileAt(type, h);
        }

        private static void Point(double radiusM, double alongM, double acrossM, out double x, out double y, out double z)
        {
            // A patch on the equator of the map frame: along runs east, across north; small angles on the sphere.
            double a = alongM / radiusM, c = acrossM / radiusM;
            x = radiusM * Math.Cos(a) * Math.Cos(c);
            y = radiusM * Math.Sin(c);
            z = radiusM * Math.Sin(a) * Math.Cos(c);
        }

        private static double DensityAt(NoiseVolumeSet set, CloudLayerDef layer, LayerTypeTable table, int type, double bodyR, double alongM, double acrossM, double alt)
        {
            double x, y, z;
            Point(bodyR + alt, alongM, acrossM, out x, out y, out z);
            return Density(set, layer, table, type, x, y, z, alt);
        }

        // The top's height map as seen from above in a low sun: Lambert shading of the top surface, the sun 20 degrees high from the left, 0.5 = flat. The relief is what a
        // deck's form looks like from above where its optical depth is saturated (the top view is white there).
        private static double[] Relief(double[] height, double cellM)
        {
            var shade = new double[TopSize * TopSize];
            double sunEl = 20.0 * Math.PI / 180.0;
            double lx = -Math.Cos(sunEl), ly = Math.Sin(sunEl), flat = ly;
            for (int j = 0; j < TopSize; j++)
                for (int i = 0; i < TopSize; i++)
                {
                    int i0 = Math.Max(0, i - 1), i1 = Math.Min(TopSize - 1, i + 1);
                    int j0 = Math.Max(0, j - 1), j1 = Math.Min(TopSize - 1, j + 1);
                    double dx = (height[j * TopSize + i1] - height[j * TopSize + i0]) / ((i1 - i0) * cellM);
                    double dz = (height[j1 * TopSize + i] - height[j0 * TopSize + i]) / ((j1 - j0) * cellM);
                    double n = Math.Sqrt(dx * dx + dz * dz + 1.0);
                    double lambert = Math.Max(0.0, (-dx * lx + ly) / n);
                    shade[j * TopSize + i] = Math.Min(1.0, 0.5 * lambert / flat);
                }
            return shade;
        }

        private static void Write(string folder, NoiseVolumeSet set, CloudLayerDef layer, LayerTypeTable table, int type, bool pannus, StringBuilder text)
        {
            const double bodyR = 600000.0;
            double inner = layer.InnerAltitudeM, outer = layer.OuterAltitudeM;
            int rows = Math.Max(60, (int)Math.Round(Columns * (outer - inner) / AlongM * 4.0));   // the height drawn 4 times stretched
            var slice = new double[rows * Columns];
            double max = 1e-12;
            for (int r = 0; r < rows; r++)
            {
                double alt = outer - (r + 0.5) / rows * (outer - inner);
                for (int c = 0; c < Columns; c++)
                {
                    double x, y, z;
                    Point(bodyR + alt, (c + 0.5) / Columns * AlongM, 0.0, out x, out y, out z);
                    double d = Density(set, layer, table, type, x, y, z, alt);
                    slice[r * Columns + c] = d;
                    if (d > max) max = d;
                }
            }
            string name = layer.Name + "-" + layer.Types[type].Name.Replace(' ', '_') + (pannus ? "-pannus" : "");
            WritePgm(Path.Combine(folder, "slice-" + name + ".pgm"), Columns, rows, slice.Select(v => Math.Sqrt(v / max)).ToArray());

            // From above: the column's optical depth and the top's height (the highest altitude with density above 2 % of the slice's peak).
            int steps = 48;
            var tau = new double[TopSize * TopSize];
            var topMap = new double[TopSize * TopSize];
            var tops = new System.Collections.Generic.List<double>();
            double stepM = (outer - inner) / steps;
            for (int j = 0; j < TopSize; j++)
                for (int i = 0; i < TopSize; i++)
                {
                    double sum = 0.0, top = double.NaN;
                    double px = (i + 0.5) / TopSize * AlongM, pz = (j + 0.5) / TopSize * AlongM;
                    for (int k = 0; k < steps; k++)
                    {
                        double alt = outer - (k + 0.5) / steps * (outer - inner);
                        double d = DensityAt(set, layer, table, type, bodyR, px, pz, alt);
                        if (double.IsNaN(top) && d > 0.02 * max)
                        {
                            // The top to a metre: bisected between this step and the empty one above it.
                            double lo = alt, hi = Math.Min(outer, alt + stepM);
                            for (int n = 0; n < 8; n++)
                            {
                                double mid = 0.5 * (lo + hi);
                                if (DensityAt(set, layer, table, type, bodyR, px, pz, mid) > 0.02 * max) lo = mid; else hi = mid;
                            }
                            top = lo;
                        }
                        sum += d * stepM;
                    }
                    tau[j * TopSize + i] = 1.0 - Math.Exp(-sum);
                    topMap[j * TopSize + i] = double.IsNaN(top) ? inner : top;
                    if (!double.IsNaN(top)) tops.Add(top);
                }
            WritePgm(Path.Combine(folder, "top-" + name + ".pgm"), TopSize, TopSize, tau);
            WritePgm(Path.Combine(folder, "relief-" + name + ".pgm"), TopSize, TopSize, Relief(topMap, AlongM / TopSize));

            // The bands: the type's height in tenths, the share of the slice's columns with density there and the mean density where there is some.
            double b, t;
            table.InterpolatedHeights(table.Count > 1 ? type / (double)(table.Count - 1) : 0.0, out b, out t);
            var line = new StringBuilder(layer.Name + " " + layer.Types[type].Name + " " + (pannus ? 1 : 0) + " |");
            for (int band = 0; band < 10; band++)
            {
                double ha = band / 10.0, hb = (band + 1) / 10.0;
                int filled = 0, count = 0;
                double dsum = 0.0;
                for (int r = 0; r < rows; r++)
                {
                    double alt = outer - (r + 0.5) / rows * (outer - inner);
                    double share = (alt - inner) / (outer - inner);
                    double h = (share - b) / (t - b);
                    if (h < ha || h >= hb) continue;
                    for (int c = 0; c < Columns; c++)
                    {
                        double d = slice[r * Columns + c];
                        count++;
                        if (d > 0.02 * max) { filled++; dsum += d; }
                    }
                }
                line.Append(string.Format(CultureInfo.InvariantCulture, " {0:0.0}:{1:0.00}/{2:0.0e0}", ha, count > 0 ? filled / (double)count : 0.0, filled > 0 ? dsum / filled : 0.0));
            }
            tops.Sort();
            if (tops.Count > 0)
                line.Append(string.Format(CultureInfo.InvariantCulture, " | {0:0} {1:0}", tops[(int)(0.1 * (tops.Count - 1))], tops[(int)(0.9 * (tops.Count - 1))]));
            text.AppendLine(line.ToString());
        }

        private static void WritePgm(string path, int w, int h, double[] values)
        {
            using (var f = new FileStream(path, FileMode.Create))
            {
                byte[] head = Encoding.ASCII.GetBytes("P5\n" + w + " " + h + "\n255\n");
                f.Write(head, 0, head.Length);
                var px = new byte[w * h];
                for (int i = 0; i < px.Length; i++) px[i] = (byte)Math.Round(255.0 * Math.Max(0.0, Math.Min(1.0, values[i])));
                f.Write(px, 0, px.Length);
            }
        }
    }
}
