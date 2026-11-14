using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // Tuning aid for the E40 L3 cover pattern: reads the maps a TypeMixExplore run without the stage dumped (TYPEMIX_STAGE=0, folder TYPEMIX_IN), applies the MESO numbers of the
    // environment (TUNE_SCALE, TUNE_OCT, TUNE_AMOUNT, TUNE_EDGE, TUNE_SHOULDER, TUNE_STRETCH) to one layer (TUNE_LAYER: its index in the dump's order rain, cumulus, cumulonimbus, cirrus)
    // and writes the stage's cover bytes under the same names into TYPEMIX_TUNE_OUT. Runs only when TYPEMIX_TUNE_OUT is set; a few seconds.
    [TestClass]
    public class TypeMixTune
    {
        private static float Env(string name, float fallback)
        {
            string v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(v) ? fallback : float.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
        }

        [TestMethod, TestCategory("Explore")]
        public void TheMeanOfThePatternAgainstTheMapValue()
        {
            string output = Environment.GetEnvironmentVariable("TYPEMIX_TUNE_OUT");
            if (string.IsNullOrEmpty(output)) return;
            Directory.CreateDirectory(output);
            CloudLayerDef layer = TypeMixFixture.ShippedLayers()[1];
            var meso = layer.Meso;
            meso.ScaleKm = Env("TUNE_SCALE", meso.ScaleKm);
            meso.Octaves = (int)Env("TUNE_OCT", meso.Octaves);
            meso.Amount = Env("TUNE_AMOUNT", meso.Amount);
            meso.Edge = Env("TUNE_EDGE", meso.Edge);
            meso.Shoulder = Env("TUNE_SHOULDER", meso.Shoulder);
            var lines = new System.Text.StringBuilder();
            foreach (int level in new[] { 13, 26, 51, 77, 102, 128, 153, 179, 204, 230 })
            {
                double sum = 0.0, rows = 0.0;
                for (ulong seed = 1; seed <= 4; seed++)
                {
                    var cover = new byte[512 * 256];
                    for (int i = 0; i < cover.Length; i++) cover[i] = (byte)level;
                    var inp = new CloudMesoInputs { Width = 512, Height = 256, Precip = null, Ut = 3.0e6, Seed = seed, RadiusKm = 600.0 };
                    byte[] coverOut = null, typeOut = null;
                    CloudMesoStage.Apply(layer, inp, cover, new byte[cover.Length], ref coverOut, ref typeOut);
                    for (int j = 40; j < 216; j++) for (int i = 0; i < 512; i++) { sum += coverOut[j * 512 + i] / 255.0; rows++; }
                }
                lines.AppendLine("m " + (level / 255.0).ToString("0.000") + " mean after " + (sum / rows).ToString("0.000"));
            }
            File.WriteAllText(Path.Combine(output, "mean.txt"), lines.ToString());
        }

        [TestMethod, TestCategory("Explore")]
        public void ApplyTheMesoNumbersToADump()
        {
            string output = Environment.GetEnvironmentVariable("TYPEMIX_TUNE_OUT"), input = Environment.GetEnvironmentVariable("TYPEMIX_IN");
            if (string.IsNullOrEmpty(output) || string.IsNullOrEmpty(input)) return;
            Directory.CreateDirectory(output);
            int index = (int)Env("TUNE_LAYER", 1);
            CloudLayerDef layer = TypeMixFixture.ShippedLayers()[index];
            var meso = layer.Meso ?? new CloudMesoSpec();
            meso.ScaleKm = Env("TUNE_SCALE", meso.ScaleKm);
            meso.Octaves = (int)Env("TUNE_OCT", meso.Octaves);
            meso.Amount = Env("TUNE_AMOUNT", meso.Amount);
            meso.Edge = Env("TUNE_EDGE", meso.Edge);
            meso.Shoulder = Env("TUNE_SHOULDER", meso.Shoulder);
            meso.Stretch = Env("TUNE_STRETCH", meso.Stretch);
            meso.RainKeep = Env("TUNE_RAINKEEP", meso.RainKeep);
            meso.Cells = Env("TUNE_CELLS", meso.Cells ? 1f : 0f) >= 0.5f;
            layer.Meso = meso;
            for (int seed = 1; seed <= 3; seed++)
                for (int k = 0; k < 8; k++)
                {
                    string tag = "s" + seed + "_t" + k + "_";
                    byte[] precip = File.ReadAllBytes(Path.Combine(input, tag + "precip.u8"));
                    byte[] cover = File.ReadAllBytes(Path.Combine(input, tag + layer.Name + "_cover.u8"));
                    var inp = new CloudMesoInputs { Width = 512, Height = 256, Precip = precip, Ut = 3.0e6 + k * TypeMixFixture.Day / 8, DriftRadS = 0.0, Seed = (ulong)seed, RadiusKm = 600.0 };
                    byte[] coverOut = null, typeOut = null;
                    CloudMesoStage.Apply(layer, inp, cover, new byte[cover.Length], ref coverOut, ref typeOut);
                    File.WriteAllBytes(Path.Combine(output, tag + layer.Name + "_cover.u8"), coverOut);
                }
        }
    }
}
