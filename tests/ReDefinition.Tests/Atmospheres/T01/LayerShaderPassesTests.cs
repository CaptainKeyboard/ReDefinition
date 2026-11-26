using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T01
{
    // Master plan 45.3: the test lattice is a shader of its own (ReDefinitionAtmospheresLayersLattice.shader) over the product's code
    // (ReDefinitionAtmospheresLayersCore.cginc). The renderer switches its material between the two, so their passes must be the same, word for word;
    // only the lattice defines CLOUD_LATTICE, and the product shader declares no lattice variant.
    [TestClass]
    public class LayerShaderPassesTests
    {
        private static string Read(string file)
        {
            string root = RepoRoot.Root("unity", "Assets", "ReDefinitionAtmospheres", "Shaders", file);
            return File.ReadAllText(Path.Combine(root, "unity", "Assets", "ReDefinitionAtmospheres", "Shaders", file)).Replace("\r\n", "\n");
        }

        // Everything after the CGINCLUDE block: the pass blocks.
        private static string Passes(string text)
        {
            int include = text.IndexOf("CGINCLUDE", System.StringComparison.Ordinal);
            int end = text.IndexOf("ENDCG", include, System.StringComparison.Ordinal);
            Assert.IsTrue(include >= 0 && end > include, "the shader has a CGINCLUDE block");
            return text.Substring(end + "ENDCG".Length);
        }

        [TestMethod]
        public void TheLatticeShaderHasTheProductShadersPassesWordForWord()
        {
            string product = Read("ReDefinitionAtmospheresLayers.shader"), lattice = Read("ReDefinitionAtmospheresLayersLattice.shader");
            Assert.AreEqual(Passes(product), Passes(lattice), "the pass blocks differ: the renderer's pass numbers would draw other passes with the lattice");
            Assert.AreEqual(8, Regex.Matches(Passes(product), @"^\s*Pass\s*$", RegexOptions.Multiline).Count, "the eight passes");
        }

        [TestMethod]
        public void OnlyTheLatticeShaderHasTheLattice()
        {
            string product = Read("ReDefinitionAtmospheresLayers.shader"), lattice = Read("ReDefinitionAtmospheresLayersLattice.shader");
            StringAssert.DoesNotMatch(product, new Regex(@"CLOUD_LATTICE\s*$|multi_compile[_a-z]*\s+_+\s+CLOUD_LATTICE|#define CLOUD_LATTICE", RegexOptions.Multiline));
            StringAssert.Contains(lattice, "#define CLOUD_LATTICE\n        #include \"ReDefinitionAtmospheresLayersCore.cginc\"");
            StringAssert.Contains(product, "#include \"ReDefinitionAtmospheresLayersCore.cginc\"");
            StringAssert.Contains(lattice, "Shader \"" + LayerRenderer.LatticeShaderName + "\"");
            StringAssert.Contains(product, "Shader \"" + LayerRenderer.ShaderName + "\"");
        }
    }
}
