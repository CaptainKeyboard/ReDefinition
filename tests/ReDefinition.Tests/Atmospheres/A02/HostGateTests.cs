using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Tests.Atmospheres.A02
{
    // The hard dependency on the main mod: the start-up gate, its Api version and member check, and the declarations (KSP assembly
    // dependency, the .version file).
    [TestClass]
    public class HostGateTests
    {
        public static class OldApi
        {
            public const int Version = HostGate.RequiredApiVersion - 1;
        }

        public static class CurrentApi
        {
            public const int Version = HostGate.RequiredApiVersion;
        }

        public static class NoVersionApi
        {
        }

        public static class FullHooks
        {
            public static void RegisterAfterUpscaling(Action<CommandBuffer, RenderTexture, Camera> h) { }
            public static void UnregisterAfterUpscaling(Action<CommandBuffer, RenderTexture, Camera> h) { }
            public static void RegisterAfterUpscalingWithDepth(Action<CommandBuffer, RenderTexture, RenderTexture, Camera> h) { }
            public static void UnregisterAfterUpscalingWithDepth(Action<CommandBuffer, RenderTexture, RenderTexture, Camera> h) { }
            public static void RegisterMotionVectors(Action<CommandBuffer, RenderTexture, RenderTexture, Camera> h) { }
            public static void UnregisterMotionVectors(Action<CommandBuffer, RenderTexture, RenderTexture, Camera> h) { }
            public static void RegisterOverlay(Action<CommandBuffer, Camera> h) { }
            public static void UnregisterOverlay(Action<CommandBuffer, Camera> h) { }
        }

        // The depth hook renamed: the case the bridge used to treat as "not there" without a word.
        public static class RenamedDepthHooks
        {
            public static void RegisterAfterUpscaling(Action<CommandBuffer, RenderTexture, Camera> h) { }
            public static void UnregisterAfterUpscaling(Action<CommandBuffer, RenderTexture, Camera> h) { }
            public static void RegisterAfterUpscalingDepth(Action<CommandBuffer, RenderTexture, RenderTexture, Camera> h) { }
            public static void UnregisterAfterUpscalingDepth(Action<CommandBuffer, RenderTexture, RenderTexture, Camera> h) { }
            public static void RegisterMotionVectors(Action<CommandBuffer, RenderTexture, RenderTexture, Camera> h) { }
            public static void UnregisterMotionVectors(Action<CommandBuffer, RenderTexture, RenderTexture, Camera> h) { }
            public static void RegisterOverlay(Action<CommandBuffer, Camera> h) { }
            public static void UnregisterOverlay(Action<CommandBuffer, Camera> h) { }
        }

        public static class FullFrame
        {
            public static void RequestHistoryReset(string reason) { }
        }

        public static class FullKeys
        {
            public static bool Pressed(string key) { return false; }
        }

        public static class NoPressedKeys
        {
            public static bool Held(string key) { return false; }
        }

        [TestMethod]
        public void ACompleteCurrentHostPasses()
        {
            Assert.IsNull(HostGate.Problem(typeof(CurrentApi), typeof(FullHooks), typeof(FullFrame), typeof(FullKeys)));
        }

        [TestMethod]
        public void TheRealMainModPasses()
        {
            // The test project references the main mod: the gate must let the real Api through, and the required version must not exceed it.
            Assert.IsNull(HostGate.Problem());
            Assert.IsTrue(HostGate.Open);
            Assert.IsTrue(ReDefinition.Api.ApiInfo.Version >= HostGate.RequiredApiVersion);
        }

        [TestMethod]
        public void AnAbsentMainModStopsTheStartWithTheReason()
        {
            string problem = HostGate.Problem(null, null, null, null);
            Assert.IsNotNull(problem);
            StringAssert.Contains(problem, "not installed");
        }

        [TestMethod]
        public void AnOldOrUnversionedApiStopsTheStart()
        {
            string old = HostGate.Problem(typeof(OldApi), typeof(FullHooks), typeof(FullFrame), typeof(FullKeys));
            Assert.IsNotNull(old);
            StringAssert.Contains(old, "version " + (HostGate.RequiredApiVersion - 1));
            string none = HostGate.Problem(typeof(NoVersionApi), typeof(FullHooks), typeof(FullFrame), typeof(FullKeys));
            Assert.IsNotNull(none);
            StringAssert.Contains(none, "no readable version");
        }

        [TestMethod]
        public void ARenamedMemberFailsLoudlyAndNamesTheMember()
        {
            string depth = HostGate.Problem(typeof(CurrentApi), typeof(RenamedDepthHooks), typeof(FullFrame), typeof(FullKeys));
            StringAssert.Contains(depth, "RegisterAfterUpscalingWithDepth");
            string reset = HostGate.Problem(typeof(CurrentApi), typeof(FullHooks), null, typeof(FullKeys));
            StringAssert.Contains(reset, "RequestHistoryReset");
            string keys = HostGate.Problem(typeof(CurrentApi), typeof(FullHooks), typeof(FullFrame), typeof(NoPressedKeys));
            StringAssert.Contains(keys, "Keys.Pressed");
            string hooks = HostGate.Problem(typeof(CurrentApi), null, typeof(FullFrame), typeof(FullKeys));
            StringAssert.Contains(hooks, "RegisterAfterUpscaling");
        }

        [TestMethod]
        public void TheStartRunsNoPartWhenTheGateRefusesAndWritesExactlyOneError()
        {
            int started = 0;
            var errors = new System.Collections.Generic.List<string>();
            bool ran = AtmospheresStartup.Begin(() => HostGate.Problem(null, null, null, null), errors.Add, () => started++);
            Assert.IsFalse(ran);
            Assert.AreEqual(0, started);
            Assert.AreEqual(1, errors.Count);
            StringAssert.Contains(errors[0], "NOT STARTED");
            StringAssert.Contains(errors[0], AtmospheresLog.Prefix);
        }

        [TestMethod]
        public void TheStartRunsThePartsOnceWhenTheGateAllows()
        {
            int started = 0;
            var errors = new System.Collections.Generic.List<string>();
            bool ran = AtmospheresStartup.Begin(
                () => HostGate.Problem(typeof(CurrentApi), typeof(FullHooks), typeof(FullFrame), typeof(FullKeys)), errors.Add, () => started++);
            Assert.IsTrue(ran);
            Assert.AreEqual(1, started);
            Assert.AreEqual(0, errors.Count);
        }

        [TestMethod]
        public void EveryAddonOfTheAtmospheresAsksTheGate()
        {
            // The classes that start by themselves (a [KSPAddon]) must not run without the main mod.
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            string folder = Path.Combine(root, "src", "Atmospheres");
            var missing = Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories)
                .Select(f => new { f, text = File.ReadAllText(f) })
                .Where(x => Regex.IsMatch(x.text, @"\[KSPAddon\(") && !x.f.EndsWith("AtmospheresStartup.cs") && !x.text.Contains("HostGate.Open"))
                .Select(x => x.f).ToList();
            Assert.AreEqual(0, missing.Count, "KSPAddon without a HostGate check: " + string.Join(", ", missing));
        }

        [TestMethod]
        public void TheAtmospheresDeclareTheMainModAsADependency()
        {
            Assembly assembly = typeof(AtmospheresStartup).Assembly;
            var dependencies = assembly.GetCustomAttributesData().Where(a => a.AttributeType.Name == "KSPAssemblyDependency")
                .Select(a => (string)a.ConstructorArguments[0].Value).ToList();
            CollectionAssert.IsSubsetOf(new[] { "ReDefinition", "ReDefinition.Common" }, dependencies);
            // The name and version the main mod declares for itself.
            var declared = typeof(HostLink).Assembly.GetCustomAttributesData().Where(a => a.AttributeType.Name == "KSPAssembly")
                .Select(a => (string)a.ConstructorArguments[0].Value).ToList();
            CollectionAssert.Contains(declared, "ReDefinition");
        }

        [TestMethod]
        public void TheVersionFileCarriesTheDependencyAndTheGeneratorWritesIt()
        {
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            string file = File.ReadAllText(Path.Combine(root, "ReDefinitionAtmospheres.version"));
            Assert.IsTrue(Regex.IsMatch(file, "\"DEPENDS\"\\s*:\\s*\\[\\s*\"ReDefinition\"\\s*,\\s*\"ReDefinitionCommon\"\\s*\\]"), file);
            // The release target rewrites that file: the rows it writes carry the same field.
            string project = File.ReadAllText(Path.Combine(root, "src", "ReDefinition.csproj"));
            Assert.IsTrue(Regex.IsMatch(project, @"_AtmospheresAvcLine Include=""&quot;DEPENDS&quot;: \[&quot;\$\(ModName\)&quot;, &quot;\$\(CommonName\)&quot;\]"""),
                "The generator of ReDefinitionAtmospheres.version no longer writes DEPENDS.");
        }
    }
}
