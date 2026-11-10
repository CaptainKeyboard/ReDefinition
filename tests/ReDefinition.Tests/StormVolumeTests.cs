using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Seasons;

namespace ReDefinition.Tests
{
    // The raymarched storm volume's pure parts: the 3D noise, the numbers the shader is fed with, the bounding cylinder and the settings.
    [TestClass]
    public class StormVolumeTests
    {
        [TestMethod]
        public void TheVolumeNoiseTilesInAllThreeAxesAndStaysInRange()
        {
            for (int seed = 0; seed < 3; seed++)
            {
                for (float z = 0.15f; z < 4; z += 0.83f)
                {
                    for (float y = 0.1f; y < 4; y += 0.71f)
                    {
                        for (float x = 0.2f; x < 4; x += 0.67f)
                        {
                            float v = StormNoise.Fbm3(x, y, z, 4, 4, seed);
                            Assert.IsTrue(v >= 0f && v <= 1f, "in range: " + v);
                            Assert.AreEqual(v, StormNoise.Fbm3(x + 4, y, z, 4, 4, seed), 1e-4f, "tiles in x");
                            Assert.AreEqual(v, StormNoise.Fbm3(x, y + 4, z, 4, 4, seed), 1e-4f, "tiles in y");
                            Assert.AreEqual(v, StormNoise.Fbm3(x, y, z + 4, 4, 4, seed), 1e-4f, "tiles in z");
                            Assert.AreEqual(v, StormNoise.Fbm3(x, y, z, 4, 4, seed), "deterministic");
                        }
                    }
                }
            }
        }

