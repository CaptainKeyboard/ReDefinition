using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ReDefinition.Tests
{
    // Both packages ship ReDefinition.Common.dll. The game keeps the one with the higher assembly version, so a Common whose code changed
    // needs a new CommonVersion (Directory.Build.props). The file src/Common/version.lock holds the version and the hash of its code.
    // After a change: raise CommonVersion, then write the lock with REDEFINITION_UPDATE_COMMON_LOCK=1 dotnet test --filter TheCommonCodeHasItsOwnVersion.
    [TestClass]
    public class CommonVersionTests
    {
        [TestMethod]
        public void TheCommonCodeHasItsOwnVersion()
        {
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            string lockFile = Path.Combine(root, "src", "Common", "version.lock");
            string version = Regex.Match(File.ReadAllText(Path.Combine(root, "Directory.Build.props")), "<CommonVersion>([^<]+)</CommonVersion>").Groups[1].Value;
            string hash = CodeHash(Path.Combine(root, "src", "Common"));
            string now = version + " " + hash;
            if (Environment.GetEnvironmentVariable("REDEFINITION_UPDATE_COMMON_LOCK") == "1")
            {
                File.WriteAllText(lockFile, now + "\n");
                return;
            }
            string[] kept = File.ReadAllText(lockFile).Trim().Split(' ');
            if (kept[0] == version)
                Assert.AreEqual(kept[1], hash, "src/Common changed but CommonVersion is still " + version
                    + ": raise it in Directory.Build.props, so that the game keeps this DLL over an older one, then write the lock (see this class).");
            else
                Assert.Fail("CommonVersion is " + version + " but version.lock was made for " + kept[0] + ": write the lock (see this class).");
        }

        // The code of Common: every source file by its path and the project file, with the line ends as LF.
        private static string CodeHash(string folder)
        {
            var text = new StringBuilder();
            foreach (string file in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories)
                         .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                                     && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                         .OrderBy(f => f.Substring(folder.Length).Replace('\\', '/'), StringComparer.Ordinal))
                text.Append(file.Substring(folder.Length).Replace('\\', '/')).Append('\n').Append(File.ReadAllText(file).Replace("\r\n", "\n")).Append('\n');
            // The project file counts too: a reference or a property in it changes the DLL as a source file does.
            text.Append("ReDefinition.Common.csproj\n").Append(File.ReadAllText(Path.Combine(folder, "ReDefinition.Common.csproj")).Replace("\r\n", "\n")).Append('\n');
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant();
        }
    }
}
