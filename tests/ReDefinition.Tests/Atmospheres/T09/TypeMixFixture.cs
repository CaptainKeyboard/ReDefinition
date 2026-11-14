using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.C25;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The maps a layer of Kerbin's cfg gets from the generator and the module's own rules (OwnCloudModule.FetchStep), for the type-mix tests and their dump: the shared maps of a time, then per
    // layer its cover and type bytes, with the E40 L3 stage (CloudMesoStage) on or off. Fog is left out (no humidity input to the deck rule).
    internal static class TypeMixFixture
    {
        internal const double Day = 21549.425;

        internal sealed class Shared
        {
            internal readonly WeatherMap Cover = new WeatherMap(), Precip = new WeatherMap(), Humidity = new WeatherMap(), Surface = new WeatherMap();
            internal double Ut;
        }

        internal static Shared Maps(GeneratorField field, int body, double ut)
        {
            field.Maps.RenderNow(body, ut);
            var s = new Shared { Ut = ut };
            Assert.IsTrue(field.GetMap(body, WeatherMapKind.Coverage, ut, s.Cover));
            Assert.IsTrue(field.GetMap(body, WeatherMapKind.Precipitation, ut, s.Precip));
            Assert.IsTrue(field.GetMap(body, WeatherMapKind.Humidity, ut, s.Humidity));
            Assert.IsTrue(field.GetMap(body, WeatherMapKind.Surface, ut, s.Surface));
            s.Cover.PackBytes(0f, 1f);
            s.Precip.PackBytes(0f, 1f);
            s.Humidity.PackBytes(0f, 1f);
            s.Surface.PackBytes(0f, 1f);
            return s;
        }

        private static WeatherMap LayerAt(GeneratorField field, int body, double ut, double heightM)
        {
            var map = new WeatherMap();
            field.GetLayerCoverage(body, ut, heightM, map);
            field.Maps.RenderLayerNow(body, (int)Math.Round(Math.Max(0.0, heightM) / GeneratorMaps.LayerStepM));
            Assert.IsTrue(field.GetLayerCoverage(body, ut, heightM, map));
            map.PackBytes(0f, 1f);
            return map;
        }

        // One layer's cover and type bytes at the shared maps' time. stage false: the generator's maps and the old rule (the precipitation's curve and the blend towards the sheet).
        internal static void Layer(GeneratorField field, int body, Shared s, CloudLayerDef l, bool stage, out byte[] cover, out byte[] type)
        {
            WeatherMap layerMap = LayerAt(field, body, s.Ut, CloudBridgeStack.MidHeightM(l));
            cover = layerMap.Bytes;
            byte[] altoMid = null, altoLow = null;
            if (l.Deck != null)
            {
                var rc = new byte[cover.Length];
                var rt = new byte[cover.Length];
                l.Deck.Apply(cover, s.Precip.Values, null, null, rc, rt);
                cover = rc;
                type = rt;
            }
            else
            {
                byte[] scratch = null, altoScratch = null;
                type = CloudLookTuning.TypeBytes(s.Precip.Bytes, l.FairType, -1f, ref scratch, l.RainType);
                if (l.AltoType >= 0f)
                {
                    altoMid = LayerAt(field, body, s.Ut, l.TypeMidHeightM(l.AltoType)).Bytes;
                    altoLow = LayerAt(field, body, s.Ut, l.TypeMidHeightM(l.FairType >= 0f ? l.FairType : 0f)).Bytes;
                    if (!stage || !l.HasClimate) type = CloudLookTuning.ApplyAlto(type, s.Precip.Bytes, altoMid, altoLow, l.AltoType, l.AltoFrom, l.AltoFull, ref altoScratch);
                }
            }
            if (!stage) return;
            var inp = new CloudMesoInputs
            {
                Width = GeneratorMaps.Width, Height = GeneratorMaps.Height, Precip = s.Precip.Bytes, Humidity = s.Humidity.Bytes, Surface = s.Surface.Bytes,
                AltoMid = altoMid, AltoLow = altoLow, Ut = s.Ut, DriftRadS = field.CloudDriftRadS(body, CloudBridgeStack.MidHeightM(l)), Seed = field.SeedOf(body), RadiusKm = 600.0
            };
            byte[] coverOut = null, typeOut = null;
            CloudMesoStage.Apply(l, inp, cover, type, ref coverOut, ref typeOut);
            cover = coverOut;
            type = typeOut;
        }

        // Kerbin's layers as the shipped cfg has them, built the way the game builds them (not the hand-made fixtures).
        internal static CloudLayerDef[] ShippedLayers()
        {
            var specs = ReDefinition.Tests.Atmospheres.T10.CloudLayerSchemaTests.ParseKerbin().Climate.Clouds.Layers;
            var result = new CloudLayerDef[4];
            string[] names = { "Kerbin-weather-rain", "Kerbin-cumulus", "Kerbin-cumulonimbus", "Kerbin-cirrus" };
            for (int i = 0; i < names.Length; i++)
            {
                CloudLayerSpec spec = null;
                foreach (CloudLayerSpec x in specs) if (x.Name == names[i]) spec = x;
                result[i] = spec.Build(1);
            }
            return result;
        }
    }
}
