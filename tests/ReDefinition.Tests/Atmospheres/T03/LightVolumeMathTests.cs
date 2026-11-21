using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.Tests
{
    // The light volume's addressing (LightVolumeMath, the CPU twin of ReDefinitionAtmospheresCloudLight.cginc and the layer shader's pass 8): the fill's voxel centre is where the
    // fetch reads that voxel, a texel holds the one voxel of the window congruent to it, a window holds the bilinear footprint of the points around the camera, and the shader's
    // constants are the twin's.
    [TestClass]
    public class LightVolumeMathTests
    {
        private const float R = 600000f;
        private static readonly Vector3 Up = new Vector3(0.3f, 0.8f, -0.52f).normalized;
        private static readonly Vector3 East = Vector3.Cross(Vector3.up, Up).normalized;
        private static readonly Vector3 North = Vector3.Cross(Up, East);

        [TestMethod]
        public void AVoxelCentreFromItsCoordinatesReadsBackAtTheSameCoordinatesAndAltitude()
        {
            foreach (var xy in new[] { new Vector2(0f, 0f), new Vector2(15312.5f, -9843.75f), new Vector2(-80000f, 79000f) })
                foreach (float alt in new[] { -300f, 2500f, 11000f })
                {
                    Vector3 p = LightVolumeMath.FromGnomonic(xy, alt, Up, East, North, R);
                    Vector2 back;
                    Assert.IsTrue(LightVolumeMath.Gnomonic(p, Up, East, North, R, out back));
                    Assert.AreEqual(xy.x, back.x, 0.05f);
                    Assert.AreEqual(xy.y, back.y, 0.05f);
                    Assert.AreEqual(R + alt, p.magnitude, 0.1f);
                }
        }

        [TestMethod]
        public void APointBeyondTheCutHasNoCoordinates()
        {
            Vector2 xy;
            Vector3 far = (Up * 0.4f + East * 0.92f).normalized * R;
            Assert.IsFalse(LightVolumeMath.Gnomonic(far, Up, East, North, R, out xy));
        }

        [TestMethod]
        public void ATexelHoldsTheOneVoxelOfTheWindowCongruentToIt()
        {
            const int grid = 128;
            foreach (float origin in new[] { -64f, -65f, 0f, 37f, -1000f, 1000f })
                for (int texel = 0; texel < grid; texel++)
                {
                    float v = LightVolumeMath.VoxelOfTexel(texel, origin, grid);
                    Assert.IsTrue(v >= origin && v < origin + grid, "voxel " + v + " outside the window at " + origin);
                    Assert.AreEqual(0f, ((v - texel) % grid + grid) % grid, 1e-3f);
                }
        }

        [TestMethod]
        public void TheWindowAroundAPointHoldsItsFootprintWithHalfTheGridToSpare()
        {
            const int grid = 128;
            const float vox = 312.5f;
            foreach (var xy in new[] { new Vector2(0f, 0f), new Vector2(15312.5f, -9843.75f), new Vector2(-1.2e5f, 3.3e4f) })
            {
                Vector2 o = LightVolumeMath.WindowOrigin(xy, vox, grid);
                Assert.IsTrue(LightVolumeMath.EdgeDistance(xy, vox, o, grid) >= grid / 2 - 2);
                Assert.IsTrue(LightVolumeMath.EdgeDistance(xy + new Vector2(grid * vox, 0f), vox, o, grid) < 0f);
            }
        }

        [TestMethod]
        public void TheShaderUsesTheTwinsConstantsAndFormulas()
        {
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            string dir = Path.Combine(root, "unity", "Assets", "ReDefinitionAtmospheres", "Shaders");
            string light = File.ReadAllText(Path.Combine(dir, "ReDefinitionAtmospheresCloudLight.cginc"));
            string layers = File.ReadAllText(Path.Combine(dir, "ReDefinitionAtmospheresLayers.shader"));
            Assert.AreEqual(LightVolumeMath.MinCos, Define(light, "LV_MIN_COS"));
            Assert.AreEqual(LightVolumeMath.MaxSlices, Define(light, "LV_MAX_SLICES"));
            Assert.AreEqual(LightVolumeMath.MinStored, Define(light, "LV_MIN_STORED"), 1e-9f);
            StringAssert.Contains(light, "float4 _LvO[" + LightVolumeMath.MaxSlices + "];");
            // the fetch: gnomonic coordinates, the bilinear footprint against each slice's window
            StringAssert.Contains(light, "float2 xy = _PlanetR * float2(dot(n, _LvE.xyz), dot(n, _LvN.xyz)) / c;");
            StringAssert.Contains(light, "float2 g = xy / vox - 0.5;");
            StringAssert.Contains(light, "float2 d0 = min(g - o0, o0 + gridN - 1.0 - g), d1 = min(g - o1, o1 + gridN - 1.0 - g);");
            // the fill: the texel's voxel and its centre
            StringAssert.Contains(layers, "float2 idx = _LvSlice.zw + rel - gridN * floor(rel / gridN);");
            StringAssert.Contains(layers, "float2 xy = (idx + 0.5) * vox;");
            StringAssert.Contains(layers, "float3 p = normalize(_LvU.xyz + (xy.x * _LvE.xyz + xy.y * _LvN.xyz) / _PlanetR) * (_PlanetR + alt);");
        }

        private static float Define(string text, string name)
        {
            Match m = Regex.Match(text, "#define " + name + @" ([0-9.eE+-]+)");
            Assert.IsTrue(m.Success, name);
            return float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
    }
}
