using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition;
using ReDefinition.Framework;

namespace ReDefinition.Tests
{
    // The Keys tab's decisions without the game: what a KEY block in a registration
    // means, which rows share a combination, and what KSP's field names are called
    // in the window.
    [TestClass]
    public class KeyBindingTests
    {
        private static ModRegistration Read(string text, out List<string> problems)
        {
            problems = new List<string>();
            ConfigNode node = ConfigNode.Parse(text).GetNode(ModRegistration.NodeName);
            return ModRegistration.FromConfigNode(node, problems);
        }

        [TestMethod]
        public void AKeyBlockIsABindingInTheKeysTab()
        {
            List<string> problems;
            ModRegistration mod = Read("MOD_SETTINGS\n{\n name = mymod\n detect = My.Settings\n"
                                       + " KEY\n {\n  name = window\n  member = My.Settings.key\n"
                                       + "  default = LeftAlt+F10\n }\n}", out problems);
            SettingRegistration binding = mod.Setting("window");
            Assert.IsTrue(binding.IsBinding);
            Assert.AreEqual(SettingCategory.Keys, binding.Row);
            // A profile sets quality, and a binding is none.
            Assert.AreEqual(SettingKind.Other, binding.Kind);
            Assert.AreEqual("LeftAlt+F10", binding.Default);
            Assert.AreEqual(0, problems.Count, string.Join("; ", problems.ToArray()));
        }

        [TestMethod]
        public void ABindingWhoseDefaultIsNoKeyIsLeftOut()
        {
            List<string> problems;
            ModRegistration mod = Read("MOD_SETTINGS\n{\n name = mymod\n detect = My.Settings\n"
                                       + " KEY\n {\n  name = window\n  member = My.Settings.key\n"
                                       + "  default = Banana\n }\n}", out problems);
            Assert.IsNull(mod.Setting("window"));
            Assert.IsTrue(problems.Exists(problem => problem.Contains("is no key binding")));
        }

        [TestMethod]
        public void ABindingNeedsNoMemberOfItsOwn()
        {
            List<string> problems;
            ModRegistration mod = Read("MOD_SETTINGS\n{\n name = mymod\n detect = My.Settings\n"
                                       + " KEY\n {\n  name = shoot\n  default = F5\n }\n}", out problems);
            SettingRegistration binding = mod.Setting("shoot");
            Assert.IsNotNull(binding);
            Assert.IsNull(binding.Member);
            Assert.AreEqual(0, problems.Count, string.Join("; ", problems.ToArray()));
        }

        [TestMethod]
        public void ModifierMembersAreReadWithTheirRule()
        {
            List<string> problems;
            ModRegistration mod = Read("MOD_SETTINGS\n{\n name = mymod\n detect = My.Settings\n"
                                       + " KEY\n {\n  name = window\n  member = My.Settings.key\n"
                                       + "  modifier1 = My.Settings.mod1\n  modifier2 = My.Settings.mod2\n"
                                       + "  modifiers = all\n }\n}", out problems);
            SettingRegistration binding = mod.Setting("window");
            Assert.AreEqual("My.Settings.mod1", binding.Modifier1);
            Assert.AreEqual("My.Settings.mod2", binding.Modifier2);
            Assert.IsTrue(binding.ModifiersAll);

            ModRegistration lone = Read("MOD_SETTINGS\n{\n name = mymod\n detect = My.Settings\n"
                                        + " KEY\n {\n  name = window\n  member = My.Settings.key\n"
                                        + "  modifier2 = My.Settings.mod2\n  modifiers = sometimes\n }\n}",
                out problems);
            Assert.IsNotNull(lone.Setting("window"));
            Assert.IsTrue(problems.Exists(problem => problem.Contains("modifier2 without modifier1")));
            Assert.IsTrue(problems.Exists(problem => problem.Contains("modifiers 'sometimes'")));
        }

        [TestMethod]
        public void WhatASettingHasAndABindingHasNotIsReported()
        {
            List<string> problems;
            Read("MOD_SETTINGS\n{\n name = mymod\n detect = My.Settings\n"
                 + " KEY\n {\n  name = window\n  member = My.Settings.key\n  min = 1\n  max = 2\n"
                 + "  choices = a, b\n  kind = Quality\n }\n}", out problems);
            Assert.IsTrue(problems.Exists(problem => problem.Contains("unknown key 'min'")));
            Assert.IsTrue(problems.Exists(problem => problem.Contains("unknown key 'choices'")));
            Assert.IsTrue(problems.Exists(problem => problem.Contains("unknown key 'kind'")));
        }

        [TestMethod]
        public void RowsThatShareACombinationAreFound()
        {
            List<string> keys = new List<string> { "a", "b", "c" };
            List<string> texts = new List<string> { "LeftAlt+F10", "LeftAlt+F10", "F11" };
            List<int> modes = new List<int> { -1, -1, -1 };
            List<string> shared = Conflicts.Sharing(keys, texts, modes);
            CollectionAssert.AreEquivalent(new[] { "a", "b" }, shared);
        }

        [TestMethod]
        public void AModifierSeparatesTwoRows()
        {
            List<string> shared = Conflicts.Sharing(new List<string> { "a", "b" },
                new List<string> { "F10", "LeftAlt+F10" }, new List<int> { -1, -1 });
            Assert.AreEqual(0, shared.Count);
        }

        [TestMethod]
        public void BindingsThatNeverCountTogetherDoNotShare()
        {
            // KSP's modeMask: 1 is staging, 4 is docking rotation.
            List<string> shared = Conflicts.Sharing(new List<string> { "a", "b" }, new List<string> { "W", "W" },
                new List<int> { 1, 4 });
            Assert.AreEqual(0, shared.Count);

            // The same mask, and a binding that counts everywhere.
            Assert.AreEqual(2, Conflicts.Sharing(new List<string> { "a", "b" }, new List<string> { "W", "W" },
                new List<int> { 1, 1 }).Count);
            Assert.AreEqual(2, Conflicts.Sharing(new List<string> { "a", "b" }, new List<string> { "W", "W" },
                new List<int> { 1, -1 }).Count);
        }

        [TestMethod]
        public void AnUnboundRowSharesWithNobody()
        {
            List<string> shared = Conflicts.Sharing(new List<string> { "a", "b" },
                new List<string> { "None", "None" }, new List<int> { -1, -1 });
            Assert.AreEqual(0, shared.Count);
        }

        [TestMethod]
        public void KspsFieldNamesAreReadableAndGrouped()
        {
            Assert.AreEqual("Pitch down", KspKeyBindings.Readable("PITCH_DOWN"));
            Assert.AreEqual("Time warp increase", KspKeyBindings.Readable("TIME_WARP_INCREASE"));
            Assert.AreEqual("Flight", KspKeyBindings.GroupOf("PITCH_DOWN"));
            Assert.AreEqual("Camera", KspKeyBindings.GroupOf("CAMERA_ORBIT_UP"));
            Assert.AreEqual("EVA", KspKeyBindings.GroupOf("EVA_FORWARD"));
            Assert.AreEqual("General", KspKeyBindings.GroupOf("PAUSE"));
        }
    }
}
