using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DocLock
{
    // The coverage: per area, language and kind, how many elements there are and how many have a comment, and
    // the broken references. It is information; the check does not fail on it.
    internal static class Report
    {
        // The coverage tables to the console and to .doclock/coverage.md.
        public static void Write(string root, Scan scan)
        {
            var b = new StringBuilder();
            b.Append("# Comment coverage\n\n");
            b.Append("Written by tools/DocLock (`report`). ").Append(scan.Files.Count).Append(" files.\n\n");
            Totals(b, "Overall", scan.Elements.GroupBy(e => "all"));
            Totals(b, "By area", scan.Elements.GroupBy(e => e.Area));
            Totals(b, "By language", scan.Elements.GroupBy(e => e.Language));
            Totals(b, "By kind", scan.Elements.GroupBy(e => e.Kind));
            b.Append("Broken references: ").Append(scan.BrokenReferences.Count).Append("\n");
            string text = b.ToString();
            Console.Write(text);
            string path = Path.Combine(root, ".doclock", "coverage.md");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        // One table: per group its elements, how many have a comment, and the share.
        private static void Totals(StringBuilder b, string title, IEnumerable<IGrouping<string, Element>> groups)
        {
            b.Append("## ").Append(title).Append("\n\n| | elements | commented | share |\n|---|---:|---:|---:|\n");
            foreach (IGrouping<string, Element> g in groups.OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                int all = g.Count();
                int documented = g.Count(Rules.Documented);
                b.Append("| ").Append(g.Key).Append(" | ").Append(all).Append(" | ").Append(documented).Append(" | ")
                 .Append((100.0 * documented / Math.Max(1, all)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
                 .Append(" % |\n");
            }
            b.Append('\n');
        }
    }

    // The wiki (WikiSite.cs): the start page, the hand-written developer pages, a page per area from its README, and
    // the indexes the code answers by itself.
    internal static class Wiki
    {
        // The pages into the folder; `source` is the address of the repository's files on GitHub, or null.
        public static void Write(string folder, Scan scan, string source)
        {
            new WikiSite(Sources.Root(), folder, scan, source).Write();
        }
    }
}
