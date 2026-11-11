using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.D38
{
    // The aurora model (L0): determinism, the night gate, the oval, the distribution of the activity, the strong event, and the
    // agreement of the shader, the config file and the renderer on names and keys.
    [TestClass]
    public class AuroraModelTests
    {
        [TestMethod]
        public void TheActivityIsDeterministicAndVaries()
        {
            for (double ut = 0; ut < 5e6; ut += 123456.7)
            {
                Assert.AreEqual(AuroraModel.Activity(ut), AuroraModel.Activity(ut));
                float a = AuroraModel.Activity(ut);
                Assert.IsTrue(a >= 0f && a <= 1f, "activity in range at " + ut);
            }
            bool differs = false;
            for (double ut = 0; ut < 2e5 && !differs; ut += 600) differs = AuroraModel.Activity(ut) != AuroraModel.Activity(ut + 7200);
            Assert.IsTrue(differs, "the activity changes with time");
        }

        [TestMethod]
        public void TheActivityIsAboveZeroPointThreeAboutThirtyPercentOfTheTime()
        {
            int above = 0, quiet = 0, total = 0, high = 0;
            // 1500 days sampled every 5 minutes.
            for (double ut = 0; ut < 1500.0 * 86400.0; ut += 300.0)
            {
                float a = AuroraModel.Activity(ut);
                total++;
                if (a > 0.3f) above++;
                if (a <= 0.0f) quiet++;
                if (a > 0.9f) high++;
            }
            double share = above / (double)total;
            Assert.IsTrue(share > 0.25 && share < 0.35, "P(A>0.3) = " + share.ToString("0.000"));
            Assert.IsTrue(quiet / (double)total > 0.45, "quiet nights are common: " + (quiet / (double)total).ToString("0.000"));
            Assert.IsTrue(high > 0, "a strong night exists");
        }

        [TestMethod]
        public void TheOvalMovesTowardsTheEquatorAndWidensWithActivity()
        {
            Assert.AreEqual(78f, AuroraModel.RingLatitudeDeg(0f, 0f, 60f), 1e-4f);
            Assert.AreEqual(66f, AuroraModel.RingLatitudeDeg(1f, 0f, 60f), 1e-4f);
            Assert.AreEqual(3f, AuroraModel.WidthDeg(0f), 1e-4f);
            Assert.AreEqual(9f, AuroraModel.WidthDeg(1f), 1e-4f);
            float last = float.MaxValue;
            for (float a = 0f; a <= 1f; a += 0.1f)
            {
                float lat = AuroraModel.RingLatitudeDeg(a, 0f, 60f);
                Assert.IsTrue(lat <= last, "the oval does not move back as the activity rises");
                last = lat;
            }
            Assert.AreEqual(70f, AuroraModel.RingLatitudeDeg(1f, 0f, 70f), 1e-4f, "the floor of the body holds");
            Assert.AreEqual(70f, AuroraModel.RingLatitudeDeg(1f, 1f, 70f), 1e-4f);
            Assert.AreEqual(60f, AuroraModel.RingLatitudeDeg(1f, 1f, 60f), 1e-4f, "a strong event reaches 60 degrees");
        }

        [TestMethod]
        public void TheNightGateOpensBetweenMinusThreeAndMinusNineDegrees()
        {
            Assert.AreEqual(0f, AuroraModel.NightGate(10f));
            Assert.AreEqual(0f, AuroraModel.NightGate(-3f));
            Assert.AreEqual(1f, AuroraModel.NightGate(-9f));
            Assert.AreEqual(1f, AuroraModel.NightGate(-25f));
            Assert.AreEqual(0.5f, AuroraModel.NightGate(-6f), 1e-5f);
            float last = -1f;
            for (float e = 0f; e >= -12f; e -= 0.25f)
            {
                float g = AuroraModel.NightGate(e);
                Assert.IsTrue(g >= last, "the gate rises as the sun sinks");
                last = g;
            }
        }

        [TestMethod]
        public void AStrongEventIsRareBoundedAndDeterministic()
        {
            int active = 0, total = 0;
            float max = 0f;
            for (double ut = 0; ut < 4000.0 * 86400.0; ut += 1800.0)
            {
                float e = AuroraModel.StrongEvent(ut);
                Assert.AreEqual(e, AuroraModel.StrongEvent(ut));
                Assert.IsTrue(e >= 0f && e <= 1f);
                total++;
                if (e > 0f) active++;
                if (e > max) max = e;
            }
            double share = active / (double)total;
            Assert.IsTrue(share > 0.002 && share < 0.04, "events fill " + share.ToString("0.0000") + " of the time");
            Assert.IsTrue(max > 0.95f, "an event reaches its top: " + max);
        }

        [TestMethod]
        public void TheFrameIsGatedByEnabledNightAndActivity()
        {
            // Find a moment of high activity.
            double high = -1;
            for (double ut = 0; ut < 400.0 * 86400.0 && high < 0; ut += 300.0)
                if (AuroraModel.Activity(ut) > 0.8f) high = ut;
            Assert.IsTrue(high >= 0);
            AuroraFrame night = AuroraModel.Compute(high, -20f, true, 60f, 1f);
            Assert.IsTrue(night.Draw && night.Intensity > 0.3f);
            Assert.IsFalse(AuroraModel.Compute(high, 20f, true, 60f, 1f).Draw, "no aurora in the day");
            Assert.IsFalse(AuroraModel.Compute(high, -20f, false, 60f, 1f).Draw, "no aurora where the body has none");
            Assert.IsFalse(AuroraModel.Compute(high, -20f, true, 60f, 0f).Draw, "strength zero");
            double quiet = -1;
            for (double ut = 0; ut < 400.0 * 86400.0 && quiet < 0; ut += 300.0)
                if (AuroraModel.Activity(ut) <= 0f && AuroraModel.StrongEvent(ut) <= 0f) quiet = ut;
            Assert.IsFalse(AuroraModel.Compute(quiet, -20f, true, 60f, 1f).Draw, "a quiet night draws nothing");
        }

        [TestMethod]
        public void TheHorizonFadeIsZeroBelowThreeDegreesAndFullFromTen()
        {
            Assert.AreEqual(0f, AuroraModel.HorizonFade(0f, 3f, 10f));
            Assert.AreEqual(0f, AuroraModel.HorizonFade(3f, 3f, 10f));
            Assert.AreEqual(1f, AuroraModel.HorizonFade(10f, 3f, 10f));
            Assert.AreEqual(1f, AuroraModel.HorizonFade(40f, 3f, 10f));
            Assert.IsTrue(AuroraModel.HorizonFade(6f, 3f, 10f) > 0f);
        }

        [TestMethod]
        public void TheSunElevationFromTheDotProduct()
        {
            Assert.AreEqual(0f, AuroraModel.SunElevationDeg(0.0), 1e-4f);
            Assert.AreEqual(90f, AuroraModel.SunElevationDeg(1.0), 1e-3f);
            Assert.AreEqual(-30f, AuroraModel.SunElevationDeg(-0.5), 1e-3f);
            Assert.AreEqual(90f, AuroraModel.SunElevationDeg(1.2), 1e-3f);
        }

        [TestMethod]
        public void TheNoiseTextureIsDeterministicTileableAndSpread()
        {
            byte[] a = AuroraNoise.Generate(64, 7u), b = AuroraNoise.Generate(64, 7u), c = AuroraNoise.Generate(64, 8u);
            CollectionAssert.AreEqual(a, b);
            CollectionAssert.AreNotEqual(a, c);
            Assert.AreEqual(64 * 64 * 4, a.Length);
            for (int ch = 0; ch < 4; ch++)
            {
                double sum = 0;
                int min = 255, max = 0;
                for (int i = ch; i < a.Length; i += 4)
                {
                    sum += a[i];
                    min = Math.Min(min, a[i]);
                    max = Math.Max(max, a[i]);
                }
                double mean = sum / (a.Length / 4) / 255.0;
                Assert.IsTrue(mean > 0.35 && mean < 0.65, "channel " + ch + " mean " + mean.ToString("0.00"));
                Assert.IsTrue(max - min > 100, "channel " + ch + " uses the range");
            }
            // The wrap: the value one lattice period over equals the value at the origin.
            Assert.AreEqual(AuroraNoise.Tile(0.13f, 0.77f, 8, 5u), AuroraNoise.Tile(1.13f, 1.77f, 8, 5u), 1e-4f);
        }

        [TestMethod]
        public void TheSettingDecidesTheShell()
        {
            bool fallback;
            Assert.IsFalse(AuroraPolicy.ShellWanted(AuroraMode.Off, false, out fallback));
            Assert.IsTrue(AuroraPolicy.ShellWanted(AuroraMode.Auto, false, out fallback));
            Assert.IsFalse(fallback);
            Assert.IsFalse(AuroraPolicy.ShellWanted(AuroraMode.Auto, true, out fallback), "Auto leaves a foreign aurora to its mod");
            Assert.IsTrue(AuroraPolicy.ShellWanted(AuroraMode.Shell2D, true, out fallback));
            Assert.IsTrue(AuroraPolicy.ShellWanted(AuroraMode.Volumetric, false, out fallback));
            Assert.IsTrue(fallback, "the volumetric variant is not built");
        }

        [TestMethod]
        public void TheAmbientShareIsOffByDefaultAndNeverAboveTwoPercent()
        {
            Assert.AreEqual(0f, AuroraPolicy.AmbientShare(false, 1f, 1f));
            Assert.AreEqual(0.02f, AuroraPolicy.AmbientShare(true, 1f, 1f), 1e-6f);
            Assert.AreEqual(0.02f, AuroraPolicy.AmbientShare(true, 5f, 9f), 1e-6f);
            Assert.AreEqual(0.005f, AuroraPolicy.AmbientShare(true, 0.5f, 0.5f), 1e-6f);
            Assert.AreEqual(0f, AuroraPolicy.AmbientShare(true, 1f, 0f));
        }

        [TestMethod]
        public void AForeignAuroraIsRecognisedByName()
        {
            Assert.IsTrue(AuroraForeign.IsAuroraName("Kerbin-Aurora-red"));
            Assert.IsTrue(AuroraForeign.IsAuroraName("kerbin-aurora"));
            Assert.IsFalse(AuroraForeign.IsAuroraName("Kerbin-base-layer"));
            Assert.IsFalse(AuroraForeign.IsAuroraName(null));
            Assert.IsNull(AuroraForeign.Find(null));
        }

        [TestMethod]
        public void TheShaderTheConfigAndTheRendererAgree()
        {
            string root = Root();
            string shader = File.ReadAllText(Path.Combine(root, "unity", "Assets", "ReDefinitionAtmospheres", "Shaders", "ReDefinitionAtmospheresAurora.shader"));
            Assert.IsTrue(shader.Contains("Shader \"" + AuroraRenderer.ShaderName + "\""), "the shader's name is the one the renderer asks for");
            foreach (string property in new[] { "_AuroraPlanet", "_AuroraPole", "_AuroraRefA", "_AuroraRing", "_AuroraLayer", "_AuroraLook", "_AuroraLook2",
                                                 "_AuroraColourA", "_AuroraColourB", "_AuroraScreen", "_AuroraNoise", "_AuroraDepth" })
                Assert.IsTrue(shader.Contains(property), "the shader declares " + property);

            string pass = File.ReadAllText(Path.Combine(root, "src", "Atmospheres", "Light", "Aurora", "AuroraPass.cs"));
            foreach (string property in new[] { "_AuroraPlanet", "_AuroraPole", "_AuroraRefA", "_AuroraRing", "_AuroraLayer", "_AuroraLook", "_AuroraLook2",
                                                 "_AuroraColourA", "_AuroraColourB", "_AuroraScreen", "_AuroraNoise", "_AuroraDepth" })
                Assert.IsTrue(pass.Contains("\"" + property + "\""), "the pass sets " + property);

            // Every tuning key the renderer reads has a line in the config, with the same default.
            string renderer = File.ReadAllText(Path.Combine(root, "src", "Atmospheres", "Light", "Aurora", "AuroraRenderer.cs"));
            string cfg = File.ReadAllText(Path.Combine(root, "GameData", "ReDefinition", "Atmospheres", "Aurora", "AtmospheresAurora.cfg"));
            var keys = new Regex("Tuning\\.(?:Get|Flag)\\(\"(aurora\\.[A-Za-z]+)\"");
            int count = 0;
            foreach (Match m in keys.Matches(renderer))
            {
                count++;
                Assert.IsTrue(Regex.IsMatch(cfg, "^\\s*" + Regex.Escape(m.Groups[1].Value) + "\\s*=", RegexOptions.Multiline), "the config names " + m.Groups[1].Value);
            }
            Assert.IsTrue(count >= 14, "the renderer reads its keys through Tuning: " + count);
            AuroraLook d = AuroraLook.Defaults();
            Assert.IsTrue(Regex.IsMatch(cfg, "aurora\\.gain\\s*=\\s*" + d.Gain.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "\\b"));
            Assert.IsTrue(Regex.IsMatch(cfg, "aurora\\.horizonStartDeg\\s*=\\s*" + d.HorizonStartDeg.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "\\b"));
            Assert.IsTrue(cfg.Contains("@REDEFINITION_BODY[Kerbin]"), "Kerbin gets the aurora rule");
        }

        [TestMethod]
        public void NothingHardWiresAKerbinConstant()
        {
            string root = Root();
            foreach (string file in Directory.GetFiles(Path.Combine(root, "src", "Atmospheres", "Light", "Aurora"), "*.cs"))
            {
                string text = File.ReadAllText(file);
                Assert.IsFalse(Regex.IsMatch(text, "\"Kerbin\"|600000|\\b70000f?\\b"), Path.GetFileName(file) + " carries no body constant");
            }
        }

        private static string Root()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.Exists(Path.Combine(dir, "unity"))) dir = Path.GetDirectoryName(dir);
            Assert.IsNotNull(dir, "the repository root");
            return dir;
        }
    }
}
