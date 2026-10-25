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
        // The members the registrations below set: a volume from 0 to 1 and a switch.
        internal static float Volume = 0.5f;
        internal static bool Labels = true;
    }

    // What the registration format gained for KSP's whole settings screen: the
    // tabs beyond graphics, a group's heading, a percentage, settings set
    // directly, and a mod whose rows are set directly with the bundling off.
    [TestClass]
    public class KspSettingsTests
    {
        // The registered mod a MOD_SETTINGS text makes, its problems added to `problems`.
        private static RegisteredMod Build(string text, List<string> problems)
        {
            ConfigNode node = ConfigNode.Parse(text).GetNode(ModRegistration.NodeName);
            return new RegisteredMod(ModRegistration.FromConfigNode(node, problems), problems);
        }

        // A registration of TabsFake setting the mod directly: the volume in the Audio tab as a percentage and
        // not bundled, the labels in the Gameplay tab.
        private const string Registration =
            "MOD_SETTINGS\n{\n name = tabsfake\n detect = ReDefinition.Tests.TabsFake\n direct = True\n"
            + " SETTING\n {\n  name = Volume\n  member = ReDefinition.Tests.TabsFake.Volume\n  row = Audio\n"
            + "  section = Volume\n  min = 0\n  max = 1\n  percent = True\n  default = 0.5\n  bundled = False\n }\n"
            + " SETTING\n {\n  name = Labels\n  member = ReDefinition.Tests.TabsFake.Labels\n  row = Gameplay\n"
            + "  default = True\n }\n}";

        // The registration's new keys arrive: direct on the mod, and on the settings their section, percent,
        // bundled (on by default) and the tab each stands in.
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

        // Each of the tabs beyond graphics is a row a setting can stand in; an unknown row is reported and the
        // setting has none.
        [TestMethod]
        public void EveryNewTabIsARowAndAnUnknownOneIsNot()
        {
            foreach (string tab in new[] { "Gameplay", "Audio", "Display", "Detail", "Axes", "Devices" })
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

        // A slider's step reaches the setting; a step of 0, a negative or no number is reported and dropped.
        [TestMethod]
        public void AStepReachesTheSliderAndABadOneIsReported()
        {
            List<string> problems = new List<string>();
            RegisteredMod mod = Build(Registration.Replace("percent = True", "percent = True\n  step = 0.1"), problems);
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
            Assert.AreEqual(0.1f, mod.Settings[0].Step, 1e-6f);

            foreach (string bad in new[] { "0", "-1", "x" })
            {
                List<string> refused = new List<string>();
                RegisteredMod other = Build(Registration.Replace("percent = True", "percent = True\n  step = " + bad), refused);
                Assert.AreEqual(1, refused.Count, "step " + bad);
                Assert.AreEqual(0f, other.Settings[0].Step, "step " + bad);
            }
        }

        // A percentage is shown on a slider only, so one without a min and max is reported.
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

        // Tooltip lines are kept to the length of the upscaler tooltip's first line: one that long stays, a
        // longer one is broken at a space so no line is longer, and the line breaks already there are kept.
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

        // A resolution is written as KSP's settings screen shows it, "3440 x 1440", and read with or without
        // the spaces; text that is no resolution, or a side of 0, is not read.
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
