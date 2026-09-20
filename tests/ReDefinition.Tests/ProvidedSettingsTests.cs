using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Settings;

namespace ReDefinition.Tests
{
    // A mod that answers for its own settings, the way a mod author writes it: no
    // reference to ReDefinition, members found by name.
    internal sealed class ProviderFake
    {
        internal static ProviderFake Instance = new ProviderFake();

        internal readonly Dictionary<string, string> Values = new Dictionary<string, string>
        {
            { "quality", "Medium" },
            { "shadows", "True" },
            { "steps", "32" },
        };

        internal bool CanTake = true;
        internal int Saves;
        internal string[] Lists = { "Low", "Medium", "High" };

        public bool Ready
        {
            get { return CanTake; }
        }

        public string Version
        {
            get { return "2.3.4"; }
        }

        public string Read(string name)
        {
            string value;
            return Values.TryGetValue(name, out value) ? value : null;
        }

        public void Write(string name, string value)
        {
            Values[name] = value;
        }

        public string[] Choices(string name)
        {
            return name == "quality" ? Lists : null;
        }

        public void Save()
        {
            Saves++;
        }
    }

    // A build of that mod without the two members a registration needs of it.
    internal sealed class ProviderWithoutReadFake
    {
        internal static ProviderWithoutReadFake Instance = new ProviderWithoutReadFake();

        public void Save()
        {
        }
    }

    // A bridge that refuses a value, as a mod may.
    internal static class RefusingProviderFake
    {
        internal static string Value = "1";

        public static string Read(string name)
        {
            return name == "level" ? Value : null;
        }

        public static bool Write(string name, string value)
        {
            if (value == "9") return false;
            Value = value;
            return true;
        }

        public static void Save()
        {
        }
    }

    // Everything static, and no Ready, Save or Choices: the smallest one.
    internal static class StaticProviderFake
    {
        internal static string Value = "4";

        public static string Read(string name)
        {
            return name == "detail" ? Value : null;
        }

        public static void Write(string name, string value)
        {
            if (name == "detail") Value = value;
        }
    }

    // A mod's own behaviour: `behaviour` naming a type of the mod instead of one of
    // ReDefinition's names.
    [TestClass]
    public class ProvidedSettingsTests
    {
        private static RegisteredMod Build(string text, List<string> problems)
        {
            ConfigNode node = ConfigNode.Parse(text).GetNode(ModRegistration.NodeName);
            return new RegisteredMod(ModRegistration.FromConfigNode(node, problems), problems);
        }

        private const string Head = "MOD_SETTINGS\n{\n name = providerfake\n"
                                    + " detect = ReDefinition.Tests.ProviderFake\n"
                                    + " behaviour = ReDefinition.Tests.ProviderFake\n";

        private static BundledSetting Only(RegisteredMod mod, string name)
        {
            foreach (BundledSetting setting in mod.Settings)
                if (setting.Key == "providerfake." + name) return setting;
            Assert.Fail("no setting " + name);
            return null;
        }

        [TestInitialize]
        public void Reset()
        {
            ProviderFake.Instance = new ProviderFake();
        }

        [TestMethod]
        public void ASettingWithoutAMemberIsReadAndWrittenByTheMod()
        {
            List<string> problems = new List<string>();
            RegisteredMod mod = Build(Head + " SETTING\n {\n  name = shadows\n  default = True\n }\n}", problems);
            BundledSetting shadows = Only(mod, "shadows");

            Assert.AreEqual("True", shadows.Read(), string.Join("; ", problems.ToArray()));
            Assert.AreEqual(SettingControl.Toggle, shadows.Control);
            shadows.Write("False");
            Assert.AreEqual("False", ProviderFake.Instance.Values["shadows"]);
        }

