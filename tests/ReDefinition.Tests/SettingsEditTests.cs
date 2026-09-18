using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Settings;

namespace ReDefinition.Tests
{
    // What the settings window edits (SettingsEdit): rows, where their values came
    // from, the status line, and Apply's steps.
    [TestClass]
    public class SettingsEditTests
    {
        private static BundledSetting Setting(string key)
        {
            return new BundledSetting { Key = key, Title = key };
        }

        private static KeyValuePair<string, string> Row(string key, string value)
        {
            return new KeyValuePair<string, string>(key, value);
        }

        private static SettingsEdit Opened(params KeyValuePair<string, string>[] rows)
        {
            SettingsEdit edit = new SettingsEdit();
            edit.Open(true, "", rows);
            return edit;
        }

        private static string Pending(SettingsEdit edit, string key)
        {
            string value;
            return edit.TryGetPending(key, out value) ? value : null;
        }

        // One profile's values, as ProfileApplier works them out.
        private static void Profile(SettingsEdit edit, string name, Dictionary<BundledSetting, string> values,
                                    params BundledSetting[] released)
        {
            edit.SetProfiles(new Dictionary<string, Dictionary<BundledSetting, string>> { { name, values } },
                new Dictionary<string, HashSet<BundledSetting>> { { name, new HashSet<BundledSetting>(released) } });
        }

        [TestMethod]
        public void OpeningTakesTheValuesThatCanBeRead()
        {
            SettingsEdit edit = Opened(Row("ksp.SYNC_VBL", "1"), Row("tufx.profileFlight", null));

            Assert.AreEqual("1", Pending(edit, "ksp.SYNC_VBL"));
            Assert.IsFalse(edit.HasPending("tufx.profileFlight"), "a value nobody could read cannot be edited");
            Assert.IsFalse(edit.RowsPending());
        }

        [TestMethod]
        public void AChangeWaitsForApplyUntilTheRowHoldsItsValueAgain()
        {
            SettingsEdit edit = Opened(Row("ksp.SYNC_VBL", "1"));

            edit.Change("ksp.SYNC_VBL", "0");
            Assert.IsTrue(edit.Unapplied(false, true, ""));
            edit.Change("ksp.SYNC_VBL", "1");
            Assert.IsFalse(edit.Unapplied(false, true, ""));
        }

        [TestMethod]
        public void ARowIsTheProfilesWhileItHoldsTheProfilesValue()
        {
            BundledSetting ocean = Setting("scatterer.useOceanShaders");
            SettingsEdit edit = Opened(Row(ocean.Key, "False"));
            Profile(edit, "high", new Dictionary<BundledSetting, string> { { ocean, "True" } });

            Assert.IsTrue(edit.ChooseProfile("high"));
            SettingsEdit.Origin from;
            Assert.IsTrue(edit.FilledBy(ocean.Key, "True", out from));
            Assert.AreEqual(SettingsEdit.Origin.Profile, from);
            Assert.IsTrue(edit.Bundled, "a profile switches the bundling on");
            Assert.AreEqual("high", edit.Profile);

            edit.Change(ocean.Key, "False");
            Assert.IsFalse(edit.FilledBy(ocean.Key, "False", out from));
            edit.Change(ocean.Key, "True");
            Assert.IsTrue(edit.FilledBy(ocean.Key, "True", out from), "back at the profile's value it is the profile's again");
        }

        [TestMethod]
        public void AHandEditEndsAHandBackForGood()
        {
            BundledSetting flares = Setting("distantobject.flaresEnabled");
            SettingsEdit edit = Opened(Row(flares.Key, "False"));
            Profile(edit, "medium", new Dictionary<BundledSetting, string> { { flares, "True" } }, flares);
            edit.ChooseProfile("medium");
            SettingsEdit.Origin from;
            Assert.IsTrue(edit.FilledBy(flares.Key, "True", out from));
            Assert.AreEqual(SettingsEdit.Origin.Released, from);

            edit.Change(flares.Key, "False");
            edit.Change(flares.Key, "True");

            Assert.IsFalse(edit.FilledBy(flares.Key, "True", out from));
        }

        [TestMethod]
        public void AProfileChosenAfterTheResetLeavesItsOtherRowsToTheReset()
        {
            BundledSetting ocean = Setting("scatterer.useOceanShaders");
            BundledSetting lights = Setting("waterfall.EnableLights");
            SettingsEdit edit = Opened(Row(ocean.Key, "False"), Row(lights.Key, "False"));

            edit.ChooseDefaults(new Dictionary<BundledSetting, string> { { ocean, "True" }, { lights, "True" } });
            Profile(edit, "high", new Dictionary<BundledSetting, string> { { ocean, "False" } });
            edit.ChooseProfile("high");

            SettingsEdit.Origin from;
            Assert.IsTrue(edit.FilledBy(lights.Key, "True", out from));
            Assert.AreEqual(SettingsEdit.Origin.Reset, from);
            Assert.AreEqual("False", Pending(edit, ocean.Key));
            Assert.AreEqual("Profile: High, every other setting back to its default -- Apply or Accept sets it",
                edit.Status("", true, false, "High", values => 0));
        }

