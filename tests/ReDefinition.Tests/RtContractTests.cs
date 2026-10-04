using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Raytracing;

namespace ReDefinition.Tests
{
    // The raytracing contract (src/DxgiProxy/RaytracingShaders/RtContract.h), which the proxy
    // and its shaders include: RtNative's constants and its packets' layouts as it gives
    // them. RT_<NAME> is the constant <Name>; RT_FRAME_PACKET_<FIELD> and
    // RT_RECONSTRUCT_PACKET_<FIELD> a field's offset (SIZE_FIELD for Size).
    [TestClass]
    public class RtContractTests
    {
        [TestMethod]
        public void EveryConstantIsTheContracts()
        {
            Dictionary<string, long> contract = Contract();
            int compared = 0;
            foreach (FieldInfo field in typeof(RtNative).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (!field.IsLiteral || field.FieldType == typeof(string)) continue;
                string name = "RT_" + Snake(field.Name);
                Assert.IsTrue(contract.ContainsKey(name), "RtContract.h has no " + name + " for RtNative." + field.Name);
                Assert.AreEqual(contract[name], Convert.ToInt64(field.GetRawConstantValue()), "RtNative." + field.Name);
                compared++;
            }
            Assert.IsTrue(compared > 30, "only " + compared + " constants compared");
        }

        [TestMethod]
        public void ThePacketsAreLaidOutAsTheContractSays()
        {
            Dictionary<string, long> contract = Contract();
            Check(contract, typeof(RtNative.FramePacket), "RT_FRAME_PACKET_");
            Check(contract, typeof(RtNative.ReconstructPacket), "RT_RECONSTRUCT_PACKET_");
            Assert.AreEqual(contract["RT_MESH_DESC_SIZE"], Marshal.SizeOf(typeof(RtNative.MeshDesc)));
            Assert.AreEqual(contract["RT_MATERIAL_DESC_SIZE"], Marshal.SizeOf(typeof(RtNative.MaterialDesc)));
            Assert.AreEqual(contract["RT_INSTANCE_SIZE"], Marshal.SizeOf(typeof(RtNative.Instance)));
            Assert.AreEqual(contract["RT_GPU_GROUP_SIZE"], Marshal.SizeOf(typeof(RtNative.GpuGroup)));
        }

        private static void Check(Dictionary<string, long> contract, Type packet, string prefix)
        {
            foreach (FieldInfo field in packet.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                string name = prefix + (field.Name == "Size" ? "SIZE_FIELD" : Snake(field.Name));
                Assert.IsTrue(contract.ContainsKey(name), "RtContract.h has no " + name + " for " + packet.Name + "." + field.Name);
                Assert.AreEqual(contract[name], Marshal.OffsetOf(packet, field.Name).ToInt64(), packet.Name + "." + field.Name);
            }
            Assert.AreEqual(contract[prefix + "SIZE"], Marshal.SizeOf(packet), packet.Name + "'s size");
        }

        // ClipToWorld -> CLIP_TO_WORLD, InputGBuffer0 -> INPUT_G_BUFFER0.
        private static string Snake(string name)
        {
            var text = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool upper = char.IsUpper(c);
                if (i > 0 && upper && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                    text.Append('_');
                text.Append(char.ToUpperInvariant(c));
            }
            return text.ToString();
        }

        // The header's integer #defines.
        private static Dictionary<string, long> Contract()
        {
            DirectoryInfo directory = new DirectoryInfo(Directory.GetCurrentDirectory());
            string path = null;
            while (directory != null && !File.Exists(path = Path.Combine(directory.FullName, "src", "DxgiProxy", "RaytracingShaders", "RtContract.h")))
                directory = directory.Parent;
            Assert.IsNotNull(directory, "RtContract.h was not found above the test's folder");
            var values = new Dictionary<string, long>();
            var define = new Regex(@"^#define\s+(RT_\w+)\s+(0x[0-9A-Fa-f]+|\d+)u?\s*(//.*)?$");
            foreach (string line in File.ReadAllLines(path))
            {
                Match m = define.Match(line.Trim());
                if (!m.Success) continue;
                string value = m.Groups[2].Value;
                values[m.Groups[1].Value] = value.StartsWith("0x", StringComparison.Ordinal)
                    ? long.Parse(value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : long.Parse(value, CultureInfo.InvariantCulture);
            }
            return values;
        }
    }
}
