using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DocLock
{
    // The lock: per element the fingerprints of its code and of its documentation when they were last
    // recorded, one line each (id, code, documentation), sorted. An element is dirty when its code's
    // fingerprint differs from the lock's and its documentation's does not: the code changed under
    // documentation that nobody looked at since.
    internal sealed class Lock
    {
        // Per element id, the fingerprints of its code and documentation as last recorded.
        private readonly Dictionary<string, (string code, string doc)> entries = new Dictionary<string, (string, string)>();

        // The ids the lock holds, for the elements gone from the code.
        public IEnumerable<string> Ids => entries.Keys;

        // Where the lock lies in the repository: .doclock/lock.tsv.
        public static string PathOf(string root) => Path.Combine(root, ".doclock", "lock.tsv");

        // The lock as written, or an empty one where there is none yet.
        public static Lock Load(string root)
        {
            var l = new Lock();
            string path = PathOf(root);
            if (!File.Exists(path)) return l;
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] parts = line.Split('\t');
                if (parts.Length >= 3) l.entries[parts[0]] = (parts[1], parts[2]);
            }
            return l;
        }

        // Whether the lock holds the element as it is now, code and documentation.
        public bool Holds(Element e) => entries.TryGetValue(e.Id, out var x) && x.code == e.CodeHash && x.doc == e.DocHash;

        // Whether the element is dirty: its code changed since it was recorded, its documentation not.
        public bool IsDirty(Element e) => entries.TryGetValue(e.Id, out var x) && x.code != e.CodeHash && x.doc == e.DocHash;

        // Every documented element that is not dirty as it is now: new ones, and those whose documentation was
        // edited with their code. Gone ones leave the lock.
        public static void Update(string root, Scan scan, Lock old)
        {
            var next = new Lock();
            int recorded = 0, dirty = 0, undocumented = 0;
            foreach (Element e in scan.Elements)
            {
                if (old.IsDirty(e))
                {
                    next.entries[e.Id] = old.entries[e.Id];
                    dirty++;
                    continue;
                }
                if (!Rules.Documented(e))
                {
                    undocumented++;
                    continue;
                }
                next.entries[e.Id] = (e.CodeHash, e.DocHash);
                recorded++;
            }
            next.Save(root);
            Console.WriteLine("Lock: " + recorded + " recorded, " + dirty + " still dirty (doclock confirm), " + undocumented + " undocumented.");
        }

        // The dirty elements whose id contains the part (or all, for "*") recorded as they are now: their
        // documentation was looked at and still holds.
        public static void Confirm(string root, Scan scan, Lock old, string part)
        {
            int confirmed = 0;
            foreach (Element e in scan.Elements)
            {
                if (!old.IsDirty(e)) continue;
                if (part != "*" && e.Id.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0) continue;
                old.entries[e.Id] = (e.CodeHash, e.DocHash);
                confirmed++;
            }
            old.Save(root);
            Console.WriteLine(confirmed + " confirmed.");
        }

        // The lock written back, sorted by id, with a header line.
        private void Save(string root)
        {
            string path = PathOf(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var b = new StringBuilder();
            b.Append("# The developer documentation's lock (tools/DocLock): id, code fingerprint, documentation fingerprint.\n");
            foreach (KeyValuePair<string, (string code, string doc)> entry in entries.OrderBy(x => x.Key, StringComparer.Ordinal))
                b.Append(entry.Key).Append('\t').Append(entry.Value.code).Append('\t').Append(entry.Value.doc).Append('\n');
            File.WriteAllText(path, b.ToString());
        }
    }

    // The coverage: per area, language and kind, how many elements there are, how many are documented, how
    // many of those are dirty; and the broken references.
    internal static class Report
    {
        // The coverage tables to the console and to .doclock/coverage.md.
        public static void Write(string root, Scan scan, Lock lockFile)
        {
            var b = new StringBuilder();
            b.Append("# Developer documentation coverage\n\n");
            b.Append("Written by tools/DocLock (`report`). ").Append(scan.Files.Count).Append(" files.\n\n");
            Totals(b, "Overall", scan.Elements.GroupBy(e => "all"), lockFile);
            Totals(b, "By area", scan.Elements.GroupBy(e => e.Area), lockFile);
            Totals(b, "By language", scan.Elements.GroupBy(e => e.Language), lockFile);
            Totals(b, "By kind", scan.Elements.GroupBy(e => e.Kind), lockFile);
            b.Append("Broken references: ").Append(scan.BrokenReferences.Count).Append("\n");
            string text = b.ToString();
            Console.Write(text);
            string path = Path.Combine(root, ".doclock", "coverage.md");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        // One table: per group its elements, how many are documented, the share, and how many of those are dirty.
        private static void Totals(StringBuilder b, string title, IEnumerable<IGrouping<string, Element>> groups, Lock lockFile)
        {
            b.Append("## ").Append(title).Append("\n\n| | elements | documented | coverage | dirty |\n|---|---:|---:|---:|---:|\n");
            foreach (IGrouping<string, Element> g in groups.OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                int all = g.Count();
                int documented = g.Count(Rules.Documented);
                int dirty = g.Count(e => Rules.Documented(e) && lockFile.IsDirty(e));
                b.Append("| ").Append(g.Key).Append(" | ").Append(all).Append(" | ").Append(documented).Append(" | ")
                 .Append((100.0 * documented / Math.Max(1, all)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
                 .Append(" % | ").Append(dirty).Append(" |\n");
            }
            b.Append('\n');
        }
    }

    // The documentation as a wiki (WikiSite.cs): the start page, the hand-written developer pages, a page per area
    // and per file made from the comments, and the indexes the code answers by itself.
    internal static class Wiki
    {
        // The pages into the folder; `source` is the address of the repository's files on GitHub, or null.
        public static void Write(string folder, Scan scan, Lock lockFile, string source)
        {
            new WikiSite(Sources.Root(), folder, scan, lockFile, source).Write();
        }
    }
}
