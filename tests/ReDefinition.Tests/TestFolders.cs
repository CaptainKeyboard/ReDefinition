using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ReDefinition.Tests
{
    // The repository as the tests find it: its root is the first folder above the test's working folder that holds a
    // given file or folder, which tells apart the repository from the output folders under it.
    internal static class RepoRoot
    {
        // The root of the repository, found by the file or folder at the relative path given; null where no folder above the
        // working folder holds it.
        internal static string Find(params string[] marker)
        {
            string relative = Path.Combine(marker);
            for (DirectoryInfo d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent)
            {
                string at = Path.Combine(d.FullName, relative);
                if (File.Exists(at) || Directory.Exists(at)) return d.FullName;
            }
            return null;
        }

        // The same, failing the test where there is none.
        internal static string Root(params string[] marker)
        {
            string root = Find(marker);
            Assert.IsNotNull(root, string.Join("/", marker) + " was not found above the test's folder");
            return root;
        }

        // The full path of the file or folder at the relative path given, in the repository found by it.
        internal static string Locate(params string[] relative)
        {
            return Path.Combine(Root(relative), Path.Combine(relative));
        }
    }

    // A test class with a folder of its own under the temp folder, made before each test and deleted with everything in
    // it after; the tests write their files there. A class that needs more makes its own TestInitialize and TestCleanup,
    // which run after this one's and before it.
    public abstract class TempFolderTests
    {
        // The test's folder.
        protected string folder;

        // Makes the folder, named after the test class and unique to the test.
        [TestInitialize]
        public void MakeFolder()
        {
            folder = Path.Combine(Path.GetTempPath(), "ReDefinition" + GetType().Name + "-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
        }

        // Deletes the folder and everything in it.
        [TestCleanup]
        public void RemoveFolder()
        {
            Directory.Delete(folder, true);
        }
    }
}
