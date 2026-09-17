using System.Collections.Generic;
using ReDefinition.Framework;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ReDefinition.Tests
{
    // A mod's settings that are not there until the mod loads them: what `ready`
    // names, and a setting beside it.
    internal static class ReadyFake
    {
        internal static object Loaded;
        internal static int Depth;
    }

    // A mod whose defaults come from its settings and a block. Its fields are only
    // reached through member paths.
    internal static class DefaultsFake
    {
        internal static int Depth = 0;
        internal static int Width = 0;
    }

    // What registrations build (RegisteredMod, ModRegistry), the defaults they give
    // (ModDefaults) and a profile's MODULE node (ProfileApplier), with types of the
    // tests' own standing in for a mod.
    [TestClass]
    public class RegistrationTests
    {
        private static ConfigNode Node(string name, string text)
        {
            return ConfigNode.Parse(text).GetNode(name);
        }

        private static RegisteredMod Build(string text, List<string> problems)
        {
            return new RegisteredMod(ModRegistration.FromConfigNode(Node(ModRegistration.NodeName, text), problems), problems);
        }

        [TestMethod]
        public void UntilItsReadyMemberHoldsSomethingAModIsNeitherReadNorSet()
        {
            List<string> problems = new List<string>();
            RegisteredMod mod = Build("REDEFINITION_MOD\n{\n name = readyfake\n detect = ReDefinition.Tests.ReadyFake\n"
                                      + " ready = ReDefinition.Tests.ReadyFake.Loaded\n SETTING\n {\n  name = Depth\n"
                                      + "  member = ReDefinition.Tests.ReadyFake.Depth\n  default = 4\n }\n}", problems);
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
            Assert.IsTrue(mod.IsInstalled);
            BundledSetting depth = mod.Settings[0];

            ReadyFake.Loaded = null;
            ReadyFake.Depth = 0;
            Assert.IsNull(depth.Read(), "a 0 before the mod has loaded is not its value");
            Assert.IsFalse(depth.Applicable());

            ReadyFake.Loaded = new object();
            ReadyFake.Depth = 4;
            Assert.AreEqual("4", depth.Read());
            Assert.IsTrue(depth.Applicable());

            ReadyFake.Loaded = false;
            Assert.IsFalse(depth.Applicable(), "False is not ready either");
        }

        [TestMethod]
        public void AReadyMemberThatIsNotThereLeavesTheModOut()
        {
            List<string> problems = new List<string>();
            RegisteredMod mod = Build("REDEFINITION_MOD\n{\n name = readyfake\n detect = ReDefinition.Tests.ReadyFake\n"
                                      + " ready = ReDefinition.Tests.ReadyFake.NotThere\n SETTING\n {\n  name = Depth\n"
                                      + "  member = ReDefinition.Tests.ReadyFake.Depth\n }\n}", problems);

            Assert.IsFalse(mod.IsInstalled);
            Assert.IsTrue(new List<string>(mod.DroppedMembers).Exists(line => line.Contains("NotThere")));
        }

        private static KeyValuePair<ConfigNode, string> Source(string title, bool own)
        {
            return new KeyValuePair<ConfigNode, string>(
                Node(ModRegistration.NodeName, "REDEFINITION_MOD\n{\n name = shared\n title = " + title
                                               + "\n detect = No.Such.Shared\n}"),
                own ? "ReDefinition/Mods/Shared" : "SomePack/Registrations");
        }

        // KSP's GameDatabase names a file's place without GameData and with slashes.
        [TestMethod]
        public void OnlyReDefinitionsOwnFolderCountsAsOwn()
        {
            Assert.IsTrue(ModRegistry.IsOwn("ReDefinition/Mods/KSP"));
            Assert.IsTrue(ModRegistry.IsOwn("redefinition/Mods/KSP"));
            Assert.IsFalse(ModRegistry.IsOwn("ReDefinitionExtras/Mods"));
            Assert.IsFalse(ModRegistry.IsOwn("Scatterer/ReDefinition"));
            Assert.IsFalse(ModRegistry.IsOwn(""));
        }

        // Theirs -- the mod's own folder, a pack -- over ReDefinition's, whichever KSP
        // read first; of two alike the later, said.
        [TestMethod]
        public void ARegistrationFromOutsideReDefinitionCountsOverItsOwn()
        {
            foreach (bool theirsFirst in new[] { true, false })
            {
                List<string> problems = new List<string>();
                List<string> notes = new List<string>();
                List<KeyValuePair<ConfigNode, string>> sources = theirsFirst
                    ? new List<KeyValuePair<ConfigNode, string>> { Source("Theirs", false), Source("Ours", true) }
                    : new List<KeyValuePair<ConfigNode, string>> { Source("Ours", true), Source("Theirs", false) };
                List<ModRegistration> read = ModRegistry.ReadSources(sources, problems, notes);
                Assert.AreEqual(1, read.Count);
                Assert.AreEqual("Theirs", read[0].Title, "theirs first: " + theirsFirst);
                Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
                Assert.AreEqual(1, notes.Count, "which registration counts is said");
            }

            List<string> twice = new List<string>();
            List<ModRegistration> alike = ModRegistry.ReadSources(
                new List<KeyValuePair<ConfigNode, string>> { Source("First", false), Source("Second", false) }, twice, null);
            Assert.AreEqual("Second", alike[0].Title);
            Assert.IsTrue(twice.Exists(problem => problem.Contains("registered twice")));
        }

        // A mod's assembly counts only from GameData: the tests' own, named as the
        // detect type's namespace, lies elsewhere, as KSP's and Unity's do. With no
        // assembly of the mod it is simply not installed.
        [TestMethod]
        public void OnlyAnAssemblyFromGameDataTellsAModsBuildWithoutItsDetectType()
        {
            RegisteredMod outside = Build("REDEFINITION_MOD\n{\n name = outside\n detect = ReDefinition.Tests.NoSuchType\n}",
                                          new List<string>());
            Assert.IsFalse(outside.IsInstalled);
            Assert.AreEqual(0, outside.DroppedMembers.Count);

            RegisteredMod absent = Build("REDEFINITION_MOD\n{\n name = absent\n detect = No.Such.Type\n}", new List<string>());
            Assert.AreEqual(0, absent.DroppedMembers.Count);

            Assert.IsTrue(RegisteredMod.InGameData(@"C:\Games\KSP\GameData\Scatterer\Plugin\Scatterer.dll"));
            Assert.IsTrue(RegisteredMod.InGameData("/home/ksp/GameData/EVE/Plugins/Atmosphere.dll"));
            Assert.IsFalse(RegisteredMod.InGameData(@"C:\Games\KSP\KSP_x64_Data\Managed\Assembly-CSharp.dll"));
            Assert.IsFalse(RegisteredMod.InGameData(null));
        }

        // Its button would hide a window nothing here opens.
        [TestMethod]
        public void AButtonWithoutAWindowIsReported()
        {
            List<string> problems = new List<string>();
            RegisteredMod mod = Build("REDEFINITION_MOD\n{\n name = defaultsfake\n detect = ReDefinition.Tests.DefaultsFake\n"
                                      + " button = SomeAssembly\n SETTING\n {\n  name = Depth\n"
                                      + "  member = ReDefinition.Tests.DefaultsFake.Depth\n  default = 4\n }\n}", problems);
            Assert.IsTrue(mod.IsInstalled);
            Assert.IsNull(mod.OwnWindow);
            Assert.IsTrue(problems.Exists(problem => problem.Contains("without a window")));
        }

        [TestMethod]
        public void ModsStandByTitleWhereverTheirFilesAre()
        {
            List<ModRegistration> registrations = new List<ModRegistration>
            {
                new ModRegistration { Name = "a", Title = "Zeta", Detect = "No.Such.Zeta" },
                new ModRegistration { Name = "z", Title = "alpha", Detect = "No.Such.Alpha" },
            };

            List<IBundledMod> mods = ModRegistry.Build(registrations, new List<string>());

            Assert.AreEqual("z", mods[0].Id);
            Assert.AreEqual("a", mods[1].Id);
        }

        [TestMethod]
        public void AVersionIsReportedOnlyWhereAValueUsedWasReadFromIt()
        {
            const string Settings = " SETTING\n {\n  name = Depth\n  member = ReDefinition.Tests.DefaultsFake.Depth\n  default = 4\n }\n";
            const string Width = " SETTING\n {\n  name = Width\n  member = ReDefinition.Tests.DefaultsFake.Width\n  default = 2\n }\n";
            string installed = Build("REDEFINITION_MOD\n{\n name = defaultsfake\n detect = ReDefinition.Tests.DefaultsFake\n"
                                     + Settings + "}", new List<string>()).Version;
            string block = " DEFAULTS\n {\n  version = " + installed + "\n  Depth = 5\n }\n";
            string head = "REDEFINITION_MOD\n{\n name = defaultsfake\n detect = ReDefinition.Tests.DefaultsFake\n version = 0.1\n";

            List<string> problems = new List<string>();
            RegisteredMod covered = Build(head + Settings + block + "}", problems);
            Dictionary<string, Dictionary<string, string>> values =
                ModDefaults.Select(new List<IBundledMod> { covered }, problems);
            Assert.AreEqual("5", values["defaultsfake"]["Depth"]);
            Assert.AreEqual(0, problems.Count, "every value used came from the block of the installed version: "
                                               + string.Join("\n", problems));

            const string Gone = " SETTING\n {\n  name = Gone\n  member = ReDefinition.Tests.DefaultsFake.Gone\n  optional = True\n"
                                + "  default = 1\n }\n";
            RegisteredMod missing = Build(head + Settings + Gone + block + "}", problems);
            ModDefaults.Select(new List<IBundledMod> { missing }, problems);
            Assert.AreEqual(0, problems.Count, "a setting this build does not have takes no value: " + string.Join("\n", problems));

            RegisteredMod partly = Build(head + Settings + Width + block + "}", problems);
            ModDefaults.Select(new List<IBundledMod> { partly }, problems);
            Assert.AreEqual(1, problems.Count);
            StringAssert.Contains(problems[0], "version 0.1");
        }

        [TestMethod]
        public void AProfileTakesTheBlocksOfItsNameThatFitTheBuild()
        {
            List<string> problems = new List<string>();
            RegisteredMod mod = Build("REDEFINITION_MOD\n{\n name = profiledfake\n detect = ReDefinition.Tests.DefaultsFake\n"
                                      + " BUILD\n {\n  name = narrow\n  has = ReDefinition.Tests.DefaultsFake.Narrow\n }\n"
                                      + " BUILD\n {\n  name = wide\n  has = ReDefinition.Tests.DefaultsFake.Width\n }\n"
                                      + " SETTING\n {\n  name = Depth\n  member = ReDefinition.Tests.DefaultsFake.Depth\n"
                                      + "  kind = Quality\n  default = 4\n }\n"
                                      + " SETTING\n {\n  name = Width\n  member = ReDefinition.Tests.DefaultsFake.Width\n"
                                      + "  kind = Quality\n  default = 2\n }\n"
                                      + " PROFILE\n {\n  name = low\n  Depth = 1\n  Width = 1\n }\n"
                                      + " PROFILE\n {\n  name = low\n  build = narrow\n  Depth = 0\n }\n"
                                      + " PROFILE\n {\n  name = low\n  build = wide\n  Width = 3\n }\n"
                                      + " PROFILE\n {\n  name = high\n  Depth = 9\n }\n}", problems);
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
            Assert.AreEqual("wide", mod.Build);
            List<IBundledMod> mods = new List<IBundledMod> { mod };

            Dictionary<string, Dictionary<string, string>> low = ModProfiles.Select(mods, "low");
            Assert.AreEqual("1", low["profiledfake"]["Depth"], "a block for another build does not count");
            Assert.AreEqual("3", low["profiledfake"]["Width"], "the block for the build counts");
            Assert.AreEqual("9", ModProfiles.Select(mods, "high")["profiledfake"]["Depth"]);
            Assert.IsFalse(ModProfiles.Select(mods, "ultra").ContainsKey("profiledfake"), "no block: the defaults stand");

            List<string> unknown = ModProfiles.Unknown(mods, new List<string> { "low" });
            Assert.AreEqual(1, unknown.Count);
            StringAssert.Contains(unknown[0], "'high'");
        }

        [TestMethod]
        public void ABlockForTheBuildCountsOverOneForEveryBuildWhereverAPatchPutsIt()
        {
            List<string> problems = new List<string>();
            RegisteredMod mod = Build("REDEFINITION_MOD\n{\n name = profiledfake\n detect = ReDefinition.Tests.DefaultsFake\n"
                                      + " BUILD\n {\n  name = wide\n  has = ReDefinition.Tests.DefaultsFake.Width\n }\n"
                                      + " SETTING\n {\n  name = Depth\n  member = ReDefinition.Tests.DefaultsFake.Depth\n"
                                      + "  kind = Quality\n  default = 4\n }\n"
                                      + " DEFAULTS\n {\n  build = wide\n  Depth = 6\n }\n"
                                      + " DEFAULTS\n {\n  Depth = 5\n }\n"
                                      + " PROFILE\n {\n  name = low\n  build = wide\n  Depth = 2\n }\n"
                                      + " PROFILE\n {\n  name = low\n  Depth = 1\n }\n}", problems);
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
            List<IBundledMod> mods = new List<IBundledMod> { mod };

            Assert.AreEqual("6", ModDefaults.Select(mods, problems)["profiledfake"]["Depth"]);
            Assert.AreEqual("2", ModProfiles.Select(mods, "low")["profiledfake"]["Depth"]);
        }

        [TestMethod]
        public void ABlockForABuildNoBuildNamesIsSaid()
        {
            List<string> problems = new List<string>();
            ModRegistration.FromConfigNode(Node(ModRegistration.NodeName,
                "REDEFINITION_MOD\n{\n name = profiledfake\n detect = ReDefinition.Tests.DefaultsFake\n"
                + " BUILD\n {\n  name = wide\n  has = ReDefinition.Tests.DefaultsFake.Width\n }\n"
                + " SETTING\n {\n  name = Depth\n  member = ReDefinition.Tests.DefaultsFake.Depth\n  kind = Quality\n }\n"
                + " DEFAULTS\n {\n  build = wdie\n  Depth = 6\n }\n"
                + " PROFILE\n {\n  name = low\n  build = wide\n  Depth = 2\n }\n}"), problems);

            Assert.AreEqual(1, problems.Count, string.Join("\n", problems));
            StringAssert.Contains(problems[0], "'wdie'");

            // A behaviour that tells the build, and no BUILD: not said.
            problems.Clear();
            ModRegistration.FromConfigNode(Node(ModRegistration.NodeName,
                "REDEFINITION_MOD\n{\n name = profiledfake\n detect = ReDefinition.Tests.DefaultsFake\n behaviour = Tufx\n"
                + " SETTING\n {\n  name = Depth\n  member = ReDefinition.Tests.DefaultsFake.Depth\n  kind = Quality\n }\n"
                + " DEFAULTS\n {\n  build = volumetric\n  Depth = 6\n }\n}"), problems);
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        }

        [TestMethod]
        public void EveryProfileTakesTheBlocksForEveryProfileWhateverTheKind()
        {
            List<string> problems = new List<string>();
            RegisteredMod mod = Build("REDEFINITION_MOD\n{\n name = profiledfake\n detect = ReDefinition.Tests.DefaultsFake\n"
                                      + " BUILD\n {\n  name = wide\n  has = ReDefinition.Tests.DefaultsFake.Width\n }\n"
                                      + " BUILD\n {\n  name = narrow\n  has = ReDefinition.Tests.DefaultsFake.Narrow\n }\n"
                                      + " SETTING\n {\n  name = Depth\n  member = ReDefinition.Tests.DefaultsFake.Depth\n"
                                      + "  kind = Taste\n  default = 4\n }\n"
                                      + " SETTING\n {\n  name = Width\n  member = ReDefinition.Tests.DefaultsFake.Width\n"
                                      + "  default = 2\n }\n"
                                      + " ALL_PROFILES\n {\n  build = wide\n  Width = 3\n }\n"
                                      + " ALL_PROFILES\n {\n  Depth = 1\n  Width = 1\n }\n"
                                      + " ALL_PROFILES\n {\n  build = narrow\n  Width = 5\n }\n}", problems);
            Assert.AreEqual(0, problems.Count, string.Join("\n", problems));

            Dictionary<string, string> all = ModProfiles.SelectAll(new List<IBundledMod> { mod })["profiledfake"];
            Assert.AreEqual("1", all["Depth"], "a setting of any kind");
            Assert.AreEqual("3", all["Width"], "the block for the installed build, over the one for every build");
        }

        [TestMethod]
        public void AModuleKeyGivenTwiceCountsOnceTheLastAndIsSaid()
        {
            GraphicsProfile profile = GraphicsProfile.FromConfigNode(Node(GraphicsProfile.NodeName,
                "REDEFINITION_PROFILE\n{\n name = high\n MODULE\n {\n  name = upscaler\n  quality = NativeAA\n"
                + "  quality = Balanced\n }\n}"), new List<string>());
            List<string> problems = new List<string>();

            List<KeyValuePair<ModuleSetting, string>> values = ProfileApplier.ModuleValues(profile, problems);

            Assert.AreEqual(1, values.Count);
            Assert.AreEqual("Balanced", values[0].Value);
            Assert.IsTrue(problems.Exists(problem => problem.Contains("'quality' twice")));

            profile = GraphicsProfile.FromConfigNode(Node(GraphicsProfile.NodeName,
                "REDEFINITION_PROFILE\n{\n name = high\n MODULE\n {\n  name = upscaler\n  quality = NativeAA\n"
                + "  quality = 3\n }\n}"), new List<string>());
            problems.Clear();
            values = ProfileApplier.ModuleValues(profile, problems);
            Assert.AreEqual(0, values.Count, "the last counts where it is refused too");
            Assert.IsTrue(problems.Exists(problem => problem.Contains("'quality' twice")));
            Assert.IsTrue(problems.Exists(problem => problem.Contains("left out")));
        }
    }
}
