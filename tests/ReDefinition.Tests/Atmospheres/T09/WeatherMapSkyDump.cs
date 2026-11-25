using System;
using System.Globalization;
using System.IO;
using AtmoSim.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.C25;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // WM1 (master plan section 26): the layer maps of Kerbin at given times twice, as the game's module makes them today (the FetchStep chain of SkyMapsExplore, plus the module's anvils)
    // and from the package B column quantities (WeatherMapSky), with the column quantities themselves, for tools/weather_metrics/wm1.py. Runs only when
    // WMSKY_OUT names a directory.
    //   WMSKY_UTS          the universal times, comma separated (default 1486800)
    //   WMSKY_SEED         the body's seed (default 664216370, SkyMapsExplore's: the game log's "body Kerbin ... seed")
    //   WMSKY_HEIGHT_ROOT  the root range of the height maps in m (the game's heightRoot of the layers, default 0: no root offset)
    // Per time u<ut>_: today_<layer>_cover / _type, wm_<layer>_cover / _type / _root (u8, 512 x 256, row 0 north), col_<quantity> (f32), wm_cumuliformDropped (f32), wm_sheetLevels (u8).
    [TestClass]
    public class WeatherMapSkyDump
    {
        private static void Bytes(string folder, string name, byte[] data)
        {
            File.WriteAllBytes(Path.Combine(folder, name + ".u8"), data);
        }

        private static void Floats(string folder, string name, float[] data)
        {
            var bytes = new byte[data.Length * 4];
            Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(Path.Combine(folder, name + ".f32"), bytes);
        }

        private static WeatherMap LayerMap(GeneratorField field, int body, double ut, double heightM)
        {
            var map = new WeatherMap();
            field.GetLayerCoverage(body, ut, heightM, map);
            field.Maps.RenderLayerNow(body, (int)Math.Round(Math.Max(0.0, heightM) / GeneratorMaps.LayerStepM));
            Assert.IsTrue(field.GetLayerCoverage(body, ut, heightM, map));
            map.PackBytes(0f, 1f);
            return map;
        }

        // The anvil inputs as the module takes them (OwnCloudModule.TakeAnvilInputs).
        private static AnvilInputs Anvil(GeneratorField field, int body, CloudLayerDef d, int w, int h)
        {
            double shear = 0.0;
            if (d.AnvilType >= 0f)
            {
                double mid = CloudBridgeStack.MidHeightM(d);
                shear = field.CloudDriftRadS(body, mid + 0.5 * d.ThicknessM) - field.CloudDriftRadS(body, mid);
            }
            return new AnvilInputs { Width = w, Height = h, RadiusM = 600000.0, ShearRadS = shear, UpdraftMs = field.CellUpdraftMsOf(body) };
        }

        [TestMethod, TestCategory("Explore")]
        public void DumpTheWeatherMapSky()
        {
            string folder = Environment.GetEnvironmentVariable("WMSKY_OUT");
            if (string.IsNullOrEmpty(folder)) Assert.Inconclusive("A measurement run: it runs only when WMSKY_OUT is set.");
            Directory.CreateDirectory(folder);
            string utText = Environment.GetEnvironmentVariable("WMSKY_UTS") ?? "1486800";
            string seedText = Environment.GetEnvironmentVariable("WMSKY_SEED");
            int seed = string.IsNullOrEmpty(seedText) ? 664216370 : int.Parse(seedText, CultureInfo.InvariantCulture);
            string rootText = Environment.GetEnvironmentVariable("WMSKY_HEIGHT_ROOT");
            float rootM = string.IsNullOrEmpty(rootText) ? 0f : float.Parse(rootText, CultureInfo.InvariantCulture);
            LayerWindOptions.SteeringOverride = LayerSteeringMode.MapsOnly;
            try
            {
                BodyClimate climate;
                GeneratorField field = SkyMapsExplore.KerbinField(seed, out climate);
                CloudLayerDef[] layers = TypeMixFixture.ShippedLayers();
                int body = FieldFixture.BodyIndex;
                GeneratorState state = field.StateOf(body);
                WeatherGenerator g = state.Generator;
                int w = GeneratorMaps.Width, h = GeneratorMaps.Height, n = w * h;
                var roots = new float[layers.Length];
                for (int l = 0; l < layers.Length; l++) roots[l] = rootM;
                foreach (string part in utText.Split(','))
                {
                    double ut = double.Parse(part, CultureInfo.InvariantCulture);
                    string tag = "u" + ((long)ut).ToString(CultureInfo.InvariantCulture) + "_";
                    TypeMixFixture.Shared s = TypeMixFixture.Maps(field, body, ut);
                    var temperature = new WeatherMap();
                    var wind = new WeatherMap();
                    Assert.IsTrue(field.GetMap(body, WeatherMapKind.SurfaceTemperature, ut, temperature));
                    Assert.IsTrue(field.GetMap(body, WeatherMapKind.WindSpeed, ut, wind));
                    var spread = new float[n];
                    for (int i = 0; i < n; i++) spread[i] = WeatherLowStratiformRule.SpreadK(temperature.Values[i], s.Humidity.Values[i]);
                    // Today: SkyMapsExplore's chain (layer map, low stratiform rule or fair type with the alto rule, meso stage, the combination with the field's cover), then the anvils.
                    var combined = new byte[layers.Length][];
                    var types = new byte[layers.Length][];
                    var rules = new WeatherLowStratiformRule[layers.Length];
                    byte[] fogCover = null, fogType = null;
                    for (int li = 0; li < layers.Length; li++)
                    {
                        CloudLayerDef l = layers[li];
                        rules[li] = l.LowStratiform;
                        WeatherMap raw = LayerMap(field, body, ut, CloudBridgeStack.MidHeightM(l));
                        byte[] cover = raw.Bytes, type;
                        byte[] altoMid = null, altoLow = null;
                        if (l.LowStratiform != null)
                        {
                            var rc = new byte[n];
                            var rt = new byte[n];
                            l.LowStratiform.Apply(cover, s.Precip.Values, spread, wind.Values, rc, rt);
                            if (WeatherMapSky.RoleOf(l) == WeatherMapSky.Role.LowStratiform && fogCover == null)
                            {
                                fogCover = new byte[n];
                                fogType = new byte[n];
                                l.LowStratiform.Apply(raw.Bytes, s.Precip.Values, spread, wind.Values, fogCover, fogType, 0f);
                            }
                            float lowStratiformRain = l.RainType;
                            if (lowStratiformRain < 1f)
                                for (int i = 0; i < n; i++) rt[i] = (byte)Math.Round(rt[i] * lowStratiformRain);
                            cover = rc;
                            type = rt;
                        }
                        else
                        {
                            byte[] scratch = null, altoScratch = null;
                            type = CloudLookTuning.TypeBytes(s.Precip.Bytes, l.FairType, -1f, ref scratch, l.RainType);
                            if (l.AltoType >= 0f)
                            {
                                altoMid = LayerMap(field, body, ut, l.TypeMidHeightM(l.AltoType)).Bytes;
                                altoLow = LayerMap(field, body, ut, l.TypeMidHeightM(l.FairType >= 0f ? l.FairType : 0f)).Bytes;
                                if (!l.HasClimate) type = CloudLookTuning.ApplyAlto(type, s.Precip.Bytes, altoMid, altoLow, l.AltoType, l.AltoFrom, l.AltoFull, ref altoScratch);
                            }
                        }
                        var inp = new CloudMesoInputs
                        {
                            Width = w, Height = h, Precip = s.Precip.Bytes, Humidity = s.Humidity.Bytes, Surface = s.Surface.Bytes,
                            AltoMid = altoMid, AltoLow = altoLow, Ut = ut, DriftRadS = field.CloudDriftRadS(body, CloudBridgeStack.MidHeightM(l)), Seed = field.SeedOf(body)
                        };
                        Assert.IsTrue(field.CloudScalesOf(body, inp), "the field's body has its scales");
                        byte[] coverOut = null, typeOut = null;
                        if ((l.Meso != null && l.Meso.On) || (l.HasClimate && l.LowStratiform == null)) CloudMesoStage.Apply(l, inp, cover, type, ref coverOut, ref typeOut);
                        else { coverOut = cover; typeOut = type; }
                        combined[li] = (byte[])coverOut.Clone();
                        types[li] = (byte[])typeOut.Clone();
                    }
                    CloudLookTuning.MeetTheField(combined, layers.Length, s.Cover.Bytes, CloudLookTuning.CoverCombine, rules, s.Precip.Values);
                    for (int li = 0; li < layers.Length; li++) AnvilMap.Apply(layers[li], combined[li], types[li], Anvil(field, body, layers[li], w, h));
                    // The column quantities (GeneratorMaps.RenderWeather's samples, with the quantities the maps do not keep).
                    var c = new WeatherMapSky.Columns
                    {
                        Width = w, Height = h, Cover = new float[n], Cumuliform = new float[n], Stratiform = new float[n], LowStratiform = new float[n], BaseM = new float[n], TopM = new float[n],
                        RhLow = new float[n], RhMid = new float[n], RhHigh = new float[n], PrecipMmH = new float[n]
                    };
                    float[] reach = new float[n], stable = new float[n], warm = new float[n], cold = new float[n], inst = new float[n];
                    for (int j = 0; j < h; j++)
                    {
                        double lat = (90.0 - (j + 0.5) * 180.0 / h) * (Math.PI / 180.0);
                        for (int i = 0; i < w; i++)
                        {
                            double lon = (-180.0 + (i + 0.5) * 360.0 / w) * (Math.PI / 180.0);
                            Weather wx;
                            g.Sample(lat, lon, 0.0, ut, out wx, true);
                            SkyColumn col;
                            SkyColumn.Derive(g.Body, ref wx, out col);
                            int k = j * w + i;
                            c.Cover[k] = (float)wx.Coverage;
                            c.Cumuliform[k] = (float)col.CumuliformAmount;
                            c.Stratiform[k] = (float)col.StratiformAmount;
                            c.LowStratiform[k] = (float)col.LowStratiformAmount;
                            c.BaseM[k] = (float)col.BaseM;
                            c.TopM[k] = (float)col.TopM;
                            c.RhLow[k] = (float)col.RhLow;
                            c.RhMid[k] = (float)col.RhMid;
                            c.RhHigh[k] = (float)col.RhHigh;
                            c.PrecipMmH[k] = (float)wx.PrecipMmH;
                            reach[k] = (float)wx.CellReach01;
                            stable[k] = (float)col.StableM;
                            warm[k] = (float)col.WarmFront01;
                            cold[k] = (float)col.ColdFront01;
                            inst[k] = (float)wx.Instability;
                        }
                    }
                    // Today's chain's maps, then the maps from the columns.
                    for (int li = 0; li < layers.Length; li++)
                    {
                        Bytes(folder, tag + "today_" + layers[li].Name + "_cover", combined[li]);
                        Bytes(folder, tag + "today_" + layers[li].Name + "_type", types[li]);
                    }
                    WeatherMapSky.Result r = WeatherMapSky.Build(layers, c, types, fogCover, fogType, roots);
                    for (int li = 0; li < layers.Length; li++)
                    {
                        Bytes(folder, tag + "wm_" + layers[li].Name + "_cover", r.Cover[li]);
                        Bytes(folder, tag + "wm_" + layers[li].Name + "_type", r.Type[li]);
                        if (r.Root[li] != null) Bytes(folder, tag + "wm_" + layers[li].Name + "_root", r.Root[li]);
                    }
                    Floats(folder, tag + "wm_cumuliformDropped", r.CumuliformDropped);
                    Bytes(folder, tag + "wm_sheetLevels", r.StratiformLevels);
                    Floats(folder, tag + "col_cover", c.Cover);
                    Floats(folder, tag + "col_cumuliform", c.Cumuliform);
                    Floats(folder, tag + "col_sheet", c.Stratiform);
                    Floats(folder, tag + "col_lowstratiform", c.LowStratiform);
                    Floats(folder, tag + "col_base", c.BaseM);
                    Floats(folder, tag + "col_top", c.TopM);
                    Floats(folder, tag + "col_rhLow", c.RhLow);
                    Floats(folder, tag + "col_rhMid", c.RhMid);
                    Floats(folder, tag + "col_rhHigh", c.RhHigh);
                    Floats(folder, tag + "col_precip", c.PrecipMmH);
                    Floats(folder, tag + "col_reach", reach);
                    Floats(folder, tag + "col_stable", stable);
                    Floats(folder, tag + "col_warm", warm);
                    Floats(folder, tag + "col_cold", cold);
                    Floats(folder, tag + "col_inst", inst);
                    Bytes(folder, tag + "fieldCover", s.Cover.Bytes);
                    Bytes(folder, tag + "precipLevel", s.Precip.Bytes);
                    Console.WriteLine("WMSKY ut " + ut + " done");
                }
                using (var meta = new StreamWriter(Path.Combine(folder, "meta.txt")))
                {
                    meta.WriteLine("width " + w + " height " + h + " seed " + seed + " heightRoot " + rootM.ToString(CultureInfo.InvariantCulture) + " tropopause " + g.Body.TropopauseM.ToString(CultureInfo.InvariantCulture));
                    foreach (CloudLayerDef l in layers)
                    {
                        meta.Write("layer " + l.Name + " role " + WeatherMapSky.RoleOf(l) + " inner " + l.InnerAltitudeM + " outer " + l.OuterAltitudeM + " types");
                        foreach (CloudTypeDef t in l.Types) meta.Write(" '" + t.Name + "' " + t.BaseShare + "-" + t.TopShare);
                        meta.WriteLine();
                    }
                }
            }
            finally
            {
                Tuning.ClearOverrides();
                LayerWindOptions.SteeringOverride = null;
            }
        }
    }
}
