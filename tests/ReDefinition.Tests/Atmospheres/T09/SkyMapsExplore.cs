using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.B13;
using ReDefinition.Tests.Atmospheres.C25;
using ReDefinition.Tests.Atmospheres.E41;
using ReDefinition.Tests.Atmospheres.T10;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // E40 F1: the maps the GAME hands to the layer pass for Kerbin at given times, with the game's own seed, Kerbin's shipped rules and the module's FetchStep chain (the low stratiform rule with
    // its fog air, the meso stage), dumped as raw bytes for tools/weather_metrics/e40f1.py and tools/e40_sky_clear.py. Runs only when SKYMAPS_OUT names a directory.
    //   SKYMAPS_UTS   the universal times, comma separated (default 1881000,1686000)
    //   SKYMAPS_SEED  the body's seed (default 664216370, the game log's "body Kerbin ... seed")
    // Per time: shared cover (the field), precipitation, humidity; per layer of the stack: the generator's own layer map (raw) and the cover and type after the module's rules (cover, type).
    [TestClass]
    public class SkyMapsExplore
    {
        private static WeatherMap LayerMap(GeneratorField field, int body, double ut, double heightM)
        {
            var map = new WeatherMap();
            field.GetLayerCoverage(body, ut, heightM, map);
            field.Maps.RenderLayerNow(body, (int)Math.Round(Math.Max(0.0, heightM) / GeneratorMaps.LayerStepM));
            Assert.IsTrue(field.GetLayerCoverage(body, ut, heightM, map));
            map.PackBytes(0f, 1f);
            return map;
        }

        internal static GeneratorField KerbinField(int seed, out BodyClimate climate)
        {
            BodyFacts facts = FieldFixture.HomeFacts("Kerbin");
            BodyClimate parsed = CloudLayerSchemaTests.ParseKerbin().Climate;
            climate = new BodyClimate("Kerbin", FieldFixture.BodyIndex, 600000.0, FieldFixture.HomeYear, 21549.425, BodyStatus.Full, seed,
                parsed.Seasons, parsed.Temperature, parsed.Precip, parsed.Covers, parsed.Ice, parsed.Generator, parsed.Clouds, parsed.Look, parsed.Sound, parsed.Wind, parsed.Gameplay,
                parsed.Aurora, parsed.LightMedium, parsed.Frost, parsed.Forecast);
            var field = new GeneratorField();
            field.Say = s => { };
            field.Maps.Synchronous = true;
            field.SetState(FieldFixture.State(climate, FieldFixture.Params(climate, facts, "Kerbin")));
            return field;
        }

        private static void Dump(string folder, string name, byte[] data)
        {
            File.WriteAllBytes(Path.Combine(folder, name + ".u8"), data);
        }

        // SKYMAPS_TABLES=<file>: the model's shares per layer, per map value byte (rows) and type value in 1/16ths (columns): "<layer> visible|closed" then 256 lines of 17 numbers.
        [TestMethod, TestCategory("Explore")]
        public void DumpTheSkyClearTables()
        {
            string file = Environment.GetEnvironmentVariable("SKYMAPS_TABLES");
            if (string.IsNullOrEmpty(file)) Assert.Inconclusive("A measurement run: it runs only when SKYMAPS_TABLES is set.");
            Tuning.Instance.BeginFrame(9000001);
            using (var w = new StreamWriter(file))
            {
                foreach (CloudLayerDef l in TypeMixFixture.ShippedLayers())
                {
                    var table = new LayerTypeTable(l);
                    var visible = new double[256, 17];
                    var closed = new double[256, 17];
                    for (int c = 0; c < 256; c++)
                        for (int t = 0; t <= 16; t++) SkyClearModel.Shares(l, table, c / 255.0, t / 16.0, out visible[c, t], out closed[c, t]);
                    foreach (string kind in new[] { "visible", "closed" })
                    {
                        w.WriteLine(l.Name + " " + kind);
                        for (int c = 0; c < 256; c++)
                        {
                            var cells = new string[17];
                            for (int t = 0; t <= 16; t++) cells[t] = (kind == "visible" ? visible[c, t] : closed[c, t]).ToString("0.###", CultureInfo.InvariantCulture);
                            w.WriteLine(string.Join(" ", cells));
                        }
                    }
                }
            }
            Tuning.ClearOverrides();
            Tuning.Instance.BeginFrame(9000002);
        }

        // THE SKY FROM THE WEATHER MAP, offline (2026-11-28): the layer covers and types the game's map sky draws for Kerbin at given times, by the module's own chain
        // (OwnCloudModule.FetchMapSky and FetchLayer: the package B maps, MapSkyRegions.Plan and Layer, the low stratiform rule's rain and fog part, the meso stage's cover
        // pattern), dumped as raw bytes for tools/weather_metrics/mapsky_preview.py. Runs only when MAPSKY_OUT names a directory; MAPSKY_UTS and SKYMAPS_SEED as above.
        // Per time: <tag>cover, precip, the parts (rising, sc, ac, low, high, tall), and per layer <name>_plan (before the pattern), _cover, _type.
        [TestMethod, TestCategory("Explore")]
        public void DumpTheMapSkyOfKerbin()
        {
            string folder = Environment.GetEnvironmentVariable("MAPSKY_OUT");
            if (string.IsNullOrEmpty(folder)) Assert.Inconclusive("A measurement run: it runs only when MAPSKY_OUT is set.");
            Directory.CreateDirectory(folder);
            string utText = Environment.GetEnvironmentVariable("MAPSKY_UTS") ?? "1486800,1767600";
            string seedText = Environment.GetEnvironmentVariable("SKYMAPS_SEED");
            int seed = string.IsNullOrEmpty(seedText) ? 664216370 : int.Parse(seedText, CultureInfo.InvariantCulture);
            LayerWindOptions.SteeringOverride = LayerSteeringMode.MapsOnly;
            try
            {
                BodyClimate climate;
                GeneratorField field = KerbinField(seed, out climate);
                CloudLayerDef[] layers = TypeMixFixture.ShippedLayers();
                TrialMeso(layers, Environment.GetEnvironmentVariable("MAPSKY_MESO"));
                int body = FieldFixture.BodyIndex;
                WeatherMapKind[] kinds =
                {
                    WeatherMapKind.ConvectiveAmount, WeatherMapKind.StratiformAmount, WeatherMapKind.LowStratiformAmount, WeatherMapKind.ConvectiveBase, WeatherMapKind.ConvectiveTop,
                    WeatherMapKind.StableShare, WeatherMapKind.HumidityLow, WeatherMapKind.HumidityMid, WeatherMapKind.HumidityHigh, WeatherMapKind.CellReach
                };
                foreach (string part in utText.Split(','))
                {
                    double ut = double.Parse(part, CultureInfo.InvariantCulture);
                    string tag = "u" + ((long)ut).ToString(CultureInfo.InvariantCulture) + "_";
                    TypeMixFixture.Shared s = TypeMixFixture.Maps(field, body, ut);
                    var maps = new WeatherMap[kinds.Length];
                    for (int k = 0; k < kinds.Length; k++)
                    {
                        maps[k] = new WeatherMap();
                        Assert.IsTrue(field.GetMap(body, kinds[k], ut, maps[k]), "the field gives " + kinds[k]);
                    }
                    var temperature = new WeatherMap();
                    var wind = new WeatherMap();
                    Assert.IsTrue(field.GetMap(body, WeatherMapKind.SurfaceTemperature, ut, temperature));
                    Assert.IsTrue(field.GetMap(body, WeatherMapKind.WindSpeed, ut, wind));
                    int n = s.Cover.Values.Length;
                    var spread = new float[n];
                    for (int i = 0; i < n; i++) spread[i] = WeatherLowStratiformRule.SpreadK(temperature.Values[i], s.Humidity.Values[i]);
                    var m0 = new MapSkyRegions.Inputs
                    {
                        Width = GeneratorMaps.Width, Height = GeneratorMaps.Height, Cover = s.Cover.Values, Cumuliform = maps[0].Values, Stratiform = maps[1].Values,
                        LowStratiform = maps[2].Values, BaseShare = maps[3].Values, TopShare = maps[4].Values, StableShare = maps[5].Values, RhLow = maps[6].Values,
                        RhMid = maps[7].Values, RhHigh = maps[8].Values, Reach = maps[9].Values, PrecipLevel = s.Precip.Values,
                        TropopauseM = field.StateOf(body).Parameters.TropopauseM, Ut = ut
                    };
                    CloudLayerDef cumuliform = null, tall = null;
                    foreach (CloudLayerDef l in layers)
                    {
                        WeatherMapSky.Role role = WeatherMapSky.RoleOf(l);
                        if (role == WeatherMapSky.Role.Cumuliform && cumuliform == null) cumuliform = l;
                        if (role == WeatherMapSky.Role.Tall && tall == null) tall = l;
                    }
                    MapSkyRegions.Parts p = MapSkyRegions.Plan(m0, cumuliform, tall);
                    Dump(folder, tag + "cover", s.Cover.Bytes);
                    Dump(folder, tag + "precip", s.Precip.Bytes);
                    DumpShares(folder, tag + "part_rising", p.Rising);
                    DumpShares(folder, tag + "part_sc", p.Sc);
                    DumpShares(folder, tag + "part_ac", p.Ac);
                    DumpShares(folder, tag + "part_low", p.Low);
                    DumpShares(folder, tag + "part_high", p.High);
                    DumpShares(folder, tag + "part_tall", p.Tall);
                    DumpShares(folder, tag + "rh_low", maps[6].Values);
                    DumpShares(folder, tag + "rh_mid", maps[7].Values);
                    DumpShares(folder, tag + "rh_high", maps[8].Values);
                    DumpShares(folder, tag + "stratiform", maps[1].Values);
                    DumpShares(folder, tag + "cumuliform", maps[0].Values);
                    foreach (CloudLayerDef l in layers)
                    {
                        WeatherMapSky.Role role = WeatherMapSky.RoleOf(l);
                        if (role == WeatherMapSky.Role.None) continue;
                        byte[] scratch = null;
                        byte[] todayType = CloudLookTuning.TypeBytes(s.Precip.Bytes, l.FairType, -1f, ref scratch, l.RainType);
                        byte[] fogC = null, fogT = null;
                        if (role == WeatherMapSky.Role.LowStratiform && l.LowStratiform != null)
                        {
                            WeatherMap raw = LayerMap(field, body, ut, CloudBridgeStack.MidHeightM(l));
                            fogC = new byte[n];
                            fogT = new byte[n];
                            l.LowStratiform.Apply(raw.Bytes, s.Precip.Values, spread, wind.Values, fogC, fogT);
                            if (l.RainType < 1f)
                                for (int i = 0; i < n; i++) fogT[i] = (byte)Math.Round(fogT[i] * l.RainType);
                        }
                        var cover = new byte[n];
                        var type = new byte[n];
                        Assert.IsTrue(MapSkyRegions.Layer(l, role, p, todayType, fogC, fogT, cover, type), "the map drives " + l.Name);
                        Dump(folder, tag + l.Name + "_plan", cover);
                        byte[] coverOut = cover, typeOut = type;
                        if (l.Meso != null && l.Meso.On)
                        {
                            var inp = new CloudMesoInputs
                            {
                                Width = GeneratorMaps.Width, Height = GeneratorMaps.Height, Precip = s.Precip.Bytes, Humidity = s.Humidity.Bytes, Surface = s.Surface.Bytes,
                                Ut = ut, DriftRadS = field.CloudDriftRadS(body, CloudBridgeStack.MidHeightM(l)), Seed = field.SeedOf(body)
                            };
                            Assert.IsTrue(field.CloudScalesOf(body, inp), "the field's body has its scales");
                            coverOut = null;
                            typeOut = null;
                            CloudMesoStage.Apply(l, inp, cover, type, ref coverOut, ref typeOut, true);
                        }
                        Dump(folder, tag + l.Name + "_cover", coverOut);
                        Dump(folder, tag + l.Name + "_type", typeOut);
                    }
                }
                using (var meta = new StreamWriter(Path.Combine(folder, "meta.txt")))
                {
                    meta.WriteLine("width " + GeneratorMaps.Width + " height " + GeneratorMaps.Height + " seed " + seed);
                    foreach (CloudLayerDef l in layers) meta.WriteLine("layer " + l.Name + " role " + WeatherMapSky.RoleOf(l) + " sigmaPerM " + l.SigmaPerM.ToString("R", CultureInfo.InvariantCulture));
                }
            }
            finally
            {
                LayerWindOptions.SteeringOverride = null;
            }
        }

        // Trial values of the layers' MESO keys for a dump, without touching the body file: MAPSKY_MESO = "<layer name part>:key=value,key=value;<layer name part>:..." (a layer
        // whose name contains the part takes the values; the keys are the cfg's).
        private static void TrialMeso(CloudLayerDef[] layers, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (string block in text.Split(';'))
            {
                int colon = block.IndexOf(':');
                if (colon < 0) continue;
                string part = block.Substring(0, colon).Trim();
                foreach (CloudLayerDef l in layers)
                {
                    if (l.Meso == null || l.Name.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    foreach (string pair in block.Substring(colon + 1).Split(','))
                    {
                        string[] kv = pair.Split('=');
                        if (kv.Length != 2) continue;
                        float v = float.Parse(kv[1].Trim(), CultureInfo.InvariantCulture);
                        CloudMesoSpec m = l.Meso;
                        switch (kv[0].Trim())
                        {
                            case "scaleDepths": m.ScaleDepths = v; break;
                            case "scaleKm": m.ScaleKm = v; break;
                            case "scaleSystems": m.ScaleSystems = v; break;
                            case "octaves": m.Octaves = (int)v; break;
                            case "amount": m.Amount = v; break;
                            case "edge": m.Edge = v; break;
                            case "shoulder": m.Shoulder = v; break;
                            case "cells": m.Cells = v > 0f; break;
                            case "stretch": m.Stretch = v; break;
                            case "rainKeep": m.RainKeep = v; break;
                            case "rainMin": m.RainMin = v; break;
                            case "scatter": m.Scatter = v; break;
                            case "scatterDepths": m.ScatterDepths = v; break;
                            case "scatterSoft": m.ScatterSoft = v; break;
                            case "rainKeepFrom": m.RainKeepFrom = v; break;
                            case "rainKeepFullAt": m.RainKeepFullAt = v; break;
                            case "rainMinFrom": m.RainMinFrom = v; break;
                            case "rainMinFullAt": m.RainMinFullAt = v; break;
                            // the low stratiform rule's keys (lowStratiformPrecipFrom and so on), for a layer that has the rule
                            case "precipFrom": if (l.LowStratiform != null) l.LowStratiform.PrecipFrom = v; break;
                            case "precipFullAt": if (l.LowStratiform != null) l.LowStratiform.PrecipFullAt = v; break;
                            case "rainFloor": if (l.LowStratiform != null) l.LowStratiform.RainFloor = v; break;
                            case "rainFloorFullAt": if (l.LowStratiform != null) l.LowStratiform.RainFloorFullAt = v; break;
                            default: Assert.Fail("MAPSKY_MESO: no key " + kv[0]); break;
                        }
                    }
                }
            }
        }

        private static void DumpShares(string folder, string name, float[] values)
        {
            var bytes = new byte[values.Length];
            for (int i = 0; i < values.Length; i++) bytes[i] = (byte)Math.Min(255.0, Math.Max(0.0, Math.Round(values[i] * 255.0)));
            Dump(folder, name, bytes);
        }

        [TestMethod, TestCategory("Explore")]
        public void DumpTheGameMapsOfKerbin()
        {
            string folder = Environment.GetEnvironmentVariable("SKYMAPS_OUT");
            if (string.IsNullOrEmpty(folder)) Assert.Inconclusive("A measurement run: it runs only when SKYMAPS_OUT is set.");
            Directory.CreateDirectory(folder);
            string utText = Environment.GetEnvironmentVariable("SKYMAPS_UTS") ?? "1881000,1686000";
            string seedText = Environment.GetEnvironmentVariable("SKYMAPS_SEED");
            int seed = string.IsNullOrEmpty(seedText) ? 664216370 : int.Parse(seedText, CultureInfo.InvariantCulture);
            LayerWindOptions.SteeringOverride = LayerSteeringMode.MapsOnly;
            try
            {
                BodyClimate climate;
                GeneratorField field = KerbinField(seed, out climate);
                // the stack as the game builds it from the shipped cfg (low stratiform rain layer, cumulus, cumulonimbus, cirrus; the low stratiform snow layer is its alternative)
                CloudLayerDef[] layers = TypeMixFixture.ShippedLayers();
                int body = FieldFixture.BodyIndex;
                foreach (string part in utText.Split(','))
                {
                    double ut = double.Parse(part, CultureInfo.InvariantCulture);
                    string tag = "u" + ((long)ut).ToString(CultureInfo.InvariantCulture) + "_";
                    TypeMixFixture.Shared s = TypeMixFixture.Maps(field, body, ut);
                    WeatherSample site;
                    field.Sample(body, -0.0972, -74.5577, 264.0, ut, out site);
                    Console.WriteLine("SITE ut " + ut + ": precipitation level " + site.Level01.ToString("0.000", CultureInfo.InvariantCulture) + ", cover " + site.CloudCover01.ToString("0.000", CultureInfo.InvariantCulture)
                        + ", tau " + site.OpticalDepthVertical.ToString("0.00", CultureInfo.InvariantCulture));
                    var temperature = new WeatherMap();
                    var wind = new WeatherMap();
                    Assert.IsTrue(field.GetMap(body, WeatherMapKind.SurfaceTemperature, ut, temperature));
                    Assert.IsTrue(field.GetMap(body, WeatherMapKind.WindSpeed, ut, wind));
                    int n = s.Cover.Values.Length;
                    var spread = new float[n];
                    for (int i = 0; i < n; i++) spread[i] = WeatherLowStratiformRule.SpreadK(temperature.Values[i], s.Humidity.Values[i]);
                    Dump(folder, tag + "sharedCover", s.Cover.Bytes);
                    Dump(folder, tag + "precip", s.Precip.Bytes);
                    Dump(folder, tag + "humidity", s.Humidity.Bytes);
                    var combined = new byte[layers.Length][];
                    var finalTypes = new byte[layers.Length][];
                    var rules = new WeatherLowStratiformRule[layers.Length];
                    for (int li = 0; li < layers.Length; li++)
                    {
                        CloudLayerDef l = layers[li];
                        rules[li] = l.LowStratiform;
                        // SKYMAPS_FALLBACK=1: the generator has made no layer map (GetLayerCoverage false): FetchStep gives every layer the shared coverage map and no mid-level sheet
                        bool fallback = Environment.GetEnvironmentVariable("SKYMAPS_FALLBACK") == "1";
                        WeatherMap raw = fallback ? s.Cover : LayerMap(field, body, ut, CloudBridgeStack.MidHeightM(l));
                        byte[] cover = raw.Bytes, type;
                        byte[] altoMid = null, altoLow = null;
                        if (l.LowStratiform != null)
                        {
                            var rc = new byte[n];
                            var rt = new byte[n];
                            l.LowStratiform.Apply(cover, s.Precip.Values, spread, wind.Values, rc, rt);
                            cover = rc;
                            type = rt;
                        }
                        else
                        {
                            byte[] scratch = null, altoScratch = null;
                            type = CloudLookTuning.TypeBytes(s.Precip.Bytes, l.FairType, -1f, ref scratch, l.RainType);
                            if (l.AltoType >= 0f && !fallback)
                            {
                                altoMid = LayerMap(field, body, ut, l.TypeMidHeightM(l.AltoType)).Bytes;
                                altoLow = LayerMap(field, body, ut, l.TypeMidHeightM(l.FairType >= 0f ? l.FairType : 0f)).Bytes;
                                if (!l.HasClimate) type = CloudLookTuning.ApplyAlto(type, s.Precip.Bytes, altoMid, altoLow, l.AltoType, l.AltoFrom, l.AltoFull, ref altoScratch);
                            }
                        }
                        Dump(folder, tag + l.Name + "_raw", raw.Bytes);
                        Dump(folder, tag + l.Name + "_lowstratiform", cover);
                        var inp = new CloudMesoInputs
                        {
                            Width = GeneratorMaps.Width, Height = GeneratorMaps.Height, Precip = s.Precip.Bytes, Humidity = s.Humidity.Bytes, Surface = s.Surface.Bytes,
                            AltoMid = altoMid, AltoLow = altoLow, Ut = ut, DriftRadS = field.CloudDriftRadS(body, CloudBridgeStack.MidHeightM(l)), Seed = field.SeedOf(body)
                        };
                        Assert.IsTrue(field.CloudScalesOf(body, inp), "the field's body has its scales");
                        byte[] coverOut = null, typeOut = null;
                        CloudMesoStage.Apply(l, inp, cover, type, ref coverOut, ref typeOut);
                        Dump(folder, tag + l.Name + "_nocombine", coverOut);
                        combined[li] = (byte[])coverOut.Clone();
                        finalTypes[li] = typeOut;
                    }
                    // the module's last step of a round (OwnCloudModule.FetchStep): the layer maps brought to the field's cover, with the product's default weight
                    CloudLookTuning.MeetTheField(combined, layers.Length, s.Cover.Bytes, CloudLookTuning.CoverCombine, rules, s.Precip.Values);
                    for (int li = 0; li < layers.Length; li++)
                    {
                        Dump(folder, tag + layers[li].Name + "_cover", combined[li]);
                        Dump(folder, tag + layers[li].Name + "_type", finalTypes[li]);
                    }
                }
                using (var meta = new StreamWriter(Path.Combine(folder, "meta.txt")))
                {
                    meta.WriteLine("width " + GeneratorMaps.Width + " height " + GeneratorMaps.Height + " seed " + seed);
                    foreach (CloudLayerDef l in layers)
                    {
                        meta.Write("layer " + l.Name + " types");
                        foreach (CloudTypeDef t in l.Types) meta.Write(" '" + t.Name + "'");
                        meta.WriteLine(" lowStratiformRule " + (l.LowStratiform != null));
                    }
                }
            }
            finally
            {
                LayerWindOptions.SteeringOverride = null;
            }
        }
    }
}
