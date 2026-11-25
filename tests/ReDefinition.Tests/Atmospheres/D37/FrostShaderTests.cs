using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.D37
{
    // The frost shader's text against the C# it must agree with: the array sizes, the tile grid, the repeated contribution-mask block.
    [TestClass]
    public class FrostShaderTests
    {
        private static string shader;
        private static string include;

        [ClassInitialize]
        public static void Read(TestContext context)
        {
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            string folder = Path.Combine(root, "unity", "Assets", "ReDefinitionAtmospheres");
            shader = File.ReadAllText(Path.Combine(folder, "Shaders", "ReDefinitionAtmospheresFrost.shader"));
            include = File.ReadAllText(Path.Combine(folder, "Include", "AtmoContribution.cginc"));
        }

        private static int ArraySize(string name)
        {
            Match m = Regex.Match(shader, @"float4\s+" + name + @"\[(\d+)\]\s*;");
            Assert.IsTrue(m.Success, name + " is not declared");
            return int.Parse(m.Groups[1].Value);
        }

        [TestMethod]
        public void ArraysAndTileGridMatchTheCode()
        {
            Assert.AreEqual(CameraFrost.MaxFlakes, ArraySize("_FrostFlakes"));
            Assert.AreEqual(CameraFrost.MaxDrops, ArraySize("_FrostDrops"));
            Assert.AreEqual(FrostTiles.Count, ArraySize("_FrostTiles"));
            // The lookup of the tile in the shader uses the same grid and the same bit split: the shader's numbers read from its text against the C# twin.
            Match grid = Regex.Match(shader, @"floor\(\s*puv\s*\*\s*float2\(\s*(\d+)\.0\s*,\s*(\d+)\.0\s*\)");
            Assert.IsTrue(grid.Success, "the tile grid's float2");
            Assert.AreEqual(FrostTiles.GridX, int.Parse(grid.Groups[1].Value), "grid x");
            Assert.AreEqual(FrostTiles.GridY, int.Parse(grid.Groups[2].Value), "grid y");
            Match index = Regex.Match(shader, @"cell\.y\s*\*\s*(\d+)\s*\+\s*cell\.x");
            Assert.IsTrue(index.Success, "the tile index");
            Assert.AreEqual(FrostTiles.GridX, int.Parse(index.Groups[1].Value), "the tile index's row length");
            Match split = Regex.Match(shader, @"i\s*<\s*(\d+)\s*\?\s*lo\s*:\s*hi");
            Assert.IsTrue(split.Success, "the bit split");
            Assert.AreEqual(FrostTiles.FlakeBitsPerFloat, int.Parse(split.Groups[1].Value), "flake bits per float");
        }

        [TestMethod]
        public void TheRepeatedContributionBlockIsTheIncludesOwn()
        {
            int start = shader.IndexOf("#ifndef REDEFINITION_ATMO_CONTRIBUTION_INCLUDED", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "the guarded block is there");
            int end = shader.IndexOf("#endif", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start);
            string block = shader.Substring(start, end - start);
            int compared = 0;
            string flatInclude = Regex.Replace(include, @"\s+", " ");
            foreach (string raw in block.Split('\n'))
            {
                string line = Regex.Replace(raw.Trim(), @"\s+", " ");
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
                // Only the frost's own id is repeated of the ids; its value is the include's.
                Assert.IsTrue(flatInclude.Contains(line), "the include has no line: " + line);
                compared++;
            }
            Assert.IsTrue(compared >= 20, "lines compared " + compared);
            Regex id = new Regex(@"#define\s+ATMO_FEATURE_FROST\s+(\d+)");
            Assert.AreEqual(id.Match(include).Groups[1].Value, id.Match(shader).Groups[1].Value);
        }
    }
}
