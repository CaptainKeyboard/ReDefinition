using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.T01
{
    // The test switches' variant (CloudDev.cginc, CloudDevVariant): the product value a shader compiles in for a test switch is the one the C# table names, every switch of the
    // table is one the shaders read through DEV, and the product's settings upload exactly those values (else the product would run the test variant, or the variant would draw
    // something other than the product without it).
    [TestClass]
    public class CloudDevVariantTests
    {
        private static readonly string[] LayerFiles =
        {
            "ReDefinitionAtmospheresLayers.shader", "ReDefinitionAtmospheresNoise.cginc", "ReDefinitionAtmospheresCloudLight.cginc", "ReDefinitionAtmospheresFarView.cginc",
            "ReDefinitionAtmospheresBaseHeight.cginc", "CloudCull.cginc", "MarchStep.cginc", "CloudCells.cginc"
        };

        private static readonly string[] ShadowFiles = { "ReDefinitionAtmospheresCloudShadow.shader", "ReDefinitionAtmospheresNoise.cginc", "CloudCells.cginc" };

        private static readonly Regex Dev = new Regex(@"DEV\((_[A-Za-z0-9]+)\.([xyzw]),\s*(-?[0-9.]+)\)");

        // "uniform.component" -> the product values the shader files compile in (each distinct value once).
        private static Dictionary<string, HashSet<float>> Literals(IEnumerable<string> files)
        {
            string root = RepoRoot.Root("unity", "Assets", "ReDefinitionAtmospheres", "Shaders", files.First());
            var found = new Dictionary<string, HashSet<float>>();
            foreach (string file in files)
            {
                string text = File.ReadAllText(Path.Combine(root, "unity", "Assets", "ReDefinitionAtmospheres", "Shaders", file));
                foreach (Match m in Dev.Matches(text))
                {
                    string key = m.Groups[1].Value + "." + m.Groups[2].Value;
                    if (!found.ContainsKey(key)) found[key] = new HashSet<float>();
                    found[key].Add(float.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
                }
            }
            return found;
        }

        private static string Key(CloudDevVariant.Entry e)
        {
            return e.Uniform + "." + "xyzw"[e.Component];
        }

        private static void TheShadersMatchTheTable(IEnumerable<string> files, CloudDevVariant.Entry[] table)
        {
            Dictionary<string, HashSet<float>> literals = Literals(files);
            var listed = table.ToDictionary(Key, e => e.Product);
            foreach (var kv in literals)
            {
                Assert.IsTrue(listed.ContainsKey(kv.Key), kv.Key + " is read through DEV but not in the table");
                Assert.AreEqual(1, kv.Value.Count, kv.Key + " has more than one product value in the shaders");
                Assert.AreEqual(listed[kv.Key], kv.Value.Single(), kv.Key + ": shader literal / table");
            }
            foreach (string key in listed.Keys) Assert.IsTrue(literals.ContainsKey(key), key + " is in the table but no shader reads it through DEV");
        }

        [TestMethod]
        public void TheLayerShadersCompileInTheTablesProductValues()
        {
            TheShadersMatchTheTable(LayerFiles, CloudDevVariant.Layer);
        }

        [TestMethod]
        public void TheShadowShaderCompilesInTheTablesProductValues()
        {
            TheShadersMatchTheTable(ShadowFiles, CloudDevVariant.Shadow);
        }

        [TestMethod]
        public void TheVariantIsOffAtTheProductValuesAndOnAtAnyOther()
        {
            Tuning.ClearOverrides();
            Tuning.Instance.BeginFrame(9100030);
            var values = new Dictionary<string, Vector4>();
            foreach (CloudDevVariant.Entry e in CloudDevVariant.Layer)
            {
                Vector4 v;
                values.TryGetValue(e.Uniform, out v);
                v[e.Component] = e.Product;
                values[e.Uniform] = v;
            }
            Assert.IsFalse(CloudDevVariant.Needed(n => values[n], CloudDevVariant.Layer));
            foreach (CloudDevVariant.Entry e in CloudDevVariant.Layer)
            {
                var changed = new Dictionary<string, Vector4>(values);
                Vector4 v = changed[e.Uniform];
                v[e.Component] = e.Product + 1f;
                changed[e.Uniform] = v;
                Assert.IsTrue(CloudDevVariant.Needed(n => changed[n], CloudDevVariant.Layer), Key(e));
            }
            Tuning.SetOverride(CloudDevVariant.TuneForce, 1f);
            try
            {
                Tuning.Instance.BeginFrame(9100031);
                Assert.IsTrue(CloudDevVariant.Needed(n => values[n], CloudDevVariant.Layer), "forced");
            }
            finally
            {
                Tuning.ClearOverrides();
                Tuning.Instance.BeginFrame(9100032);
            }
        }

        // The product's settings give the table's values (the uploads in LayerRenderer.Apply, CloudLightSettings.Upload, LayerNoise.Bind, LayerReconstructor).
        [TestMethod]
        public void TheProductsSettingsUploadTheTablesValues()
        {
            var frame = new LayerFrame();
            Tuning.ClearOverrides();
            Tuning.Instance.BeginFrame(9100033);
            OwnCloudOptions.Apply(frame, key => AtmospheresSettings.Definition(key).Default);
            Assert.AreEqual(0f, frame.RayLimitM, "_LimitP.x");
            Assert.AreEqual(0, frame.AidMode, "_LimitP.y");
            Assert.AreEqual(0, frame.LayerMask, "_LimitP.z");
            Assert.IsTrue(frame.UseSceneDepth, "_LimitP.w");
            Assert.IsTrue(CloudOutputStage.GammaDefault > 1.0001f, "_OutP.w");
            Assert.AreEqual(1f, frame.LeapScales, "_SpacingP.z");
            Assert.AreEqual(0f, frame.CostCutM, "_ReuseP.y");
            Assert.IsTrue(frame.CoverageBound, "_CostP.x");
            Assert.AreEqual(0f, frame.CostDebug, "_CostP.y");
            Assert.IsFalse(frame.FixedConeRotation, "_DitherP.y");
            Assert.AreEqual(3, frame.ConePattern, "_DitherP.y");
            Assert.AreEqual(3, frame.DitherSequence, "_DitherP.z");
            Assert.IsTrue(frame.DitherOwnGrid, "_DitherP.w");
            Assert.AreEqual(0f, new ReconSettings().FarRotateM, "_FarRot.x");
            Assert.IsFalse(EdgeSettings.AdaptiveDefault, "_EdgeP.x");
            var light = new CloudLightSettings();
            Assert.AreEqual(0f, light.ConeSpread, "_LightR.x");
            Assert.AreEqual(0f, light.CellRadiusM, "_CellA.w");
            Assert.IsTrue(CloudLookTuning.NoiseBlend, "_BlendP.x");
        }
    }
}
