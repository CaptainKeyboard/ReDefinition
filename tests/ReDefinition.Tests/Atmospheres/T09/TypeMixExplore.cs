using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.C25;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // E40 type-mix audit: the maps the cloud painter hands to the layer pass, made by the real generator and the module's own rules (TypeMixFixture), dumped as raw bytes for
    // tools/e40_type_mix.py. Per seed and time: the shared coverage and precipitation maps, and per layer of Kerbin's cfg the layer's coverage bytes and type bytes. TYPEMIX_STAGE=0
    // dumps without the E40 L3 stage (the generator's maps and the old type rule), anything else with it. It runs only when TYPEMIX_OUT names a directory (a few minutes of CPU).
    [TestClass]
    public class TypeMixExplore
    {
        private static void Dump(string folder, string name, byte[] data)
        {
            File.WriteAllBytes(Path.Combine(folder, name + ".u8"), data);
        }

        [TestMethod, TestCategory("Explore")]
        public void DumpTheMapsOfAGameDay()
        {
            string folder = Environment.GetEnvironmentVariable("TYPEMIX_OUT");
            if (string.IsNullOrEmpty(folder)) return;
            bool stage = Environment.GetEnvironmentVariable("TYPEMIX_STAGE") != "0";
            Directory.CreateDirectory(folder);
            int[] seeds = { 1, 2, 3 };
            int times = 8, firstTime = 0, lastTime = 7;
            // TYPEMIX_ONLY=<seed>:<time>: that one map of the eight times of the seed (the e40-body series needs one)
            string only = Environment.GetEnvironmentVariable("TYPEMIX_ONLY");
            if (!string.IsNullOrEmpty(only))
            {
                string[] part = only.Split(':');
                seeds = new[] { int.Parse(part[0]) };
                firstTime = lastTime = int.Parse(part[1]);
            }
            double t0 = 3.0e6;
            // TYPEMIX_BODY=second: the second body of the P1 tests (FieldFixture.SecondFacts) with Kerbin's layer set scaled to its troposphere (TypeMixFixture.ShippedLayersScaled).
            bool second = Environment.GetEnvironmentVariable("TYPEMIX_BODY") == "second";
            CloudLayerDef[] layers = second ? TypeMixFixture.ShippedLayersScaled(7.5 / 10.5) : TypeMixFixture.ShippedLayers();
            LayerWindOptions.SteeringOverride = LayerSteeringMode.MapsOnly;
            try
            {
                var meta = new StringBuilder();
                meta.AppendLine("width " + GeneratorMaps.Width + " height " + GeneratorMaps.Height + " radiusM " + (second ? 500000 : 600000) + " day " + (second ? FieldFixture.SecondFacts().DaySeconds : TypeMixFixture.Day) + " t0 " + t0 + " stage " + (stage ? 1 : 0));
                foreach (CloudLayerDef l in layers)
                {
                    meta.Append("layer " + l.Name + " mid " + CloudBridgeStack.MidHeightM(l) + " types");
                    foreach (CloudTypeDef t in l.Types) meta.Append(" '" + t.Name + "'");
                    meta.AppendLine(" fair " + l.FairType + " alto " + l.AltoType + " rain " + l.RainType + " deck " + (l.Deck != null) + " noiseScaleM " + l.NoiseScaleM + " noiseAmount " + l.NoiseAmount);
                }
                File.WriteAllText(Path.Combine(folder, "meta.txt"), meta.ToString());
                int w, h;
                bool ocean;
                float[] heights = DumpReader.Heights(second ? "Laythe" : "Kerbin", out w, out h, out ocean);
                var hb = new byte[heights.Length * 4];
                Buffer.BlockCopy(heights, 0, hb, 0, hb.Length);
                File.WriteAllBytes(Path.Combine(folder, "heights_" + w + "x" + h + ".f32"), hb);
                foreach (int seed in seeds)
                {
                    GeneratorField field = second ? FieldFixture.SecondField(seed) : FieldFixture.Field(seed);
                    int body = FieldFixture.BodyIndex;
                    for (int k = firstTime; k <= lastTime; k++)
                    {
                        double ut = t0 + k * (second ? FieldFixture.SecondFacts().DaySeconds : TypeMixFixture.Day) / times;
                        string tag = "s" + seed + "_t" + k + "_";
                        TypeMixFixture.Shared shared = TypeMixFixture.Maps(field, body, ut);
                        Dump(folder, tag + "sharedCover", shared.Cover.Bytes);
                        Dump(folder, tag + "precip", shared.Precip.Bytes);
                        foreach (CloudLayerDef l in layers)
                        {
                            byte[] cover, type;
                            TypeMixFixture.Layer(field, body, shared, l, stage, out cover, out type);
                            Dump(folder, tag + l.Name + "_cover", cover);
                            Dump(folder, tag + l.Name + "_type", type);
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
