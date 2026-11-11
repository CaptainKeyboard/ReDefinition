using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.C22
{
    // The pure numbers of the precipitation renderer: hash, counts, size floor, gust limits, fog limits.
    [TestClass]
    public class PrecipMathTests
    {
        [TestMethod]
        public void TheHashIsDeterministicAndSpreadsOverTheBox()
        {
            for (int cell = 0; cell < 50; cell++)
            {
                float ax, ay, az, bx, by, bz;
                PrecipMath.Place(cell, 12345, out ax, out ay, out az);
                PrecipMath.Place(cell, 12345, out bx, out by, out bz);
                Assert.AreEqual(ax, bx);
                Assert.AreEqual(ay, by);
                Assert.AreEqual(az, bz);
                Assert.IsTrue(ax >= 0f && ax < 1f && ay >= 0f && ay < 1f && az >= 0f && az < 1f, "the place is inside the box");
            }
            float x1, y1, z1, x2, y2, z2;
            PrecipMath.Place(7, 1, out x1, out y1, out z1);
            PrecipMath.Place(7, 2, out x2, out y2, out z2);
            Assert.IsTrue(x1 != x2 || y1 != y2 || z1 != z2, "another seed gives another pattern");

            // Each axis is spread evenly: ten bins of 20000 flakes are each within 10 % of the mean.
            var bins = new int[3, 10];
            const int N = 20000;
            for (int cell = 0; cell < N; cell++)
            {
                float x, y, z;
                PrecipMath.Place(cell, 99, out x, out y, out z);
                bins[0, (int)(x * 10)]++;
                bins[1, (int)(y * 10)]++;
                bins[2, (int)(z * 10)]++;
            }
            for (int axis = 0; axis < 3; axis++)
                for (int b = 0; b < 10; b++)
                    Assert.IsTrue(Math.Abs(bins[axis, b] - N / 10) < N / 100, "axis " + axis + " bin " + b + " has " + bins[axis, b]);
        }

        [TestMethod]
        public void TheHashMatchesTheShaderConstants()
        {
            string shader = System.IO.File.ReadAllText(ShaderPath());
            Assert.IsTrue(shader.Contains("0x7feb352du") && shader.Contains("0x846ca68bu") && shader.Contains("0x9e3779b9u") && shader.Contains("0x85ebca6bu"),
                "the shader mixes with the constants of PrecipMath.Hash");
            Assert.IsTrue(shader.Contains("Shader \"" + PrecipitationRenderer.ShaderName + "\""), "the shader's name is the one the renderer asks for");
            // The same value through the two formulas written out for one input: the shader's H3 is the module's Hash(cell, seed, salt).
            uint inner = unchecked(5u + 0x9e3779b9U * 1u);
            uint mixed = unchecked(7u * 0x85ebca6bU);
            Assert.AreEqual(PrecipMath.Hash(PrecipMath.Hash(inner) ^ mixed), PrecipMath.Hash(5, 7, 0));
        }

        private static string ShaderPath()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir, "unity")))
                dir = System.IO.Path.GetDirectoryName(dir);
            Assert.IsNotNull(dir, "the repository root");
            return System.IO.Path.Combine(dir, "unity", "Assets", "ReDefinitionAtmospheres", "Shaders", "ReDefinitionAtmospheresPrecip.shader");
        }

        [TestMethod]
        public void TheWrapKeepsEveryFlakeInTheBoxAroundTheCamera()
        {
            for (int cell = 0; cell < 200; cell++)
            {
                float x, y, z;
                PrecipMath.Place(cell, 3, out x, out y, out z);
                for (float drift = -3.3f; drift < 3.3f; drift += 0.37f)
                {
                    float w = PrecipMath.Wrap(x, drift, 0.21f);
                    Assert.IsTrue(w >= 0f && w < 1f);
                }
            }
            // A camera that moved one whole box sees the same pattern.
            float a = PrecipMath.Wrap(0.3f, 0.1f, 0.25f), b = PrecipMath.Wrap(0.3f, 0.1f, 1.25f);
            Assert.AreEqual(a, b, 1e-5f);
            Assert.AreEqual(0f, PrecipMath.EdgeFade(0f, 0.1f), "a flake at the edge is invisible");
            Assert.AreEqual(1f, PrecipMath.EdgeFade(0.5f, 0.1f), "the middle is full");
        }

        [TestMethod]
        public void CountsScaleWithLevelToThePowerOnePointThree()
        {
            Assert.AreEqual(0f, PrecipMath.LevelScale(0f));
            Assert.AreEqual((float)Math.Pow(0.5, 1.3), PrecipMath.LevelScale(0.5f), 1e-6f);
            Assert.AreEqual(1f, PrecipMath.LevelScale(1f), 1e-6f);
            // At a visibility that does not limit, the count follows the power law.
            int low = PrecipMath.FlakeCount(0.3f, 100f, 18f, 18f, 0.34f, 0.01f, 10f, 8000f, 1f);
            int high = PrecipMath.FlakeCount(0.6f, 100f, 18f, 18f, 0.34f, 0.01f, 10f, 8000f, 1f);
            Assert.AreEqual(Math.Pow(2.0, 1.3), (double)high / low, 0.05);
            // Monotone in level.
            int previous = 0;
            for (float level = 0.04f; level <= 1f; level += 0.05f)
            {
                int n = PrecipMath.FlakeCount(level, 800f, 18f, 18f, 0.34f, 0.05f, 0.35f, 8000f, 1f);
                Assert.IsTrue(n >= previous, "count falls at level " + level);
                previous = n;
            }
        }

        [TestMethod]
        public void NothingDrawsBelowTheLowestLevelAndTheNearLayerWaitsForPointTwo()
        {
            for (int layer = 0; layer < 3; layer++)
            {
                Assert.IsFalse(PrecipMath.LayerActive(layer, 0.0399f));
                Assert.AreEqual(0, PrecipMath.FlakeCount(0.03f, 100f, 18f, 18f, 0.34f, 0.05f, 0.35f, 8000f, 1f));
            }
            Assert.IsFalse(PrecipMath.LayerActive(PrecipMath.Near, 0.19f));
            Assert.IsTrue(PrecipMath.LayerActive(PrecipMath.Near, 0.2f));
            Assert.IsTrue(PrecipMath.LayerActive(PrecipMath.Mid, 0.05f));
            Assert.IsTrue(PrecipMath.LayerActive(PrecipMath.Far, 0.05f));
            Assert.AreEqual(0, PrecipMath.FlakeCount(0.5f, 100f, 18f, 18f, 0.34f, 0.05f, 0.35f, 8000f, 0f), "density 0 draws nothing");
            Assert.IsFalse(PrecipMath.LayerActive(1, float.NaN));
        }

        [TestMethod]
        public void ClearAirAsksForFewFlakesAndTheCapRulesInADenseFall()
        {
            int clear = PrecipMath.FlakeCount(0.6f, 20000f, 18f, 18f, 0.34f, 0.05f, 0.35f, 8000f, 1f);
            int dense = PrecipMath.FlakeCount(0.6f, 60f, 18f, 18f, 0.34f, 0.05f, 0.35f, 8000f, 1f);
            Assert.IsTrue(clear < dense / 10, "a visibility of 20 km carries few flakes: " + clear + " vs " + dense);
            int capped = PrecipMath.FlakeCount(1f, 20f, 36f, 36f, 0.6f, 0.02f, 0.35f, 8000f, 1f);
            Assert.IsTrue(capped <= 8000 * 0.6f + 1, "the cap rules at the top of the scale: " + capped);
        }

        [TestMethod]
        public void TheSizeFloorIsThreePixelsAt1080AndScalesWithTheHeight()
        {
            float fov = 60f * (float)Math.PI / 180f;
            float metresPerPixel = 2f * 30f * (float)Math.Tan(fov / 2f) / 1080f;
            float size = PrecipMath.SizeWithFloor(0.02f, 30f, fov, 1080f, 3f);
            Assert.AreEqual(3f * metresPerPixel, size, 1e-5f, "a small flake is raised to 3 pixels");
            Assert.AreEqual(0.5f, PrecipMath.SizeWithFloor(0.5f, 30f, fov, 1080f, 3f), "a large flake keeps its size");
            float at2160 = PrecipMath.SizeWithFloor(0.02f, 30f, fov, 2160f, 3f);
            Assert.AreEqual(size, at2160, 1e-5f, "6 pixels at 2160 lines are the same angle as 3 at 1080");
            Assert.AreEqual(1f, PrecipMath.FloorContrast(0.5f, 0.5f, 0.45f));
            Assert.AreEqual(0.45f, PrecipMath.FloorContrast(0.001f, size, 0.45f), 1e-6f, "the contrast of a floored flake does not go below the minimum");
            float half = PrecipMath.FloorContrast(size * 0.8f, size, 0.1f);
            Assert.AreEqual(0.64f, half, 1e-5f, "the opacity follows the area");
        }

        [TestMethod]
        public void TheGustFactorMovesAtMostAQuarterPerSecond()
        {
            float factor = 1f;
            float t = 0f;
            while (factor < 1.4f - 1e-4f && t < 10f)
            {
                float next = PrecipMath.Slew(factor, 1.4f, PrecipMath.GustSlewPerS, 1f / 60f);
                Assert.IsTrue(next - factor <= PrecipMath.GustSlewPerS / 60f + 1e-6f, "one frame moved too far");
                factor = next;
                t += 1f / 60f;
            }
            Assert.IsTrue(t >= 1.59f, "0.4 at 25 % a second takes 1.6 s, took " + t);
            Assert.AreEqual(1f + 0.4f * 1f, PrecipMath.GustTarget(1f, 0.4f), 1e-6f);
            Assert.AreEqual(1f - 0.4f, PrecipMath.GustTarget(0f, 0.4f), 1e-6f);
            Assert.AreEqual(1f, PrecipMath.GustTarget(0.5f, 0.4f), 1e-6f);
            Assert.IsTrue(PrecipMath.GustAmplitude(0.04f) < PrecipMath.GustAmplitude(1f));
        }

        [TestMethod]
        public void TheGustTurnsTheFlakesAtMostTwelveDegreesAtThirtyDegreesPerSecond()
        {
            Assert.AreEqual(12f, PrecipMath.GustDirectionTarget(1f), 1e-5f);
            Assert.AreEqual(-12f, PrecipMath.GustDirectionTarget(0f), 1e-5f);
            float angle = 0f;
            float t = 0f;
            float previous = 0f;
            while (Math.Abs(angle - 12f) > 1e-4f && t < 5f)
            {
                angle = PrecipMath.SlewAngle(angle, 12f, PrecipMath.GustDirectionRateDegPerS, 1f / 60f);
                Assert.IsTrue(Math.Abs(angle - previous) <= 0.5f + 1e-4f, "30 degrees a second is 0.5 a frame");
                previous = angle;
                t += 1f / 60f;
            }
            Assert.AreEqual(12f / 30f, t, 0.03f);
            // The short way round 360.
            Assert.AreEqual(365f, PrecipMath.SlewAngle(355f, 5f, 1000f, 1f), 1e-3f, "the short way from 355 to 5 is across 360");
            Assert.AreEqual(0.03f, PrecipMath.GustVeilAlpha(1.1f), 1e-5f, "0.03 of veil per 0.1 of gust");
            Assert.AreEqual(0f, PrecipMath.GustVeilAlpha(0.8f), "a lull adds nothing");
        }

        [TestMethod]
        public void TheFogGustStaysWithinTwelvePercentAndThreeSeconds()
        {
            float min = float.MaxValue, max = float.MinValue;
            for (double t = 0; t < 30; t += 0.05)
            {
                float f = PrecipMath.FogFactor(t, 0.5f, 0.5f);
                min = Math.Min(min, f);
                max = Math.Max(max, f);
            }
            Assert.IsTrue(max <= 1.12001f && min >= 0.87999f, "amplitude is capped at 12 %: " + min + " .. " + max);
            // The period is held to 3 s: the factor at 0.75 s of a 3 s period is at its peak, whatever shorter period was asked.
            Assert.AreEqual(1.12f, PrecipMath.FogFactor(0.75, 0.12f, 0.1f), 1e-4f);
            Assert.AreEqual(1f, PrecipMath.FogFactor(100.0, 0f, 6f), 1e-6f);
        }

        [TestMethod]
        public void TheFogRampNeverMovesFasterThanThreeHertzOfTwelvePercent()
        {
            float slew = PrecipMath.FogSlewPerS(3f);
            Assert.AreEqual(2f * (float)Math.PI * 3f * 0.12f, slew, 1e-5f);
            float density = 0.001f;
            float worst = 0f;
            for (int i = 0; i < 600; i++)
            {
                float next = PrecipMath.FogStep(density, 0.004f, 3f, 1f / 60f);
                worst = Math.Max(worst, (next - density) / Math.Max(Math.Max(density, 0.004f), 1e-6f) * 60f);
                density = next;
            }
            Assert.IsTrue(worst <= slew + 1e-3f, "relative rate " + worst + " is over " + slew);
            Assert.AreEqual(0.004f, density, 1e-6f, "it arrives");
            // A 6 s swing of 12 % passes through the ramp unchanged.
            var ramp = new FogRamp();
            double sumErr = 0;
            ramp.Step(0.002f, 1f, 1f, 1f, 1f, 0.12f, 6f, 3f, 1f, 0f);
            for (int i = 1; i < 720; i++)
            {
                ramp.Step(0.002f, 1f, 1f, 1f, 1f, 0.12f, 6f, 3f, 1f, 1f / 60f);
                float want = 0.002f * PrecipMath.FogFactor(i / 60.0, 0.12f, 6f);
                sumErr = Math.Max(sumErr, Math.Abs(ramp.Density - want) / want);
            }
            Assert.IsTrue(sumErr < 0.01, "the ramp follows a slow swing: " + sumErr);
        }

        [TestMethod]
        public void TheFogRampTakesTheFirstTargetAtOnceAndScalesTheColour()
        {
            var ramp = new FogRamp();
            ramp.Step(0.003f, 0.8f, 0.6f, 0.4f, 0f, 0f, 6f, 3f, 0.5f, 0.016f);
            Assert.AreEqual(0.003f, ramp.Density, 1e-7f);
            Assert.AreEqual(0.4f, ramp.R, 1e-6f);
            Assert.AreEqual(0.3f, ramp.G, 1e-6f);
            Assert.AreEqual(0.2f, ramp.B, 1e-6f);
            ramp.Reset();
            Assert.IsFalse(ramp.Started);
        }
    }
}
