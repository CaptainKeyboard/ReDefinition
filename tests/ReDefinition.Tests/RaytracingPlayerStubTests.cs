using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Upscaler;

namespace ReDefinition.Tests
{
    // The raytracing test player compiles the FSR 3 port without the rig and takes the
    // one value the port reads from a stub (tests/RaytracingPlayer/.../UpscalerRigStub.cs).
    [TestClass]
    public class RaytracingPlayerStubTests
    {
        // The stub's MaximumSharpness, read from its source, equals UpscalerRig.MaximumSharpness: otherwise the
        // test player would sharpen differently from the game.
        [TestMethod]
        public void TheStubHasTheRigsMaximumSharpness()
        {
            DirectoryInfo directory = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "RaytracingPlayer")))
                directory = directory.Parent;
            Assert.IsNotNull(directory, "tests/RaytracingPlayer was not found above the test's folder");
            string stub = File.ReadAllText(Path.Combine(directory.FullName, "tests", "RaytracingPlayer", "Assets", "Runner",
                                                        "UpscalerRigStub.cs"));
            Match value = Regex.Match(stub, @"MaximumSharpness\s*=\s*([0-9.]+)f");
            Assert.IsTrue(value.Success, "MaximumSharpness not found in the stub");
            Assert.AreEqual(UpscalerRig.MaximumSharpness, float.Parse(value.Groups[1].Value, CultureInfo.InvariantCulture));
        }
    }
}
