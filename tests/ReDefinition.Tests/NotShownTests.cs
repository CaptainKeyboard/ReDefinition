using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Framework;

namespace ReDefinition.Tests
{
    // A mod's settings object as KSP saves it: [Persistent] fields, one of which a
    // newer build added.
    internal sealed class PersistentFake
    {
        internal static PersistentFake Instance = new PersistentFake();

        [Persistent] public bool shadows = true;
        [Persistent] public int detail = 2;
        [Persistent] public bool addedLater = false;

        // Not saved: no setting of the mod's.
        public float runningTime;
    }

    // What the settings window is told it cannot show: only a row a build really
    // lacks, and a setting a build saves that no registration names -- never a
    // version on its own.
    [TestClass]
    public class NotShownTests
    {
        private static RegisteredMod Build(string text)
        {
            List<string> problems = new List<string>();
            ConfigNode node = ConfigNode.Parse(text).GetNode(ModRegistration.NodeName);
            return new RegisteredMod(ModRegistration.FromConfigNode(node, problems), problems);
        }

        private const string Head = "MOD_SETTINGS\n{\n name = persistentfake\n detect = ReDefinition.Tests.PersistentFake\n";

        [TestMethod]
        public void EverySavedFieldNamedLeavesNothingOut()
        {
            RegisteredMod mod = Build(Head
                + " SETTING\n {\n  name = shadows\n  member = ReDefinition.Tests.PersistentFake.Instance.shadows\n"
                + "  row = ShadowsAndReflections\n  default = True\n }\n"
                + " SETTING\n {\n  name = detail\n  member = ReDefinition.Tests.PersistentFake.Instance.detail\n  default = 2\n }\n"
                + " SETTING\n {\n  name = addedLater\n  leftOut = a switch for debugging\n }\n}");
            Assert.AreEqual(0, mod.RowsNotShown.Count);
            Assert.AreEqual(0, mod.SettingsNotKnown.Count);
        }

        [TestMethod]
        public void ASavedFieldNoRegistrationNamesIsReported()
        {
            RegisteredMod mod = Build(Head
                + " SETTING\n {\n  name = shadows\n  member = ReDefinition.Tests.PersistentFake.Instance.shadows\n"
                + "  row = ShadowsAndReflections\n  default = True\n }\n"
                + " SETTING\n {\n  name = detail\n  member = ReDefinition.Tests.PersistentFake.Instance.detail\n  default = 2\n }\n}");
            CollectionAssert.AreEqual(new[] { "addedLater" }, new List<string>(mod.SettingsNotKnown));
            // A field the mod does not save is no setting of it.
            Assert.IsFalse(mod.SettingsNotKnown.Contains("runningTime"));
        }

        [TestMethod]
        public void ARowThisBuildLacksIsReported()
        {
            RegisteredMod mod = Build(Head
                + " SETTING\n {\n  name = shadows\n  member = ReDefinition.Tests.PersistentFake.Instance.shadows\n"
                + "  row = ShadowsAndReflections\n  default = True\n }\n"
                + " SETTING\n {\n  name = gone\n  member = ReDefinition.Tests.PersistentFake.Instance.gone\n"
                + "  title = Gone switch\n  row = Effects\n  default = True\n }\n"
                + " SETTING\n {\n  name = detail\n  member = ReDefinition.Tests.PersistentFake.Instance.detail\n  default = 2\n }\n"
                + " SETTING\n {\n  name = addedLater\n  member = ReDefinition.Tests.PersistentFake.Instance.addedLater\n  default = False\n }\n}");
            CollectionAssert.AreEqual(new[] { "Gone switch" }, new List<string>(mod.RowsNotShown));
        }

        [TestMethod]
        public void ARowWithoutAPlaceOrOneABuildIsKnownToLackIsNotReported()
        {
            RegisteredMod mod = Build(Head
                + " SETTING\n {\n  name = shadows\n  member = ReDefinition.Tests.PersistentFake.Instance.shadows\n"
                + "  row = ShadowsAndReflections\n  default = True\n }\n"
                // No row: it was never shown.
                + " SETTING\n {\n  name = hidden\n  member = ReDefinition.Tests.PersistentFake.Instance.hidden\n  default = 1\n }\n"
                // Optional: a build is known to lack it.
                + " SETTING\n {\n  name = otherBuild\n  member = ReDefinition.Tests.PersistentFake.Instance.otherBuild\n"
                + "  row = Effects\n  optional = only in the other build\n  default = True\n }\n"
                + " SETTING\n {\n  name = detail\n  member = ReDefinition.Tests.PersistentFake.Instance.detail\n  default = 2\n }\n"
                + " SETTING\n {\n  name = addedLater\n  member = ReDefinition.Tests.PersistentFake.Instance.addedLater\n  default = False\n }\n}");
            Assert.AreEqual(0, mod.RowsNotShown.Count);
        }

        [TestMethod]
        public void ARowWhoseDefaultNoLongerFitsIsReported()
        {
            RegisteredMod mod = Build(Head
                + " SETTING\n {\n  name = shadows\n  member = ReDefinition.Tests.PersistentFake.Instance.shadows\n"
                + "  title = Shadows\n  row = ShadowsAndReflections\n  default = Soft\n }\n"
                + " SETTING\n {\n  name = detail\n  member = ReDefinition.Tests.PersistentFake.Instance.detail\n  default = 2\n }\n"
                + " SETTING\n {\n  name = addedLater\n  member = ReDefinition.Tests.PersistentFake.Instance.addedLater\n  default = False\n }\n}");
            CollectionAssert.AreEqual(new[] { "Shadows" }, new List<string>(mod.RowsNotShown));
        }
    }
}
