using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Bridges;
using ReDefinition.Settings;

namespace ReDefinition.Tests
{
    // The upscaling techniques (UpscalerBackend): their order in the technique row,
    // their names, and how the row reads and writes them.
    [TestClass]
    public class UpscalerBackendTests
    {
        [TestMethod]
        public void TheRowOffersFsr3DlssAmdAndXessInThatOrder()
        {
            ModuleSetting row = OurModules.Rows().Single(setting => setting.Title == "Technique");
            CollectionAssert.AreEqual(new[] { "Auto", "Fsr3", "Dlss", "Amd", "Xess" }, row.Choices);
        }

        [TestMethod]
        public void EachTechniqueHasItsName()
        {
            Assert.AreEqual("FSR 3", UpscalerBackends.Name(UpscalerBackend.Fsr3));
            Assert.AreEqual("DLSS", UpscalerBackends.Name(UpscalerBackend.Dlss));
            Assert.AreEqual("AMD FSR (DLL)", UpscalerBackends.Name(UpscalerBackend.Amd));
            Assert.AreEqual("XeSS (DLL)", UpscalerBackends.Name(UpscalerBackend.Xess));
        }

        [TestMethod]
        public void XessIsWrittenAndReadByName()
        {
            ModuleSetting row = OurModules.Rows().Single(setting => setting.Title == "Technique");
            OwnSettings settings = new OwnSettings();

            row.Write(settings, "Xess");

            Assert.AreEqual(UpscalerBackend.Xess, settings.Backend);
            Assert.AreEqual("Xess", row.Read(settings));
        }
    }
}
