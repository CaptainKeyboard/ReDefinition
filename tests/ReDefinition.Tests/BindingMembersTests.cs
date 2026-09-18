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

        [TestMethod]
        public void OneModifierMemberHoldsOne()
        {
            BundledSetting setting = Build(" KEY\n {\n  name = window\n  member = ReDefinition.Tests.BindingFake.Key\n"
                                           + "  modifier1 = ReDefinition.Tests.BindingFake.Modifier1\n }\n");
            Assert.AreEqual(1, setting.MaxModifiers);
        }

        [TestMethod]
        public void TwoModifierMembersHoldTwo()
        {
            BundledSetting setting = Build(" KEY\n {\n  name = window\n  member = ReDefinition.Tests.BindingFake.Key\n"
                                           + "  modifier1 = ReDefinition.Tests.BindingFake.Modifier1\n"
                                           + "  modifier2 = ReDefinition.Tests.BindingFake.Modifier2\n }\n");
            Assert.AreEqual(2, setting.MaxModifiers);
        }

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
