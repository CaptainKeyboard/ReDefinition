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
    // tool finds every element and says which have a comment. It checks the comments that exist: that they
    // name only files, switches and members that exist, and that they keep to the contract for comments.
    //
    // The command report, the default, writes the coverage per area, language and kind to the console and
    // .doclock/coverage.md. The command lint-baseline accepts the findings of the comments as they stand
    // (.doclock/lint-baseline.tsv). The command wiki <folder> writes the wiki: the developer pages, a page per area
    // from its README, and the indexes the code answers by itself. Main describes refs, and the methods Check,
    // List, LintReport, Sample and Heads describe check, list, lint, sample and heads. What is left out, and why,
    // is in Sources.Excluded.
    //
    // Build and run it as:
    //
    //   dotnet build tools/DocLock -c Release, then: dotnet <its DocLock.dll> <command> [options]
    public static class Program
    {
        // Runs one command on the repository the working folder lies in; 0 for success, 1 for a failed check, 2 for a
        // wrong call. The command refs prints the comments'
        // references that name nothing that exists.
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
            switch (command)
            {
                case "report":
                    Report.Write(root, scan);
                    return 0;
                case "check":
                    return Check(root, scan);
                case "list":
                    List(scan, argument);
                    return 0;
                case "refs":
                    foreach (string broken in scan.BrokenReferences) Console.WriteLine(broken);
                    Console.WriteLine(scan.BrokenReferences.Count + " broken references.");
                    return 0;
                case "lint":
                    LintReport(scan, argument);
                    return 0;
                case "sample":
                    Sample(root, scan, args);
                    return 0;
                case "heads":
                    Heads(scan);
                    return 0;
                case "lint-baseline":
                    LintBaseline.Write(root, Lint.All(scan.Elements.Where(Rules.Documented)));
                    Console.WriteLine("Baseline written.");
                    return 0;
                case "wiki":
                    Wiki.Write(argument ?? Path.Combine(root, ".doclock", "wiki"), scan, Sources.Browse(root));
                    return 0;
                default:
                    Console.Error.WriteLine("Commands: report, check, list, refs, wiki, lint, lint-baseline, sample, heads.");
                    return 2;
            }
        }

        // The command check: exit code 1 where a comment names a file, switch or member that does not exist, or where a
        // comment is new or changed and breaks the contract. A missing comment does not fail it.
        private static int Check(string root, Scan scan)
        {
            var baseline = new LintBaseline(root);
            var fresh = Lint.All(scan.Elements.Where(Rules.Documented)).Where(f => !baseline.Knows(f.element, f.finding)).ToList();
            int commented = scan.Elements.Count(Rules.Documented);
            Console.WriteLine(scan.Elements.Count + " elements, " + commented + " with a comment: " + scan.BrokenReferences.Count
                              + " broken references, " + fresh.Count + " new comment lint findings.");
            foreach (string r in scan.BrokenReferences.Take(20)) Console.WriteLine("  reference     " + r);
            foreach (var f in fresh.Take(20)) Console.WriteLine("  lint          " + f.element.Path + ":" + f.element.Line + "  " + f.element.Kind + " " + f.element.Name + "  (" + f.finding.Rule + ": " + f.finding.Detail + ")");
            bool ok = scan.BrokenReferences.Count == 0 && fresh.Count == 0;
            if (!ok)
                Console.WriteLine("Correct the reference, or shorten the comment (docs/development/writing-code-comments.md).");
            return ok ? 0 : 1;
        }

        // The elements a reader meets first, for a review: every type, enum, shader, script and config file with its own
        // comment, one per line as for the sample command.
        private static void Heads(Scan scan)
        {
            string[] kinds = { "class", "struct", "interface", "enum", "union", "delegate", "shader", "script", "module", "config", "pass" };
            foreach (Element e in scan.Elements.Where(e => e.Doc.Length > 0 && !e.SharesDoc && kinds.Contains(e.Kind)).OrderBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.Line))
                Console.WriteLine(e.Path + ":" + e.Line + "\t" + e.Kind + "\t" + e.Name);
        }

        // A random sample of documented elements for a review, one per line: file:line, kind, name. The arguments are the
        // count, the seed, and the first commit to compare with: half the sample comes from the files changed since that
        // commit, where the comments were rewritten, and half from the other files.
        private static void Sample(string root, Scan scan, string[] args)
        {
            int count = args.Length > 1 ? int.Parse(args[1]) : 100;
            var random = new Random(args.Length > 2 ? int.Parse(args[2]) : 1);
            var changed = new HashSet<string>(StringComparer.Ordinal);
            if (args.Length > 3)
            {
                var info = new ProcessStartInfo("git", "diff --name-only " + args[3]) { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false };
                using (Process git = Process.Start(info))
                {
                    string line;
                    while ((line = git.StandardOutput.ReadLine()) != null) changed.Add(line.Replace('\\', '/'));
                    git.WaitForExit();
                }
            }
            var pool = scan.Elements.Where(e => e.Doc.Length > 0 && !e.SharesDoc && e.Kind != "enum member").ToList();
            var rewritten = pool.Where(e => changed.Contains(e.Path)).OrderBy(e => random.Next()).Take(count / 2);
            var rest = pool.Where(e => !changed.Contains(e.Path)).OrderBy(e => random.Next()).Take(count - count / 2);
            foreach (Element e in rewritten.Concat(rest).OrderBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.Line))
                Console.WriteLine(e.Path + ":" + e.Line + "	" + e.Kind + "	" + e.Name + (e.Signature.Length > 0 ? "(" + e.Signature + ")" : ""));
        }

        // The command lint [rule]: the comments the contract finds fault with, the count per rule and, given a rule, each
        // of its findings with the file and line.
        private static void LintReport(Scan scan, string rule)
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

        // The command list [area|path]: the elements without a comment, under a path prefix or in an area, one per
        // line: file:line, kind, name.
        private static void List(Scan scan, string filter)
        {
            foreach (Element e in scan.Elements)
            {
                if (filter != null && !e.Path.StartsWith(filter, StringComparison.OrdinalIgnoreCase) && e.Area != filter) continue;
                if (Rules.Documented(e)) continue;
                Console.WriteLine(e.Path + ":" + e.Line + "\t" + e.Kind + "\t" + e.Name + (e.Signature.Length > 0 ? "(" + e.Signature + ")" : ""));
            }
        }
    }

    // Which files are this project's code.
    internal static class Sources
    {
        // Left out is code of others that is vendored or adapted with its own documentation (the proxy's
        // headers of AMD, NVIDIA, Intel and Microsoft; AMD's FidelityFX shaders and FSR3Unity's port of
        // them). Also left out are copies (the test player's linked sources, which the build copies from src),
        // what Unity or the build generates, and the documentation itself.
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
