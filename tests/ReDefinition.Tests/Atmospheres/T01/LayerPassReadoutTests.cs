using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.T01
{
    // E40 PP: the readout of the layer pass's uniforms (the probe's pass.json, the weather player's own readout) names every uniform the layer shader and its includes
    // declare, each once and of the right kind, and writes JSON a reader can take (round-trip numbers, null for NaN and for an array the material does not hold).
    [TestClass]
    public class LayerPassReadoutTests
    {
        private static readonly string[] Files =
        {
            "ReDefinitionAtmospheresLayers.shader", "ReDefinitionAtmospheresNoise.cginc", "ReDefinitionAtmospheresCloudLight.cginc", "ReDefinitionAtmospheresFarView.cginc",
            "ReDefinitionAtmospheresBaseHeight.cginc", "CloudCull.cginc", "MarchStep.cginc"
        };

        // name -> kind ("float", "float4", "float4[]", "float4x4", "texture") as the shader files declare it
        private static Dictionary<string, string> Declared()
        {
            string root = RepoRoot.Root("unity", "Assets", "ReDefinitionAtmospheres", "Shaders", Files[0]);
            var found = new Dictionary<string, string>();
            var plain = new Regex(@"^\s*(?:uniform\s+)?(float|float4|float4x4|sampler2D|sampler3D|sampler2D_float)\s+(_[A-Za-z0-9_]+(?:\[\d+\])?(?:\s*,\s*_[A-Za-z0-9_]+(?:\[\d+\])?)*)\s*;", RegexOptions.Multiline);
            var array = new Regex(@"UNITY_DECLARE_TEX2DARRAY\((_[A-Za-z0-9_]+)\)");
            foreach (string file in Files)
            {
                string text = File.ReadAllText(Path.Combine(root, "unity", "Assets", "ReDefinitionAtmospheres", "Shaders", file));
                foreach (Match m in plain.Matches(text))
                    foreach (string part in m.Groups[2].Value.Split(','))
                    {
                        string name = part.Trim();
                        bool isArray = name.Contains("[");
                        if (isArray) name = name.Substring(0, name.IndexOf('['));
                        string type = m.Groups[1].Value;
                        string kind = type.StartsWith("sampler", StringComparison.Ordinal) ? "texture" : isArray ? type + "[]" : type;
                        if (!found.ContainsKey(name)) found[name] = kind;
                    }
                foreach (Match m in array.Matches(text))
                    if (!found.ContainsKey(m.Groups[1].Value)) found[m.Groups[1].Value] = "texture";
            }
            return found;
        }

        [TestMethod]
        public void TheListsAreTheShadersUniforms()
        {
            Dictionary<string, string> declared = Declared();
            Assert.IsTrue(declared.Count > 80, "the shader's uniforms were read (" + declared.Count + ")");
            var listed = new Dictionary<string, string>();
            foreach (string n in LayerPassReadout.Floats) listed.Add(n, "float");
            foreach (string n in LayerPassReadout.Vectors) listed.Add(n, "float4");
            foreach (string n in LayerPassReadout.Arrays) listed.Add(n, "float4[]");
            foreach (string n in LayerPassReadout.Matrices) listed.Add(n, "float4x4");
            foreach (string n in LayerPassReadout.Textures) listed.Add(n, "texture");
            string missing = string.Join(", ", declared.Keys.Where(k => !listed.ContainsKey(k)).OrderBy(k => k));
            string extra = string.Join(", ", listed.Keys.Where(k => !declared.ContainsKey(k)).OrderBy(k => k));
            string wrong = string.Join(", ", listed.Where(kv => declared.ContainsKey(kv.Key) && declared[kv.Key] != kv.Value).Select(kv => kv.Key + " " + kv.Value + " / " + declared[kv.Key]));
            Assert.AreEqual("", missing, "declared in the shader, not read back");
            Assert.AreEqual("", extra, "read back, not declared");
            Assert.AreEqual("", wrong, "kind listed / declared");
        }

        private sealed class Fake : LayerPassReadout.ISource
        {
            public float Float(string name) { return name == "_PlanetR" ? 600000f : float.NaN; }
            public Vector4 Vector(string name) { return name == "_Sun" ? new Vector4(0.1f, -0.25f, 1f / 3f, 4000f) : Vector4.zero; }
            public Vector4[] Array(string name) { return name == "_LayerA" ? new[] { new Vector4(1f, 2f, 3f, 4f), Vector4.one } : null; }
            public Matrix4x4 Matrix(string name) { return Matrix4x4.identity; }
            public string Texture(string name) { return name == "_CovMaps" ? "512x256x4" : null; }
            public bool KeywordOn(string name) { return name == "CLOUD_LIGHTVOL"; }
            public string[] KeywordsOn() { return new[] { "CLOUD_LIGHTVOL" }; }
        }

        // A source whose vector array read throws (an array never set on the material) and whose keyword list is unknown.
        private sealed class Throwing : LayerPassReadout.ISource
        {
            public float Float(string name) { return 0f; }
            public Vector4 Vector(string name) { return Vector4.zero; }
            public Vector4[] Array(string name) { if (name == "_LvO") throw new System.ArgumentException("no such array"); return null; }
            public Matrix4x4 Matrix(string name) { return Matrix4x4.identity; }
            public string Texture(string name) { return null; }
            public bool KeywordOn(string name) { return false; }
            public string[] KeywordsOn() { return null; }
        }

        [TestMethod]
        public void AThrowingReadIsNullAndNamed()
        {
            string json = LayerPassReadout.Uniforms(new Throwing());
            StringAssert.Contains(json, "\"_LvO\": null");
            StringAssert.Contains(json, "\"readFailures\": [\"_LvO: ArgumentException: no such array\"]");
            StringAssert.Contains(json, "\"CLOUD_LIGHTVOL\": false");
            StringAssert.Contains(json, "\"keywordsOn\": null");
            Assert.AreEqual(json.Count(c => c == '{'), json.Count(c => c == '}'), "balanced braces");
        }

        [TestMethod]
        public void TheJsonHoldsTheNumbersExactly()
        {
            string json = LayerPassReadout.Uniforms(new Fake());
            StringAssert.Contains(json, "\"_PlanetR\": 600000");
            StringAssert.Contains(json, "\"_LayerCount\": null");
            StringAssert.Contains(json, "\"_Sun\": [0.1, -0.25, " + (1f / 3f).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ", 4000]");
            StringAssert.Contains(json, "\"_LayerA\": [[1, 2, 3, 4], [1, 1, 1, 1]]");
            StringAssert.Contains(json, "\"_LayerB\": null");
            StringAssert.Contains(json, "\"_WorldToMap\": [[1, 0, 0, 0], [0, 1, 0, 0], [0, 0, 1, 0], [0, 0, 0, 1]]");
            StringAssert.Contains(json, "\"_CovMaps\": \"512x256x4\"");
            StringAssert.Contains(json, "\"_NoiseShape\": null");
            StringAssert.Contains(json, "\"keywords\": {\"CLOUD_LIGHTVOL\": true, \"REDEFINITION_MAPSKY\": false}");
            StringAssert.Contains(json, "\"keywordsOn\": [\"CLOUD_LIGHTVOL\"]");
            StringAssert.Contains(json, "\"readFailures\": []");
            Assert.AreEqual(json.Count(c => c == '{'), json.Count(c => c == '}'), "balanced braces");
            Assert.AreEqual(json.Count(c => c == '['), json.Count(c => c == ']'), "balanced brackets");
            Assert.AreEqual("\"a\\\"b\\\\c\\u000a\"", LayerPassReadout.Str("a\"b\\c\n"));
        }
    }
}
