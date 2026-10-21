using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Bridges;

namespace ReDefinition.Tests
{
    // The frame generation row's choices and which of them the technique that runs allows
    // (FrameGenerationChoice, from the proxy's KspFgLimits).
    [TestClass]
    public class FrameGenerationChoiceTests
    {
        private static FrameGenerationChoice.Limits Dlss(int most, int dynamic)
        {
            return new FrameGenerationChoice.Limits { Technique = 2, Most = most, Dynamic = dynamic };
        }

        [TestMethod]
        public void TheValuesAreOffTheMultipliersAndDynamic()
        {
            CollectionAssert.AreEqual(new[] { "Off", "2x", "3x", "4x", "5x", "6x", "Dynamic" }, FrameGenerationChoice.All);
            Assert.AreEqual(3, FrameGenerationChoice.Multiplier("3x"));
            Assert.AreEqual(0, FrameGenerationChoice.Multiplier("Dynamic"));
            Assert.IsTrue(FrameGenerationChoice.Valid("6x"));
            Assert.IsTrue(FrameGenerationChoice.Valid("Dynamic"));
            Assert.IsFalse(FrameGenerationChoice.Valid("Off"), "Off is the switch's, not the multiplier's");
            Assert.IsFalse(FrameGenerationChoice.Valid("7x"));
            Assert.IsFalse(FrameGenerationChoice.Valid(null));
        }

        [TestMethod]
        public void EverythingIsOfferedWhileTheTechniqueHasSaidNothing()
        {
            FrameGenerationChoice.Limits none = new FrameGenerationChoice.Limits { Dynamic = -1 };
            foreach (string choice in FrameGenerationChoice.All)
                Assert.IsTrue(FrameGenerationChoice.Offered(choice, none), choice);
            Assert.IsNull(FrameGenerationChoice.Note(none, false));
        }

        [TestMethod]
        public void DlssOffersUpToWhatItsStateReports()
        {
            FrameGenerationChoice.Limits limits = Dlss(4, 1);
            Assert.IsTrue(FrameGenerationChoice.Offered("4x", limits));
            Assert.IsFalse(FrameGenerationChoice.Offered("5x", limits));
            Assert.IsTrue(FrameGenerationChoice.Offered("Dynamic", limits));
            Assert.AreEqual("4x", FrameGenerationChoice.Effective("6x", limits));
            Assert.AreEqual("3x", FrameGenerationChoice.Effective("3x", limits));
        }

        [TestMethod]
        public void DynamicNeedsTheSupportTheStateReports()
        {
            Assert.IsFalse(FrameGenerationChoice.Offered("Dynamic", Dlss(4, 0)));
            Assert.AreEqual("4x", FrameGenerationChoice.Effective("Dynamic", Dlss(4, 0)));
            Assert.IsTrue(FrameGenerationChoice.Offered("Dynamic", Dlss(4, -1)), "not known yet: not held against it");
        }

        [TestMethod]
        public void FsrAndXessAreTwoTimesAndNeverDynamic()
        {
            FrameGenerationChoice.Limits fsr = new FrameGenerationChoice.Limits { Technique = 1, Most = 2, Dynamic = 0 };
            Assert.IsTrue(FrameGenerationChoice.Offered("2x", fsr));
            Assert.IsFalse(FrameGenerationChoice.Offered("3x", fsr));
            Assert.IsFalse(FrameGenerationChoice.Offered("Dynamic", fsr));
            FrameGenerationChoice.Limits xess = new FrameGenerationChoice.Limits { Technique = 3, Most = 3, Dynamic = 0 };
            Assert.IsTrue(FrameGenerationChoice.Offered("3x", xess));
            Assert.IsFalse(FrameGenerationChoice.Offered("4x", xess));
        }

        [TestMethod]
        public void TheNoteSaysWhichStepsAreNotOfferedAndWhy()
        {
            string package = FrameGenerationChoice.Note(Dlss(3, 0), true);
            StringAssert.StartsWith(package, "4x to 6x: the package allows up to 3x");
            StringAssert.Contains(package, "raise its multiplier");
            StringAssert.Contains(package, "restart of KSP");
            StringAssert.Contains(package, "Dynamic: not supported here");

            string gpu = FrameGenerationChoice.Note(Dlss(4, 1), false);
            Assert.AreEqual("5x and 6x: this GPU allows up to 4x.", gpu);

            string fsr = FrameGenerationChoice.Note(new FrameGenerationChoice.Limits { Technique = 1, Most = 2 }, false);
            StringAssert.StartsWith(fsr, "3x to 6x: FSR 3 generates one frame between two.");
            StringAssert.Contains(fsr, "Dynamic: DLSS frame generation only.");

            Assert.IsNull(FrameGenerationChoice.Note(Dlss(6, 1), false), "all offered");
        }

        // The row holds two settings: the switch, which a profile sets as "enabled", and the multiplier.
        [TestMethod]
        public void TheRowHoldsTheSwitchAndTheMultiplier()
        {
            OwnSettings settings = new OwnSettings();
            ModuleSetting row = OurModules.FrameGeneration.Setting("mode");
            Assert.AreEqual("Off", row.Read(settings));
            row.Write(settings, "4x");
            Assert.IsTrue(settings.FrameGeneration);
            Assert.AreEqual("4x", settings.FrameGenerationMultiplier);
            Assert.AreEqual("4x", row.Read(settings));
            row.Write(settings, "Off");
            Assert.IsFalse(settings.FrameGeneration);
            Assert.AreEqual("4x", settings.FrameGenerationMultiplier, "held while off");
            Assert.AreEqual("Off", row.Read(settings));
            row.Write(settings, "Dynamic");
            Assert.AreEqual("Dynamic", settings.FrameGenerationMultiplier);

            // Saved by its key; a value that is none leaves the multiplier as it is.
            Assert.IsTrue(settings.SetValue("frameGenerationMultiplier", "3x"));
            Assert.AreEqual("3x", settings.ValueOf("frameGenerationMultiplier"));
            settings.SetValue("frameGenerationMultiplier", "9x");
            Assert.AreEqual("3x", settings.FrameGenerationMultiplier);
            Assert.AreEqual("2x", new OwnSettings().FrameGenerationMultiplier);
        }

        [TestMethod]
        public void TheProfilesSwitchIsStillThereButNotARow()
        {
            ModuleSetting enabled = OurModules.FrameGeneration.Setting("enabled");
            Assert.IsNotNull(enabled);
            Assert.IsFalse(enabled.Present());
            Assert.IsTrue(OurModules.Rows().Contains(OurModules.FrameGeneration.Setting("mode")));
            Assert.IsFalse(OurModules.Rows().Contains(enabled));
        }

        [TestMethod]
        public void TheLabelNamesDynamicForDlssOnly()
        {
            Assert.AreEqual("DLSS dynamic", FrameGenerationBridge.TechniqueLabel(2, 3, true));
            Assert.AreEqual("DLSS 3x", FrameGenerationBridge.TechniqueLabel(2, 3, false));
            Assert.AreEqual("XeSS 2x", FrameGenerationBridge.TechniqueLabel(3, 2, true));
            Assert.AreEqual("FSR 3", FrameGenerationBridge.TechniqueLabel(1, 2, true));
        }
    }
}
