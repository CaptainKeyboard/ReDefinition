using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Settings;
using ReDefinition.Window;

namespace ReDefinition.Tests
{
    // The Controls tab's decisions without the game: what a KEY block in a registration
    // means, which rows share a combination, and what KSP's field names are called
    // in the window.
    [TestClass]
    public class KeyBindingTests
    {
        // Reads a MOD_SETTINGS text into a registration, giving back its problems.
        private static ModRegistration Read(string text, out List<string> problems)
        {
            problems = new List<string>();
            ConfigNode node = ConfigNode.Parse(text).GetNode(ModRegistration.NodeName);
            return ModRegistration.FromConfigNode(node, problems);
        }

        // A KEY block makes a binding that stands in the Keys tab, which no profile sets, with its default.
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

        // A binding's group names its section, Mods where none is named; a section named like one of KSP's groups
        // counts in that group's situations, any other everywhere.
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

        // A binding whose default is no key binding is left out and reported.
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

        // A binding may have no member of its own: ReDefinition keeps it, and the mod asks ReDefinition.Api whether it is pressed.
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

        // A binding's modifier members are read with their rule; a second without a first and an unknown rule are
        // reported, the binding kept.
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

        // The keys a setting has and a binding has not -- min, max, choices, kind -- are reported as unknown.
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

        // A binding that counts in every situation.
        private const int All = KspKeyBindings.Everywhere;

        // The rows of `texts` that share a combination, by keys a, b, c ..., with their situations, KSP's
        // switchState modes (-1: any) and KSP's defaults (null: not KSP's).
        private static List<string> Sharing(string[] texts, int[] situations, int[] modes, string[] shipped)
        {
            List<string> keys = new List<string>();
            for (int i = 0; i < texts.Length; i++) keys.Add(((char)('a' + i)).ToString());
            return Conflicts.Sharing(keys, texts, situations, modes, shipped);
        }

        // Two rows on the same combination in the same situation are both marked; a row on another key is not.
        [TestMethod]
        public void RowsThatShareACombinationAreFound()
        {
            List<string> shared = Sharing(new[] { "LeftAlt+F10", "LeftAlt+F10", "F11" }, new[] { All, All, All },
                new[] { -1, -1, -1 }, new string[] { null, null, null });
            CollectionAssert.AreEquivalent(new[] { "a", "b" }, shared);
        }

        // A key alone and the same key with a modifier are different combinations and do not share.
        [TestMethod]
        public void AModifierSeparatesTwoRows()
        {
            Assert.AreEqual(0, Sharing(new[] { "F10", "LeftAlt+F10" }, new[] { All, All }, new[] { -1, -1 },
                new string[] { null, null }).Count);
        }

        // One key for EVA and for flying a vessel is no conflict; in the same situation, or against a binding
        // that counts everywhere, it is.
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

        // Two flight bindings for modes of KSP's switchState that never count together do not share a key; in
        // the same mode they do.
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

        // Two of KSP's rows it ships on the same key are not marked while both stand at their default, and are
        // once one is moved onto the other's key.
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

        // KSP binds a modifier as a key of its own, which a combination does not: two KSP rows on LeftShift share.
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

        // Against one of KSP's own bindings only the key counts, since KSP fires it whatever modifiers are held;
        // between other bindings the modifiers tell them apart.
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

        // Rows without a key share nothing, though their texts are the same.
        [TestMethod]
        public void AnUnboundRowSharesWithNobody()
        {
            Assert.AreEqual(0, Sharing(new[] { "None", "None" }, new[] { All, All }, new[] { -1, -1 },
                new string[] { null, null }).Count);
        }

        // Fitted to a mod that keeps fewer modifiers, a combination keeps the first ones, and one that fits stays.
        [TestMethod]
        public void AModWithOneModifierKeepsTheFirst()
        {
            KeyCombination combination = KeyCombination.Parse("LeftControl+LeftAlt+F10");
            Assert.AreEqual("LeftControl+F10", combination.WithAtMost(1).ToString());
            Assert.AreEqual("F10", combination.WithAtMost(0).ToString());
            // What already fits stays as it is.
            Assert.AreEqual("LeftControl+LeftAlt+F10", combination.WithAtMost(2).ToString());
        }

        // KSP's field names in their several spellings read as words in sentence case, each in its group, and
        // a binding counts in the situations of its group.
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
