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
    }
}
