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
        // The size in bytes both sides must have.
        private const int UpscalerConstantsSize = 148;

        // The C# UpscalerConstants marshal to that size.
        [TestMethod]
        public void TheUpscalerConstantsHaveTheShadersSize()
        {
            Assert.AreEqual(UpscalerConstantsSize, Marshal.SizeOf(typeof(Fsr3Upscaler.UpscalerConstants)));
        }

        // The fields of KspCb_cbFSR3Upscaler in the ported shader header add up to that size.
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

        // The repository's root, found as the first folder above the test's that holds unity/Assets/ReDefinition.
        private static string Repository()
        {
            return RepoRoot.Root("unity", "Assets", "ReDefinition");
        }
    }
}