        [TestMethod]
        public void ARowAnEarlierProfileFilledGoesBackToTheResetsDefault()
        {
            BundledSetting ocean = Setting("scatterer.useOceanShaders");
            SettingsEdit edit = Opened(Row(ocean.Key, "False"));
            edit.ChooseDefaults(new Dictionary<BundledSetting, string> { { ocean, "True" } });
            edit.SetProfiles(
                new Dictionary<string, Dictionary<BundledSetting, string>>
                {
                    { "low", new Dictionary<BundledSetting, string> { { ocean, "False" } } },
                    { "max", new Dictionary<BundledSetting, string>() },
                },
                new Dictionary<string, HashSet<BundledSetting>>());

            edit.ChooseProfile("low");
            Assert.AreEqual("False", Pending(edit, ocean.Key));
            edit.ChooseProfile("max");

            Assert.AreEqual("True", Pending(edit, ocean.Key));
        }

        [TestMethod]
        public void ASyncTakesTheModsValueOnlyForARowNotChangedHere()
        {
            BundledSetting untouched = Setting("a");
            BundledSetting changed = Setting("b");
            BundledSetting profiles = Setting("c");
            SettingsEdit edit = Opened(Row(untouched.Key, "1"), Row(changed.Key, "1"), Row(profiles.Key, "1"));
            edit.Change(changed.Key, "5");
            Profile(edit, "high", new Dictionary<BundledSetting, string> { { profiles, "1" } });
            edit.ChooseProfile("high");

            Assert.IsTrue(edit.Sync(untouched.Key, "2"));
            Assert.IsTrue(edit.Sync(changed.Key, "2"));
            Assert.IsTrue(edit.Sync(profiles.Key, "2"));

            Assert.AreEqual("2", Pending(edit, untouched.Key));
            Assert.AreEqual("5", Pending(edit, changed.Key), "the player's change stays for Apply");
            Assert.AreEqual("1", Pending(edit, profiles.Key), "the profile's row is still the profile's");
            Assert.IsFalse(edit.Sync(untouched.Key, "2"), "nothing new");
        }

        [TestMethod]
        public void ApplyTakesEachRowAsItsValueCameIntoIt()
        {
            BundledSetting byHand = Setting("byHand");
            BundledSetting unchanged = Setting("unchanged");
            BundledSetting handedBack = Setting("handedBack");
            BundledSetting reset = Setting("reset");
            BundledSetting profileHeld = Setting("profileHeld");
            BundledSetting profileNotHeld = Setting("profileNotHeld");
            SettingsEdit edit = Opened(Row(byHand.Key, "1"), Row(unchanged.Key, "1"), Row(handedBack.Key, "1"),
                Row(reset.Key, "1"), Row(profileHeld.Key, "1"), Row(profileNotHeld.Key, "1"));
            edit.ChooseDefaults(new Dictionary<BundledSetting, string> { { reset, "1" } });
            Profile(edit, "high",
                new Dictionary<BundledSetting, string> { { handedBack, "1" }, { profileHeld, "1" }, { profileNotHeld, "1" } },
                handedBack);
            edit.ChooseProfile("high");
            edit.Change(byHand.Key, "2");

            List<SettingsEdit.Step> steps = edit.Steps(
                new[] { byHand, unchanged, handedBack, reset, profileHeld, profileNotHeld },
                (setting, value) => setting == profileHeld).ToList();

            Assert.AreEqual(4, steps.Count);
            Assert.AreEqual(byHand, steps[0].Setting);
            Assert.AreEqual(SettingsEdit.StepKind.Set, steps[0].Kind);
            Assert.AreEqual("2", steps[0].Value);
            Assert.AreEqual(handedBack, steps[1].Setting);
            Assert.AreEqual(SettingsEdit.StepKind.Release, steps[1].Kind);
            Assert.AreEqual(reset, steps[2].Setting);
            Assert.AreEqual(SettingsEdit.StepKind.Reset, steps[2].Kind);
            Assert.AreEqual(profileNotHeld, steps[3].Setting, "a per-save value the save holds is set as the choice");
            Assert.AreEqual(SettingsEdit.StepKind.Set, steps[3].Kind);
        }

        [TestMethod]
        public void TheStatusLineSaysWhatTheRowsHold()
        {
            BundledSetting ocean = Setting("scatterer.useOceanShaders");
            SettingsEdit edit = Opened(Row(ocean.Key, "True"));

            Assert.AreEqual("No profile chosen. High is the one to start with.", edit.Status("", true, false, null, v => 0));
            edit.Change(ocean.Key, "False");
            Assert.AreEqual("No profile chosen -- Apply or Accept sets it", edit.Status("", true, false, null, v => 0));

            edit.Change(ocean.Key, "True");
            Profile(edit, "high", new Dictionary<BundledSetting, string> { { ocean, "True" } });
            edit.ChooseProfile("high");
            Assert.AreEqual("Profile: High", edit.Status("high", true, false, "High", v => 0));
            Assert.AreEqual("Custom, changed from High in 2 settings -- Apply or Accept sets it",
                edit.Status("", true, false, "High", v => 2));

            edit.Profile = "gone";
            Assert.AreEqual("The profile 'gone' is not installed any more -- Apply or Accept sets it",
                edit.Status("high", true, false, null, v => 0));
        }
    }
}
