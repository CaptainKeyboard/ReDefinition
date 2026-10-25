using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Api;
using ReDefinition.Window;
using UnityEngine;

namespace ReDefinition.Tests
{
    // The diagnostics window's decisions that need no game: what the settings file may
    // hold for its position and page, the keys that end the typing in its search field,
    // the texts it makes once, and the mods' keys while the field has the keyboard.
    [TestClass]
    public class DiagnosticsWindowTests
    {
        // Parsing a float rejects NaN and the infinities as well as text that is no number.
        [TestMethod]
        public void AFloatThatIsNotANumberIsNotRead()
        {
            float value;
            Assert.IsTrue(OwnSettings.ParseFloat("12.5", out value));
            Assert.AreEqual(12.5f, value);
            Assert.IsFalse(OwnSettings.ParseFloat("NaN", out value));
            Assert.IsFalse(OwnSettings.ParseFloat("Infinity", out value));
            Assert.IsFalse(OwnSettings.ParseFloat("-Infinity", out value));
            Assert.IsFalse(OwnSettings.ParseFloat("far", out value));
        }

        // A window position of NaN or infinity in the settings file leaves the default, which would otherwise
        // put the window where it cannot be seen.
        [TestMethod]
        public void AWindowPositionOfNaNLeavesTheDefault()
        {
            OwnSettings settings = new OwnSettings();
            float x = settings.DiagnosticsX;
            float y = settings.DiagnosticsY;
            settings.SetValue("diagnosticsX", "NaN");
            settings.SetValue("diagnosticsY", "Infinity");
            Assert.AreEqual(x, settings.DiagnosticsX);
            Assert.AreEqual(y, settings.DiagnosticsY);
            settings.SetValue("diagnosticsX", "120");
            Assert.AreEqual(120f, settings.DiagnosticsX);
        }

        // A page is read by its name or its number only where the page exists: no undefined numbers, flag
        // combinations, unknown names or empty text.
        [TestMethod]
        public void AnEnumValueThePageDoesNotHaveIsNotRead()
        {
            DebugCategory page;
            Assert.IsTrue(OwnSettings.ParseEnum("Capture", out page));
            Assert.AreEqual(DebugCategory.Capture, page);
            Assert.IsTrue(OwnSettings.ParseEnum("2", out page));
            Assert.AreEqual(DebugCategory.FrameGeneration, page);
            Assert.IsFalse(OwnSettings.ParseEnum("99", out page));
            Assert.IsFalse(OwnSettings.ParseEnum("-1", out page));
            Assert.IsFalse(OwnSettings.ParseEnum("Overview, Capture", out page));
            Assert.IsFalse(OwnSettings.ParseEnum("Nowhere", out page));
            Assert.IsFalse(OwnSettings.ParseEnum("", out page));
            Assert.IsFalse(OwnSettings.ParseEnum(null, out page));
        }

        // An undefined page in the settings file leaves the page as it was; the default is the Overview.
        [TestMethod]
        public void AnUndefinedPageLeavesTheDefaultPage()
        {
            OwnSettings settings = new OwnSettings { DiagnosticsPage = DebugCategory.Capture };
            settings.SetValue("diagnosticsPage", "99");
            Assert.AreEqual(DebugCategory.Capture, settings.DiagnosticsPage);
            Assert.AreEqual(DebugCategory.Overview, new OwnSettings().DiagnosticsPage);
        }

        // Escape, Return and the keypad's Enter end the typing in the search field; other keys do not.
        [TestMethod]
        public void EscapeAndReturnEndTheTyping()
        {
            Assert.IsTrue(DiagnosticsContent.EndsTyping(KeyCode.Escape));
            Assert.IsTrue(DiagnosticsContent.EndsTyping(KeyCode.Return));
            Assert.IsTrue(DiagnosticsContent.EndsTyping(KeyCode.KeypadEnter));
            Assert.IsFalse(DiagnosticsContent.EndsTyping(KeyCode.A));
            Assert.IsFalse(DiagnosticsContent.EndsTyping(KeyCode.Backspace));
        }

        // A page's entry adds the number of its options that pass the filter when there are any, and stays one
        // object while that number changes; without a filter or matches it is the title alone.
        [TestMethod]
        public void ThePageEntryNamesTheCountOnlyWhileItChanges()
        {
            DiagnosticsContent content = new DiagnosticsContent();
            GUIContent entry = content.PageEntry(DebugCategory.Capture, -1);
            Assert.AreEqual("Capture & log", entry.text);
            Assert.AreEqual("Capture & log  (3)", content.PageEntry(DebugCategory.Capture, 3).text);
            Assert.AreSame(entry, content.PageEntry(DebugCategory.Capture, 3));
            Assert.AreEqual("Capture & log", content.PageEntry(DebugCategory.Capture, 0).text);
            Assert.AreEqual("Overview", content.PageEntry(DebugCategory.Overview, -1).text);
        }

        // The changed chip shows its count and stays one object, its text rewritten when the count changes.
        [TestMethod]
        public void TheChangedChipCountsAndStaysTheSameObject()
        {
            DiagnosticsContent content = new DiagnosticsContent();
            GUIContent chip = content.ChangedChip(2);
            Assert.AreEqual("Changed (2)", chip.text);
            Assert.AreSame(chip, content.ChangedChip(5));
            Assert.AreEqual("Changed (5)", chip.text);
        }

        // A row's label is one object kept from frame to frame whose text follows the option's dynamic label;
        // the chip's tooltip is the reason it is greyed out where there is one, else the option's tooltip.
        [TestMethod]
        public void ARowKeepsItsContentAndFollowsItsLabel()
        {
            DiagnosticsContent content = new DiagnosticsContent();
            string label = "first";
            DebugOption option = new DebugOption("x", DebugCategory.Capture, "x", DebugKind.Switch, DebugPersistence.Saved)
            {
                Tooltip = "Tip",
                DynamicLabel = () => label,
            };
            GUIContent first = content.Label(option);
            Assert.AreEqual("first", first.text);
            Assert.AreEqual("Tip", first.tooltip);
            label = "second";
            Assert.AreSame(first, content.Label(option));
            Assert.AreEqual("second", first.text);
            Assert.AreEqual("why not", content.Chip(option, "why not").tooltip);
            Assert.AreEqual("Tip", content.Chip(option, null).tooltip);
        }

        // While the search field has the keyboard the mods' keys read as neither pressed, held nor released,
        // so typing a letter does not also work a mod's key.
        [TestMethod]
        public void ModsKeysAreSilentWhileTheSearchFieldHasTheKeyboard()
        {
            Func<bool> before = Keys.DiagnosticsTyping;
            try
            {
                Keys.DiagnosticsTyping = () => true;
                Assert.IsTrue(Keys.Blocked);
                Assert.IsFalse(Keys.Pressed("mymod.key"));
                Assert.IsFalse(Keys.Held("mymod.key"));
                Assert.IsFalse(Keys.Released("mymod.key"));
            }
            finally
            {
                Keys.DiagnosticsTyping = before;
            }
        }
    }
}
