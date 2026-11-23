using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.A01
{
    // Cleanup 2 C (2026-11-23): the probes and the test tooling are not in the shipped atmosphere DLL. They compile into ReDefinition.Atmospheres.Probes.dll, the
    // registry finds their modules there, the deploy copies that DLL to the test installs, and the release zip never takes it.
    [TestClass]
    public class ProbeAssemblyTests
    {
        [TestMethod]
        public void TheShippedDllHoldsNoProbe()
        {
            Type[] shipped = typeof(IAtmospheresModule).Assembly.GetTypes();
            Assert.IsFalse(shipped.Any(t => typeof(SeriesProbe).IsAssignableFrom(t)), "a series probe in the shipped DLL");
            Assert.IsFalse(shipped.Any(t => t.Name == "SeriesProbe" || t.Name == "ForcedField" || t.Name == "AtmoRecon"), "the test tooling in the shipped DLL");
            Assert.AreEqual(ModuleRegistry.ProbeAssemblyName, typeof(SeriesProbe).Assembly.GetName().Name);
            Assert.AreEqual(ModuleRegistry.ProbeAssemblyName, typeof(T09Probe).Assembly.GetName().Name);
        }

        [TestMethod]
        public void TheRegistryFindsTheProbesInTheirOwnDll()
        {
            var shipped = ModuleRegistry.Discover(typeof(IAtmospheresModule).Assembly);
            var both = ModuleRegistry.Discover(typeof(IAtmospheresModule).Assembly, typeof(SeriesProbe).Assembly);
            CollectionAssert.DoesNotContain(shipped, typeof(T09Probe));
            CollectionAssert.Contains(both, typeof(T09Probe));
            Assert.IsTrue(shipped.All(both.Contains), "every shipped module stays");
            Assert.AreEqual(shipped.Count, ModuleRegistry.Discover(typeof(IAtmospheresModule).Assembly, null).Count, "a missing probe DLL (a player's install) changes nothing");
        }

        [TestMethod]
        public void TheDeployCopiesTheProbesAndTheReleaseZipDoesNot()
        {
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            string project = File.ReadAllText(Path.Combine(root, "src", "ReDefinition.csproj"));
            int deploy = project.IndexOf("<Target Name=\"DeployToGameData\"", StringComparison.Ordinal);
            int release = project.IndexOf("<Target Name=\"MakeReleaseZip\"", StringComparison.Ordinal);
            Assert.IsTrue(deploy >= 0 && release > deploy, "both targets, the deploy first");
            string deployText = project.Substring(deploy, release - deploy), releaseText = project.Substring(release);
            StringAssert.Contains(deployText, "ReDefinition.Atmospheres.Probes.dll");
            Assert.IsFalse(Regex.IsMatch(releaseText, @"Atmospheres\.Probes"), "the release zip names the probe DLL");
            StringAssert.Contains(File.ReadAllText(Path.Combine(root, "tools", "atmo_series.py")), "build/ReDefinition.Atmospheres.Probes.dll");
        }
    }
}
