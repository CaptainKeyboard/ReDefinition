using System;
using ReDefinition.Framework;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ReDefinition.Tests
{
    // What reaches the mods, and when (BundledStore): each test one decision the
    // store makes, with mods and a game that are stand-ins.
    [TestClass]
    public class BundledStoreTests
    {
        private FakeHost host;
        private BundledStore store;

        [TestInitialize]
        public void NewStore()
        {
            host = new FakeHost();
            store = new BundledStore(host);
        }

        private FakeMod Mod(string id, SettingsSaving saving)
        {
            FakeMod mod = new FakeMod(id, saving);
            host.Mods.Add(mod);
            return mod;
        }

        private BundledSetting PerSave(FakeMod mod, string name, string value)
        {
            BundledSetting setting = mod.Add(name, value);
            setting.Context = () => host.Save;
            return setting;
        }

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

        [TestMethod]
        public void WhatTheModsRequireCountsOverEveryValueSet()
        {
            FakeMod ksp = Mod("ksp", SettingsSaving.InModFiles);
            BundledSetting resolution = ksp.Add("REFLECTION_PROBE_TEXTURE_RESOLUTION", "0");
            host.AdjustWith = (setting, value) => "1";

            store.Set(resolution, "3", false);

            Assert.AreEqual("1", ksp.Values["REFLECTION_PROBE_TEXTURE_RESOLUTION"]);
        }

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

        [TestMethod]
        public void SwitchingTheBundlingOffDropsWhatOnlyReDefinitionKept()
        {
            FakeMod waterfall = Mod("waterfall", SettingsSaving.AtEveryStart);
            BundledSetting lights = waterfall.Add("EnableLights", "True");
            store.Set(lights, "False", false);
            store.ProfileName = "high";

            store.SetEnabled(false);

            Assert.IsFalse(store.Ledger.Contains(lights.Key));
            Assert.AreEqual("", store.ProfileName);
            store.Set(lights, "True", false);
            Assert.AreEqual("False", waterfall.Values["EnableLights"], "nothing is set while the bundling is off");
        }

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
    }
}
