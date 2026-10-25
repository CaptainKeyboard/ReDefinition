using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Settings;
using UnityEngine;

namespace ReDefinition.Tests
{
    // The key binding as text: what the registrations, the store and KSP's
    // settings.cfg hold, and what the Controls tab shows.
    [TestClass]
    public class KeyCombinationTests
    {
        // A key without modifiers is read by its KeyCode name and written back the same.
        [TestMethod]
        public void AKeyOnItsOwnReadsAndWritesBack()
        {
            KeyCombination combination;
            Assert.IsTrue(KeyCombination.TryParse("F11", out combination));
            Assert.AreEqual(KeyCode.F11, combination.Key);
            Assert.AreEqual(0, combination.ModifierCount);
            Assert.AreEqual("F11", combination.ToString());
        }

        // Up to two modifiers go before the key, joined by plus signs, and are written back so.
        [TestMethod]
        public void ModifiersComeBeforeTheKey()
        {
            KeyCombination combination;
            Assert.IsTrue(KeyCombination.TryParse("LeftAlt+F10", out combination));
            Assert.AreEqual(KeyCode.F10, combination.Key);
            Assert.IsTrue(combination.HasModifier(KeyCode.LeftAlt));
            Assert.AreEqual("LeftAlt+F10", combination.ToString());

            Assert.IsTrue(KeyCombination.TryParse("RightControl+RightShift+U", out combination));
            Assert.AreEqual(2, combination.ModifierCount);
            Assert.AreEqual("RightControl+RightShift+U", combination.ToString());
        }

        // Modifiers written in another order give the same combination, written in its one order.
        [TestMethod]
        public void TheOrderOfTheModifiersDoesNotMakeAnotherCombination()
        {
            KeyCombination written = KeyCombination.Parse("RightShift+RightControl+U");
            Assert.AreEqual("RightControl+RightShift+U", written.ToString());
            Assert.AreEqual(KeyCombination.Parse("RightControl+RightShift+U"), written);
        }

        // Left and right modifiers are different combinations.
        [TestMethod]
        public void LeftAndRightAreToldApart()
        {
            Assert.AreNotEqual(KeyCombination.Parse("LeftAlt+F10"), KeyCombination.Parse("RightAlt+F10"));
        }

        // "None" and blank text are an unbound key, written back as None; no text at all is refused.
        [TestMethod]
        public void NothingAndNoneAreUnbound()
        {
            KeyCombination combination;
            Assert.IsTrue(KeyCombination.TryParse("None", out combination));
            Assert.IsFalse(combination.IsBound);
            Assert.AreEqual("None", combination.ToString());

            Assert.IsTrue(KeyCombination.TryParse("   ", out combination));
            Assert.IsFalse(combination.IsBound);

            Assert.IsFalse(KeyCombination.TryParse(null, out combination));
            Assert.IsFalse(combination.IsBound);
        }

        // Text that cannot be a binding is refused: a modifier alone, the left and right mouse buttons, three
        // modifiers, an unknown key, a modifier after the key, or no key after the plus.
        [TestMethod]
        public void WhatCannotBeABindingIsRefused()
        {
            KeyCombination combination;
            // A modifier alone.
            Assert.IsFalse(KeyCombination.TryParse("LeftAlt", out combination));
            // The mouse buttons the game uses itself.
            Assert.IsFalse(KeyCombination.TryParse("Mouse0", out combination));
            Assert.IsFalse(KeyCombination.TryParse("Mouse1", out combination));
            // Three modifiers, a key that is none, and a modifier after the key.
            Assert.IsFalse(KeyCombination.TryParse("LeftAlt+LeftShift+LeftControl+F1", out combination));
            Assert.IsFalse(KeyCombination.TryParse("Banana", out combination));
            Assert.IsFalse(KeyCombination.TryParse("F1+LeftAlt", out combination));
            Assert.IsFalse(KeyCombination.TryParse("LeftAlt+", out combination));
        }

        // The mouse buttons beyond the two the game uses can be bound, with a modifier too.
        [TestMethod]
        public void TheOtherMouseButtonsCanBeBound()
        {
            KeyCombination combination;
            Assert.IsTrue(KeyCombination.TryParse("Mouse2", out combination));
            Assert.AreEqual(KeyCode.Mouse2, combination.Key);
            Assert.IsTrue(KeyCombination.TryParse("LeftControl+Mouse3", out combination));
            Assert.AreEqual(KeyCode.Mouse3, combination.Key);
        }

        // A modifier switch steps from none to the left key, to the right one and back to none.
        [TestMethod]
        public void AModifierSwitchGoesThroughLeftAndRightToNone()
        {
            KeyCombination combination = KeyCombination.Parse("F10");
            combination = combination.Cycled(KeyCode.LeftAlt, KeyCode.RightAlt);
            Assert.AreEqual("LeftAlt+F10", combination.ToString());
            combination = combination.Cycled(KeyCode.LeftAlt, KeyCode.RightAlt);
            Assert.AreEqual("RightAlt+F10", combination.ToString());
            combination = combination.Cycled(KeyCode.LeftAlt, KeyCode.RightAlt);
            Assert.AreEqual("F10", combination.ToString());
        }

        // Switching one modifier keeps the other, and a third modifier, which has no room, changes nothing.
        [TestMethod]
        public void AModifierSwitchKeepsTheOtherModifier()
        {
            KeyCombination combination = KeyCombination.Parse("LeftControl+F10");
            combination = combination.Cycled(KeyCode.LeftAlt, KeyCode.RightAlt);
            Assert.AreEqual("LeftControl+LeftAlt+F10", combination.ToString());
            combination = combination.Cycled(KeyCode.LeftControl, KeyCode.RightControl);
            Assert.AreEqual("RightControl+LeftAlt+F10", combination.ToString());
            // A third one has no room and changes nothing.
            Assert.AreEqual(combination, combination.Cycled(KeyCode.LeftShift, KeyCode.RightShift));
        }

        // Taking a new key keeps the modifiers; a modifier or AltGr is not taken as the key.
        [TestMethod]
        public void ANewKeyKeepsTheModifiers()
        {
            KeyCombination combination = KeyCombination.Parse("RightControl+RightShift+U");
            Assert.AreEqual("RightControl+RightShift+P", combination.WithKey(KeyCode.P).ToString());
            // A modifier or AltGr is no key to take.
            Assert.AreEqual(combination, combination.WithKey(KeyCode.LeftAlt));
            Assert.AreEqual(combination, combination.WithKey(KeyCode.AltGr));
        }

        // AltGr cannot be bound and is no key in text: it is a modifier on the keyboards that have it.
        [TestMethod]
        public void AltGrIsNoKey()
        {
            Assert.IsFalse(KeyCombination.CanBind(KeyCode.AltGr));
            Assert.IsFalse(KeyCombination.IsText("AltGr"));
        }

        // The texts KSP's settings.cfg and other mods keep keys in, KeyCode names and None, are read as such.
        [TestMethod]
        public void KspsOwnSpellingIsTaken()
        {
            // settings.cfg: "primary = W", "secondary = None".
            Assert.AreEqual(KeyCode.W, KeyCombination.Parse("W").Key);
            Assert.IsFalse(KeyCombination.Parse("None").IsBound);
            // Scatterer keeps its keys as KeyCode names.
            Assert.AreEqual(KeyCode.F10, KeyCombination.Parse("F10").Key);
        }
    }
}