        [TestMethod]
        public void AMemberPathStillCountsBesideIt()
        {
            List<string> problems = new List<string>();
            RegisteredMod mod = Build(Head
                + " SETTING\n {\n  name = detail\n  member = ReDefinition.Tests.StaticProviderFake.Value\n  default = 4\n }\n"
                + " SETTING\n {\n  name = steps\n  default = 32\n }\n}", problems);

            StaticProviderFake.Value = "4";
            Only(mod, "detail").Write("7");
            Assert.AreEqual("7", StaticProviderFake.Value);
            Assert.IsFalse(ProviderFake.Instance.Values.ContainsKey("detail"));

            Only(mod, "steps").Write("64");
            Assert.AreEqual("64", ProviderFake.Instance.Values["steps"]);
        }

        [TestMethod]
        public void TheModsOwnListIsTheRowsList()
        {
            RegisteredMod mod = Build(Head + " SETTING\n {\n  name = quality\n  default = Medium\n }\n}",
                                      new List<string>());
            BundledSetting quality = Only(mod, "quality");

            Assert.AreEqual(SettingControl.Choice, quality.Control);
            CollectionAssert.AreEqual(new[] { "Low", "Medium", "High" }, quality.CurrentChoices());

            // What the mod knows only while it runs.
            ProviderFake.Instance.Lists = new[] { "Low", "Medium", "High", "Ultra" };
            CollectionAssert.AreEqual(new[] { "Low", "Medium", "High", "Ultra" }, quality.CurrentChoices());
        }

        [TestMethod]
        public void NothingIsReadWhileTheModSaysItIsNotReady()
        {
            RegisteredMod mod = Build(Head + " SETTING\n {\n  name = shadows\n  default = True\n }\n}",
                                      new List<string>());
            BundledSetting shadows = Only(mod, "shadows");

            ProviderFake.Instance.CanTake = false;
            Assert.IsNull(shadows.Read());
            Assert.IsFalse(shadows.Applicable());

            ProviderFake.Instance.CanTake = true;
            Assert.AreEqual("True", shadows.Read());
            Assert.IsTrue(shadows.Applicable());
        }

        [TestMethod]
        public void TheModSavesThroughItsOwnMethod()
        {
            RegisteredMod mod = Build(Head + " SETTING\n {\n  name = shadows\n  default = True\n }\n}",
                                      new List<string>());
            mod.Save();
            Assert.AreEqual(1, ProviderFake.Instance.Saves);

            // Not while it cannot take values: the store would count what waits for
            // it as saved.
            ProviderFake.Instance.CanTake = false;
            try
            {
                mod.Save();
                Assert.Fail("saved although the mod is not ready");
            }
            catch (System.InvalidOperationException)
            {
            }
            Assert.AreEqual(1, ProviderFake.Instance.Saves);
        }

        [TestMethod]
        public void TheVersionTheModTellsCounts()
        {
            RegisteredMod mod = Build(Head + " SETTING\n {\n  name = shadows\n  default = True\n }\n}",
                                      new List<string>());
            Assert.AreEqual("2.3.4", mod.Version);
        }

        [TestMethod]
        public void StaticMembersAloneAreEnough()
        {
            StaticProviderFake.Value = "4";
            RegisteredMod mod = Build("MOD_SETTINGS\n{\n name = staticfake\n"
                                      + " detect = ReDefinition.Tests.StaticProviderFake\n"
                                      + " behaviour = ReDefinition.Tests.StaticProviderFake\n"
                                      + " SETTING\n {\n  name = detail\n  default = 4\n }\n}", new List<string>());
            BundledSetting detail = mod.Settings[0];

            Assert.AreEqual("4", detail.Read());
            detail.Write("8");
            Assert.AreEqual("8", StaticProviderFake.Value);
        }

        [TestMethod]
        public void ABuildWithoutReadAndWriteLeavesTheModOut()
        {
            List<string> problems = new List<string>();
            RegisteredMod mod = Build("MOD_SETTINGS\n{\n name = withoutread\n"
                                      + " detect = ReDefinition.Tests.ProviderWithoutReadFake\n"
                                      + " behaviour = ReDefinition.Tests.ProviderWithoutReadFake\n"
                                      + " SETTING\n {\n  name = shadows\n  default = True\n }\n}", problems);

            Assert.AreEqual(0, mod.Settings.Count);
            Assert.IsTrue(problems.Exists(p => p.Contains("Read")), string.Join("; ", problems.ToArray()));
        }

