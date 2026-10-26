using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Refactor
{
    // Restructures the repository from a manifest, so that a move of many files is one reviewed step and not
    // a hand edit of every reference. One line per operation, fields separated by tabs, "#" starts a comment:
    //
    //   move      <from> <to> [namespace]     one file; its types go into the namespace where one is given
    //   movedir   <from> <to> [namespace]     every file under a folder, keeping the paths below it
    //   namespace <old> <new>                 renames a namespace in every text file
    //   rename    <old> <new>                 renames an identifier: its tokens in C#, its words elsewhere
    //   text      <old> <new>                 replaces a string in every text file (paths, names)
    //
    //   dotnet <Refactor.dll> apply <manifest> [--dry-run]
    //
    // Moves run with git mv. Then the namespaces of moved types are set, the namespace and identifier
    // renames and the path rewrites run over the text files, and the usings that a moved type needs
    // (Usings.cs) are added. The compiler then says what is left; unused usings go with
    // "dotnet format style --diagnostics IDE0005".
    public static class Program
    {
        // Runs the manifest; 0 for success, 2 for a wrong call or a manifest line it cannot read.
        public static int Main(string[] args)
        {
            if (args.Length < 2 || args[0] != "apply")
            {
                Console.Error.WriteLine("Usage: Refactor apply <manifest> [--dry-run]");
                return 2;
            }
            bool dry = args.Contains("--dry-run");
            string root = Repo.Root();
            if (root == null)
            {
                Console.Error.WriteLine("Not inside the repository.");
                return 2;
            }
            List<Operation> operations;
            try
            {
                operations = Operation.Read(args[1]);
            }
            catch (FormatException e)
            {
                Console.Error.WriteLine(e.Message);
                return 2;
            }
            var plan = new Plan(root, operations, dry);
            plan.Run();
            return 0;
        }
    }

    // One manifest line: the operation and its fields.
    internal sealed class Operation
    {
        // The operation's name, and up to three fields behind it.
        public string Verb;
        public string A;
        public string B;
        public string C;

        // The manifest's operations in file order; a line it cannot read throws a FormatException.
        public static List<Operation> Read(string path)
        {
            var result = new List<Operation>();
            int number = 0;
            foreach (string raw in File.ReadAllLines(path))
            {
                number++;
                string line = raw.TrimEnd();
                if (line.Length == 0 || line.TrimStart().StartsWith("#")) continue;
                string[] f = line.Split('\t');
                bool known = f[0] == "move" || f[0] == "movedir" || f[0] == "namespace" || f[0] == "rename" || f[0] == "text";
                if (!known || f.Length < 3) throw new FormatException(path + ":" + number + ": cannot read '" + line + "'");
                result.Add(new Operation { Verb = f[0], A = f[1], B = f[2], C = f.Length > 3 ? f[3] : null });
            }
            return result;
        }
    }

    // The repository as git knows it, and the places a rewrite leaves alone.
    internal static class Repo
    {
        // The folders and files no rewrite touches: vendored code, build output, the lock, this tool's manifests.
        private static readonly string[] Excluded =
        {
            ".git/", "src/DxgiProxy/extern/", "third_party/", "build/", "/obj/", "/bin/", ".doclock/", "tools/Refactor/",
            "unity/Library/", "tests/RaytracingPlayer/Library/", "tests/RaytracingPlayer/Assets/Linked/", "licenses/",
        };

        // The extensions of text files a rewrite reads; CMakeLists.txt is covered by .txt.
        private static readonly string[] TextExtensions =
        {
            ".cs", ".cpp", ".h", ".hlsl", ".hlsli", ".shader", ".compute", ".cginc", ".py", ".ps1", ".md", ".txt", ".cmake",
            ".csproj", ".props", ".json", ".yml", ".yaml", ".cfg", ".ini", ".js", ".html", ".css", ".sh", ".bat", ".def", ".reg",
        };

        // The repository's root: the working folder or the first folder above it with a .git.
        public static string Root()
        {
            string dir = Directory.GetCurrentDirectory();
            while (dir != null && !Directory.Exists(Path.Combine(dir, ".git"))) dir = Path.GetDirectoryName(dir);
            return dir;
        }

        // Runs git in the root and returns its output lines; throws when git fails.
        public static List<string> Git(string root, params string[] arguments)
        {
            var info = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string a in arguments) info.ArgumentList.Add(a);
            using (Process git = Process.Start(info))
            {
                string output = git.StandardOutput.ReadToEnd();
                string error = git.StandardError.ReadToEnd();
                git.WaitForExit();
                if (git.ExitCode != 0) throw new InvalidOperationException("git " + string.Join(" ", arguments) + ": " + error.Trim());
                return output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
            }
        }

        // The files of the repository, tracked or new and not ignored, with forward slashes.
        public static List<string> Files(string root)
        {
            return Git(root, "ls-files", "--cached", "--others", "--exclude-standard")
                .Select(p => p.Replace('\\', '/'))
                .Where(p => File.Exists(Path.Combine(root, p)))
                .Distinct()
                .ToList();
        }

        // Whether a rewrite reads this file: a text file outside the excluded places.
        public static bool IsRewritable(string path)
        {
            if (Excluded.Any(x => path.StartsWith(x, StringComparison.Ordinal) || x.StartsWith("/") && path.Contains(x))) return false;
            return TextExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
        }

        // The text of a file, and the line break it uses most, so that a rewrite keeps it.
        public static string NewLine(string text)
        {
            int crlf = Regex.Matches(text, "\r\n").Count;
            int lf = Regex.Matches(text, "(?<!\r)\n").Count;
            return crlf >= lf && crlf > 0 ? "\r\n" : "\n";
        }
    }

    // A manifest run: the moves, the namespace edits, the rewrites and the usings.
    internal sealed class Plan
    {
        // The repository's root, the operations, and whether nothing is to be written.
        private readonly string root;
        private readonly List<Operation> operations;
        private readonly bool dry;

        // Every file moved: old path, new path and the namespace its types go into (null: unchanged).
        private readonly List<(string from, string to, string ns)> moves = new List<(string, string, string)>();

        // The paths of files changed after the moves, for the report and for the unused-using pass.
        private readonly HashSet<string> changed = new HashSet<string>();

        // Creates a plan for the repository and the manifest's operations.
        public Plan(string root, List<Operation> operations, bool dry)
        {
            this.root = root;
            this.operations = operations;
            this.dry = dry;
        }

        // Runs the operations in the order that keeps references valid: moves, namespaces, renames, paths, usings.
        public void Run()
        {
            foreach (Operation o in operations.Where(o => o.Verb == "move")) Move(o.A, o.B, o.C);
            foreach (Operation o in operations.Where(o => o.Verb == "movedir")) MoveDir(o.A, o.B, o.C);
            Console.WriteLine(moves.Count + " files moved.");
            if (dry) return;

            var types = new List<MovedType>();
            foreach (var m in moves.Where(m => m.to.EndsWith(".cs", StringComparison.Ordinal)))
                types.AddRange(Namespaces.Set(root, m.to, m.ns, changed));

            var rewrites = new List<(Regex pattern, string replacement)>();
            foreach (Operation o in operations.Where(o => o.Verb == "namespace"))
                rewrites.Add((new Regex(@"(?<![\w.])" + Regex.Escape(o.A) + @"(?!\w)"), o.B));
            // Longest paths first, so that a folder rewrite does not cut a file's path short.
            foreach (var m in moves.OrderByDescending(m => m.from.Length))
                rewrites.Add((new Regex(@"(?<![\w-])" + Regex.Escape(m.from) + @"(?![\w])"), m.to));
            foreach (Operation o in operations.Where(o => o.Verb == "movedir"))
                rewrites.Add((new Regex(@"(?<![\w-])" + Regex.Escape(o.A.TrimEnd('/')) + @"(?![\w-])"), o.B.TrimEnd('/')));
            foreach (Operation o in operations.Where(o => o.Verb == "text"))
                rewrites.Add((new Regex(Regex.Escape(o.A)), o.B));
            var words = operations.Where(o => o.Verb == "rename").ToList();

            // A moved type named with its old namespace, in full or from any of its parts on (Window.UiHygiene,
            // ReDefinition.Window.UiHygiene), is named by its own name: the using takes its place.
            var qualified = new List<(Regex pattern, string name)>();
            foreach (MovedType t in types.Where(t => t.OldNs != t.NewNs).GroupBy(t => t.OldNs + "." + t.Name).Select(g => g.First()))
            {
                string[] parts = t.OldNs.Split('.');
                var suffixes = Enumerable.Range(0, parts.Length).Select(i => string.Join(@"\.", parts.Skip(i).Select(Regex.Escape)));
                qualified.Add((new Regex(@"(?<![\w.])(?:" + string.Join("|", suffixes) + @")\." + Regex.Escape(t.Name) + @"(?![\w])"), t.Name));
            }

            var files = Repo.Files(root).Where(Repo.IsRewritable).ToList();
            foreach (string path in files)
            {
                string full = Path.Combine(root, path);
                string text = TextFile.Read(full, out bool bom);
                string result = text;
                if (path.EndsWith(".cs", StringComparison.Ordinal))
                    foreach (var q in qualified) result = q.pattern.Replace(result, m => q.name);
                foreach (var r in rewrites) result = r.pattern.Replace(result, m => r.replacement);
                foreach (Operation w in words)
                {
                    result = path.EndsWith(".cs", StringComparison.Ordinal)
                        ? Renames.InCSharp(result, w.A, w.B)
                        : Regex.Replace(result, @"\b" + Regex.Escape(w.A) + @"\b", w.B);
                }
                if (result == text) continue;
                TextFile.Write(full, result, bom);
                changed.Add(path);
            }
            Console.WriteLine(changed.Count + " files rewritten.");

            // The moved types, under their namespaces after the renames.
            for (int i = 0; i < types.Count; i++)
            {
                types[i] = types[i].Renamed(operations.Where(o => o.Verb == "namespace").ToList());
            }
            int added = Usings.Add(root, files.Where(f => f.EndsWith(".cs", StringComparison.Ordinal)).ToList(), types, changed);
            Console.WriteLine(added + " usings added.");
            File.WriteAllLines(Path.Combine(root, "build", "refactor-changed.txt"), changed.OrderBy(c => c, StringComparer.Ordinal));
            Console.WriteLine("The changed files are listed in build/refactor-changed.txt.");
        }

        // Moves one file with git (or with the file system where git does not know it) and records it.
        private void Move(string from, string to, string ns)
        {
            moves.Add((from, to, ns));
            if (dry) { Console.WriteLine("move " + from + " -> " + to); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, to)));
            try
            {
                Repo.Git(root, "mv", from, to);
            }
            catch (InvalidOperationException)
            {
                File.Move(Path.Combine(root, from), Path.Combine(root, to));
            }
        }

        // Moves every file under a folder, keeping the paths below it.
        private void MoveDir(string from, string to, string ns)
        {
            string prefix = from.TrimEnd('/') + "/";
            foreach (string path in Repo.Files(root).Where(p => p.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                Move(path, to.TrimEnd('/') + "/" + path.Substring(prefix.Length), ns);
        }
    }
}

namespace Refactor
{
    // Reads and writes text files so that a rewrite changes only what it means to: the byte order mark and the
    // line breaks stay as the file had them.
    internal static class TextFile
    {
        // The text of a file and whether it starts with a byte order mark.
        public static string Read(string path, out bool bom)
        {
            byte[] bytes = File.ReadAllBytes(path);
            bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            return new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        }

        // Writes the text, with a byte order mark where the file had one.
        public static void Write(string path, string text, bool bom)
        {
            File.WriteAllText(path, text, new UTF8Encoding(bom));
        }
    }
}
