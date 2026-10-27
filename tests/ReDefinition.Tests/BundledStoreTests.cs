using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Settings;

namespace ReDefinition.Tests
{
    // What reaches the mods, and when (BundledStore): each test one decision the
    // store makes, with mods and a game that are stand-ins.
    [TestClass]
    public class BundledStoreTests
    {
        // The stand-in game and the store under test.
        private FakeHost host;
        private BundledStore store;

        // A fresh host and store for each test.
        [TestInitialize]
        public void NewStore()
        {
            host = new FakeHost();
            store = new BundledStore(host);
        }

        // A fake mod of that id, saving as given, installed in the host.
        private FakeMod Mod(string id, SettingsSaving saving)
        {
            FakeMod mod = new FakeMod(id, saving);
            host.Mods.Add(mod);
            return mod;
        }

        // A setting of the mod whose value is kept per save, the host's save telling which.
        private BundledSetting PerSave(FakeMod mod, string name, string value)
        {
            BundledSetting setting = mod.Add(name, value);
            setting.Context = () => host.Save;
            return setting;
        }

        // A value for a mod that saves it in its own files is kept until the mod has saved it, the value from
        // before staying for Restore.
        [TestMethod]
        public void AValueForAModThatSavesIsKeptUntilTheModHasSavedIt()
        {
            FakeMod scatterer = Mod("scatterer", SettingsSaving.InModFiles);
            BundledSetting ocean = scatterer.Add("useOceanShaders", "False");

            store.Set(ocean, "True", false);
            Assert.AreEqual("True", scatterer.Values["useOceanShaders"]);
            Assert.AreEqual(0, scatterer.Saves);
            Assert.IsTrue(store.Ledger.Contains(ocean.Key), "kept while the mod has not saved it");

            store.SaveNow();
            Assert.AreEqual(1, scatterer.Saves);
            Assert.IsFalse(store.Ledger.Contains(ocean.Key), "saved in the mod, it is no longer kept here");
            Assert.AreEqual("False", store.BeforeReDefinition(ocean));
            Assert.IsTrue(store.CanRestore);
        }

        // bundled.cfg is written with the value from before ahead of the mod getting the new one, otherwise only by a save
        // while something kept changed.
        [TestMethod]
        public void OurFileIsWrittenOnlyWhenWhatIsKeptChanged()
        {
            FakeMod scatterer = Mod("scatterer", SettingsSaving.InModFiles);
            BundledSetting ocean = scatterer.Add("useOceanShaders", "True");

            store.SaveIfDirty();
            Assert.AreEqual(0, host.FileWrites, "nothing changed");

            store.Set(ocean, "False", false);
            Assert.AreEqual(1, host.FileWrites, "the value from before, written before the mod got the new one");
            store.Set(ocean, "True", false);
            Assert.AreEqual(1, host.FileWrites, "a later change is written by the caller's save");
            store.SaveIfDirty();
            Assert.AreEqual(2, host.FileWrites);
            store.SaveIfDirty();
            Assert.AreEqual(2, host.FileWrites, "clean again once written");
        }

        // Its own files never hold the value set here, so there is nothing a backup on
        // disk would have to put back: set and reset reach it while bundled.cfg cannot
        // be written.
        [TestMethod]
        public void AModThatReadsItsValueAtEveryStartGetsItWithoutOurFileWritten()
        {
            FakeMod waterfall = Mod("waterfall", SettingsSaving.AtEveryStart);
            BundledSetting lights = waterfall.Add("EnableLights", "True");

            host.FileWriteFails = true;
            store.Set(lights, "False", false);
            Assert.AreEqual("False", waterfall.Values["EnableLights"]);
            store.ResetTo(lights, "True");
            Assert.AreEqual("True", waterfall.Values["EnableLights"], "the reset reaches it too");

            Assert.AreEqual(0, host.FileWrites);
            host.FileWriteFails = false;
            store.SaveIfDirty();
            Assert.AreEqual(1, host.FileWrites, "what is kept, saved with the next write");
        }

