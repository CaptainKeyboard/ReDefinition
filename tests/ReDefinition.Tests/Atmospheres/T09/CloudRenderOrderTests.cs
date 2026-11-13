using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The order of the own cloud pass against Scatterer's screen-space scattering at the pass's camera event (OwnCloudPlan.MoveBehindScattering).
    // The lists are the buffer names of K-T2d start 51 (T09d env cameraBuffers) with the buffers the installed Scatterer adds per frame
    // (ScatteringCommandBuffer: "Scatterer screen-space scattering CommandBuffer", "Scatterer 1/4 res screen-space scattering CommandBuffer").
    // The move itself needs the game; its row is T09d.env.renderOrder.
    [TestClass]
    public class CloudRenderOrderTests
    {
        private const string Pass = "ReDefinition layer pass";

        private static List<string> Start51(string scattering)
        {
            var names = new List<string> { "ReDefinition moon glow", "ReDefinition layer depth", Pass, "ReDefinition volume pass" };
            if (scattering != null) names.Add(scattering);
            return names;
        }

        [TestMethod]
        public void ThePassMovesWhereScatterersScatteringStandsBehindIt()
        {
            Assert.IsTrue(OwnCloudPlan.MoveBehindScattering(true, Start51("Scatterer screen-space scattering CommandBuffer"), Pass));
            Assert.IsTrue(OwnCloudPlan.MoveBehindScattering(true, Start51("Scatterer 1/4 res screen-space scattering CommandBuffer"), Pass));
        }

        [TestMethod]
        public void WithoutAScatteringBufferTheOrderStaysAsItIs()
        {
            Assert.IsFalse(OwnCloudPlan.MoveBehindScattering(true, Start51(null), Pass), "no Scatterer: the default order");
            Assert.IsFalse(OwnCloudPlan.MoveBehindScattering(true, new List<string>(), Pass));
            Assert.IsFalse(OwnCloudPlan.MoveBehindScattering(true, null, Pass));
        }

        [TestMethod]
        public void AfterTheMoveThePassIsBehindTheScatteringAndNothingMoves()
        {
            var moved = new List<string> { "ReDefinition moon glow", "ReDefinition volume pass", "Scatterer screen-space scattering CommandBuffer", "ReDefinition layer depth", Pass };
            Assert.IsFalse(OwnCloudPlan.MoveBehindScattering(true, moved, Pass));
        }

        [TestMethod]
        public void TheSwitchOffKeepsTheOldOrder()
        {
            Assert.IsFalse(OwnCloudPlan.MoveBehindScattering(false, Start51("Scatterer screen-space scattering CommandBuffer"), Pass));
        }

        [TestMethod]
        public void AProfilerMarkerAtTheEventKeepsThePassInsideItsBracket()
        {
            List<string> names = Start51("Scatterer screen-space scattering CommandBuffer");
            names.Add("Camera 00: until the end of BeforeForwardAlpha");
            Assert.IsFalse(OwnCloudPlan.MoveBehindScattering(true, names, Pass));
        }

        [TestMethod]
        public void AListWithoutThePassMovesNothing()
        {
            var names = new List<string> { "ReDefinition moon glow", "Scatterer screen-space scattering CommandBuffer" };
            Assert.IsFalse(OwnCloudPlan.MoveBehindScattering(true, names, Pass));
        }

        // Every buffer name Scatterer 0.0838's DLL carries (read from the installed Scatterer.dll), and the two the plan moves behind.
        private static readonly string[] ScattererBuffers =
        {
            "Scatterer raymarched godrays CommandBuffer", "Scatterer DepthToDistanceCommandBuffer", "Scatterer ScreenspaceShadowMaskCopy CommandBuffer",
            "Scatterer ShadowRemoveFade CommandBuffer", "Scatterer ScreenspaceShadowMaskmodulate CommandBuffer", "Scatterer ShadowMapRetrieve CommandBuffer",
            "Scatterer caustics shadowMask modulate CommandBuffer", "Scatterer caustics renderer CommandBuffer", "Scatterer legacy godrays CommandBuffer",
            "Scatterer shadowMap copy CommandBuffer", "Scatterer shadowMap cascade copy CommandBuffer", "Scatterer ocean Geometry CommandBuffer",
            "Scatterer ocean Shading CommandBuffer", "Scatterer screen copy CommandBuffer", "Scatterer scaled deferred depth copy CommandBuffer",
            "Scatterer scaled deferred depth cleanup CommandBuffer",
        };

        [TestMethod]
        public void OnlyTheTwoScreenSpaceScatteringBuffersOfTheInstalledScattererMoveThePass()
        {
            foreach (string other in ScattererBuffers)
                Assert.IsFalse(OwnCloudPlan.MoveBehindScattering(true, Start51(other), Pass), "moved behind " + other);
            Assert.IsTrue(OwnCloudPlan.MoveBehindScattering(true, Start51("Scatterer screen-space scattering CommandBuffer"), Pass));
            Assert.IsTrue(OwnCloudPlan.MoveBehindScattering(true, Start51("Scatterer 1/4 res screen-space scattering CommandBuffer"), Pass));
        }

        [TestMethod]
        public void ANearMissNameMovesNothing()
        {
            // An unknown or renamed buffer is not guessed at: the order stays as it is.
            foreach (string near in new[]
            {
                "Scatterer screenspace scattering CommandBuffer", "Scatterer Screen-Space Scattering CommandBuffer", "Scatterer screen space scattering CommandBuffer",
                "Scatterer screen-space scatter CommandBuffer", "Scatterer screen-space", "", "screen-space-scattering",
            })
                Assert.IsFalse(OwnCloudPlan.MoveBehindScattering(true, Start51(near), Pass), "moved behind '" + near + "'");
        }

        [TestMethod]
        public void AnUnknownPassNameMovesNothing()
        {
            var names = Start51("Scatterer screen-space scattering CommandBuffer");
            Assert.IsFalse(OwnCloudPlan.MoveBehindScattering(true, names, "ReDefinition layer pass renamed"));
            Assert.IsFalse(OwnCloudPlan.MoveBehindScattering(true, names, null));
            Assert.IsFalse(OwnCloudPlan.MoveBehindScattering(true, names, ""));
            Assert.IsFalse(OwnCloudPlan.MoveBehindScattering(true, new List<string> { null, Pass, null }, Pass));
        }

        [TestMethod]
        public void ThePassNameIsTheNameOfTheBufferTheRendererMakes()
        {
            string source = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot.Root("src", "ReDefinition.csproj"), "src", "Atmospheres", "Render", "Layers", "LayerRenderer.cs"));
            StringAssert.Contains(source, "name = \"" + Pass + "\"");
        }

        [TestMethod]
        public void TheMarkIsInTheBufferNamesOfTheInstalledScatterer()
        {
            string root = System.Environment.GetEnvironmentVariable("KspRoot") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program";
            string dll = System.IO.Path.Combine(root, "GameData", "Scatterer", "Scatterer.dll");
            if (!System.IO.File.Exists(dll)) Assert.Inconclusive("Scatterer is not installed at " + dll);
            // The names are UTF-16 literals of the assembly; decoding at both byte alignments finds them wherever they stand.
            byte[] bytes = System.IO.File.ReadAllBytes(dll);
            string text = System.Text.Encoding.Unicode.GetString(bytes);
            string text1 = System.Text.Encoding.Unicode.GetString(bytes, 1, bytes.Length - 1);
            foreach (string name in new[] { "Scatterer screen-space scattering CommandBuffer", "Scatterer 1/4 res screen-space scattering CommandBuffer" })
                Assert.IsTrue(text.Contains(name) || text1.Contains(name), "the installed Scatterer.dll no longer carries the buffer name '" + name + "'");
            foreach (string name in ScattererBuffers)
                Assert.IsTrue(text.Contains(name) || text1.Contains(name), "the list of Scatterer buffers in this test is stale: '" + name + "' is not in the installed Scatterer.dll");
        }

        [TestMethod]
        public void TheSlabDrawsOnlyWhileTheLayerPassDoesNot()
        {
            Assert.IsTrue(OwnCloudPlan.SlabDraws(true, false));
            Assert.IsFalse(OwnCloudPlan.SlabDraws(true, true), "two renderers of the same clouds");
            Assert.IsFalse(OwnCloudPlan.SlabDraws(false, false));
            Assert.IsFalse(OwnCloudPlan.SlabDraws(false, true));
        }

        [TestMethod]
        public void TheVolumePassAsksTheRuleBeforeItAttaches()
        {
            string source = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot.Root("src", "ReDefinition.csproj"), "src", "Atmospheres", "Render", "Volume", "VolumePass.cs"));
            int ask = source.IndexOf("OwnCloudPlan.SlabDraws(slab, OwnCloudModule.Drawing)", System.StringComparison.Ordinal);
            int attach = source.IndexOf("renderer.Attach(cam, CameraEvent.BeforeForwardAlpha)", System.StringComparison.Ordinal);
            Assert.IsTrue(ask > 0 && attach > ask, "the slab attaches without asking OwnCloudPlan.SlabDraws");
        }
    }
}
