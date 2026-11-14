using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.C25;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // E40 type-mix audit: the maps the cloud painter hands to the layer pass, made by the real generator and the module's own rules, dumped as raw bytes for tools/e40_type_mix.py.
    // Per seed and time: the shared coverage and precipitation maps, and per layer of Kerbin's cfg the layer's coverage bytes and type bytes exactly as OwnCloudModule.FetchStep
    // builds them (layer map at the layer's mid height, the deck rule for the decks and the cumulonimbus, fairType / rainType / the altocumulus rule for the others). Fog is left
    // out (no humidity input): the rain deck shows no fog there. It runs only when TYPEMIX_OUT names a directory (a minute or two of CPU).
    [TestClass]
    public class TypeMixExplore
    {
        private const double Day = 21549.425;

        private static byte[] Bytes(float[] values)
        {
            var b = new byte[values.Length];
            for (int i = 0; i < b.Length; i++) b[i] = (byte)Math.Min(255.0, Math.Max(0.0, Math.Round(values[i] * 255.0, MidpointRounding.ToEven)));
            return b;
        }

        private static void Dump(string folder, string name, byte[] data)
        {
            File.WriteAllBytes(Path.Combine(folder, name + ".u8"), data);
        }

        [TestMethod, TestCategory("Explore")]
        public void DumpTheMapsOfAGameDay()
        {
            string folder = Environment.GetEnvironmentVariable("TYPEMIX_OUT");
            if (string.IsNullOrEmpty(folder)) return;
            Directory.CreateDirectory(folder);
            int[] seeds = { 1, 2, 3 };
            int times = 8;
            double t0 = 3.0e6;
            CloudLayerDef[] layers = { CloudLookFixtures.Rain(), CloudLookFixtures.Cumulus(), CloudLookFixtures.Cumulonimbus(), CloudLookFixtures.Cirrus() };
            LayerWindOptions.SteeringOverride = LayerSteeringMode.MapsOnly;
            try
            {
                var meta = new StringBuilder();
                meta.AppendLine("width " + GeneratorMaps.Width + " height " + GeneratorMaps.Height + " radiusM 600000 day " + Day + " t0 " + t0);
                foreach (CloudLayerDef l in layers)
                {
                    meta.Append("layer " + l.Name + " mid " + CloudBridgeStack.MidHeightM(l) + " types");
                    foreach (CloudTypeDef t in l.Types) meta.Append(" '" + t.Name + "'");
                    meta.AppendLine(" fair " + l.FairType + " alto " + l.AltoType + " rain " + l.RainType + " deck " + (l.Deck != null) + " noiseScaleM " + l.NoiseScaleM + " noiseAmount " + l.NoiseAmount);
                }
                File.WriteAllText(Path.Combine(folder, "meta.txt"), meta.ToString());
                int w, h;
                bool ocean;
                float[] heights = DumpReader.Heights("Kerbin", out w, out h, out ocean);
                var hb = new byte[heights.Length * 4];
                Buffer.BlockCopy(heights, 0, hb, 0, hb.Length);
                File.WriteAllBytes(Path.Combine(folder, "heights_" + w + "x" + h + ".f32"), hb);
                foreach (int seed in seeds)
                {
                    GeneratorField field = FieldFixture.Field(seed);
                    int body = FieldFixture.BodyIndex;
                    for (int k = 0; k < times; k++)
                    {
                        double ut = t0 + k * Day / times;
                        string tag = "s" + seed + "_t" + k + "_";
                        field.Maps.RenderNow(body, ut);
                        var cov = new WeatherMap();
                        var typ = new WeatherMap();
                        Assert.IsTrue(field.GetMap(body, WeatherMapKind.Coverage, ut, cov));
                        Assert.IsTrue(field.GetMap(body, WeatherMapKind.Precipitation, ut, typ));
                        cov.PackBytes(0f, 1f);
                        typ.PackBytes(0f, 1f);
                        Dump(folder, tag + "sharedCover", cov.Bytes);
                        Dump(folder, tag + "precip", typ.Bytes);
                        foreach (CloudLayerDef l in layers)
                        {
                            double mid = CloudBridgeStack.MidHeightM(l);
                            var map = new WeatherMap();
                            field.GetLayerCoverage(body, ut, mid, map);
                            int key = (int)Math.Round(Math.Max(0.0, mid) / GeneratorMaps.LayerStepM);
                            field.Maps.RenderLayerNow(body, key);
                            Assert.IsTrue(field.GetLayerCoverage(body, ut, mid, map));
                            map.PackBytes(0f, 1f);
                            byte[] cover = map.Bytes, typeBytes;
                            if (l.Deck != null)
                            {
                                var rc = new byte[cover.Length];
                                var rt = new byte[cover.Length];
                                l.Deck.Apply(cover, typ.Values, null, null, rc, rt);
                                cover = rc;
                                typeBytes = rt;
                            }
                            else
                            {
                                byte[] scratch = null, altoScratch = null;
                                typeBytes = CloudLookTuning.TypeBytes(typ.Bytes, l.FairType, -1f, ref scratch, l.RainType);
                                if (l.AltoType >= 0f)
                                {
                                    var altoMid = new WeatherMap();
                                    var altoLow = new WeatherMap();
                                    foreach (var pair in new[] { Tuple.Create(l.TypeMidHeightM(l.AltoType), altoMid), Tuple.Create(l.TypeMidHeightM(l.FairType >= 0f ? l.FairType : 0f), altoLow) })
                                    {
                                        field.GetLayerCoverage(body, ut, pair.Item1, pair.Item2);
                                        field.Maps.RenderLayerNow(body, (int)Math.Round(Math.Max(0.0, pair.Item1) / GeneratorMaps.LayerStepM));
                                        Assert.IsTrue(field.GetLayerCoverage(body, ut, pair.Item1, pair.Item2));
                                        pair.Item2.PackBytes(0f, 1f);
                                    }
                                    typeBytes = CloudLookTuning.ApplyAlto(typeBytes, typ.Bytes, altoMid.Bytes, altoLow.Bytes, l.AltoType, l.AltoFrom, l.AltoFull, ref altoScratch);
                                }
                            }
                            Dump(folder, tag + l.Name + "_cover", cover);
                            Dump(folder, tag + l.Name + "_type", typeBytes);
                        }
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