        [TestMethod]
        public void TheVolumeUsesTheRangeIsDeterministicAndHasNoSeamAtTheEdge()
        {
            const int size = 32;
            byte[] a = StormNoise.Volume(size, 4, 4, 5);
            byte[] b = StormNoise.Volume(size, 4, 4, 5);
            CollectionAssert.AreEqual(a, b);
            Assert.AreEqual(size * size * size, a.Length);
            int min = 255, max = 0;
            double sum = 0;
            foreach (byte v in a) { min = Math.Min(min, v); max = Math.Max(max, v); sum += v; }
            Assert.IsTrue(max - min > 120, "the volume has thin and thick parts: " + min + ".." + max);
            double mean = sum / a.Length;
            Assert.IsTrue(mean > 90 && mean < 165, "the mean is near the middle: " + mean);
            // The last voxel and the first are neighbours on the lattice: the step across the edge is no bigger than one inside.
            double inside = 0, across = 0;
            int count = 0;
            for (int z = 0; z < size; z++)
            {
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size - 1; x++)
                    {
                        inside += Math.Abs(a[(z * size + y) * size + x] - a[(z * size + y) * size + x + 1]);
                        count++;
                    }
                    across += Math.Abs(a[(z * size + y) * size + size - 1] - a[(z * size + y) * size]);
                }
            }
            Assert.IsTrue(across / (size * size) < 2.0 * inside / count, "no seam: across " + across / (size * size) + " inside " + inside / count);
        }

        [TestMethod]
        public void TheWindOffsetsAreWrappedByTheTilesAndTheSequenceDoesNotJump()
        {
            float x, z, shearX, shearZ, fall;
            for (double t = 0; t < 400000; t += 12345.6)
            {
                StormVolumeMath.Wind(33, t, out x, out z, out shearX, out shearZ, out fall);
                Assert.IsTrue(x >= 0 && x < StormVolumeMath.CloudTileM, "x " + x);
                Assert.IsTrue(z >= 0 && z < StormVolumeMath.CloudTileM, "z " + z);
                Assert.IsTrue(fall >= 0 && fall < StormVolumeMath.StreakTileM, "fall " + fall);
                Assert.AreEqual(StormVolumeMath.Shear, Math.Sqrt(shearX * shearX + shearZ * shearZ), 1e-5);
            }
            // The pattern is the same a whole tile of travel later: the wrapped offset repeats.
            float x0, x1, z0, z1, f0, f1;
            double period = StormVolumeMath.CloudTileM / StormVolumeMath.WindSpeed;
            StormVolumeMath.Wind(0, 100, out x0, out z0, out shearX, out shearZ, out f0);
            StormVolumeMath.Wind(0, 100 + period, out x1, out z1, out shearX, out shearZ, out f1);
            Assert.AreEqual(x0, x1, 0.01f);
            Assert.AreEqual(z0, z1, 0.01f);
            Assert.AreEqual(0f, StormVolumeMath.Wrap(-0.0, 10));
            Assert.AreEqual(9f, StormVolumeMath.Wrap(-1, 10), 1e-5f);
        }

        [TestMethod]
        public void TheShaderParametersFollowTheCell()
        {
            Assert.AreEqual(1f / 1200000f, StormVolumeMath.Curvature(600000), 1e-12f);
            float below = StormVolumeMath.BelowM(12000, 600000);
            Assert.AreEqual(120f + 150f, below, 0.01f, "the ground falls 120 m at the rim of a 12 km cell, plus a margin");
            Assert.AreEqual(700f + StormVolumeMath.SnowOnlyHeadroomM, StormVolumeMath.TopM(700), 0.01f, "the volume reaches just over the cloud base");
            Assert.AreEqual(300.0, StormVolumeMath.CullDistanceM(60), 1e-9, "at least 300 m");
            Assert.AreEqual(1000.0, StormVolumeMath.CullDistanceM(200), 1e-9, "5 times the visibility");
            Assert.AreEqual(1050.0, StormVolumeMath.InsideRayM(60, 700, 72000), 1e-9, "the veil's height decides where the fog is short");
            Assert.AreEqual(800.0, StormVolumeMath.InsideRayM(200, 300, 72000), 1e-9, "4 times the visibility");
            Assert.AreEqual(5000.0, StormVolumeMath.InsideRayM(60000, 700, 5000), 1e-9, "never more than the range");
            Assert.AreEqual(16, StormVolumeMath.Steps(1));
            Assert.AreEqual(28, StormVolumeMath.Steps(28));
            Assert.AreEqual(48, StormVolumeMath.Steps(500));
            Assert.IsTrue(StormVolumeMath.Widening * Math.Cos(Math.PI / StormVolumeMath.Segments) > 1.0, "the polygon holds the cell's circle");
        }

        [TestMethod]
        public void TheBoundingCylinderIsClosedAndEveryFaceLooksOutward()
        {
            const int segments = StormVolumeMath.Segments;
            float[] v = StormVolumeMath.CylinderVertices(segments);
            int[] t = StormVolumeMath.CylinderTriangles(segments);
            Assert.AreEqual((segments * 2 + 2) * 3, v.Length);
            Assert.AreEqual(segments * 12, t.Length);
            var edges = new System.Collections.Generic.Dictionary<long, int>();
            for (int i = 0; i < t.Length; i += 3)
            {
                int a = t[i], b = t[i + 1], c = t[i + 2];
                Assert.IsTrue(a >= 0 && a < v.Length / 3 && b >= 0 && b < v.Length / 3 && c >= 0 && c < v.Length / 3);
                // Unity's front face is the one whose normal, cross(b - a, c - a), looks at the viewer.
                double ux = v[b * 3] - v[a * 3], uy = v[b * 3 + 1] - v[a * 3 + 1], uz = v[b * 3 + 2] - v[a * 3 + 2];
                double wx = v[c * 3] - v[a * 3], wy = v[c * 3 + 1] - v[a * 3 + 1], wz = v[c * 3 + 2] - v[a * 3 + 2];
                double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
                double cx = (v[a * 3] + v[b * 3] + v[c * 3]) / 3, cy = (v[a * 3 + 1] + v[b * 3 + 1] + v[c * 3 + 1]) / 3 - 0.5,
                       cz = (v[a * 3 + 2] + v[b * 3 + 2] + v[c * 3 + 2]) / 3;
                Assert.IsTrue(nx * cx + ny * cy + nz * cz > 0, "face " + i / 3 + " looks outward");
                foreach (long e in new[] { Edge(a, b), Edge(b, c), Edge(c, a) }) { int n; edges.TryGetValue(e, out n); edges[e] = n + 1; }
            }
            foreach (var pair in edges) Assert.AreEqual(2, pair.Value, "every edge is shared by two faces: the mesh is closed");
            for (int i = 0; i < v.Length; i += 3)
            {
                Assert.IsTrue(v[i + 1] == 0f || v[i + 1] == 1f, "y is the bottom or the top");
                Assert.IsTrue(Math.Sqrt(v[i] * v[i] + v[i + 2] * v[i + 2]) < 1.0001);
            }
        }

        private static long Edge(int a, int b)
        {
            return a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        }

        [TestMethod]
        public void TheVolumeStepsComeBackAndAreBroughtIntoTheirRanges()
        {
            string folder = Path.Combine(Path.GetTempPath(), "volume-settings-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(folder, "settings.cfg");
            ConfigNode saved = SeasonsSettings.Snapshot();
            try
            {
                Assert.AreEqual(28, SeasonsSettings.StormVolumeStepsDefault);
                SeasonsSettings.StormVolumeSteps = 40;
                SeasonsSettings.Write(file);
                SeasonsSettings.StormVolumeSteps = 28;
                SeasonsSettings.Read(file);
                Assert.AreEqual(40, SeasonsSettings.StormVolumeSteps);

                File.WriteAllText(file, "stormVolumeSteps = 3\n");
                SeasonsSettings.Read(file);
                Assert.AreEqual(16, SeasonsSettings.StormVolumeSteps);
                File.WriteAllText(file, "stormVolumeSteps = 500\n");
                SeasonsSettings.Read(file);
                Assert.AreEqual(48, SeasonsSettings.StormVolumeSteps);
                File.WriteAllText(file, "stormVolumeSteps = x\n");
                SeasonsSettings.Read(file);
                Assert.AreEqual(48, SeasonsSettings.StormVolumeSteps, "a value that is not a number is ignored");
            }
            finally
            {
                SeasonsSettings.Restore(saved);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        [TestMethod]
        public void TheBundleAndTheShaderAreWhereTheLoaderLooks()
        {
            string repo = AppContext.BaseDirectory;
            while (repo != null && !File.Exists(Path.Combine(repo, "ReDefinition.sln"))) repo = Path.GetDirectoryName(repo);
            Assert.IsNotNull(repo, "the repository root");
            string shader = Path.Combine(repo, "unity", "Assets", "ReDefinitionSeasons", "Shaders", "ReDefinitionSeasonsStormVolume.shader");
            Assert.IsTrue(File.Exists(shader), shader);
            Assert.IsTrue(File.ReadAllText(shader).Contains("Shader \"" + StormShaders.VolumeShaderName + "\""), "the shader's name is the one the loader asks for");
            Assert.AreEqual("seasons.shaders", StormShaders.BundleName);
            Assert.IsTrue(File.ReadAllText(Path.Combine(repo, "unity", "Assets", "ReDefinitionSeasons", "Editor", "SeasonsBundleBuilder.cs"))
                .Contains("\"" + StormShaders.BundleName + "\""), "the builder writes the file the loader reads");
        }
    }
}
