using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Bridges;
using ReDefinition.Modules;
using ReDefinition.Settings;

namespace ReDefinition.Tests
{
    // The upscaling techniques (UpscalerBackend): their order in the technique row,
    // their names, and how the row reads and writes them.
    [TestClass]
    public class UpscalerBackendTests
    {
        // The Upscaler row offers Off and Auto, then FSR 3, DLSS, AMD's FSR and XeSS in that order.
        [TestMethod]
        public void TheRowOffersFsr3DlssAmdAndXessInThatOrder()
        {
            ModuleSetting row = OurModules.Rows().Single(setting => setting.Title == "Upscaler");
            CollectionAssert.AreEqual(new[] { "Off", "Auto", "Fsr3", "Dlss", "Amd", "Xess" }, row.Choices);
        }

        // Each technique's name as the window shows it; the DLL ones say so.
        [TestMethod]
        public void EachTechniqueHasItsName()
        {
            Assert.AreEqual("FSR 3", UpscalerBackends.Name(UpscalerBackend.Fsr3));
            Assert.AreEqual("DLSS", UpscalerBackends.Name(UpscalerBackend.Dlss));
            Assert.AreEqual("AMD FSR (DLL)", UpscalerBackends.Name(UpscalerBackend.Amd));
            Assert.AreEqual("XeSS (DLL)", UpscalerBackends.Name(UpscalerBackend.Xess));
        }

        // Writing a technique switches the upscaler on and reads back by name; Off switches it off and keeps the
        // technique chosen.
        [TestMethod]
        public void XessIsWrittenAndReadByName()
        {
            ModuleSetting row = OurModules.Rows().Single(setting => setting.Title == "Upscaler");
            OwnSettings settings = new OwnSettings();

            row.Write(settings, "Xess");

            Assert.AreEqual(UpscalerBackend.Xess, settings.Backend);
            Assert.IsTrue(settings.Enabled, "a technique switches the upscaler on");
            Assert.AreEqual("Xess", row.Read(settings));

            row.Write(settings, "Off");
            Assert.IsFalse(settings.Enabled);
            Assert.AreEqual("Off", row.Read(settings));
            Assert.AreEqual(UpscalerBackend.Xess, settings.Backend, "off keeps the technique chosen");
        }
    }
}
