using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Upscaler;

namespace ReDefinition.Tests
{
    // The raytracing test player compiles the FSR 3 port without the integration and takes the
    // one value the port reads from a stub (tests/RaytracingPlayer/.../UpscalerIntegrationStub.cs).
    [TestClass]
    public class RaytracingPlayerStubTests
    {
        // The stub's MaximumSharpness, read from its source, equals UpscalerIntegration.MaximumSharpness: otherwise the
        // test player would sharpen differently from the game.
        [TestMethod]
        public void TheStubHasTheIntegrationsMaximumSharpness()
        {
            string stub = File.ReadAllText(RepoRoot.Locate("tests", "RaytracingPlayer", "Assets", "Runner", "UpscalerIntegrationStub.cs"));
            Match value = Regex.Match(stub, @"MaximumSharpness\s*=\s*([0-9.]+)f");
            Assert.IsTrue(value.Success, "MaximumSharpness not found in the stub");
            Assert.AreEqual(UpscalerIntegration.MaximumSharpness, float.Parse(value.Groups[1].Value, CultureInfo.InvariantCulture));
        }
    }
}
