using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Framework;
using UnityEngine;

namespace ReDefinition.Tests
{
    // The key binding as text: what the registrations, the store and KSP's
    // settings.cfg hold, and what the Keys tab shows.
    [TestClass]
    public class KeyCombinationTests
    {
        [TestMethod]
        public void AKeyOnItsOwnReadsAndWritesBack()
        {
            KeyCombination combination;
            Assert.IsTrue(KeyCombination.TryParse("F11", out combination));
            Assert.AreEqual(KeyCode.F11, combination.Key);
            Assert.AreEqual(0, combination.ModifierCount);
            Assert.AreEqual("F11", combination.ToString());
        }

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

        [TestMethod]
        public void TheOrderOfTheModifiersDoesNotMakeAnotherCombination()
        {
            KeyCombination written = KeyCombination.Parse("RightShift+RightControl+U");
            Assert.AreEqual("RightControl+RightShift+U", written.ToString());
            Assert.AreEqual(KeyCombination.Parse("RightControl+RightShift+U"), written);
        }

        [TestMethod]
        public void LeftAndRightAreToldApart()
        {
            Assert.AreNotEqual(KeyCombination.Parse("LeftAlt+F10"), KeyCombination.Parse("RightAlt+F10"));
        }

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

        [TestMethod]
        public void TheOtherMouseButtonsCanBeBound()
        {
            KeyCombination combination;
            Assert.IsTrue(KeyCombination.TryParse("Mouse2", out combination));
            Assert.AreEqual(KeyCode.Mouse2, combination.Key);
            Assert.IsTrue(KeyCombination.TryParse("LeftControl+Mouse3", out combination));
            Assert.AreEqual(KeyCode.Mouse3, combination.Key);
        }

        [TestMethod]
        public void AModifierIsAddedAndTakenAwayAgain()
        {
            KeyCombination combination = KeyCombination.Parse("F10");
            combination = combination.Toggled(KeyCode.LeftAlt);
            Assert.AreEqual("LeftAlt+F10", combination.ToString());

            combination = combination.Toggled(KeyCode.LeftShift);
            Assert.AreEqual("LeftAlt+LeftShift+F10", combination.ToString());

            // A third takes the second one's place.
            combination = combination.Toggled(KeyCode.LeftControl);
            Assert.AreEqual("LeftControl+LeftAlt+F10", combination.ToString());

            combination = combination.Toggled(KeyCode.LeftAlt);
            Assert.AreEqual("LeftControl+F10", combination.ToString());

            // A key that is no modifier changes nothing.
            Assert.AreEqual(combination, combination.Toggled(KeyCode.F1));
        }

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
