using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ReDefinition.Tests
{
    // The main mod's build copies into the game's GameData and empties folders there. A test run builds the main mod, so the test
    // project has to keep that off by itself, whatever command line starts the run.
    [TestClass]
    public class BuildGuardTests
    {
        [TestMethod]
        public void TheTestProjectNeverDeploysIntoTheGame()
        {
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            string tests = File.ReadAllText(Path.Combine(root, "tests", "ReDefinition.Tests", "ReDefinition.Tests.csproj"));
            Match reference = Regex.Match(tests, @"<ProjectReference\b[^>]*src\\ReDefinition\.csproj[^>]*>");
            Assert.IsTrue(reference.Success, "The test project no longer references the main mod.");
            Assert.IsTrue(Regex.IsMatch(reference.Value, @"AdditionalProperties=""[^""]*DeployToGameData=false"), "The reference to the main mod does not pin DeployToGameData=false: " + reference.Value);
        }

        [TestMethod]
        public void OnlyTheMainProjectHasADeployTarget()
        {
            string root = RepoRoot.Root("src", "ReDefinition.csproj");
            string main = File.ReadAllText(Path.Combine(root, "src", "ReDefinition.csproj"));
            Assert.IsTrue(Regex.IsMatch(main, @"<Target Name=""DeployToGameData""[^>]*Condition=""'\$\(DeployToGameData\)' == 'true'"), "The deploy target is no longer conditioned on DeployToGameData.");
            foreach (string project in new[] { "Common\\ReDefinition.Common.csproj", "Atmospheres\\ReDefinition.Atmospheres.csproj", "Dashboard\\ReDefinition.Dashboard.csproj", "Planner\\ReDefinition.Planner.csproj" })
                StringAssert.DoesNotMatch(File.ReadAllText(Path.Combine(root, "src", project)), new Regex("KspGameData[^\\r\\n]*(Copy|RemoveDir)"), project + " writes into GameData");
        }
    }
}
