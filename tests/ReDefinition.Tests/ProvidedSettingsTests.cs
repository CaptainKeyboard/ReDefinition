using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Settings;

namespace ReDefinition.Tests
{
    // A mod that answers for its own settings, the way a mod author writes it: no
    // reference to ReDefinition, members found by name.
    internal sealed class ProviderFake
    {
        // The instance the registration's behaviour reaches.
        internal static ProviderFake Instance = new ProviderFake();

        // The mod's settings by name.
        internal readonly Dictionary<string, string> Values = new Dictionary<string, string>
        {
            { "quality", "Medium" },
            { "shadows", "True" },
            { "steps", "32" },
        };

        // Whether the mod is ready, how often it saved, and the quality setting's list.
        internal bool CanTake = true;
        internal int Saves;
        internal string[] Lists = { "Low", "Medium", "High" };

        // Whether the mod can take values now.
        public bool Ready
        {
            get { return CanTake; }
        }

        // The mod's version, which its defaults are checked against.
        public string Version
        {
            get { return "2.3.4"; }
        }

        // The setting's value by name, null for one the mod does not have.
        public string Read(string name)
        {
            string value;
            return Values.TryGetValue(name, out value) ? value : null;
        }

        // Takes the value for the setting of that name.
        public void Write(string name, string value)
        {
            Values[name] = value;
        }

        // The list the quality setting offers, none for the others.
        public string[] Choices(string name)
        {
            return name == "quality" ? Lists : null;
        }

        // Counts each save the store asks for.
        public void Save()
        {
            Saves++;
        }
    }

    // A build of that mod without the two members a registration needs of it.
    internal sealed class ProviderWithoutReadFake
    {
        // The instance the registration's behaviour reaches.
        internal static ProviderWithoutReadFake Instance = new ProviderWithoutReadFake();

        // A save routine without Read and Write beside it.
        public void Save()
        {
        }
    }

    // A bridge that refuses a value, as a mod may.
    internal static class RefusingProviderFake
    {
        // The one setting the bridge holds.
        internal static string Value = "1";

        // The value of level, null for any other name.
        public static string Read(string name)
        {
            return name == "level" ? Value : null;
        }

        // Takes the value but refuses 9, answering false.
        public static bool Write(string name, string value)
        {
            if (value == "9") return false;
            Value = value;
            return true;
        }

        // Saves nothing: the values live in the field.
        public static void Save()
        {
        }
    }

    // Everything static, and no Ready, Save or Choices: the smallest one.
    internal static class StaticProviderFake
    {
        // The one setting the bridge holds.
        internal static string Value = "4";

        // The value of detail, null for any other name.
        public static string Read(string name)
        {
            return name == "detail" ? Value : null;
        }

        // Takes the value of detail and ignores any other name.
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
        // The registered mod a MOD_SETTINGS text makes, its problems added to `problems`.
        private static RegisteredMod Build(string text, List<string> problems)
        {
            ConfigNode node = ConfigNode.Parse(text).GetNode(ModRegistration.NodeName);
            return new RegisteredMod(ModRegistration.FromConfigNode(node, problems), problems);
        }

        // The registration's head: ProviderFake detected, and answering for its settings itself.
        private const string Head = "MOD_SETTINGS\n{\n name = providerfake\n"
                                    + " detect = ReDefinition.Tests.ProviderFake\n"
                                    + " behaviour = ReDefinition.Tests.ProviderFake\n";

        // The mod's setting of that name, failing the test where there is none.
        private static BundledSetting Only(RegisteredMod mod, string name)
        {
            foreach (BundledSetting setting in mod.Settings)
                if (setting.Key == "providerfake." + name) return setting;
            Assert.Fail("no setting " + name);
            return null;
        }

        // A fresh ProviderFake for each test.
        [TestInitialize]
        public void Reset()
        {
            ProviderFake.Instance = new ProviderFake();
        }

        // A setting without a member is read and written through the mod's Read and Write, a True or False value
        // making it a toggle.
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

        // A member path still counts for a setting that has one, beside the ones the mod answers for.
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

        // Where the mod gives choices for a setting, the row is a list of them, read again as the mod's list changes.
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

        // Nothing is read and the setting does not apply while the mod's Ready says no.
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

        // Saving calls the mod's own Save, but not while the mod is not ready: the store would count what waits
        // for it as saved.
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

        // The version the mod's Version gives is the one its defaults are checked against.
        [TestMethod]
        public void TheVersionTheModTellsCounts()
        {
            RegisteredMod mod = Build(Head + " SETTING\n {\n  name = shadows\n  default = True\n }\n}",
                                      new List<string>());
            Assert.AreEqual("2.3.4", mod.Version);
        }

        // A static type with Read and Write alone is enough to answer for a mod's settings.
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

        // A behaviour type without Read and Write leaves the mod without settings, and that is reported.
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

        // A key binding stays ReDefinition's to keep, never passed to the mod's Write, with the bundling on or off;
        // the mod reads it through ReDefinition.Api.Keys.
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
            Assert.IsFalse(binding.Bundled, "ReDefinition's own to keep, with the bundling on or off");
            Assert.IsTrue(binding.KeptHere);
        }

        // A value the mod's Write refuses throws and is not taken; the mod keeps its value.
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

        // The mod's Ready gates a setting with a member path as well: nothing is read while it is not ready.
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

        // A mod that keeps its values in its own files but has no Save is reported: what is set would not last.
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

        // A setting the mod answers for needs a default in the registration, and one without is reported.
        [TestMethod]
        public void ASettingWithoutADefaultIsReported()
        {
            List<string> problems = new List<string>();
            Build(Head + " SETTING\n {\n  name = shadows\n }\n}", problems);
            Assert.IsTrue(problems.Exists(p => p.Contains("needs a default")), string.Join("; ", problems.ToArray()));
        }

        // A setting with a default gives no such report.
        [TestMethod]
        public void ASettingWithADefaultIsNotReported()
        {
            List<string> problems = new List<string>();
            Build(Head + " SETTING\n {\n  name = shadows\n  default = True\n }\n}", problems);
            Assert.IsFalse(problems.Exists(p => p.Contains("needs a default")),
                           string.Join("; ", problems.ToArray()));
        }

        // A behaviour type the mod does not have leaves the mod without settings, naming the type.
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
