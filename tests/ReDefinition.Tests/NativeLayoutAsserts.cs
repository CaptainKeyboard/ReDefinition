using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ReDefinition.Tests
{
    // The sizes and offsets a native header of the proxy asserts with static_assert(sizeof(T) == n) and static_assert(offsetof(T, field) == n), so that the layout tests
    // compare the managed structs against the header's own numbers instead of copies of them.
    internal static class NativeLayoutAsserts
    {
        private static readonly Regex Size = new Regex(@"static_assert\(\s*sizeof\(\s*(\w+)\s*\)\s*==\s*(\d+)\s*,");
        private static readonly Regex Offset = new Regex(@"static_assert\(\s*offsetof\(\s*(\w+)\s*,\s*(\w+)\s*\)\s*==\s*(\d+)\s*,");

        // Keys "Type" (its size) and "Type.field" (an offset), from the header at the path below the repository root.
        internal static Dictionary<string, int> Read(params string[] path)
        {
            string text = File.ReadAllText(RepoRoot.Locate(path));
            var values = new Dictionary<string, int>();
            foreach (Match m in Size.Matches(text)) values[m.Groups[1].Value] = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            foreach (Match m in Offset.Matches(text)) values[m.Groups[1].Value + "." + m.Groups[2].Value] = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            return values;
        }

        internal static int Of(Dictionary<string, int> values, string key)
        {
            int v;
            Assert.IsTrue(values.TryGetValue(key, out v), "the header asserts no " + key);
            return v;
        }
    }
}
