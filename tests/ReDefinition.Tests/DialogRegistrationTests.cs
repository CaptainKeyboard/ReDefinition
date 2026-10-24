using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Settings;

namespace ReDefinition.Tests
{
    // A mod with a dialog: its content from a static method, and from an
    // instance that is there only once the mod made it.
    internal sealed class DialogFake
    {
        internal static DialogFake Instance;
        internal static bool Shown;
        internal static int Opened;

        internal static DialogGUIBase[] Content()
        {
            Opened++;
            return new DialogGUIBase[] { new DialogGUISpace(4f), null, new DialogGUISpace(8f) };
        }

        internal DialogGUIBase One()
        {
            return new DialogGUISpace(2f);
        }

        internal static void Nothing()
        {
        }

        internal static DialogGUIBase WithParameter(int id)
        {
            return null;
        }
    }

    // DIALOG blocks: read, checked against the mod's members, and placed as rows
    // without being settings.
    [TestClass]
    public class DialogRegistrationTests
    {
        private const string Mod = "MOD_SETTINGS\n{\n name = dialogfake\n detect = ReDefinition.Tests.DialogFake\n"
                                   + " SETTING\n {\n  name = Shown\n  member = ReDefinition.Tests.DialogFake.Shown\n"
                                   + "  default = False\n  row = Devices\n }\n";

        private static RegisteredMod Build(string blocks, List<string> problems)
        {
            ConfigNode node = ConfigNode.Parse(Mod + blocks + "}").GetNode(ModRegistration.NodeName);
            return new RegisteredMod(ModRegistration.FromConfigNode(node, problems), problems);
        }

        private static string Dialog(string body)
        {
            return " DIALOG\n {\n" + body + " }\n";
        }

        [TestMethod]
        public void ADialogIsARowOfItsTabButNoSetting()
        {
            List<string> problems = new List<string>();
            RegisteredMod mod = Build(Dialog("  name = connect\n  content = ReDefinition.Tests.DialogFake.Content\n"
                                             + "  title = Phone\n  button = Connect ...\n  heading = Connect a phone\n"
                                             + "  width = 420\n  row = Devices\n"), problems);

            CollectionAssert.AreEqual(new string[0], problems);
            Assert.IsTrue(mod.IsInstalled);
            Assert.AreEqual(1, mod.Settings.Count);
            Assert.AreEqual(1, mod.Dialogs.Count);
            BundledSetting dialog = mod.Dialogs[0];
            Assert.AreEqual("dialogfake.connect", dialog.Key);
            Assert.AreEqual(SettingControl.Dialog, dialog.Control);
            Assert.IsFalse(dialog.Bundled);
            Assert.AreEqual("Phone", dialog.Title);
            Assert.AreEqual("Connect ...", dialog.DialogButton);
            Assert.AreEqual("Connect a phone", dialog.DialogHeading);
            Assert.AreEqual(420f, dialog.DialogWidth);
            Assert.IsTrue(dialog.Applicable());

            // Asked for each time it opens; what is null is left out.
            int before = DialogFake.Opened;
            Assert.AreEqual(2, dialog.DialogContent().Length);
            Assert.AreEqual(before + 1, DialogFake.Opened);

            List<BundledSetting> rows = WindowLayout.In(SettingCategory.Devices, new List<IBundledMod> { mod });
            Assert.AreEqual(2, rows.Count);
            Assert.AreEqual("dialogfake.Shown", rows[0].Key);
            Assert.AreSame(dialog, rows[1]);
            Assert.AreEqual(0, WindowLayout.In(SettingCategory.Display, new List<IBundledMod> { mod }).Count);
        }

        [TestMethod]
        public void AnOrderPutsTheDialogAmongTheSettings()
        {
            List<string> problems = new List<string>();
            RegisteredMod mod = Build(Dialog("  name = connect\n  content = ReDefinition.Tests.DialogFake.Content\n"
                                             + "  row = Devices\n  order = 1\n"), problems);
            List<BundledSetting> rows = WindowLayout.In(SettingCategory.Devices, new List<IBundledMod> { mod });
            Assert.AreEqual("dialogfake.connect", rows[0].Key);
            Assert.AreEqual("Open ...", rows[0].DialogButton);
            Assert.AreEqual("connect", rows[0].DialogHeading);
            Assert.AreEqual(DialogRegistration.DefaultWidth, rows[0].DialogWidth);
        }