        // No mod gets a value until its value from before is on disk; once the file can be written the kept
        // choice reaches it.
        [TestMethod]
        public void NoModGetsAValueWhoseValueFromBeforeIsNotOnDisk()
        {
            FakeMod scatterer = Mod("scatterer", SettingsSaving.InModFiles);
            BundledSetting ocean = scatterer.Add("useOceanShaders", "False");

            host.FileWriteFails = true;
            store.Set(ocean, "True", false);
            Assert.AreEqual("False", scatterer.Values["useOceanShaders"], "the mod keeps its value");
            Assert.AreEqual(0, host.FileWrites, "nothing written");

            host.FileWriteFails = false;
            store.ReapplyStored("tick", null, false);
            Assert.AreEqual("True", scatterer.Values["useOceanShaders"], "the kept choice reaches it once the file is written");
            Assert.AreEqual("False", store.BeforeReDefinition(ocean));
            store.SaveIfDirty();
            int written = host.FileWrites;
            store.SaveIfDirty();
            Assert.AreEqual(written, host.FileWrites, "clean once written");
        }

        // A choice taken from a mod's own window is written not from inside the mod's hook but at the next tick,
        // once.
        [TestMethod]
        public void AChoiceTakenFromAModsOwnWindowIsWrittenAtTheNextTick()
        {
            FakeMod tufx = Mod("tufx", SettingsSaving.PerSave);
            BundledSetting flight = PerSave(tufx, "profileFlight", "Default-Flight");
            host.Save = "career";
            store.Set(flight, "Blackrack_TUFX", true);
            int written = host.FileWrites;

            tufx.Values["profileFlight"] = "Default-Tracking";
            Assert.IsTrue(store.TakeFromMod(flight, "Default-Tracking", false));
            Assert.AreEqual(written, host.FileWrites, "not written from inside the mod's hook");
            store.ReapplyStored("tick", null, false);
            Assert.AreEqual(written + 1, host.FileWrites, "the next tick writes what changed");
            store.ReapplyStored("tick", null, false);
            Assert.AreEqual(written + 1, host.FileWrites, "and only once");
        }

        // A value for a mod that cannot take it yet stays kept and reaches it on the tick once it can, saved then.
        [TestMethod]
        public void AValueForAModThatCannotTakeItYetReachesItOnTheTickOnceItCan()
        {
            FakeMod trajectories = Mod("trajectories", SettingsSaving.InModFiles);
            BundledSetting depth = trajectories.Add("MaxPatchCount", "4");
            bool ready = false;
            depth.Applicable = () => ready;

            store.Set(depth, "6", true);
            Assert.AreEqual("4", trajectories.Values["MaxPatchCount"]);
            Assert.IsTrue(store.Ledger.Contains(depth.Key), "kept while the mod cannot take it");

            store.ReapplyWaiting("tick");
            Assert.AreEqual("4", trajectories.Values["MaxPatchCount"], "still not ready");

            ready = true;
            store.ReapplyWaiting("tick");
            Assert.AreEqual("6", trajectories.Values["MaxPatchCount"]);
            Assert.AreEqual(1, trajectories.Saves);
            Assert.IsFalse(store.Ledger.Contains(depth.Key), "saved in the mod");

            store.ReapplyWaiting("tick");
            Assert.AreEqual(1, trajectories.Saves, "nothing waits any more");
        }

        // A value for a mod that reads it at every start stays kept, without asking the mod to save.
        [TestMethod]
        public void AValueSetAtEveryStartStaysKept()
        {
            FakeMod waterfall = Mod("waterfall", SettingsSaving.AtEveryStart);
            BundledSetting lights = waterfall.Add("EnableLights", "True");

            store.Set(lights, "False", true);

            Assert.AreEqual("False", waterfall.Values["EnableLights"]);
            Assert.AreEqual(0, waterfall.Saves, "a mod without a save routine is not asked to save");
            Assert.IsTrue(store.Ledger.Contains(lights.Key));
            Assert.AreEqual("False", store.Current(lights));
        }

