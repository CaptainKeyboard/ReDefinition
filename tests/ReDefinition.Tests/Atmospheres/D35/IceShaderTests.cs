using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.D35
{
    // Package D35, the shader repeats the CPU's pattern: the hash constants, the order of the operations, the band table and the look
    // constants are read from the shader text and compared with IceHash and IceMask (the values are compared on the GPU by the series
    // rows D35.hash_*, D35.worley_max_error and D35.phase.*.rendered_gpu).
    [TestClass]
    public class IceShaderTests
    {
        private static string OverlayText()
        {
            string root = RepoRoot.Root("unity", "Assets", "ReDefinitionAtmospheres", "Shaders", "ReDefinitionAtmospheresIceOverlay.shader");
            return File.ReadAllText(Path.Combine(root, "unity", "Assets", "ReDefinitionAtmospheres", "Shaders", "ReDefinitionAtmospheresIceOverlay.shader"));
        }

        private static string Compact(string text)
        {
            return Regex.Replace(text, @"\s+", " ");
        }

        private static float[] ArrayOf(string text, string name)
        {
            Match m = Regex.Match(text, name + @"\[\d+\]\s*=\s*\{([^}]*)\}");
            Assert.IsTrue(m.Success, "the shader has no array " + name);
            string[] parts = m.Groups[1].Value.Split(',');
            var values = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++) values[i] = float.Parse(parts[i].Trim(), CultureInfo.InvariantCulture);
            return values;
        }

        private static float ConstOf(string text, string name)
        {
            Match m = Regex.Match(text, @"static const float " + name + @"\s*=\s*([0-9.]+);");
            Assert.IsTrue(m.Success, "the shader has no constant " + name);
            return float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        [TestMethod]
        public void TheShaderUsesTheSameHashConstantsAndTheSameOperationsAsTheCpu()
        {
            string text = Compact(OverlayText());
            Assert.IsTrue(text.Contains("v ^= v >> 16u; v *= 0x7feb352du; v ^= v >> 15u; v *= 0x846ca68bu; v ^= v >> 16u; return v;"), "the lowbias32 mixer");
            Assert.AreEqual(0x7feb352du, IceHash.MixMultiplierA);
            Assert.AreEqual(0x846ca68bu, IceHash.MixMultiplierB);
            Assert.IsTrue(text.Contains("IceMix(IceMix(IceMix(seed + asuint(c.z)) + asuint(c.y)) + asuint(c.x))"), "the cell hash");
            Assert.IsTrue(text.Contains("IceMix(h + 0x9E3779B9u)") && text.Contains("IceMix(h + 0x7F4A7C15u)"), "the jitter salts");
            Assert.AreEqual(0x9E3779B9u, IceHash.JitterYSalt);
            Assert.AreEqual(0x7F4A7C15u, IceHash.JitterZSalt);
            Assert.IsTrue(text.Contains("IceUnit(IceMix(nearest ^ 0x6A09E667u))"), "the cell value");
            Assert.AreEqual(0x6A09E667u, IceHash.ValueSalt);
            Assert.IsTrue(text.Contains("seed ^ 0xBB67AE85u"), "the dither salt");
            Assert.AreEqual(0xBB67AE85u, IceHash.DitherSalt);
            Assert.IsTrue(text.Contains("return (h >> 8u) * (1.0 / 16777216.0);"), "the unit number");
            Assert.IsTrue(text.Contains("0.1 + 0.8 * float3("), "the jitter range");
            Assert.AreEqual(0.1f, IceHash.JitterLow);
            Assert.AreEqual(0.8f, IceHash.JitterSpan);
            Assert.IsTrue(text.Contains("for (int dz = -1; dz <= 1; dz++)") && text.Contains("for (int dy = -1; dy <= 1; dy++)") && text.Contains("for (int dx = -1; dx <= 1; dx++)"), "27 cells");
        }

        [TestMethod]
        public void TheShaderCarriesTheMaskTablesAndConstantsOfTheCpu()
        {
            string text = OverlayText();
            CollectionAssertEqual(IceMask.BandWidths, ArrayOf(text, "BandWidths"), "band widths");
            CollectionAssertEqual(IceMask.BandShares, ArrayOf(text, "BandShares"), "band shares");
            Assert.AreEqual(IceMask.ThinM, ConstOf(text, "ThinM"), 1e-7);
            Assert.AreEqual(IceMask.ThickM, ConstOf(text, "ThickM"), 1e-7);
            Assert.AreEqual(IceMask.ThinOpacity, ConstOf(text, "ThinOpacity"), 1e-7);
            Assert.AreEqual(IceMask.ThicknessSpread, ConstOf(text, "ThicknessSpread"), 1e-7);
            Assert.AreEqual(IceMask.EdgeNoiseScale, ConstOf(text, "EdgeNoiseScale"), 1e-7);
            Assert.AreEqual(IceMask.MaxBandShare, ConstOf(text, "MaxBandShare"), 1e-7);
            string compact = Compact(text);
            Assert.IsTrue(compact.Contains("float edge = 2.0 * min(f, 1.0 - f);"), "the edge factor");
            Assert.IsTrue(compact.Contains("local = f + _ShoreGain * edge * (saturate(shore) - saturate(shoreMean))"), "the shore term");
            Assert.IsTrue(compact.Contains("+ _EdgeNoise * edge * (0.35 + 0.65 * (1.0 - saturate(shore))) * noise;"), "the edge noise");
            Assert.IsTrue(compact.Contains("float cellChance = min(1.0, local / max(1e-3, 1.0 - band));"), "the cell chance");
            Assert.IsTrue(compact.Contains("float band = min(MaxBandShare, max(0.0, _LeadFraction) * (1.0 - f));"), "the band share");
            Assert.IsTrue(compact.Contains("if (w.y - w.x < width)"), "the lead test");
            Assert.IsTrue(compact.Contains("seed ^ 0xA5A5A5A5u"), "the edge-noise seed");
        }

        [TestMethod]
        public void TheShaderNamesAndPassesAreThoseTheCodeAndTheProbeUse()
        {
            string text = OverlayText();
            Assert.IsTrue(text.Contains("Shader \"" + IceGroundOverlay.ShaderName + "\""));
            Assert.IsTrue(text.Contains("\"Queue\" = \"Geometry+" + (IceGroundOverlay.SheetQueue - 2000) + "\""), "queue 2504");
            string[] passes = { "ICE", "COVERAGE", "NOISE", "HASH" };
            MatchCollection found = Regex.Matches(text, "Name \"([A-Z]+)\"");
            Assert.AreEqual(passes.Length, found.Count);
            for (int i = 0; i < passes.Length; i++) Assert.AreEqual(passes[i], found[i].Groups[1].Value, "pass " + i);
            Assert.AreEqual(1, IceGpu.CoveragePass);
            Assert.AreEqual(2, IceGpu.NoisePass);
            Assert.AreEqual(3, IceGpu.HashPass);
            Assert.IsTrue(text.Contains("ZWrite On"), "the sheet writes depth");
            string shell = File.ReadAllText(Path.Combine(RepoRoot.Root("unity", "Assets", "ReDefinitionAtmospheres", "Shaders", "ReDefinitionAtmospheresIceShell.shader"),
                "unity", "Assets", "ReDefinitionAtmospheres", "Shaders", "ReDefinitionAtmospheresIceShell.shader"));
            Assert.IsTrue(shell.Contains("Shader \"" + IceOrbitLayer.ShaderName + "\""));
        }

        [TestMethod]
        public void TheShaderFilesCarryMetaFiles()
        {
            string root = RepoRoot.Root("unity", "Assets", "ReDefinitionAtmospheres", "Shaders");
            foreach (string name in new[] { "ReDefinitionAtmospheresIceOverlay.shader", "ReDefinitionAtmospheresIceShell.shader" })
            {
                string meta = File.ReadAllText(Path.Combine(root, "unity", "Assets", "ReDefinitionAtmospheres", "Shaders", name + ".meta"));
                Assert.IsTrue(Regex.IsMatch(meta, "guid: [0-9a-f]{32}"), name + " meta guid");
                Assert.IsTrue(meta.Contains("ShaderImporter"), name + " importer");
            }
        }

        private static void CollectionAssertEqual(float[] expected, float[] actual, string what)
        {
            Assert.AreEqual(expected.Length, actual.Length, what + " length");
            for (int i = 0; i < expected.Length; i++) Assert.AreEqual(expected[i], actual[i], 1e-7, what + " " + i);
        }
    }
}
