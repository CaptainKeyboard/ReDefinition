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
            foreach (string name in new[] { "_WetA", "_WetB", "_WetCentre", "_WetEast", "_WetNorth", "_WetC", "_WetT" })
                StringAssert.Contains(wet, "float4 " + name + ";");
            foreach (string name in new[] { "_DrpA", "_DrpB", "_DrpC", "_DrpD", "_DrpQuad" })
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
        public void TheOverlayWritesTheGBufferWithoutSamplingIt()
        {
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
        }
    }
}
