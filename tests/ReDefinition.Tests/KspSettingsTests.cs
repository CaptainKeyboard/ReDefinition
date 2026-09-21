using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Settings;
using ReDefinition.Settings.Behaviours;

namespace ReDefinition.Tests
{
    // A mod whose settings stand in the new tabs: one bundled as usual, one set
    // directly, one shown as a percentage.
    internal static class TabsFake
    {
        internal static float Volume = 0.5f;
        internal static bool Labels = true;
    }

    // What the registration format gained for KSP's whole settings screen: the
    // tabs beyond graphics, a group's heading, a percentage, settings set
    // directly, and a mod whose rows are set directly with the bundling off.
    [TestClass]
    public class KspSettingsTests
    {
        private static RegisteredMod Build(string text, List<string> problems)
        {
            ConfigNode node = ConfigNode.Parse(text).GetNode(ModRegistration.NodeName);
            return new RegisteredMod(ModRegistration.FromConfigNode(node, problems), problems);
        }

        private const string Registration =
            "MOD_SETTINGS\n{\n name = tabsfake\n detect = ReDefinition.Tests.TabsFake\n direct = True\n"
            + " SETTING\n {\n  name = Volume\n  member = ReDefinition.Tests.TabsFake.Volume\n  row = Audio\n"
            + "  section = Volume\n  min = 0\n  max = 1\n  percent = True\n  default = 0.5\n  bundled = False\n }\n"
            + " SETTING\n {\n  name = Labels\n  member = ReDefinition.Tests.TabsFake.Labels\n  row = Gameplay\n"
            + "  default = True\n }\n}";

        [TestMethod]
        public void TheNewKeysReachTheSettingAndTheMod()
        {
            List<string> problems = new List<string>();
            RegisteredMod mod = Build(Registration, problems);
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
            Assert.IsTrue(mod.Registration.Direct, "direct is the mod's");

            BundledSetting volume = mod.Settings[0];
            Assert.AreEqual("Volume", volume.Section);
            Assert.IsTrue(volume.Percent);
            Assert.IsFalse(volume.Bundled, "bundled = False sets it directly");
            Assert.AreEqual(SettingCategory.Audio, mod.Registration.Setting("Volume").Row);

            BundledSetting labels = mod.Settings[1];
            Assert.IsNull(labels.Section);
            Assert.IsFalse(labels.Percent);
            Assert.IsTrue(labels.Bundled, "bundled by default");
            Assert.AreEqual(SettingCategory.Gameplay, mod.Registration.Setting("Labels").Row);
        }

        [TestMethod]
        public void EveryNewTabIsARowAndAnUnknownOneIsNot()
        {
            foreach (string tab in new[] { "Audio", "Gameplay", "System", "Input", "Axes" })
            {
                List<string> problems = new List<string>();
                RegisteredMod mod = Build("MOD_SETTINGS\n{\n name = tabsfake\n detect = ReDefinition.Tests.TabsFake\n"
                                          + " SETTING\n {\n  name = Labels\n  member = ReDefinition.Tests.TabsFake.Labels\n"
                                          + "  row = " + tab + "\n  default = True\n }\n}", problems);
                Assert.AreEqual(0, problems.Count, tab + ": " + string.Join("\n", problems));
                Assert.IsNotNull(mod.Registration.Setting("Labels").Row, tab);
            }

            List<string> refused = new List<string>();
            RegisteredMod other = Build("MOD_SETTINGS\n{\n name = tabsfake\n detect = ReDefinition.Tests.TabsFake\n"
                                        + " SETTING\n {\n  name = Labels\n  member = ReDefinition.Tests.TabsFake.Labels\n"
                                        + "  row = Sound\n  default = True\n }\n}", refused);
            Assert.AreEqual(1, refused.Count, "row 'Sound' is reported");
            Assert.IsNull(other.Registration.Setting("Labels").Row);
        }

        [TestMethod]
        public void APercentageWithoutARangeIsReported()
        {
            List<string> problems = new List<string>();
            Build("MOD_SETTINGS\n{\n name = tabsfake\n detect = ReDefinition.Tests.TabsFake\n"
                  + " SETTING\n {\n  name = Volume\n  member = ReDefinition.Tests.TabsFake.Volume\n  percent = True\n"
                  + "  default = 0.5\n }\n}", problems);
            Assert.AreEqual(1, problems.Count, string.Join("\n", problems));
            StringAssert.Contains(problems[0], "percent");
        }

        [TestMethod]
        public void ATooltipLineLongerThanTheUpscalersFirstIsBrokenAtASpace()
        {
            string upscaler = "The upscaler on the 3D scene, FSR 3 or the chosen technique: temporal antialiasing,";
            Assert.AreEqual(ReDefinition.Window.TooltipText.MaxLine, upscaler.Length);
            Assert.AreEqual(upscaler, ReDefinition.Window.TooltipText.Wrap(upscaler), "a line of the length stays");

            string longer = upscaler + " and in every mode but AA only also upscaling.";
            string wrapped = ReDefinition.Window.TooltipText.Wrap(longer + "\nShort.");
            foreach (string line in wrapped.Split('\n'))
                Assert.IsTrue(line.Length <= ReDefinition.Window.TooltipText.MaxLine, line);
            Assert.AreEqual(longer.Replace("antialiasing, and", "antialiasing,\nand") + "\nShort.", wrapped);
        }

        [TestMethod]
        public void AResolutionIsWrittenAndReadAsKspShowsIt()
        {
            Assert.AreEqual("3440 x 1440", KspBehaviour.ResolutionText(3440, 1440));
            int width;
            int height;
            Assert.IsTrue(KspBehaviour.ParseResolution("3440 x 1440", out width, out height));
            Assert.AreEqual(3440, width);
            Assert.AreEqual(1440, height);
            Assert.IsTrue(KspBehaviour.ParseResolution("1920x1080", out width, out height));
            Assert.AreEqual(1080, height);
            Assert.IsFalse(KspBehaviour.ParseResolution("wide", out width, out height));
            Assert.IsFalse(KspBehaviour.ParseResolution("0 x 720", out width, out height));
            Assert.IsFalse(KspBehaviour.ParseResolution("", out width, out height));
        }
    }
}
