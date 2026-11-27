using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T01
{
    // The layer pass's shader over its code (ReDefinitionAtmospheresLayersCore.cginc, master plan 45.3): the renderer draws its passes by number, so their count is pinned. Master plan
    // 47.U stage 1: there is ONE shader and one way a cloud's place is found; the test lattice (a second shader with CLOUD_LATTICE defined, and the same keyword in the cloud shadow's
    // shader) is gone and must not come back as a second path.
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
        public void TheLayerShaderHasItsNinePasses()
        {
            string product = Read("ReDefinitionAtmospheresLayers.shader");
            Assert.AreEqual(9, Regex.Matches(Passes(product), @"^\s*Pass\s*$", RegexOptions.Multiline).Count, "the nine passes (8: the own sky probe's face)");
            StringAssert.Contains(product, "#include \"ReDefinitionAtmospheresLayersCore.cginc\"");
            StringAssert.Contains(product, "Shader \"" + LayerRenderer.ShaderName + "\"");
        }

        [TestMethod]
        public void NoShaderOfTheCloudsHasALatticeVariant()
        {
            string folder = Path.GetDirectoryName(Path.Combine(RepoRoot.Root("unity", "Assets", "ReDefinitionAtmospheres", "Shaders", "CloudCells.cginc"),
                                                               "unity", "Assets", "ReDefinitionAtmospheres", "Shaders", "CloudCells.cginc"));
            Assert.IsFalse(File.Exists(Path.Combine(folder, "ReDefinitionAtmospheresLayersLattice.shader")), "the lattice's shader is gone");
            foreach (string file in new[] { "ReDefinitionAtmospheresLayers.shader", "ReDefinitionAtmospheresLayersCore.cginc", "ReDefinitionAtmospheresCloudShadow.shader", "CloudCells.cginc" })
            {
                string text = Read(file);
                StringAssert.DoesNotMatch(text, new Regex(@"#\s*(if|ifdef|ifndef|define)\b[^\n]*CLOUD_LATTICE|multi_compile[_a-z]*[^\n]*CLOUD_LATTICE"), file);
                StringAssert.DoesNotMatch(text, new Regex(@"\bCellCover\s*\(|\bCellProfile\s*\("), file + ": the lattice's functions");
            }
        }
    }
}
