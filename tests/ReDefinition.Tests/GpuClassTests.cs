using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Bridges;

namespace ReDefinition.Tests
{
    // The class a graphics card falls in (GpuClasses.Of), which a profile's
    // values are chosen by.
    [TestClass]
    public class GpuClassTests
    {
        private const int Nvidia = 0x10DE;
        private const int Amd = 0x1002;
        private const int Intel = 0x8086;

        [DataTestMethod]
        [DataRow("NVIDIA GeForce RTX 2060", "Rtx20")]
        [DataRow("NVIDIA GeForce RTX 2080 Ti", "Rtx20")]
        [DataRow("NVIDIA GeForce RTX 3070", "Rtx30")]
        [DataRow("NVIDIA GeForce RTX 3090 Ti", "Rtx30")]
        [DataRow("NVIDIA RTX A4500", "Rtx30")]
        [DataRow("NVIDIA GeForce RTX 4090", "Rtx40")]
        [DataRow("NVIDIA RTX 4000 Ada Generation", "Rtx40")]
        [DataRow("NVIDIA GeForce RTX 5070 Ti", "Rtx50")]
        [DataRow("NVIDIA GeForce GTX 1080", "NvidiaOlder")]
        [DataRow("NVIDIA GeForce GTX 1660 Super", "NvidiaOlder")]
        public void NvidiaCardsByTheirName(string name, string expected)
        {
            Assert.AreEqual(expected, GpuClasses.Of(Nvidia, name, 0, 0).ToString());
        }

        [DataTestMethod]
        [DataRow(0x160u, 0x4u, "Rtx20")]
        [DataRow(0x160u, 0x7u, "NvidiaOlder")]
        [DataRow(0x170u, 0x2u, "Rtx30")]
        [DataRow(0x190u, 0x2u, "Rtx40")]
        [DataRow(0x1B0u, 0x2u, "Rtx50")]
        [DataRow(0x130u, 0x2u, "NvidiaOlder")]
        public void NvidiaCardsByNvapisArchitecture(uint architecture, uint implementation, string expected)
        {
            // The name says RTX 5090; NVAPI's answer counts.
            Assert.AreEqual(expected, GpuClasses.Of(Nvidia, "NVIDIA GeForce RTX 5090", architecture, implementation).ToString());
        }

        [DataTestMethod]
        [DataRow("AMD Radeon RX 9070 XT", "Radeon9000")]
        [DataRow("AMD Radeon RX 7900 XTX", "Radeon7000")]
        [DataRow("AMD Radeon RX 6800 XT", "RadeonOlder")]
        [DataRow("AMD Radeon RX 580 Series", "RadeonOlder")]
        [DataRow("AMD Radeon(TM) 780M Graphics", "RadeonOlder")]
        public void RadeonCardsByTheirName(string name, string expected)
        {
            Assert.AreEqual(expected, GpuClasses.Of(Amd, name, 0, 0).ToString());
        }

        [TestMethod]
        public void IntelAndTheRest()
        {
            Assert.AreEqual(GpuClass.Intel, GpuClasses.Of(Intel, "Intel(R) Arc(TM) A770 Graphics", 0, 0));
            Assert.AreEqual(GpuClass.Other, GpuClasses.Of(0x1414, "Microsoft Basic Render Driver", 0, 0));
            Assert.AreEqual(GpuClass.Unknown, GpuClasses.Of(0, "", 0, 0));
        }

        [TestMethod]
        public void AClassIsReadByItsName()
        {
            Assert.AreEqual(GpuClass.Rtx40, GpuClasses.Parse("rtx40"));
            Assert.AreEqual(GpuClass.Unknown, GpuClasses.Parse("rtx 40"));
            Assert.AreEqual(GpuClass.Unknown, GpuClasses.Parse("99"));
        }
    }
}
