using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Proxy;

namespace ReDefinition.Tests
{
    // The render events' ids (src/DxgiProxy/Managed/ProxyContract.h), which the proxy's dispatcher and the harness
    // include: ProxyEvents has each as the header does. PX_EVENT_<NAME> is the constant <Name>.
    [TestClass]
    public class ProxyContractTests
    {
        // Every constant of ProxyEvents has its PX_EVENT_ define with the same value, and the header defines no
        // event the managed side does not know.
        [TestMethod]
        public void EveryEventIsTheContracts()
        {
            Dictionary<string, long> contract = Contract();
            var seen = new HashSet<string>();
            foreach (FieldInfo field in typeof(ProxyEvents).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (!field.IsLiteral) continue;
                string name = "PX_EVENT_" + Snake(field.Name);
                Assert.IsTrue(contract.ContainsKey(name), "ProxyContract.h has no " + name + " for ProxyEvents." + field.Name);
                Assert.AreEqual(contract[name], System.Convert.ToInt64(field.GetRawConstantValue()), "ProxyEvents." + field.Name);
                seen.Add(name);
            }
            foreach (string name in contract.Keys)
                Assert.IsTrue(seen.Contains(name), "ProxyEvents has no constant for " + name);
            Assert.IsTrue(seen.Count >= 13, "only " + seen.Count + " events compared");
        }

        // The ids are all different, so that no two events reach the same handler.
        [TestMethod]
        public void NoTwoEventsShareAnId()
        {
            var ids = new Dictionary<long, string>();
            foreach (var define in Contract())
            {
                string other;
                Assert.IsFalse(ids.TryGetValue(define.Value, out other), define.Key + " and " + other + " share the id " + define.Value);
                ids[define.Value] = define.Key;
            }
        }

        // The header's integer #defines.
        private static Dictionary<string, long> Contract()
        {
            string path = RepoRoot.Locate("src", "DxgiProxy", "Managed", "ProxyContract.h");
            var values = new Dictionary<string, long>();
            var define = new Regex(@"^#define\s+(PX_EVENT_\w+)\s+(\d+)\s*(//.*)?$");
            foreach (string line in File.ReadAllLines(path))
            {
                Match m = define.Match(line);
                if (m.Success) values[m.Groups[1].Value] = long.Parse(m.Groups[2].Value);
            }
            return values;
        }

        // D3d12CreatePass -> D3D12_CREATE_PASS, DlssSizeQuery -> DLSS_SIZE_QUERY.
        private static string Snake(string name)
        {
            var text = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool upper = char.IsUpper(c);
                if (i > 0 && upper && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                    text.Append('_');
                text.Append(char.ToUpperInvariant(c));
            }
            return text.ToString();
        }
    }
}
