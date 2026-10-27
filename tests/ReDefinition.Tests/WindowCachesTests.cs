using System.Collections.Generic;
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Settings;
using ReDefinition.Ui;
using ReDefinition.Window;

namespace ReDefinition.Tests
{
    // The helpers the settings window keeps its per-frame texts and comparisons with: each answers what the plain
    // computation answers, and does the work again only when its inputs change.
    [TestClass]
    public class WindowCachesTests
    {
        [TestMethod]
        public void GreyTextIsTheGreyOfItsSourceAndEmptyForNone()
        {
            GreyText grey = new GreyText();
            Assert.AreEqual("", grey.Of(null));
            string first = grey.Of("Downloading 1.0 MB");
            Assert.AreEqual("<color=#9a9a9a>Downloading 1.0 MB</color>", first);
            Assert.AreSame(first, grey.Of("Downloading 1.0 MB"));
            Assert.AreEqual("<color=#9a9a9a>Done</color>", grey.Of("Done"));
            Assert.AreEqual("", grey.Of(null));
        }

        [TestMethod]
        public void JoinedLinesLeavesOutWhatSaysNothing()
        {
            JoinedLines lines = new JoinedLines();
            Assert.AreEqual("", lines.Of(null, null));
            Assert.AreEqual("", lines.Of("", ""));
            Assert.AreEqual("a", lines.Of("a", ""));
            Assert.AreEqual("b", lines.Of("", "b"));
            Assert.AreEqual("a\nb", lines.Of("a", "b"));
            Assert.AreEqual("a\nc", lines.Of("a", "", "c"));
            Assert.AreEqual("a\nb\nc", lines.Of("a", "b", "c"));
            string joined = lines.Of("a", "b", "c");
            Assert.AreSame(joined, lines.Of("a", "b", "c"));
            Assert.AreEqual("a\nb", lines.Of("a", "b"));
        }

        [TestMethod]
        public void TwoDecimalsFollowsTheNumber()
        {
            TwoDecimals text = new TwoDecimals();
            Assert.AreEqual("0.05", text.Of(0.05f));
            string same = text.Of(0.05f);
            Assert.AreSame(same, text.Of(0.05f));
            Assert.AreEqual("1.00", text.Of(1f));
            Assert.AreEqual("0.00", text.Of(0f));
            Assert.AreEqual("NaN", text.Of(float.NaN));
        }

        // The comparison answers what the nodes' comparison answers, for a change to each field Save writes and back.
        [TestMethod]
        public void AnAxisComparisonAnswersAsTheNodesDo()
        {
            Action<AxisBinding_Single>[] changes =
            {
                side => side.name = "Pad",
                side => side.axisIdx = 3,
                side => side.inverted = !side.inverted,
                side => side.sensitivity = 0.5f,
                side => side.deadzone = 0.2f,
                side => side.scale = 2f,
                side => side.inputLockMask = 4,
                side => side.switchState = InputBindingModes.None,
            };
            foreach (bool primary in new[] { true, false })
            foreach (bool onLive in new[] { true, false })
            {
                foreach (Action<AxisBinding_Single> change in changes)
                {
                    AxisBinding live = new AxisBinding();
                    AxisBinding copy = new AxisBinding();
                    KspAxes.Comparison comparison = new KspAxes.Comparison();
                    Assert.IsTrue(comparison.Of(live, copy));
                    Assert.IsTrue(comparison.Of(live, copy));

                    AxisBinding changed = onLive ? live : copy;
                    change(primary ? changed.primary : changed.secondary);
                    Assert.AreEqual(KspAxes.Same(live, copy), comparison.Of(live, copy));
                    Assert.IsFalse(comparison.Of(live, copy));
                    Assert.IsFalse(comparison.Of(live, copy));

                    AxisBinding other = onLive ? copy : live;
                    change(primary ? other.primary : other.secondary);
                    Assert.AreEqual(KspAxes.Same(live, copy), comparison.Of(live, copy));
                    Assert.IsTrue(comparison.Of(live, copy));
                }
            }
        }

        // The switch for all is on while every button is hidden, sets all of them from a click, and a click that
        // only repeats what the switches say sets nothing.
        [TestMethod]
        public void AToolbarChoiceHidesAllOrNone()
        {
            IBundledMod one = new FakeMod("one", SettingsSaving.InModFiles);
            IBundledMod two = new FakeMod("two", SettingsSaving.InModFiles);
            Dictionary<string, bool> hidden = new Dictionary<string, bool> { { "one", false }, { "two", false } };
            int sets = 0;
            ToolbarChoice choice = new ToolbarChoice(new[] { one, two }, mod => hidden[mod.Id],
                (mod, hide) => { hidden[mod.Id] = hide; sets++; });
            Assert.AreEqual(2, choice.Count);
            Assert.IsFalse(choice.AllHidden());

            Assert.IsTrue(choice.SetAll(true));
            Assert.IsTrue(choice.AllHidden());
            Assert.AreEqual(2, sets);
            Assert.IsFalse(choice.SetAll(true));
            Assert.AreEqual(2, sets);

            choice.Set(two, false);
            Assert.IsFalse(choice.AllHidden());
            // The switch for all follows the others in every frame, and that must not switch the first one off.
            Assert.IsFalse(choice.SetAll(false));
            Assert.IsTrue(hidden["one"]);
        }

        [TestMethod]
        public void AToolbarChoiceCountsItsExtraEntry()
        {
            bool extra = false;
            ToolbarChoice choice = new ToolbarChoice(new IBundledMod[0], mod => false, (mod, hide) => { },
                () => extra, hide => extra = hide);
            Assert.AreEqual(1, choice.Count);
            Assert.IsFalse(choice.Empty);
            Assert.IsFalse(choice.AllHidden());
            Assert.IsTrue(choice.SetAll(true));
            Assert.IsTrue(extra);
            Assert.IsTrue(choice.AllHidden());

            Assert.IsTrue(new ToolbarChoice(new IBundledMod[0], mod => false, (mod, hide) => { }).Empty);
        }

        [TestMethod]
        public void AnAxisComparisonOfAMissingBindingIsTheNodesAnswer()
        {
            KspAxes.Comparison comparison = new KspAxes.Comparison();
            Assert.IsTrue(comparison.Of(null, null));
            Assert.IsFalse(comparison.Of(null, new AxisBinding()));
            Assert.IsFalse(comparison.Of(new AxisBinding(), null));
        }
    }
}
