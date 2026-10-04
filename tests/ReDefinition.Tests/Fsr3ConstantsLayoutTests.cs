using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using FidelityFX.FSR3;

namespace ReDefinition.Tests
{
    // FSR 3's constants go to the shaders as one element of a StructuredBuffer
    // (tools/port_fsr3_shaders.py), so the C# struct and the shaders' struct
    // KspCb_cbFSR3Upscaler must have the same size. A mismatch gives no error,
    // only shifted constants.
    [TestClass]
    public class Fsr3ConstantsLayoutTests
    {
        private const int UpscalerConstantsSize = 148;

        [TestMethod]
        public void TheUpscalerConstantsHaveTheShadersSize()
        {
            Assert.AreEqual(UpscalerConstantsSize, Marshal.SizeOf(typeof(Fsr3Upscaler.UpscalerConstants)));
        }

        [TestMethod]
        public void TheShadersStructHasTheSameSize()
        {
            string header = Path.Combine(Repository(), "unity", "Assets", "ReDefinition", "Shaders",
                "shaders", "fsr3upscaler", "ffx_fsr3upscaler_callbacks_hlsl.h");
            string text = File.ReadAllText(header);

            Match body = Regex.Match(text, @"struct KspCb_cbFSR3Upscaler\s*\{(?<body>[^}]*)\}");
            Assert.IsTrue(body.Success, "struct KspCb_cbFSR3Upscaler not found in " + header);

            var sizes = new Dictionary<string, int>
            {
                { "FfxFloat32", 4 }, { "FfxFloat32x2", 8 }, { "FfxFloat32x4", 16 },
                { "FfxInt32", 4 }, { "FfxInt32x2", 8 }, { "FfxUInt32", 4 },
            };
            int size = 0;
            foreach (Match field in Regex.Matches(body.Groups["body"].Value, @"^\s*(Ffx\w+)\s+\w+\s*;", RegexOptions.Multiline))
            {
                string type = field.Groups[1].Value;
                Assert.IsTrue(sizes.ContainsKey(type), "unknown field type " + type);
                size += sizes[type];
            }
            Assert.AreEqual(UpscalerConstantsSize, size);
        }

        private static string Repository()
        {
            DirectoryInfo directory = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "unity", "Assets", "ReDefinition")))
                directory = directory.Parent;
            Assert.IsNotNull(directory, "the repository's unity folder was not found above the test's folder");
            return directory.FullName;
        }
    }
}
