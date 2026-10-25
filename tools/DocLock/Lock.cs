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

    // The documentation as Markdown pages for a wiki: Home with the areas, a page per area with its files,
    // a page per file with each element, its kind, line and documentation.
    internal static class Wiki
    {
        // The pages into the folder: Home, one page per area, one per file.
        public static void Write(string folder, Scan scan, Lock lockFile, string source)
        {
            Directory.CreateDirectory(folder);
            int documented = scan.Elements.Count(Rules.Documented);
            var home = new StringBuilder("# ReDefinition developer documentation\n\nWritten from the code's comments by tools/DocLock: ")
                .Append(scan.Elements.Count).Append(" elements in ").Append(scan.Files.Count).Append(" files, ")
                .Append((100.0 * documented / Math.Max(1, scan.Elements.Count)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
                .Append(" % documented.\n\n");
            foreach (IGrouping<string, Element> area in scan.Elements.GroupBy(e => e.Area).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                string areaPage = Page(area.Key);
                home.Append("- [").Append(area.Key).Append("](").Append(areaPage).Append(")\n");
                var areaText = new StringBuilder("# ").Append(area.Key).Append("\n\n");
                foreach (IGrouping<string, Element> file in area.GroupBy(e => e.Path).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    string filePage = Page(file.Key);
                    Element head = file.FirstOrDefault(e => e.Kind == "script" || e.Kind == "module" || e.Kind == "shader") ?? file.First();
                    areaText.Append("- [").Append(file.Key).Append("](").Append(filePage).Append(") -- ")
                            .Append(Escape(FirstSentence(head.Doc))).Append('\n');
                    var fileText = new StringBuilder("# ").Append(file.Key).Append("\n\n");
                    if (source != null) fileText.Append("[Source](").Append(source).Append(file.Key).Append(")\n\n");
                    foreach (Element e in file)
                    {
                        string line = source != null ? "[line " + e.Line + "](" + source + e.Path + "#L" + e.Line + ")" : "line " + e.Line;
                        fileText.Append("### ").Append(e.Kind).Append(" `").Append(e.Name).Append(e.Signature.Length > 0 ? "(" + e.Signature + ")" : "")
                                .Append("`\n\n").Append(line).Append(lockFile.IsDirty(e) ? " -- **dirty**: the code changed since this was written" : "")
                                .Append("\n\n").Append(e.Doc.Length > 0 ? Escape(e.Doc) : e.SharesDoc ? "(with the comment of the run above)" : "(undocumented)")
                                .Append("\n\n");
                    }
                    File.WriteAllText(Path.Combine(folder, filePage + ".md"), fileText.ToString());
                }
                File.WriteAllText(Path.Combine(folder, areaPage + ".md"), areaText.ToString());
            }
            File.WriteAllText(Path.Combine(folder, "Home.md"), home.ToString());
            Console.WriteLine("Wiki pages in " + folder + ".");
        }

        // A comment's text for Markdown: its underscores, stars and angle brackets as written, not as markup.
        private static string Escape(string text)
        {
            return text.Replace("\\", "\\\\").Replace("_", "\\_").Replace("*", "\\*").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        // A page name for a path: slashes and spaces as dashes, which wiki links take.
        private static string Page(string name) => name.Replace('/', '-').Replace('\\', '-').Replace(' ', '-');

        // The first sentence of a documentation, for the area pages' file list.
        private static string FirstSentence(string doc)
        {
            string flat = Element.Normalise(doc);
            int stop = flat.IndexOf(". ", StringComparison.Ordinal);
            return stop > 0 ? flat.Substring(0, stop + 1) : flat;
        }
    }
}