        // The value from before is kept per save: a choice for every save goes into each save that loads, with
        // that save's own value kept for it.
        [TestMethod]
        public void TheValueFromBeforeIsKeptPerSave()
        {
            FakeMod tufx = Mod("tufx", SettingsSaving.PerSave);
            BundledSetting flight = PerSave(tufx, "profileFlight", "Default-Flight");

            host.Save = "career";
            store.Set(flight, "Blackrack_TUFX", true);
            Assert.IsTrue(store.Ledger.Contains(flight.Key), "a choice for every save stays kept");

            host.Save = "sandbox";
            tufx.Values["profileFlight"] = "Sandbox-Own";
            store.ReapplyStored("sandbox loaded", null, true);

            Assert.AreEqual("Blackrack_TUFX", tufx.Values["profileFlight"], "the choice goes into the save that loads");
            Assert.AreEqual("Sandbox-Own", store.BeforeReDefinition(flight));
            host.Save = "career";
            Assert.AreEqual("Default-Flight", store.BeforeReDefinition(flight));
        }

        // A per-save value counts as held only where a choice for every save is kept here; a value for the game as
        // a whole is held by the mod holding it.
        [TestMethod]
        public void APerSaveValueIsAChoiceForEverySaveOnlyWhenKeptHere()
        {
            FakeMod tufx = Mod("tufx", SettingsSaving.PerSave);
            BundledSetting flight = PerSave(tufx, "profileFlight", "Default-Flight");
            FakeMod ksp = Mod("ksp", SettingsSaving.InModFiles);
            BundledSetting vsync = ksp.Add("SYNC_VBL", "1");

            Assert.IsFalse(store.Holds(flight, "Default-Flight"), "the save holds it, no choice is kept");
            store.Set(flight, "Blackrack_TUFX", false);
            Assert.IsTrue(store.Holds(flight, "Blackrack_TUFX"));
            Assert.IsFalse(store.Holds(flight, "Default-Flight"));
            Assert.IsTrue(store.Holds(vsync, "1"), "a value for the game as a whole needs nothing more");
        }

        // The reset of a mod without files of its own writes only where the mod differs from the default, and
        // keeps nothing, so its own config stands at the next start.
        [TestMethod]
        public void TheResetOfAModWithoutFilesWritesOnlyWhereTheModDiffers()
        {
            FakeMod waterfall = Mod("waterfall", SettingsSaving.AtEveryStart);
            BundledSetting lights = waterfall.Add("EnableLights", "True");

            store.ResetTo(lights, "True");
            Assert.AreEqual(0, waterfall.WritesOf("EnableLights"), "it holds its default already");

            store.Set(lights, "False", false);
            store.ResetTo(lights, "True");

            Assert.AreEqual("True", waterfall.Values["EnableLights"]);
            Assert.AreEqual(2, waterfall.WritesOf("EnableLights"));
            Assert.IsFalse(store.Ledger.Contains(lights.Key), "its own config stands at the next start");
        }

        // The reset of a mod that saves its values is written even where the mod shows the default already, and
        // kept until it is saved.
        [TestMethod]
        public void TheResetOfAModThatSavesIsWrittenEvenWhereItShowsTheDefault()
        {
            FakeMod scatterer = Mod("scatterer", SettingsSaving.InModFiles);
            BundledSetting ocean = scatterer.Add("useOceanShaders", "True");

            store.ResetTo(ocean, "True");

            Assert.AreEqual(1, scatterer.WritesOf("useOceanShaders"));
            Assert.IsTrue(store.Ledger.Contains(ocean.Key));
            store.SaveNow();
            Assert.IsFalse(store.Ledger.Contains(ocean.Key));
        }

        // Restore puts back what the mod had before ReDefinition and saves it, leaving nothing to restore and no
        // profile chosen.
        [TestMethod]
        public void RestorePutsBackWhatTheModHadBefore()
        {
            FakeMod scatterer = Mod("scatterer", SettingsSaving.InModFiles);
            BundledSetting ocean = scatterer.Add("useOceanShaders", "False");
            store.Set(ocean, "True", true);

            store.RestoreBackup();

            Assert.AreEqual("False", scatterer.Values["useOceanShaders"]);
            Assert.IsFalse(store.CanRestore, "put back and saved, nothing is left to restore");
            Assert.AreEqual("", store.ProfileName);
        }

