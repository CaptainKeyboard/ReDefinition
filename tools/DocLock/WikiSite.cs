using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DocLock
{
    // The wiki as a site and not a dump. It has a Home that says where to start, a sidebar and a footer.
    // The current hand-written pages of docs/development (start-here, architecture, flows, how-tos) are pages, and each area and file has a page made
    // from the comments. The indexes are the answers that the code gives by itself: entry points, hooks,
    // assumptions about other mods, switches, ini keys, registrations, exports, types and dependencies.
    internal sealed class WikiSite
    {
        // The repository's root, its files as git knows them, the elements, the lock, and the address of a file on GitHub.
        private readonly string root;
        private readonly Scan scan;
        private readonly string blob;
        private readonly string tree;
        private readonly string folder;

        // The hand-written pages under docs/development that go into the wiki, by file stem.
        private readonly Dictionary<string, string> hand = new Dictionary<string, string>(StringComparer.Ordinal);

        // The headings of the hand-written pages, by page name, for the sidebar.
        private readonly Dictionary<string, string> titles = new Dictionary<string, string>(StringComparer.Ordinal);

        // The pages written, by name, with the lines the sidebar and the footer need.
        private readonly List<string> written = new List<string>();

        // Prepares the site for the folder; nothing is written before Write.
        public WikiSite(string root, string folder, Scan scan, string blob)
        {
            this.root = root;
            this.folder = folder;
            this.scan = scan;
            this.blob = blob;
            tree = blob == null ? null : blob.Replace("/blob/main/", "/tree/main/");
        }

        // Writes every page into the folder.
        public void Write()
        {
            Directory.CreateDirectory(folder);
            foreach (string old in Directory.GetFiles(folder, "*.md")) File.Delete(old);
            ReadHandPages();
            WriteHandPages();
            WriteAreaPages();
            WriteIndexes();
            WriteHome();
            WriteSidebar();
            WriteFooter();
            Console.WriteLine("Wiki pages in " + folder + ": " + Directory.GetFiles(folder, "*.md").Length + ".");
        }

        // The hand-written developer pages of docs/development that go into the wiki. The page for writing pages and the
        // older explanations stay out. Home comes from start-here.md.
        private void ReadHandPages()
        {
            string dir = Path.Combine(root, "docs", "development");
            if (!Directory.Exists(dir)) return;
            foreach (string file in Directory.GetFiles(dir, "*.md"))
            {
                string stem = Path.GetFileNameWithoutExtension(file);
                // Only the pages written against the code as it is now; the older explanations stay in docs/ until they are.
                bool wanted = stem == "start-here" || stem == "architecture" || stem == "tools-index" || stem == "writing-code-comments"
                              || stem.StartsWith("flow-", StringComparison.Ordinal) || stem.StartsWith("howto-", StringComparison.Ordinal);
                if (!wanted) continue;
                hand[stem] = File.ReadAllText(file);
                Match heading = Regex.Match(hand[stem], @"^# (.+?)?$", RegexOptions.Multiline);
                if (heading.Success) titles[stem == "start-here" ? "Home" : Title(stem)] = heading.Groups[1].Value;
            }
        }

        // Each hand-written page as a wiki page, its links turned into wiki links and its file paths into links
        // to the repository.
        private void WriteHandPages()
        {
            foreach (var page in hand)
            {
                string name = page.Key == "start-here" ? "Home" : Title(page.Key);
                Save(name, Convert(page.Value, page.Key));
            }
        }

        // The page name of a file stem: words with capitals, as the wiki lists them (flow-frame becomes Flow-frame).
        private static string Title(string stem)
        {
            return char.ToUpperInvariant(stem[0]) + stem.Substring(1);
        }

        // Page text turned for the wiki: a relative link to another page becomes the page's name, a link to
        // another file of the repository a link to GitHub, and a path in code font a link to the file.
        private string Convert(string text, string stem)
        {
            text = text.Replace("\r\n", "\n");
            text = Regex.Replace(text, @"\]\(([A-Za-z0-9_\-]+)\.md(#[^)]*)?\)", m =>
            {
                string target = m.Groups[1].Value == "start-here" ? "Home" : Title(m.Groups[1].Value);
                return hand.ContainsKey(m.Groups[1].Value) ? "](" + target + m.Groups[2].Value + ")" : "](" + Repo("docs/development/" + m.Groups[1].Value + ".md") + ")";
            });
            text = Regex.Replace(text, @"\]\(((?:\.\./)+[^)#]+?)(#[^)]*)?\)", m =>
            {
                string relative = m.Groups[1].Value;
                string path = Path.GetFullPath(Path.Combine(root, "docs", "development", relative)).Substring(root.Length + 1).Replace('\\', '/');
                return "](" + Repo(path) + m.Groups[2].Value + ")";
            });
            if (blob != null)
                text = Regex.Replace(text, @"`((?:src|tools|tests|unity|GameData|docs)/[A-Za-z0-9_./\-]+)`", m =>
                {
                    string path = m.Groups[1].Value.TrimEnd('/');
                    string full = Path.Combine(root, path);
                    if (!File.Exists(full) && !Directory.Exists(full)) return m.Value;
                    return "[`" + m.Groups[1].Value + "`](" + Repo(path) + ")";
                });
            return text;
        }

        // The address of a repository path on GitHub: a folder under tree, a file under blob.
        private string Repo(string path)
        {
            if (blob == null) return path;
            return (Directory.Exists(Path.Combine(root, path)) ? tree : blob) + path;
        }

        // A page name for a path: slashes and spaces as dashes, which wiki links take.
        private static string Page(string name) => name.Replace('/', '-').Replace('\\', '-').Replace(' ', '-');

        // Writes a page and remembers its name.
        private void Save(string name, string text)
        {
            File.WriteAllText(Path.Combine(folder, name + ".md"), text.Replace("\r\n", "\n"), new UTF8Encoding(false));
            Match heading = Regex.Match(text, @"^# (.+?)\r?$", RegexOptions.Multiline);
            if (heading.Success && !titles.ContainsKey(name)) titles[name] = heading.Groups[1].Value;
            written.Add(name);
        }

        // A comment's text for Markdown: its underscores, stars and angle brackets as written, not as markup.
        private static string Escape(string text)
        {
            return text.Replace("\\", "\\\\").Replace("_", "\\_").Replace("*", "\\*").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        // The first sentence of a comment.
        private static string First(string doc)
        {
            string flat = Element.Normalise(doc);
            int stop = flat.IndexOf(". ", StringComparison.Ordinal);
            return stop > 0 ? flat.Substring(0, stop + 1) : flat;
        }


        // One page per area that has a README: the README, then the areas it uses and the areas that use it.
        private void WriteAreaPages()
        {
            var uses = AreaEdges();
            foreach (string area in scan.Elements.Select(e => e.Area).Distinct().OrderBy(a => a, StringComparer.Ordinal))
            {
                string readme = Path.Combine(root, area.Replace('/', Path.DirectorySeparatorChar), "README.md");
                if (!File.Exists(readme)) continue;
                var text = new StringBuilder(Convert(File.ReadAllText(readme), "").TrimEnd()).Append("\n\n");
                if (uses.TryGetValue(area, out var edges))
                {
                    if (edges.uses.Count > 0) text.Append("**Uses:** ").Append(string.Join(", ", edges.uses.Where(HasPage).Select(AreaLink))).Append("\n\n");
                    if (edges.usedBy.Count > 0) text.Append("**Used by:** ").Append(string.Join(", ", edges.usedBy.Where(HasPage).Select(AreaLink))).Append("\n\n");
                }
                text.Append("[The folder on GitHub](").Append(Repo(area)).Append(")\n");
                Save(Page(area), text.ToString());
            }
        }

        // Whether an area has a page: it has a README.
        private bool HasPage(string area)
        {
            return File.Exists(Path.Combine(root, area.Replace('/', Path.DirectorySeparatorChar), "README.md"));
        }

        // A link to an area's page, by the area's name.
        private static string AreaLink(string area)
        {
            return "[" + (area == "src" ? "add-on" : area.Replace("src/", "")) + "](" + Page(area) + ")";
        }

        // The areas each area uses and is used by, from the usings and the qualified names of the C# sources.
        private Dictionary<string, (List<string> uses, List<string> usedBy)> AreaEdges()
        {
            var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var qualified = new Regex(@"(?<![\w.])ReDefinition\.(\w+)\.[A-Z]");
            var usingOurs = new Regex(@"^using ReDefinition\.(\w+)", RegexOptions.Multiline);
            foreach (string file in scan.Files.Where(f => f.StartsWith("src/", StringComparison.Ordinal) && f.EndsWith(".cs", StringComparison.Ordinal)))
            {
                string area = new Element { Path = file }.Area;
                string text = File.ReadAllText(Path.Combine(root, file));
                foreach (Match m in usingOurs.Matches(text).Cast<Match>().Concat(qualified.Matches(text).Cast<Match>()))
                {
                    string used = "src/" + m.Groups[1].Value;
                    if (used == area) continue;
                    if (!edges.TryGetValue(area, out HashSet<string> set)) edges[area] = set = new HashSet<string>();
                    set.Add(used);
                }
            }
            var known = new HashSet<string>(scan.Elements.Select(e => e.Area));
            var result = new Dictionary<string, (List<string>, List<string>)>();
            foreach (string area in known)
            {
                var uses = edges.TryGetValue(area, out HashSet<string> u) ? u.Where(known.Contains).OrderBy(x => x, StringComparer.Ordinal).ToList() : new List<string>();
                var usedBy = edges.Where(kv => kv.Value.Contains(area)).Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
                result[area] = (uses, usedBy);
            }
            return result;
        }

        // The indexes the code answers by itself.
        private void WriteIndexes()
        {
            Save("Index-entry-points", EntryPoints());
            Save("Index-harmony-hooks", HarmonyHooks());
            Save("Index-foreign-assumptions", ForeignAssumptions());
            Save("Index-command-line-switches", Switches());
            Save("Index-proxy-ini-keys", IniKeys());
            Save("Index-registrations", Registrations());
            Save("Index-proxy-exports", Exports());
            Save("Index-dependencies", Dependencies());
            string glossary = Path.Combine(root, "docs", "glossary.md");
            if (File.Exists(glossary)) Save("Glossary", Convert(File.ReadAllText(glossary), ""));
        }

        // The source text of a file, with its lines.
        private string[] Lines(string path) => File.ReadAllText(Path.Combine(root, path)).Replace("\r\n", "\n").Split('\n');

        // Every KSPAddon class, by the scene it starts in.
        private string EntryPoints()
        {
            var rows = new List<(string scene, string persists, string type, string file, int line)>();
            var addon = new Regex(@"\[KSPAddon\(KSPAddon\.Startup\.(\w+),\s*(true|false)\)\]");
            var declaration = new Regex(@"\bclass\s+(\w+)");
            foreach (string file in scan.Files.Where(f => f.EndsWith(".cs", StringComparison.Ordinal) && f.StartsWith("src/", StringComparison.Ordinal)))
            {
                string[] lines = Lines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    Match m = addon.Match(lines[i]);
                    if (!m.Success) continue;
                    for (int j = i; j < Math.Min(lines.Length, i + 6); j++)
                    {
                        Match c = declaration.Match(lines[j]);
                        if (c.Success) { rows.Add((m.Groups[1].Value, m.Groups[2].Value == "true" ? "once" : "per scene", c.Groups[1].Value, file, j + 1)); break; }
                    }
                }
            }
            var text = new StringBuilder("# Entry points\n\nThe game starts these classes: each has a `KSPAddon` attribute. `once` means the instance lives for the whole run; `per scene` means the game makes a new one whenever it enters the scene.\n\n");
            text.Append("| Scene | Lifetime | Class | File |\n|---|---|---|---|\n");
            foreach (var r in rows.OrderBy(r => r.scene, StringComparer.Ordinal).ThenBy(r => r.type, StringComparer.Ordinal))
                text.Append("| ").Append(r.scene).Append(" | ").Append(r.persists).Append(" | `").Append(r.type).Append("` | ")
                    .Append(Link(r.file, r.line)).Append(" |\n");
            text.Append("\nThe native proxy's entry points are the exports of [the proxy](Index-proxy-exports) and `DllMain` in ")
                .Append(Link("src/DxgiProxy/SwapChain/DxgiExports.cpp", 0)).Append(".\n");
            return text.ToString();
        }

        // A link to a source file, to a line of it when given.
        private string Link(string path, int line)
        {
            string label = "`" + path + (line > 0 ? ":" + line : "") + "`";
            return blob == null ? label : "[" + label + "](" + blob + path + (line > 0 ? "#L" + line : "") + ")";
        }

        // Every place that installs a Harmony patch through HarmonyHooks.Install.
        private string HarmonyHooks()
        {
            var text = new StringBuilder("# Harmony hooks\n\nEvery patch of the game's or another mod's code goes through `HarmonyHooks.Install` (`src/Core/HarmonyHooks.cs`), which installs all of a class's hooks or none. Each row is a place that installs hooks, with the patch's id.\n\n| Patch id | Installed by | File |\n|---|---|---|\n");
            var install = new Regex(@"HarmonyHooks\.Install\(\s*(""[^""]*""(?:\s*\+\s*[^,]+)?),\s*typeof\((\w+)\)");
            var rows = new List<(string id, string type, string file, int line)>();
            foreach (string file in scan.Files.Where(f => f.EndsWith(".cs", StringComparison.Ordinal) && f.StartsWith("src/", StringComparison.Ordinal)))
            {
                string source = File.ReadAllText(Path.Combine(root, file));
                foreach (Match m in install.Matches(source))
                    rows.Add((m.Groups[1].Value.Trim('"'), m.Groups[2].Value, file, source.Substring(0, m.Index).Count(c => c == '\n') + 1));
            }
            foreach (var r in rows.OrderBy(r => r.id, StringComparer.Ordinal))
                text.Append("| `").Append(r.id).Append("` | `").Append(r.type).Append("` | ").Append(Link(r.file, r.line)).Append(" |\n");
            return text.ToString();
        }

        // The assumptions about other mods' code that the catalogue ForeignAssumptions holds.
        private string ForeignAssumptions()
        {
            var text = new StringBuilder("# Assumptions about other mods\n\nWhat ReDefinition takes for granted about code it does not own. `tools/checks/check_foreign_code.ps1` verifies each against the installed build, and the game logs the ones that do not hold.\n\n| Id | Mod | What is assumed |\n|---|---|---|\n");
            string path = "src/Core/ForeignAssumptions.cs";
            if (File.Exists(Path.Combine(root, path)))
            {
                string source = File.ReadAllText(Path.Combine(root, path));
                foreach (Match m in Regex.Matches(source, @"new ForeignAssumption\(\s*""([^""]+)"",\s*""([^""]+)"",\s*((?:""(?:[^""\\]|\\.)*""\s*\+?\s*)+)"))
                {
                    string description = string.Concat(Regex.Matches(m.Groups[3].Value, @"""((?:[^""\\]|\\.)*)""").Cast<Match>().Select(x => x.Groups[1].Value));
                    text.Append("| `").Append(m.Groups[1].Value).Append("` | ").Append(m.Groups[2].Value).Append(" | ").Append(Escape(description).Replace("|", "\\|")).Append(" |\n");
                }
            }
            return text.ToString();
        }

        // Every -redefinition switch the code reads, with where it is read.
        private string Switches()
        {
            var found = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            var literal = new Regex(@"""(-redefinition[A-Za-z]+)""");
            foreach (string file in scan.Files.Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".cpp", StringComparison.Ordinal)))
            {
                string source = File.ReadAllText(Path.Combine(root, file));
                foreach (Match m in literal.Matches(source))
                {
                    if (!found.TryGetValue(m.Groups[1].Value, out List<string> files)) found[m.Groups[1].Value] = files = new List<string>();
                    string where = file + ":" + (source.Substring(0, m.Index).Count(c => c == '\n') + 1);
                    if (!files.Any(f => f.StartsWith(file + ":", StringComparison.Ordinal))) files.Add(where);
                }
            }
            var text = new StringBuilder("# Command-line switches\n\nSwitches for probes, profilers and test scenes. A player never gives them. `CommandLine` (`src/Core/CommandLine.cs`) reads them.\n\n| Switch | Read in |\n|---|---|\n");
            foreach (var s in found)
            {
                text.Append("| `").Append(s.Key).Append("` | ");
                text.Append(string.Join(", ", s.Value.Take(3).Select(w => Link(w.Substring(0, w.LastIndexOf(':')), int.Parse(w.Substring(w.LastIndexOf(':') + 1))))));
                if (s.Value.Count > 3) text.Append(" and ").Append(s.Value.Count - 3).Append(" more");
                text.Append(" |\n");
            }
            return text.ToString();
        }

        // The keys of ReDefinitionProxy.ini with their defaults and comments.
        private string IniKeys()
        {
            var text = new StringBuilder("# Proxy ini keys\n\n`ReDefinitionProxy.ini` next to the game's executable. The proxy writes it with these defaults and re-reads the keys marked live while the game runs. The proxy's reader is `src/DxgiProxy/Common/Config.cpp`.\n\n| Key | Default | What it does |\n|---|---|---|\n");
            foreach (Element e in scan.Elements.Where(e => e.Language == "ini" && e.Kind == "key"))
            {
                string value = e.Code.Contains('=') ? e.Code.Substring(e.Code.IndexOf('=') + 1).Trim() : "";
                text.Append("| `").Append(e.Name).Append("` | `").Append(value).Append("` | ").Append(Escape(First(e.Doc)).Replace("|", "\\|")).Append(" |\n");
            }
            return text.ToString();
        }

        // The mod registrations under GameData/ReDefinition/Mods: one row per mod, with its settings counted.
        private string Registrations()
        {
            var text = new StringBuilder("# Registrations\n\nEach file under `GameData/ReDefinition/Mods` registers one mod's settings with ReDefinition. The format is in [the registration reference](" + Repo("docs/modders/registration-reference.md") + ").\n\n| Mod | Settings | File | What it covers |\n|---|---|---|---|\n");
            foreach (var file in scan.Elements.Where(e => e.Language == "cfg" && e.Path.StartsWith("GameData/ReDefinition/Mods/", StringComparison.Ordinal)).GroupBy(e => e.Path))
            {
                Element top = file.FirstOrDefault(e => e.Kind == "config");
                int settings = file.Count(e => e.Kind == "node" && e.Name.Contains("/SETTING["));
                text.Append("| `").Append(Path.GetFileNameWithoutExtension(file.Key)).Append("` | ").Append(settings).Append(" | ")
                    .Append(Link(file.Key, 0)).Append(" | ").Append(Escape(First(top?.Doc ?? "")).Replace("|", "\\|")).Append(" |\n");
            }
            return text.ToString();
        }

        // The functions the managed side calls in the proxy.
        private string Exports()
        {
            var text = new StringBuilder("# Proxy exports\n\nThe functions of `dxgi.dll` that the mod calls (`src/Proxy` declares each with `DllImport`) and the exports Windows loads it for.\n\n| Function | What it does | File |\n|---|---|---|\n");
            foreach (Element e in scan.Elements.Where(e => e.Language == "cpp" && e.Kind == "function" && e.Path.EndsWith("ManagedBridge.cpp", StringComparison.Ordinal)
                                                          && e.Name.Contains("::") == false && e.Name.StartsWith("Ksp", StringComparison.Ordinal)))
                text.Append("| `").Append(e.Name).Append("` | ").Append(Escape(First(e.Doc)).Replace("|", "\\|")).Append(" | ").Append(Link(e.Path, e.Line)).Append(" |\n");
            text.Append("\nThe DXGI exports are listed in `src/DxgiProxy/ReDefinitionProxy.def`; each forwards to the system's library.\n");
            return text.ToString();
        }

        // The areas and what each uses.
        private string Dependencies()
        {
            var text = new StringBuilder("# Dependencies between areas\n\nFrom the usings and the qualified names of the C# sources. `tests/ReDefinition.Tests/ArchitectureTests.cs` holds this table: a dependency that is not in it fails the tests.\n\n| Area | Uses | Used by |\n|---|---|---|\n");
            foreach (var area in AreaEdges().Where(a => a.Key.StartsWith("src/", StringComparison.Ordinal) && !a.Key.StartsWith("src/DxgiProxy", StringComparison.Ordinal)).OrderBy(a => a.Key, StringComparer.Ordinal))
                text.Append("| ").Append(AreaLink(area.Key)).Append(" | ").Append(string.Join(", ", area.Value.uses.Select(AreaLink))).Append(" | ")
                    .Append(string.Join(", ", area.Value.usedBy.Select(AreaLink))).Append(" |\n");
            return text.ToString();
        }

        // Home: the start-here page, or a list when there is none.
        private void WriteHome()
        {
            if (written.Contains("Home")) return;
            var home = new StringBuilder("# ReDefinition developer documentation\n\nWritten from the code's comments by tools/DocLock.\n\n");
            foreach (string name in written.Where(n => n.StartsWith("src-", StringComparison.Ordinal) && n.Count(c => c == '-') == 1).OrderBy(n => n, StringComparer.Ordinal))
                home.Append("- [").Append(name).Append("](").Append(name).Append(")\n");
            Save("Home", home.ToString());
        }

        // The sidebar every page shows: where to start, the flows, the how-tos, the indexes and the areas.
        private void WriteSidebar()
        {
            var side = new StringBuilder("**[Start here](Home)**\n\n");
            Section(side, "Understand", n => n == "Architecture" || n.StartsWith("Flow-", StringComparison.Ordinal));
            Section(side, "Do", n => n.StartsWith("Howto-", StringComparison.Ordinal) || n == "Tools-index" || n == "Writing-code-comments");
            Section(side, "Look up", n => n.StartsWith("Index-", StringComparison.Ordinal) || n == "Glossary");
            side.Append("**Code by area**\n\n");
            side.Append("- [The add-on](src)\n");
            foreach (string name in written.Where(n => n.StartsWith("src-", StringComparison.Ordinal) && n.Count(c => c == '-') == 1 && !n.Contains(".")).OrderBy(n => n, StringComparer.Ordinal))
            {
                side.Append("- [").Append(name.Substring(4)).Append("](").Append(name).Append(")\n");
                if (name != "src-DxgiProxy") continue;
                foreach (string sub in written.Where(n => n.StartsWith("src-DxgiProxy-", StringComparison.Ordinal) && n.Count(c => c == '-') == 2 && !n.Contains(".")).OrderBy(n => n, StringComparer.Ordinal))
                    side.Append("  - [").Append(sub.Substring(14)).Append("](").Append(sub).Append(")\n");
            }
            foreach (string name in written.Where(n => (n.StartsWith("tests-", StringComparison.Ordinal) || n.StartsWith("tools", StringComparison.Ordinal) || n.StartsWith("unity-", StringComparison.Ordinal)
                                                       || n.StartsWith("GameData-", StringComparison.Ordinal)) && !n.Contains(".")).OrderBy(n => n, StringComparer.Ordinal))
                side.Append("- [").Append(name).Append("](").Append(name).Append(")\n");
            Save("_Sidebar", side.ToString());
        }

        // The order of the pages the sidebar lists first: the flows in the order to read them.
        private static readonly string[] ReadingOrder =
        {
            "Architecture", "Flow-startup", "Flow-setting", "Flow-game-ui", "Flow-frame", "Flow-native-channels", "Flow-mod-interface",
            "Flow-proxy-present", "Flow-raytracing", "Flow-shaders", "Flow-dashboard",
            "Howto-add-a-setting", "Howto-add-a-proxy-feature", "Howto-build-and-check", "Tools-index", "Writing-code-comments",
        };

        // A page's place in the reading order; the pages that are not in it come after, by name.
        private static int Rank(string name)
        {
            int at = Array.IndexOf(ReadingOrder, name);
            return at >= 0 ? at : ReadingOrder.Length;
        }

        // A sidebar section: a heading and a link to each page that fits.
        private void Section(StringBuilder side, string heading, Func<string, bool> fits)
        {
            var pages = written.Where(fits).OrderBy(n => Rank(n)).ThenBy(n => n, StringComparer.Ordinal).ToList();
            if (pages.Count == 0) return;
            side.Append("**").Append(heading).Append("**\n\n");
            foreach (string name in pages) side.Append("- [").Append(titles.TryGetValue(name, out string title) ? title : name.Replace('-', ' ')).Append("](").Append(name).Append(")\n");
            side.Append('\n');
        }

        // The footer: where the pages come from and how much they cover.
        private void WriteFooter()
        {
            int documented = scan.Elements.Count(Rules.Documented);
            string commit = Git("rev-parse --short HEAD");
            Save("_Footer", "---\nGenerated from the code's comments at `" + commit + "`: " + scan.Elements.Count + " elements in " + scan.Files.Count + " files, "
                 + (100.0 * documented / Math.Max(1, scan.Elements.Count)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                 + " % documented. Change the comments or `docs/development`, not these pages.\n");
        }

        // The output of a git command in the repository, or "" where git fails.
        private string Git(string arguments)
        {
            try
            {
                var info = new ProcessStartInfo("git", arguments) { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false };
                using (Process git = Process.Start(info))
                {
                    string output = git.StandardOutput.ReadToEnd().Trim();
                    git.WaitForExit();
                    return output;
                }
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}
