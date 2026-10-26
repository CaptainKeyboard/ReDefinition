using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DocLock
{
    // The developer documentation's check. The documentation of this project's code is in the code: a comment
    // directly above every element (or after it on its line, or above the run of fields it stands in). This
    // tool finds every element, says which are undocumented, and holds in a lock (.doclock/lock.tsv) a
    // fingerprint of each element's code and of its documentation, so that a change to the code that leaves
    // its documentation as it was marks the documentation dirty, until it is fixed or confirmed.
    //
    //   dotnet build tools/DocLock -c Release, then: dotnet <its DocLock.dll> <command> [options]
    //
    //   report               coverage per area, language and kind, to the console and .doclock/coverage.md
    //   check                fails (exit 1) on undocumented, dirty or unlocked elements, broken references and new lint findings
    //   update               records every documented element that is not dirty; dirty ones stay dirty
    //   confirm <part of id> confirms the dirty elements whose id contains it ("*" for all): the documentation
    //                        still holds for the changed code
    //   list [area|path]     the undocumented, dirty and unlocked elements, file:line
    //   refs                 the comments' references that name nothing that exists
    //   lint [rule]          the comments the contract finds fault with: counts per rule, or the findings of one rule
    //   lint-baseline        accepts the findings of the comments as they stand (.doclock/lint-baseline.tsv)
    //   wiki <folder>        the documentation as Markdown pages, one per file, with an index per area
    //
    // What is left out, and why, is in Sources.Excluded.
    public static class Program
    {
        // Runs one command on the repository the working folder lies in; 0 for success, 1 for a failed check, 2 for a
        // wrong call.
        public static int Main(string[] args)
        {
            string root = Sources.Root();
            if (root == null)
            {
                Console.Error.WriteLine("Not inside the repository.");
                return 2;
            }
            string command = args.Length > 0 ? args[0] : "report";
            string argument = args.Length > 1 ? args[1] : null;
            var scan = Scan.Run(root);
            var lockFile = Lock.Load(root);
            switch (command)
            {
                case "report":
                    Report.Write(root, scan, lockFile);
                    return 0;
                case "check":
                    return Check(root, scan, lockFile);
                case "update":
                    Lock.Update(root, scan, lockFile);
                    return 0;
                case "confirm":
                    if (argument == null) { Console.Error.WriteLine("confirm <part of an id, or *>"); return 2; }
                    Lock.Confirm(root, scan, lockFile, argument);
                    return 0;
                case "list":
                    List(scan, lockFile, argument);
                    return 0;
                case "refs":
                    foreach (string broken in scan.BrokenReferences) Console.WriteLine(broken);
                    Console.WriteLine(scan.BrokenReferences.Count + " broken references.");
                    return 0;
                case "lint":
                    LintReport(scan, lockFile, argument);
                    return 0;
                case "lint-baseline":
                    LintBaseline.Write(root, Lint.All(scan.Elements.Where(Rules.Documented)));
                    Console.WriteLine("Baseline written.");
                    return 0;
                case "wiki":
                    Wiki.Write(argument ?? Path.Combine(root, ".doclock", "wiki"), scan, lockFile, Sources.Browse(root));
                    return 0;
                default:
                    Console.Error.WriteLine("Commands: report, check, update, confirm, list, refs, wiki, lint, lint-baseline.");
                    return 2;
            }
        }

        // The check: counts, the first offenders of each kind, and 1 where anything is undocumented, dirty, unlocked,
        // stale or broken.
        private static int Check(string root, Scan scan, Lock lockFile)
        {
            List<Element> undocumented = scan.Elements.Where(e => !Rules.Documented(e)).ToList();
            List<Element> dirty = scan.Elements.Where(e => Rules.Documented(e) && lockFile.IsDirty(e)).ToList();
            List<Element> unlocked = scan.Elements.Where(e => Rules.Documented(e) && !lockFile.IsDirty(e) && !lockFile.Holds(e)).ToList();
            int stale = lockFile.Ids.Count(id => !scan.ById.ContainsKey(id));
            var baseline = new LintBaseline(root);
            var fresh = Lint.All(scan.Elements.Where(Rules.Documented)).Where(f => !baseline.Knows(f.element, f.finding)).ToList();
            Console.WriteLine(scan.Elements.Count + " elements: " + undocumented.Count + " undocumented, " + dirty.Count + " dirty, "
                              + unlocked.Count + " not in the lock, " + stale + " gone from the code but in the lock, "
                              + scan.BrokenReferences.Count + " broken references, " + fresh.Count + " new comment lint findings.");
            foreach (Element e in undocumented.Take(40)) Console.WriteLine("  undocumented  " + e.Path + ":" + e.Line + "  " + e.Kind + " " + e.Name);
            foreach (Element e in dirty.Take(40)) Console.WriteLine("  dirty         " + e.Path + ":" + e.Line + "  " + e.Kind + " " + e.Name);
            foreach (Element e in unlocked.Take(20)) Console.WriteLine("  not locked    " + e.Path + ":" + e.Line + "  " + e.Kind + " " + e.Name);
            foreach (string r in scan.BrokenReferences.Take(20)) Console.WriteLine("  reference     " + r);
            foreach (var f in fresh.Take(20)) Console.WriteLine("  lint          " + f.element.Path + ":" + f.element.Line + "  " + f.element.Kind + " " + f.element.Name + "  (" + f.finding.Rule + ": " + f.finding.Detail + ")");
            bool ok = undocumented.Count == 0 && dirty.Count == 0 && unlocked.Count == 0 && stale == 0 && scan.BrokenReferences.Count == 0 && fresh.Count == 0;
            if (!ok)
                Console.WriteLine("Document what is undocumented; fix or confirm (doclock confirm <id>) what is dirty; then doclock update.");
            return ok ? 0 : 1;
        }

        // The comment lint's findings: the count per rule and, given a rule, each of its findings with the file and line.
        private static void LintReport(Scan scan, Lock lockFile, string rule)
        {
            var findings = Lint.All(scan.Elements.Where(Rules.Documented));
            if (rule == null)
            {
                foreach (var group in findings.GroupBy(f => f.finding.Rule).OrderByDescending(g => g.Count()))
                    Console.WriteLine(group.Count().ToString().PadLeft(6) + "  " + group.Key);
                Console.WriteLine(findings.Select(f => f.element.Id).Distinct().Count() + " elements of " + scan.Elements.Count + " with a finding.");
                return;
            }
            foreach (var f in findings.Where(f => f.finding.Rule == rule))
                Console.WriteLine(f.element.Path + ":" + f.element.Line + "\t" + f.element.Kind + " " + f.element.Name + "\t" + f.finding.Detail);
        }

        // The elements that need attention, under a path prefix or in an area, one per line: file:line, state, kind,
        // name.
        private static void List(Scan scan, Lock lockFile, string filter)
        {
            foreach (Element e in scan.Elements)
            {
                if (filter != null && !e.Path.StartsWith(filter, StringComparison.OrdinalIgnoreCase) && e.Area != filter) continue;
                string state = !Rules.Documented(e) ? "undocumented" : lockFile.IsDirty(e) ? "dirty" : !lockFile.Holds(e) ? "not locked" : null;
                if (state == null) continue;
                Console.WriteLine(e.Path + ":" + e.Line + "\t" + state + "\t" + e.Kind + "\t" + e.Name + (e.Signature.Length > 0 ? "(" + e.Signature + ")" : ""));
            }
        }
    }

    // Which files are this project's code.
    internal static class Sources
    {
        // Left out: code of others that is vendored or adapted with its own documentation (the proxy's
        // headers of AMD, NVIDIA, Intel and Microsoft; AMD's FidelityFX shaders and FSR3Unity's port of
        // them), copies (the test player's linked sources, which the build copies from src), what Unity or
        // the build generates, and the documentation itself.
        public static readonly string[] Excluded =
        {
            "src/DxgiProxy/extern/",
            "third_party/",
            "tests/RaytracingPlayer/Assets/Linked/",
            "tests/RaytracingPlayer/Library/",
            "unity/Library/",
            "unity/Assets/ReDefinition/Shaders/ffx_",
            "unity/Assets/ReDefinition/Shaders/shaders/",
            "src/Fsr3/",
            "docs/",
            "/obj/",
            "/bin/",
            "__pycache__",
            ".doclock/",
        };

        // The source files scanned, by extension.
        private static readonly string[] Extensions = { ".cs", ".cpp", ".h", ".hlsl", ".hlsli", ".shader", ".compute", ".cginc", ".py", ".ps1", ".js", ".ini", ".cfg" };

        // Where a file of the repository is shown on GitHub: the origin's address with blob/main/, or null where
        // the origin is not GitHub's.
        public static string Browse(string root)
        {
            var info = new ProcessStartInfo("git", "remote get-url origin") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false };
            using (Process git = Process.Start(info))
            {
                string url = git.StandardOutput.ReadToEnd().Trim();
                git.WaitForExit();
                if (!url.StartsWith("https://github.com/", StringComparison.Ordinal)) return null;
                if (url.EndsWith(".git", StringComparison.Ordinal)) url = url.Substring(0, url.Length - 4);
                return url + "/blob/main/";
            }
        }

        // The repository's root: the working folder or the first folder above it with a .git.
        public static string Root()
        {
            string dir = Directory.GetCurrentDirectory();
            while (dir != null && !Directory.Exists(Path.Combine(dir, ".git"))) dir = Path.GetDirectoryName(dir);
            return dir;
        }

        // The repository's files as git knows them, tracked or new and not ignored.
        public static List<string> Files(string root)
        {
            var info = new ProcessStartInfo("git", "ls-files --cached --others --exclude-standard")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            var result = new List<string>();
            using (Process git = Process.Start(info))
            {
                string line;
                while ((line = git.StandardOutput.ReadLine()) != null)
                {
                    string path = line.Replace('\\', '/');
                    if (!Extensions.Contains(Path.GetExtension(path).ToLowerInvariant())) continue;
                    if (Excluded.Any(x => path.StartsWith(x, StringComparison.Ordinal) || x.StartsWith("/") && path.Contains(x) || x == "__pycache__" && path.Contains(x)))
                        continue;
                    if (!File.Exists(Path.Combine(root, path))) continue;
                    result.Add(path);
                }
                git.WaitForExit();
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }
    }

    // Every element of the code, and the references in their documentation that name nothing.
    internal sealed class Scan
    {
        // Every element found, in file order; by id; the broken references; the files scanned.
        public readonly List<Element> Elements = new List<Element>();
        public readonly Dictionary<string, Element> ById = new Dictionary<string, Element>();
        public readonly List<string> BrokenReferences = new List<string>();
        public readonly List<string> Files = new List<string>();

        // Scans every source file, shares documentation across partial types and C++ declarations, numbers ids that
        // come twice, and checks the references.
        public static Scan Run(string root)
        {
            var scan = new Scan();
            var index = new CSharpIndex();
            var allFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var codeText = new StringBuilder();
            foreach (string path in Sources.Files(root))
            {
                string text = File.ReadAllText(Path.Combine(root, path));
                scan.Files.Add(path);
                string ext = Path.GetExtension(path).ToLowerInvariant();
                switch (ext)
                {
                    case ".cs": CSharpScanner.Scan(path, text, scan.Elements, index); codeText.Append(text); break;
                    case ".cpp": case ".h": CLikeScanner.Scan(path, "cpp", text, scan.Elements); codeText.Append(text); break;
                    case ".hlsl": case ".hlsli": case ".compute": case ".cginc": CLikeScanner.Scan(path, "hlsl", text, scan.Elements); break;
                    case ".shader": CLikeScanner.ScanShaderLab(path, text, scan.Elements); break;
                    case ".py": ScriptScanner.Python(path, text, scan.Elements); codeText.Append(text); break;
                    case ".ps1": ScriptScanner.PowerShell(path, text, scan.Elements); codeText.Append(text); break;
                    case ".js": ScriptScanner.JavaScript(path, text, scan.Elements); break;
                    case ".ini": ConfigScanner.Ini(path, text, scan.Elements); break;
                    case ".cfg": ConfigScanner.Cfg(path, text, scan.Elements); break;
                }
            }
            index.SharePartials();
            ShareDeclarations(scan.Elements);
            // Ids twice over (the same overload in #if branches, a field in two partial parts): numbered.
            var seen = new Dictionary<string, int>();
            foreach (Element e in scan.Elements)
            {
                string id = e.Id;
                if (seen.TryGetValue(id, out int n))
                {
                    seen[id] = n + 1;
                    e.Signature += "#" + (n + 1);
                }
                else seen[id] = 1;
                scan.ById[e.Id] = e;
            }
            var gitFiles = new ProcessStartInfo("git", "ls-files") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false };
            using (Process git = Process.Start(gitFiles))
            {
                string line;
                while ((line = git.StandardOutput.ReadLine()) != null) allFiles.Add(line.Replace('\\', '/'));
            }
            References.Check(scan, index, allFiles, codeText.ToString());
            return scan;
        }

        // A C++ function documented at its declaration or at its definition is documented at both.
        private static void ShareDeclarations(List<Element> elements)
        {
            foreach (IGrouping<string, Element> group in elements.Where(e => e.Language == "cpp" && e.Kind == "function").GroupBy(e => Last(e.Name, 2)))
            {
                if (!group.Any(e => Rules.Enough(e.Doc, e.Name))) continue;
                foreach (Element e in group)
                    if (!Rules.Enough(e.Doc, e.Name)) e.DocumentedElsewhere = true;
            }
        }

        // The last parts of a qualified name: a definition outside its namespace block names fewer of them.
        private static string Last(string name, int parts)
        {
            string[] split = name.Split(new[] { "::" }, StringSplitOptions.None);
            return string.Join("::", split.Skip(Math.Max(0, split.Length - parts)));
        }
    }

    // The references a comment makes that can be checked: a path of this repository, a command line switch
    // of the probes and tools, a member of one of our C# types (Type.Member).
    internal static class References
    {
        // A path of this repository, a probe or tool switch, and Type.Member in a comment.
        private static readonly Regex RepoPath = new Regex(@"(?<![\w/])((?:src|tools|tests|unity|GameData)/[\w./-]+\.\w+)");
        private static readonly Regex Switch = new Regex(@"(?<![\w-])(-redefinition[A-Za-z]+)");
        private static readonly Regex Member = new Regex(@"\b([A-Z]\w+)\.([A-Z]\w+)\b");

        // The switches as the code reads them: in quotes.
        private static readonly Regex QuotedSwitch = new Regex(@"[""'](-redefinition[A-Za-z]+)[""']");

        // Every element's documentation searched for paths, switches and members that do not exist.
        public static void Check(Scan scan, CSharpIndex index, HashSet<string> files, string code)
        {
            var switches = new HashSet<string>(QuotedSwitch.Matches(code).Select(m => m.Groups[1].Value));
            // A partial type's files (HostStack.Tufx.cs) are named as Type.Part.
            var partFiles = new HashSet<string>(files.Select(f => Path.GetFileNameWithoutExtension(f)), StringComparer.Ordinal);
            foreach (Element e in scan.Elements)
            {
                if (string.IsNullOrEmpty(e.Doc)) continue;
                foreach (Match m in RepoPath.Matches(e.Doc))
                {
                    string p = m.Groups[1].Value.TrimEnd('.', ',', ')');
                    // What the game writes at run time, other mods' files, and paths shortened with "...".
                    if (p.Contains("...") || p.StartsWith("GameData/") && (!p.StartsWith("GameData/ReDefinition/") || p.StartsWith("GameData/ReDefinition/PluginData/")))
                        continue;
                    if (p.Contains("/bin/") || p.Contains("/obj/")) continue;
                    if (!files.Contains(p) && !files.Any(f => f.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase)))
                        scan.BrokenReferences.Add(e.Path + ":" + e.Line + "  " + p + " (no such file)");
                }
                foreach (Match m in Switch.Matches(e.Doc))
                    if (!switches.Contains(m.Groups[1].Value))
                        scan.BrokenReferences.Add(e.Path + ":" + e.Line + "  " + m.Groups[1].Value + " (no such switch in the code)");
                foreach (Match m in Member.Matches(e.Doc))
                {
                    string type = m.Groups[1].Value, member = m.Groups[2].Value;
                    if (index.Namespaces.Contains(type)) continue;
                    if (!index.Members.TryGetValue(type, out HashSet<string> members)) continue;
                    if (members.Contains(member) || index.Members.ContainsKey(member) || partFiles.Contains(type + "." + member)) continue;
                    scan.BrokenReferences.Add(e.Path + ":" + e.Line + "  " + type + "." + member + " (no such member of " + type + ")");
                }
            }
        }
    }
}