        // A per-save value is put back in the save it came from only, once that save loads again.
        [TestMethod]
        public void APerSaveValueIsPutBackInItsOwnSaveOnly()
        {
            FakeMod distantObject = Mod("distantobject", SettingsSaving.PerSave);
            BundledSetting flares = PerSave(distantObject, "flaresEnabled", "True");
            host.Save = "career";
            store.Set(flares, "False", true);

            host.Save = "sandbox";
            distantObject.Values["flaresEnabled"] = "False";
            store.RestoreBackup();
            Assert.AreEqual("False", distantObject.Values["flaresEnabled"], "the sandbox had no value from before");

            host.Save = "career";
            store.NoteSceneLoaded();
            store.ReapplyStored("career loaded", null, true);
            Assert.AreEqual("True", distantObject.Values["flaresEnabled"]);
            Assert.IsFalse(store.CanRestore);
        }

        // A row a profile releases goes back to what the mod had before and is no longer kept.
        [TestMethod]
        public void ARowAProfileReleasesGoesBackToWhatItHadBefore()
        {
            FakeMod scatterer = Mod("scatterer", SettingsSaving.InModFiles);
            BundledSetting ocean = scatterer.Add("useOceanShaders", "False");
            store.Set(ocean, "True", true);

            store.Release(ocean);

            Assert.AreEqual("False", scatterer.Values["useOceanShaders"]);
            Assert.IsFalse(store.Ledger.Contains(ocean.Key));
        }

        // A correction of a per-save value stays in the loaded save unless a choice for every save is kept, which
        // then becomes the corrected value.
        [TestMethod]
        public void ACorrectionOfAPerSaveValueStaysInTheLoadedSave()
        {
            FakeMod tufx = Mod("tufx", SettingsSaving.PerSave);
            BundledSetting flight = PerSave(tufx, "profileFlight", "Default-Flight");
            host.Save = "career";

            Assert.IsTrue(store.Correct(flight, "Blackrack_TUFX"));
            Assert.AreEqual("Blackrack_TUFX", tufx.Values["profileFlight"]);
            Assert.IsFalse(store.Ledger.Contains(flight.Key), "no choice for every save is made of it");

            store.Set(flight, "Default-Flight", false);
            Assert.IsTrue(store.Correct(flight, "Blackrack_TUFX"));
            string kept;
            Assert.IsTrue(store.Ledger.TryStored(flight.Key, out kept));
            Assert.AreEqual("Blackrack_TUFX", kept, "a choice kept for every save becomes the corrected one");
        }

        // A value from a mod's own window that the other mods forbid is not taken: the kept choice goes back into
        // the save and the reasons are told; an allowed one becomes the choice.
        [TestMethod]
        public void AValueTheModsForbidInTheirOwnWindowDoesNotBecomeTheChoice()
        {
            FakeMod tufx = Mod("tufx", SettingsSaving.PerSave);
            BundledSetting flight = PerSave(tufx, "profileFlight", "Default-Flight");
            host.Save = "career";
            store.Set(flight, "Good", false);
            host.AllowsWith = (setting, value) => value != "Bad";

            tufx.Values["profileFlight"] = "Bad";
            Assert.IsFalse(store.TakeFromMod(flight, "Bad", false));
            Assert.AreEqual("Good", tufx.Values["profileFlight"], "the choice kept goes straight back into the save");
            CollectionAssert.Contains(host.Told, flight.Key);

            Assert.IsTrue(store.TakeFromMod(flight, "Fine", false));
            string kept;
            store.Ledger.TryStored(flight.Key, out kept);
            Assert.AreEqual("Fine", kept);
        }

        // What the installed mods require of a setting counts over the value set: with a requirement that adjusts every
        // value to 1, setting 3 leaves the mod at 1.
        [TestMethod]
        public void WhatTheModsRequireCountsOverEveryValueSet()
        {
            FakeMod ksp = Mod("ksp", SettingsSaving.InModFiles);
            BundledSetting resolution = ksp.Add("REFLECTION_PROBE_TEXTURE_RESOLUTION", "0");
            host.AdjustWith = (setting, value) => "1";

            store.Set(resolution, "3", false);

            Assert.AreEqual("1", ksp.Values["REFLECTION_PROBE_TEXTURE_RESOLUTION"]);
        }

