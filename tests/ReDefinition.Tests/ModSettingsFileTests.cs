using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Core;

namespace ReDefinition.Tests
{
    // The helper a mod's settings file is read and written with: what it gives back for a file that is whole, missing or damaged.
    [TestClass]
    public class ModSettingsFileTests
    {
        private static string NewPath()
        {
            return Path.Combine(Path.GetTempPath(), "mod-settings-" + Guid.NewGuid().ToString("N"), "sub", "file.cfg");
        }

        private static void Clean(string path)
        {
            string root = Path.GetDirectoryName(Path.GetDirectoryName(path));
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        [TestMethod]
        public void TheValuesComeBackAsTheyWereWritten()
        {
            string path = NewPath();
            try
            {
                Assert.IsTrue(ModSettingsFile.Write(path, n =>
                {
                    n.AddValue("flag", "True");
                    n.AddValue("name", "abc");
                    n.AddValue("count", "7");
                    n.AddValue("scale", "1.5");
                }));
                string seen = ModSettingsFile.Read(path, n =>
                {
                    bool flag; string name; int count; float scale;
                    Assert.IsTrue(ModSettingsFile.TryBool(n, "flag", out flag));
                    Assert.IsTrue(ModSettingsFile.TryString(n, "name", out name));
                    Assert.IsTrue(ModSettingsFile.TryInt(n, "count", out count));
                    Assert.IsTrue(ModSettingsFile.TryFloat(n, "scale", out scale));
                    int other; string none;
                    Assert.IsFalse(ModSettingsFile.TryInt(n, "name", out other));
                    Assert.IsFalse(ModSettingsFile.TryString(n, "absent", out none));
                    return flag + name + count + scale;
                }, "fallback");
                Assert.AreEqual("Trueabc71.5", seen);
            }
            finally { Clean(path); }
        }

        [TestMethod]
        public void AMissingFileGivesTheFallback()
        {
            string path = NewPath();
            Assert.AreEqual(42, ModSettingsFile.Read(path, n => 1, 42));
        }

        [TestMethod]
        public void ADamagedFileGivesTheFallback()
        {
            string path = NewPath();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "{ { not a config \u0000 }");
                Assert.AreEqual(42, ModSettingsFile.Read(path, n => { int v; return ModSettingsFile.TryInt(n, "count", out v) ? v : 42; }, 42));
            }
            finally { Clean(path); }
        }

        [TestMethod]
        public void WritingCreatesTheMissingFolder()
        {
            string path = NewPath();
            try
            {
                Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(path)));
                Assert.IsTrue(ModSettingsFile.Write(path, n => n.AddValue("a", "b")));
                Assert.IsTrue(File.Exists(path));
            }
            finally { Clean(path); }
        }
    }
}
