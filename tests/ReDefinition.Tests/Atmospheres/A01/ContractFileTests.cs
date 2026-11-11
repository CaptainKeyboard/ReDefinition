using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.A01
{
    // src/Atmospheres/CONTRACT.md names every frozen type and every setting, so that the packages find what they need in one place.
    [TestClass]
    public class ContractFileTests
    {
        private static string Contract()
        {
            return File.ReadAllText(Path.Combine(RepoRoot.Root("src", "ReDefinition.csproj"), "src", "Atmospheres", "CONTRACT.md"));
        }

        [TestMethod]
        public void TheContractNamesEveryPublicTypeOfTheSeam()
        {
            string text = Contract();
            string folder = Path.Combine(RepoRoot.Root("src", "ReDefinition.csproj"), "src", "Common", "Weather");
            var declaration = new Regex(@"^\s*public\s+(?:sealed\s+|static\s+|abstract\s+)*(?:class|struct|enum|interface)\s+(?<name>\w+)", RegexOptions.Multiline);
            int count = 0;
            foreach (string file in Directory.GetFiles(folder, "*.cs"))
            {
                foreach (Match m in declaration.Matches(File.ReadAllText(file)))
                {
                    string name = m.Groups["name"].Value;
                    Assert.IsTrue(text.Contains("`" + name + "`"), name + " is not in CONTRACT.md");
                    count++;
                }
            }
            Assert.IsTrue(count >= 30, "types: " + count);
        }

        [TestMethod]
        public void TheContractNamesTheTypesOfThePlatform()
        {
            string text = Contract();
            foreach (string name in new[]
            {
                "IAtmospheresModule", "AtmospheresModuleAttribute", "ModuleRegistry", "Tuning", "BodyWatcher", "AtmospheresStartup", "IPostUpscaleCompositor",
                "IOrbitOverlayLayer", "IEveFieldSuppressor", "IBodyProvider", "Bodies", "BodyClimate", "BodyStatus", "SeasonRules", "TemperatureRules", "PrecipRules",
                "PrecipTypeRule", "CoverRules", "IceRules", "CellRules", "SimRules", "CloudRules", "LookRules", "SoundRules", "WindRules", "GameplayRules",
                "AuroraRules", "LightMediumRules", "FrostRules", "ForecastRules", "AtmospheresSettings"
            })
            {
                Assert.IsTrue(text.Contains("`" + name), name + " is not in CONTRACT.md");
            }
        }

        [TestMethod]
        public void TheContractListsEverySettingWithItsDefault()
        {
            string text = Contract();
            foreach (SettingDef def in AtmospheresSettings.Definitions)
            {
                Assert.IsTrue(Regex.IsMatch(text, @"^\| `" + def.Key + @"` \|", RegexOptions.Multiline), def.Key + " is not in the table of CONTRACT.md");
            }
        }

        [TestMethod]
        public void TheContractKeepsTheDefaultsOfItsTableInStep()
        {
            string text = Contract();
            foreach (SettingDef def in AtmospheresSettings.Definitions)
            {
                Match row = Regex.Match(text, @"^\| `" + def.Key + @"` \| (?<default>[^|]*) \| (?<range>[^|]*) \|", RegexOptions.Multiline);
                string expected = def.Kind == SettingKind.Text ? (def.DefaultText.Length == 0 ? "empty" : def.DefaultText)
                    : def.Kind == SettingKind.Bool ? (def.Default != 0.0 ? "on" : "off") : def.Format(def.Default);
                Assert.AreEqual(expected, row.Groups["default"].Value.Trim(), def.Key + ": default");
                if (def.Kind == SettingKind.Choice) Assert.AreEqual(string.Join(" / ", def.Choices), row.Groups["range"].Value.Trim(), def.Key + ": choices");
            }
        }

        [TestMethod]
        public void TheContractHoldsTheAuditTheLedgerAndTheMergeRule()
        {
            string text = Contract();
            foreach (string heading in new[] { "Audit of the settings", "The budget ledger", "Shed order", "The merge rule", "The key sweep rule", "`cloudSource`", "Cloud types per level", "Tuning" })
            {
                Assert.IsTrue(text.Contains(heading), heading);
            }
            foreach (string removed in new[] { "iceWinterLine", "freezeLineDeg", "lagYears" }) Assert.IsTrue(text.Contains(removed), removed);
        }
    }
}