        // A choice the mod does not offer now stays kept and reaches it once the mod offers it again.
        [TestMethod]
        public void AChoiceTheModDoesNotOfferWaitsUntilItDoes()
        {
            FakeMod tufx = Mod("tufx", SettingsSaving.AtEveryStart);
            BundledSetting flight = tufx.Add("profileFlight", "Default-Flight");
            flight.Control = SettingControl.Choice;
            flight.Choices = new[] { "Default-Flight", "Blackrack_TUFX" };

            store.Set(flight, "From-A-Removed-Pack", false);
            Assert.AreEqual("Default-Flight", tufx.Values["profileFlight"]);
            Assert.IsTrue(store.Ledger.Contains(flight.Key));

            flight.Choices = new[] { "Default-Flight", "Blackrack_TUFX", "From-A-Removed-Pack" };
            store.ReapplyStored("pack back", null, true);
            Assert.AreEqual("From-A-Removed-Pack", tufx.Values["profileFlight"]);
        }

        // A value whose mod failed to save it stays kept, with a warning, and is saved again at the next scene
        // change.
        [TestMethod]
        public void AValueWhoseSaveFailedIsSavedAgainAtTheNextSceneChange()
        {
            FakeMod scatterer = Mod("scatterer", SettingsSaving.InModFiles);
            BundledSetting ocean = scatterer.Add("useOceanShaders", "False");
            scatterer.SaveFails = true;

            store.Set(ocean, "True", true);
            Assert.IsTrue(store.Ledger.Contains(ocean.Key), "kept while its mod has not saved it");
            Assert.AreEqual(1, host.Warnings.Count);

            scatterer.SaveFails = false;
            store.NoteSceneLoaded();
            store.ReapplyStored("next scene", null, true);
            Assert.AreEqual(1, scatterer.Saves);
            Assert.IsFalse(store.Ledger.Contains(ocean.Key));
        }

        // Switching the bundling off drops the values only ReDefinition kept and sets nothing more; the profile
        // stays chosen, its upscaler part needing no bundling.
        [TestMethod]
        public void SwitchingTheBundlingOffDropsWhatOnlyReDefinitionKept()
        {
            FakeMod waterfall = Mod("waterfall", SettingsSaving.AtEveryStart);
            BundledSetting lights = waterfall.Add("EnableLights", "True");
            store.Set(lights, "False", false);
            store.ProfileName = "high";

            store.SetEnabled(false);

            Assert.IsFalse(store.Ledger.Contains(lights.Key));
            Assert.AreEqual("high", store.ProfileName, "the profile stays chosen: the upscaler's part needs no bundling");
            store.Set(lights, "True", false);
            Assert.AreEqual("False", waterfall.Values["EnableLights"], "nothing is set while the bundling is off");
        }

        // Switched on again, the mods installed now count as having had the
        // profile: the next start does not put its values over what they hold.
        [TestMethod]
        public void SwitchingTheBundlingOnAgainCatchesNothingUp()
        {
            Mod("scatterer", SettingsSaving.InModFiles);
            store.SetEnabled(false);
            store.ProfileName = "high";
            Mod("eve", SettingsSaving.InModFiles);

            store.SetEnabled(true);
            Assert.AreEqual(0, store.ModsWithoutTheProfile().Count);
        }

        // KSP's own reset runs before the window's steps: what KSP held before it
        // stays the value from before ReDefinition, which Restore puts back --
        // also for a setting the steps give back the value it had.
        [TestMethod]
        public void AValueChangedFromOutsideAheadOfTheStepsKeepsItsValueFromBefore()
        {
            FakeMod ksp = Mod("ksp", SettingsSaving.InModFiles);
            BundledSetting antialiasing = ksp.Add("ANTI_ALIASING", "4");
            BundledSetting vsync = ksp.Add("SYNC_VBL", "1");
            BundledSetting untouched = ksp.Add("FRAMERATE_LIMIT", "120");
            KeyValuePair<BundledSetting, string>[] before =
            {
                new KeyValuePair<BundledSetting, string>(antialiasing, "4"),
                new KeyValuePair<BundledSetting, string>(vsync, "1"),
                new KeyValuePair<BundledSetting, string>(untouched, "120"),
            };

            ksp.Values["ANTI_ALIASING"] = "2";
            ksp.Values["SYNC_VBL"] = "0";
            int written = host.FileWrites;
            Assert.IsTrue(store.KeepBefore(before));
            Assert.IsTrue(host.FileWrites > written, "on disk before KSP saves");
            store.ResetTo(antialiasing, "0");
            store.ResetTo(vsync, "1");
            store.SaveNow();

            Assert.AreEqual("4", store.BeforeReDefinition(antialiasing));
            Assert.AreEqual("1", store.BeforeReDefinition(vsync));
            BundledLedger.Backup backup;
            Assert.IsFalse(store.Ledger.TryBackup("", untouched.Key, out backup), "what the reset left needs nothing");

            store.RestoreBackup();
            Assert.AreEqual("4", ksp.Values["ANTI_ALIASING"]);
            Assert.AreEqual("1", ksp.Values["SYNC_VBL"]);
        }

