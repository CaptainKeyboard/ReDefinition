using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The three hand-overs between the main mod and the own cloud pass (package T09): the moons' light, the profiler markers and the cloud shadow.
    // The Atmospheres cannot reference the main mod, so the first two travel through HostSeams (Common), which HostLink fills.
    [TestClass]
    public class HandOverTests
    {
        [TestMethod]
        public void WithoutTheMainModTheMoonProviderAnswersNoLightAndTheMarkerDoesNothing()
        {
            Vector3 towards;
            Color colour;
            Assert.IsFalse(CloudBridgeLights.Moon(null, new Vector3d(0, 0, 0), out towards, out colour));
            CloudBridgeLights.Mark(null, CloudBridgeLights.MarkBegin);
        }

        [TestMethod]
        public void TheMoonProviderAndTheMarkerReadTheSeamsAtCallTime()
        {
            var oldMoon = HostSeams.CloudMoonLight;
            var oldMark = HostSeams.GpuMark;
            try
            {
                string marked = null;
                HostSeams.CloudMoonLight = (CelestialBody main, Vector3d eye, out Vector3 towards, out Color colour) =>
                {
                    towards = Vector3.right;
                    colour = new Color(0.1f, 0.2f, 0.3f);
                    return true;
                };
                HostSeams.GpuMark = (buffer, label) => marked = label;
                Vector3 t;
                Color c;
                Assert.IsTrue(CloudBridgeLights.Moon(null, new Vector3d(0, 0, 0), out t, out c));
                Assert.AreEqual(Vector3.right, t);
                Assert.AreEqual(0.2f, c.g, 1e-6f);
                CloudBridgeLights.Mark(null, CloudBridgeLights.MarkBegin);
                Assert.AreEqual("ReDefinition clouds: begin", marked);
                Assert.AreEqual("ReDefinition clouds: end", CloudBridgeLights.MarkEnd);
            }
            finally
            {
                HostSeams.CloudMoonLight = oldMoon;
                HostSeams.GpuMark = oldMark;
            }
        }

        [TestMethod]
        public void TheMainModFillsBothSeamsFromTheSourcesOfTheGroundAndTheProfiler()
        {
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            string link = File.ReadAllText(Path.Combine(root, "src", "HostLink.cs"));
            StringAssert.Contains(link, "HostSeams.CloudMoonLight = CloudMoonLight;");
            StringAssert.Contains(link, "HostSeams.GpuMark = GpuProfiler.Mark;");
            StringAssert.Contains(link, "RtReflectors.CloudLight(main, eye, percent");
            StringAssert.Contains(link, "addon.Live.NightClouds");
        }

        [TestMethod]
        public void TheCloudShadowRigTakesTheSceneCamerasAndNotTheDisc()
        {
            Assert.AreEqual(CloudShadowRole.None, OwnCloudModule.ShadowRole(null));
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            string module = File.ReadAllText(Path.Combine(root, "src", "Atmospheres", "Light", "Cloud", "OwnCloudModule.cs"));
            // The rig starts after the module's own pre-cull handler and is gated by the switches (ownClouds, cloudShadows, a layer).
            Assert.IsTrue(module.IndexOf("Camera.onPreCull += OnPreCull;") < module.IndexOf("shadowRig.Start();"));
            StringAssert.Contains(module, "OwnCloudOptions.ShadowActive(");
            StringAssert.Contains(module, "shadowRig.Stop();");
        }
    }
}
