using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Settings;
using UnityEngine;

namespace ReDefinition.Tests
{
    // A mod's key as its members keep it: a KeyCode alone, a key with its modifiers
    // beside it, or the whole combination as text.
    internal static class BindingFake
    {
        // The members a KEY block names: the key, two modifiers kept beside it, and a combination kept as text.
        internal static KeyCode Key = KeyCode.F10;
        internal static KeyCode Modifier1 = KeyCode.None;
        internal static KeyCode Modifier2 = KeyCode.None;
        internal static string Text = "LeftAlt+F10";
    }

    // How many modifiers a binding offers follows the members that hold them, and a
    // write never gives a member what it cannot hold.
    [TestClass]
    public class BindingMembersTests
    {
        // The one setting a MOD_SETTINGS registration of BindingFake with the given KEY block makes, asserting
        // the registration has no problems.
        private static BundledSetting Build(string key)
        {
            List<string> problems = new List<string>();
            ConfigNode node = ConfigNode.Parse("MOD_SETTINGS\n{\n name = bindingfake\n"
                                               + " detect = ReDefinition.Tests.BindingFake\n" + key + "}")
                                        .GetNode(ModRegistration.NodeName);
            RegisteredMod mod = new RegisteredMod(ModRegistration.FromConfigNode(node, problems), problems);
            Assert.AreEqual(1, mod.Settings.Count, string.Join("; ", problems.ToArray()));
            return mod.Settings[0];
        }

        // A key kept as a KeyCode alone offers no modifier; a combination written to it gives the member its key.
        [TestMethod]
        public void AKeyCodeAloneHoldsNoModifier()
        {
            BindingFake.Key = KeyCode.F10;
            BundledSetting setting = Build(" KEY\n {\n  name = window\n  member = ReDefinition.Tests.BindingFake.Key\n }\n");
            Assert.AreEqual(0, setting.MaxModifiers);
            // A combination written anyway gives the member its key, and nothing throws.
            setting.Write("LeftAlt+F9");
            Assert.AreEqual(KeyCode.F9, BindingFake.Key);
        }

        // A key with one modifier member beside it offers one modifier.
        [TestMethod]
        public void OneModifierMemberHoldsOne()
        {
            BundledSetting setting = Build(" KEY\n {\n  name = window\n  member = ReDefinition.Tests.BindingFake.Key\n"
                                           + "  modifier1 = ReDefinition.Tests.BindingFake.Modifier1\n }\n");
            Assert.AreEqual(1, setting.MaxModifiers);
        }

        // A key with two modifier members offers two modifiers.
        [TestMethod]
        public void TwoModifierMembersHoldTwo()
        {
            BundledSetting setting = Build(" KEY\n {\n  name = window\n  member = ReDefinition.Tests.BindingFake.Key\n"
                                           + "  modifier1 = ReDefinition.Tests.BindingFake.Modifier1\n"
                                           + "  modifier2 = ReDefinition.Tests.BindingFake.Modifier2\n }\n");
            Assert.AreEqual(2, setting.MaxModifiers);
        }

        // Deferred's rule: every modifier member held with the key. One modifier
        // goes into both, and the key alone into both as the key itself.
        [TestMethod]
        public void AllModifiersWithoutOneHoldTheKeyItself()
        {
            BundledSetting setting = Build(" KEY\n {\n  name = window\n  member = ReDefinition.Tests.BindingFake.Key\n"
                                           + "  modifier1 = ReDefinition.Tests.BindingFake.Modifier1\n"
                                           + "  modifier2 = ReDefinition.Tests.BindingFake.Modifier2\n"
                                           + "  modifiers = all\n }\n");
            setting.Write("LeftAlt+D");
            Assert.AreEqual(KeyCode.LeftAlt, BindingFake.Modifier1);
            Assert.AreEqual(KeyCode.LeftAlt, BindingFake.Modifier2);
            Assert.AreEqual("LeftAlt+D", setting.Read());

            setting.Write("D");
            Assert.AreEqual(KeyCode.D, BindingFake.Key);
            Assert.AreEqual(KeyCode.D, BindingFake.Modifier1);
            Assert.AreEqual(KeyCode.D, BindingFake.Modifier2);
            Assert.AreEqual("D", setting.Read());

            setting.Write(KeyCombination.NoneText);
            Assert.AreEqual(KeyCode.None, BindingFake.Modifier1);
            Assert.AreEqual(KeyCode.None, BindingFake.Modifier2);
        }

        // A key kept as text holds the whole combination, two modifiers with the key, and is written as given.
        [TestMethod]
        public void TextHoldsTheWholeCombination()
        {
            BundledSetting setting = Build(" KEY\n {\n  name = window\n  member = ReDefinition.Tests.BindingFake.Text\n }\n");
            Assert.AreEqual(2, setting.MaxModifiers);
            setting.Write("RightControl+RightShift+J");
            Assert.AreEqual("RightControl+RightShift+J", BindingFake.Text);
        }
    }
}