        // A value from before already kept is not replaced by what KSP holds ahead of the window's steps.
        [TestMethod]
        public void AValueFromBeforeKeptAlreadyIsNotReplacedAheadOfTheSteps()
        {
            FakeMod ksp = Mod("ksp", SettingsSaving.InModFiles);
            BundledSetting antialiasing = ksp.Add("ANTI_ALIASING", "4");
            store.Set(antialiasing, "8", true);

            ksp.Values["ANTI_ALIASING"] = "2";
            store.KeepBefore(new[] { new KeyValuePair<BundledSetting, string>(antialiasing, "8") });
            Assert.AreEqual("4", store.BeforeReDefinition(antialiasing));
        }

        // Where the value from before cannot be written to disk, KeepBefore says so.
        [TestMethod]
        public void AValueFromBeforeThatCannotBeWrittenIsSaid()
        {
            FakeMod ksp = Mod("ksp", SettingsSaving.InModFiles);
            BundledSetting antialiasing = ksp.Add("ANTI_ALIASING", "4");
            ksp.Values["ANTI_ALIASING"] = "2";
            host.FileWriteFails = true;

            Assert.IsFalse(store.KeepBefore(new[] { new KeyValuePair<BundledSetting, string>(antialiasing, "4") }));
        }

        // A value that could not be put back is tried again in the next scene, not on every tick of this one.
        [TestMethod]
        public void AValueThatCouldNotBePutBackIsTriedAgainInTheNextSceneNotOnEveryTick()
        {
            FakeMod scatterer = Mod("scatterer", SettingsSaving.InModFiles);
            BundledSetting ocean = scatterer.Add("useOceanShaders", "False");
            store.Set(ocean, "True", true);

            scatterer.WriteFails = new InvalidOperationException("not now");
            store.Release(ocean);
            Assert.AreEqual("True", scatterer.Values["useOceanShaders"]);

            store.ReapplyStored("tick", null, false);
            Assert.AreEqual("True", scatterer.Values["useOceanShaders"], "tried once in this scene already");

            store.NoteSceneLoaded();
            store.ReapplyStored("next scene", null, false);
            Assert.AreEqual("False", scatterer.Values["useOceanShaders"]);
        }

        // A mod installed since the profile was applied, or now of another build, is named as lacking the
        // profile, until the profile is applied again.
        [TestMethod]
        public void AModInstalledOrBuiltDifferentlySinceTheProfileIsNamed()
        {
            FakeMod scatterer = Mod("scatterer", SettingsSaving.InModFiles);
            store.ProfileName = "high";
            Assert.AreEqual(0, store.ModsWithoutTheProfile().Count, "the profile was applied with this mod installed");

            Mod("eve", SettingsSaving.InModFiles);
            CollectionAssert.AreEqual(new[] { "eve" }, store.ModsWithoutTheProfile(),
                                      "a mod installed since has never had the profile");

            scatterer.Build = "volumetric";
            CollectionAssert.AreEquivalent(new[] { "eve", "scatterer" }, store.ModsWithoutTheProfile(),
                                           "another build of a mod has settings the profile never set");

            store.NoteProfileApplied();
            Assert.AreEqual(0, store.ModsWithoutTheProfile().Count, "once they have it, they are not named again");
        }

        // Without a profile chosen, or with the bundling off, no mod is named as lacking the profile.
        [TestMethod]
        public void WithNoProfileOrWithTheBundlingOffNoModIsNamed()
        {
            Mod("scatterer", SettingsSaving.InModFiles);
            Assert.AreEqual(0, store.ModsWithoutTheProfile().Count, "no profile chosen: nothing to catch up with");

            store.ProfileName = "high";
            Mod("eve", SettingsSaving.InModFiles);
            store.SetEnabled(false);
            Assert.AreEqual(0, store.ModsWithoutTheProfile().Count, "with the bundling off the mods keep their own");
        }

