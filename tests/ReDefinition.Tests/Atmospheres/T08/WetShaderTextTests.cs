using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T08
{
    // The shaders' text against the C# that must agree with them: every property the passes set is declared in the shader, the shader
    // names are the ones the code asks for, and the field threshold of the puddles is the inverse of the normal distribution the
    // shader's field is taken to follow.
    [TestClass]
    public class WetShaderTextTests
    {
        private static string wet, droplet;

        [ClassInitialize]
        public static void Read(TestContext context)
        {
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            string folder = Path.Combine(root, "unity", "Assets", "ReDefinitionAtmospheres", "Shaders");
            wet = File.ReadAllText(Path.Combine(folder, "ReDefinitionAtmospheresWet.shader"));
            droplet = File.ReadAllText(Path.Combine(folder, "ReDefinitionAtmospheresDroplet.shader"));
        }

        [TestMethod]
        public void TheShaderNamesAreTheOnesTheCodeAsksFor()
        {
            StringAssert.Contains(wet, "Shader \"" + WetSurfacesPass.ShaderName + "\"");
            StringAssert.Contains(droplet, "Shader \"" + DropletPass.ShaderName + "\"");
        }

        [TestMethod]
        public void EveryPropertyThePassesSetIsDeclared()
        {
            foreach (string name in new[] { "_WetA", "_WetB", "_WetCentre", "_WetEast", "_WetNorth", "_WetC" })
                StringAssert.Contains(wet, "float4 " + name + ";");
            foreach (string name in new[] { "_DrpA", "_DrpB", "_DrpC", "_DrpQuad" })
                StringAssert.Contains(droplet, "float4 " + name + ";");
            foreach (string name in new[] { "sampler2D _MainTex;", "sampler2D _DrpCopy;", "sampler2D _DrpMask;" })
                StringAssert.Contains(droplet, name);
        }

        [TestMethod]
        public void ThePassesAreInTheOrderTheCodeRecords()
        {
            // Wet: 0 G-buffer, 1 map. Droplet: 0 host, 1 copy, 2 quad, 3 map.
            Assert.IsTrue(wet.IndexOf("Name \"WetGBuffer\"", StringComparison.Ordinal) < wet.IndexOf("Name \"WetMap\"", StringComparison.Ordinal));
            int host = droplet.IndexOf("Name \"DropHost\"", StringComparison.Ordinal);
            int copy = droplet.IndexOf("Name \"DropCopy\"", StringComparison.Ordinal);
            int quad = droplet.IndexOf("Name \"DropQuad\"", StringComparison.Ordinal);
            int map = droplet.IndexOf("Name \"DropMap\"", StringComparison.Ordinal);
            Assert.IsTrue(host >= 0 && host < copy && copy < quad && quad < map);
        }

        [TestMethod]
        public void TheGBufferTargetsAreBlendedWithoutReadingThem()
        {
            StringAssert.Contains(wet, "Blend 0 DstColor Zero");
            StringAssert.Contains(wet, "Blend 1 One One");
            StringAssert.Contains(wet, "Blend 2 SrcAlpha OneMinusSrcAlpha");
            StringAssert.Contains(wet, "Blend 3 DstColor Zero");
            StringAssert.Contains(wet, "ColorMask A 1");
            Assert.IsFalse(wet.Contains("_CameraGBufferTexture"), "the overlay writes the G-buffer and must not sample it");
        }

        [TestMethod]
        public void TheDepressionFieldPeriodsAreWholeCellsOfTheWrap()
        {
            // The body-fixed offset wraps every 8192 m; the field's lattice must repeat over exactly that, for every octave.
            float scale = 1f / 32f;
            foreach (float octave in new[] { 1f, 4f, 16f })
            {
                float cells = WetSurfacesPass.WrapM * scale * octave;
                Assert.AreEqual(Math.Round(cells), cells, 1e-3f, "octave " + octave);
            }
            StringAssert.Contains(wet, "ValueNoise(p * s, period)");
            StringAssert.Contains(wet, "period * 4.0");
            StringAssert.Contains(wet, "period * 16.0");
        }

        [TestMethod]
        public void TheInverseNormalMatchesKnownQuantiles()
        {
            Assert.AreEqual(0f, WetnessLookMath.InverseNormal(0.5f), 1e-6f);
            Assert.AreEqual(1f, WetnessLookMath.InverseNormal(0.8413447f), 1e-4f);
            Assert.AreEqual(-1.6448536f, WetnessLookMath.InverseNormal(0.05f), 1e-4f);
            Assert.AreEqual(2.3263479f, WetnessLookMath.InverseNormal(0.99f), 1e-4f);
            Assert.AreEqual(0f, WetnessLookMath.FieldThreshold(0f), 1e-6f);
            Assert.AreEqual(WetnessLookMath.FieldMean, WetnessLookMath.FieldThreshold(0.5f), 1e-5f);
            Assert.IsTrue(WetnessLookMath.FieldThreshold(0.2f) < WetnessLookMath.FieldThreshold(0.4f));
        }
    }
}
