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
        public void ABindingNamesItsSection()
        {
            List<string> problems;
            ModRegistration mod = Read("MOD_SETTINGS\n{\n name = mymod\n detect = My.Settings\n"
                                       + " KEY\n {\n  name = window\n  member = My.Settings.key\n  group = EVA\n }\n"
                                       + " KEY\n {\n  name = other\n  member = My.Settings.other\n }\n}", out problems);
            Assert.AreEqual("EVA", mod.Setting("window").KeyGroup);
            // None named: Mods.
            Assert.IsNull(mod.Setting("other").KeyGroup);
            Assert.AreEqual(0, problems.Count, string.Join("; ", problems.ToArray()));
            // A section named like one of KSP's counts where that group does, any other everywhere.
            Assert.AreEqual(KspKeyBindings.Eva, KspKeyBindings.SituationsOfGroup("eva"));
            Assert.AreEqual(KspKeyBindings.Everywhere, KspKeyBindings.SituationsOfGroup("Mods"));
            Assert.AreEqual(KspKeyBindings.Everywhere, KspKeyBindings.SituationsOfGroup("My mod's tools"));
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

        private const int All = KspKeyBindings.Everywhere;

        private static List<string> Sharing(string[] texts, int[] situations, int[] modes, string[] shipped)
        {
            List<string> keys = new List<string>();
            for (int i = 0; i < texts.Length; i++) keys.Add(((char)('a' + i)).ToString());
            return Conflicts.Sharing(keys, texts, situations, modes, shipped);
        }

        [TestMethod]
        public void RowsThatShareACombinationAreFound()
        {
            List<string> shared = Sharing(new[] { "LeftAlt+F10", "LeftAlt+F10", "F11" }, new[] { All, All, All },
                new[] { -1, -1, -1 }, new string[] { null, null, null });
            CollectionAssert.AreEquivalent(new[] { "a", "b" }, shared);
        }

        [TestMethod]
        public void AModifierSeparatesTwoRows()
        {
            Assert.AreEqual(0, Sharing(new[] { "F10", "LeftAlt+F10" }, new[] { All, All }, new[] { -1, -1 },
                new string[] { null, null }).Count);
        }

        [TestMethod]
        public void TheSameKeyOnEvaAndInFlightIsNoConflict()
        {
            // B boards on EVA and brakes a vessel.
            Assert.AreEqual(0, Sharing(new[] { "B", "B" }, new[] { KspKeyBindings.Eva, KspKeyBindings.Flight },
                new[] { -1, -1 }, new string[] { null, null }).Count);
            // In the same situation it is.
            Assert.AreEqual(2, Sharing(new[] { "B", "B" }, new[] { KspKeyBindings.Flight, KspKeyBindings.Flight },
                new[] { -1, -1 }, new string[] { null, null }).Count);
            // A binding that counts everywhere meets both.
            Assert.AreEqual(2, Sharing(new[] { "B", "B" }, new[] { KspKeyBindings.Eva, All },
                new[] { -1, -1 }, new string[] { null, null }).Count);
        }

        [TestMethod]
        public void FlightModesThatNeverCountTogetherDoNotShare()
        {
            // Space stages (switchState 1), and in docking mode (6) switches translation.
            int flight = KspKeyBindings.Flight;
            Assert.AreEqual(0, Sharing(new[] { "Space", "Space" }, new[] { flight, flight }, new[] { 1, 6 },
                new string[] { null, null }).Count);
            Assert.AreEqual(2, Sharing(new[] { "Space", "Space" }, new[] { flight, flight }, new[] { 1, 1 },
                new string[] { null, null }).Count);
        }

        [TestMethod]
        public void KeysKspShipsTogetherAreNotMarkedWhileAtTheirDefault()
        {
            int flight = KspKeyBindings.Flight;
            // W pitches and drives a rover, as KSP ships them.
            Assert.AreEqual(0, Sharing(new[] { "W", "W" }, new[] { flight, flight }, new[] { 5, 5 },
                new[] { "W", "W" }).Count);
            // Moved onto a key another one holds, they are marked.
            Assert.AreEqual(2, Sharing(new[] { "W", "W" }, new[] { flight, flight }, new[] { 5, 5 },
                new[] { "W", "S" }).Count);
        }

        [TestMethod]
        public void KspBindsModifiersAsKeys()
        {
            // THROTTLE_UP is LeftShift: a key of its own for KSP.
            Assert.AreEqual(UnityEngine.KeyCode.LeftShift, KeyCombination.ParseLoose("LeftShift").Key);
            Assert.IsFalse(KeyCombination.Parse("LeftShift").IsBound);
            int flight = KspKeyBindings.Flight;
            Assert.AreEqual(2, Sharing(new[] { "LeftShift", "LeftShift" }, new[] { flight, flight }, new[] { 1, 1 },
                new string[] { null, null }).Count);
        }

        [TestMethod]
        public void AgainstKspsOwnOnlyTheKeyCounts()
        {
            int flight = KspKeyBindings.Flight;
            // KSP's U fires whatever modifiers are held: RightControl+RightShift+U sets it off.
            Assert.AreEqual(2, Sharing(new[] { "RightControl+RightShift+U", "U" }, new[] { All, flight },
                new[] { -1, -1 }, new string[] { null, "U" }).Count);
            // Between two bindings that are not KSP's, the modifiers still tell them apart.
            Assert.AreEqual(0, Sharing(new[] { "RightControl+RightShift+U", "U" }, new[] { All, All },
                new[] { -1, -1 }, new string[] { null, null }).Count);
        }

        [TestMethod]
        public void AnUnboundRowSharesWithNobody()
        {
            Assert.AreEqual(0, Sharing(new[] { "None", "None" }, new[] { All, All }, new[] { -1, -1 },
                new string[] { null, null }).Count);
        }

        [TestMethod]
        public void AModWithOneModifierKeepsTheFirst()
        {
            KeyCombination combination = KeyCombination.Parse("LeftControl+LeftAlt+F10");
            Assert.AreEqual("LeftControl+F10", combination.WithAtMost(1).ToString());
            Assert.AreEqual("F10", combination.WithAtMost(0).ToString());
            // What already fits stays as it is.
            Assert.AreEqual("LeftControl+LeftAlt+F10", combination.WithAtMost(2).ToString());
        }

        [TestMethod]
        public void KspsFieldNamesAreReadableAndGrouped()
        {
            Assert.AreEqual("Pitch down", KspKeyBindings.Readable("PITCH_DOWN"));
            Assert.AreEqual("Time warp increase", KspKeyBindings.Readable("TIME_WARP_INCREASE"));
            // KSP's other spellings: camel case, and a number at the end.
            Assert.AreEqual("Editor pitch up", KspKeyBindings.Readable("Editor_pitchUp"));
            Assert.AreEqual("Custom action group 10", KspKeyBindings.Readable("CustomActionGroup10"));
            Assert.AreEqual("Eva pack forward", KspKeyBindings.Readable("EVA_Pack_forward"));

            Assert.AreEqual("Flight", KspKeyBindings.GroupOf("PITCH_DOWN"));
            Assert.AreEqual("Flight", KspKeyBindings.GroupOf("CustomActionGroup1"));
            Assert.AreEqual("Flight", KspKeyBindings.GroupOf("Docking_toggleRotLin"));
            Assert.AreEqual("Camera", KspKeyBindings.GroupOf("CAMERA_ORBIT_UP"));
            Assert.AreEqual("EVA", KspKeyBindings.GroupOf("EVA_forward"));
            Assert.AreEqual("Editor", KspKeyBindings.GroupOf("Editor_pitchUp"));
            Assert.AreEqual("General", KspKeyBindings.GroupOf("PAUSE"));
            Assert.AreEqual(KspKeyBindings.Everywhere, KspKeyBindings.SituationsOf("QUICKSAVE"));
            Assert.AreEqual(KspKeyBindings.Eva, KspKeyBindings.SituationsOf("EVA_Board"));
        }
    }
}