        // A batch of first changes -- a profile applied for the first time -- sets every value from before and
        // writes bundled.cfg once for it, not once per setting. What it leaves in the ledger and in the mods is
        // what the same changes one by one leave: nothing for a setting its mod refused, cannot take now, holds
        // already, or fails to write.
        [TestMethod]
        public void AFirstProfileWritesOurFileOnceAndKeepsTheSameBackups()
        {
            int singly;
            string alone = FirstProfile(false, out singly);
            int batched;
            string together = FirstProfile(true, out batched);

            Assert.AreEqual(alone, together, "the same backups and the same values in the mods");
            Assert.IsTrue(alone.Contains("|scatterer.s1=False"), "the values from before were kept: " + alone);
            Assert.IsTrue(alone.Contains("|waterfall.w1=False"), "also for a mod that reads its value at every start: " + alone);
            foreach (string notChanged in new[] { "refused", "waiting", "same", "fails" })
                Assert.IsFalse(alone.Contains("|scatterer." + notChanged + "="),
                               "nothing kept for " + notChanged + ": " + alone);
            Assert.IsTrue(singly > 4, "one write for each backup of a mod that saves: " + singly);
            Assert.AreEqual(1, batched, "one write in all");
        }

        // The batch of AFirstProfileWritesOurFileOnceAndKeepsTheSameBackups, with or without Prepare. Returns the
        // ledger's backups and the mods' values as text, and how often bundled.cfg was written.
        private string FirstProfile(bool prepare, out int fileWrites)
        {
            NewStore();
            FakeMod scatterer = Mod("scatterer", SettingsSaving.InModFiles);
            FakeMod waterfall = Mod("waterfall", SettingsSaving.AtEveryStart);
            List<KeyValuePair<BundledSetting, string>> coming = new List<KeyValuePair<BundledSetting, string>>();
            // The failing write is the first one the mod gets, so it is the first setting's.
            BundledSetting fails = scatterer.Add("fails", "False");
            scatterer.WriteFails = new InvalidOperationException("the mod is not ready");
            coming.Add(new KeyValuePair<BundledSetting, string>(fails, "True"));
            for (int i = 1; i <= 5; i++) coming.Add(new KeyValuePair<BundledSetting, string>(scatterer.Add("s" + i, "False"), "True"));
            coming.Add(new KeyValuePair<BundledSetting, string>(waterfall.Add("w1", "False"), "True"));
            coming.Add(new KeyValuePair<BundledSetting, string>(PerSave(scatterer, "perSave", "False"), "True"));
            BundledSetting refused = scatterer.Add("refused", "A");
            refused.Control = SettingControl.Choice;
            refused.Choices = new[] { "A", "B" };
            coming.Add(new KeyValuePair<BundledSetting, string>(refused, "Gone"));
            BundledSetting waiting = scatterer.Add("waiting", "False");
            waiting.Applicable = () => false;
            coming.Add(new KeyValuePair<BundledSetting, string>(waiting, "True"));
            coming.Add(new KeyValuePair<BundledSetting, string>(scatterer.Add("same", "True"), "True"));

            if (prepare) store.Prepare(coming);
            try
            {
                foreach (KeyValuePair<BundledSetting, string> pair in coming) store.Set(pair.Key, pair.Value, false);
            }
            finally
            {
                if (prepare) store.EndPrepare();
            }

            List<string> lines = new List<string>();
            foreach (string context in store.Ledger.Contexts())
                foreach (KeyValuePair<string, BundledLedger.Backup> backup in store.Ledger.BackupsIn(context))
                    lines.Add(context + "|" + backup.Key + "=" + backup.Value.Value);
            lines.Sort();
            foreach (FakeMod mod in host.Mods)
                foreach (KeyValuePair<string, string> value in mod.Values) lines.Add(mod.Id + "." + value.Key + "=" + value.Value);
            fileWrites = host.FileWrites;
            return string.Join("\n", lines.ToArray());
        }
    }
}