        [TestMethod]
        public void AKeyBindingStaysWithReDefinition()
        {
            List<string> problems = new List<string>();
            RegisteredMod mod = Build(Head
                + " KEY\n {\n  name = window\n  title = MyMod's window\n  default = LeftAlt+F10\n }\n}", problems);
            BundledSetting binding = Only(mod, "window");

            // The row takes a key, and the bridge never sees it: ReDefinition keeps it,
            // and the mod reads it through ReDefinition.Api.Keys.
            Assert.AreEqual(SettingControl.Binding, binding.Control);
            Assert.AreEqual("LeftAlt+F10", binding.Read());
            binding.Write("LeftControl+F9");
            Assert.AreEqual("LeftControl+F9", binding.Read());
            Assert.IsFalse(ProviderFake.Instance.Values.ContainsKey("window"));
        }

        [TestMethod]
        public void AValueTheModRefusesIsNotTaken()
        {
            RefusingProviderFake.Value = "1";
            RegisteredMod mod = Build("MOD_SETTINGS\n{\n name = refusingfake\n"
                                      + " detect = ReDefinition.Tests.RefusingProviderFake\n"
                                      + " behaviour = ReDefinition.Tests.RefusingProviderFake\n"
                                      + " SETTING\n {\n  name = level\n  default = 1\n }\n}", new List<string>());
            BundledSetting level = mod.Settings[0];

            level.Write("4");
            Assert.AreEqual("4", RefusingProviderFake.Value);
            try
            {
                level.Write("9");
                Assert.Fail("a refused value counted as set");
            }
            catch (System.InvalidOperationException)
            {
            }
            Assert.AreEqual("4", RefusingProviderFake.Value);
        }

        [TestMethod]
        public void ReadyGatesTheSettingsWithAMemberToo()
        {
            StaticProviderFake.Value = "4";
            RegisteredMod mod = Build(Head
                + " SETTING\n {\n  name = detail\n  member = ReDefinition.Tests.StaticProviderFake.Value\n"
                + "  default = 4\n }\n}", new List<string>());
            BundledSetting detail = Only(mod, "detail");

            ProviderFake.Instance.CanTake = false;
            Assert.IsNull(detail.Read());
            Assert.IsFalse(detail.Applicable());

            ProviderFake.Instance.CanTake = true;
            Assert.AreEqual("4", detail.Read());
        }

        [TestMethod]
        public void AModThatKeepsValuesInItsFilesNeedsSomewhereToSave()
        {
            List<string> problems = new List<string>();
            Build("MOD_SETTINGS\n{\n name = staticfake\n"
                  + " detect = ReDefinition.Tests.StaticProviderFake\n"
                  + " behaviour = ReDefinition.Tests.StaticProviderFake\n"
                  + " saving = InModFiles\n"
                  + " SETTING\n {\n  name = detail\n  default = 4\n }\n}", problems);
            Assert.IsTrue(problems.Exists(p => p.Contains("InModFiles")), string.Join("; ", problems.ToArray()));
        }

        [TestMethod]
        public void ASettingWithoutADefaultIsReported()
        {
            List<string> problems = new List<string>();
            Build(Head + " SETTING\n {\n  name = shadows\n }\n}", problems);
            Assert.IsTrue(problems.Exists(p => p.Contains("needs a default")), string.Join("; ", problems.ToArray()));
        }

        [TestMethod]
        public void ATypeThatIsNotThereLeavesTheModOut()
        {
            List<string> problems = new List<string>();
            RegisteredMod mod = Build("MOD_SETTINGS\n{\n name = nothere\n"
                                      + " detect = ReDefinition.Tests.ProviderFake\n"
                                      + " behaviour = ReDefinition.Tests.NoSuchBridge\n"
                                      + " SETTING\n {\n  name = shadows\n  default = True\n }\n}", problems);

            Assert.AreEqual(0, mod.Settings.Count);
            Assert.IsTrue(problems.Exists(p => p.Contains("NoSuchBridge")), string.Join("; ", problems.ToArray()));
        }
    }
}