        [TestMethod]
        public void AnInstancesDialogWaitsForTheInstance()
        {
            List<string> problems = new List<string>();
            RegisteredMod mod = Build(Dialog("  name = connect\n  content = ReDefinition.Tests.DialogFake.Instance.One\n"
                                             + "  row = Devices\n"), problems);
            CollectionAssert.AreEqual(new string[0], problems);
            BundledSetting dialog = mod.Dialogs[0];
            DialogFake.Instance = null;
            try
            {
                Assert.IsFalse(dialog.Applicable());
                DialogFake.Instance = new DialogFake();
                Assert.IsTrue(dialog.Applicable());
                Assert.AreEqual(1, dialog.DialogContent().Length);
            }
            finally
            {
                DialogFake.Instance = null;
            }
        }

        [TestMethod]
        public void ADialogItsBuildCannotOpenIsLeftOut()
        {
            foreach (string content in new[]
                     {
                         "ReDefinition.Tests.DialogFake.Nothing", "ReDefinition.Tests.DialogFake.WithParameter",
                         "ReDefinition.Tests.DialogFake.Missing", "ReDefinition.Tests.DialogFake.Shown",
                     })
            {
                List<string> problems = new List<string>();
                RegisteredMod mod = Build(Dialog("  name = connect\n  content = " + content + "\n  row = Devices\n"),
                                          problems);
                Assert.AreEqual(0, mod.Dialogs.Count, content);
                Assert.IsTrue(problems.Exists(p => p.Contains("dialog 'connect'") && p.Contains("left out")), content);
                Assert.IsTrue(new List<string>(mod.DroppedMembers).Exists(line => line.StartsWith("connect")), content);
            }

            // Optional, without a word.
            List<string> quiet = new List<string>();
            RegisteredMod optional = Build(Dialog("  name = connect\n  content = ReDefinition.Tests.DialogFake.Missing\n"
                                                  + "  row = Devices\n  optional = True\n"), quiet);
            Assert.AreEqual(0, optional.Dialogs.Count);
            CollectionAssert.AreEqual(new string[0], quiet);
            Assert.AreEqual(0, optional.DroppedMembers.Count);
        }

        [TestMethod]
        public void ADialogNeedsItsContentARowAndANameOfItsOwn()
        {
            string[][] cases =
            {
                new[] { "  name = connect\n  row = Devices\n", "no content" },
                new[] { "  name = connect\n  content = ReDefinition.Tests.DialogFake.Content\n", "no row" },
                new[] { "  name = connect\n  content = ReDefinition.Tests.DialogFake.Content\n  row = Nowhere\n", "row 'Nowhere'" },
                new[] { "  name = Shown\n  content = ReDefinition.Tests.DialogFake.Content\n  row = Devices\n", "a setting of the mod" },
            };
            foreach (string[] test in cases)
            {
                List<string> problems = new List<string>();
                ModRegistration registration = ModRegistration.FromConfigNode(
                    ConfigNode.Parse(Mod + Dialog(test[0]) + "}").GetNode(ModRegistration.NodeName), problems);
                Assert.AreEqual(0, registration.Dialogs.Count, test[1]);
                Assert.IsTrue(problems.Exists(p => p.Contains(test[1]) && p.Contains("left out")), test[1]);
            }

            List<string> widthProblems = new List<string>();
            ModRegistration wide = ModRegistration.FromConfigNode(ConfigNode.Parse(Mod + Dialog(
                "  name = connect\n  content = ReDefinition.Tests.DialogFake.Content\n  row = Devices\n  width = 5000\n"
                + "  colour = red\n") + "}").GetNode(ModRegistration.NodeName), widthProblems);
            Assert.AreEqual(DialogRegistration.DefaultWidth, wide.Dialogs[0].Width);
            Assert.IsTrue(widthProblems.Exists(p => p.Contains("width '5000'")));
            Assert.IsTrue(widthProblems.Exists(p => p.Contains("colour")));
        }
    }
}
