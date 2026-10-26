using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Refactor
{
    // A top-level type that a move put into another namespace: its name, the namespace it came from and the one
    // it is in now, and the file that declares it.
    internal sealed class MovedType
    {
        // The type's simple name, without any namespace.
        public string Name;
        // The namespace the type came from and the one it is in now.
        public string OldNs;
        public string NewNs;
        // The repository path of the file that declares it.
        public string File;

        // The same type with the manifest's namespace renames applied to both namespaces.
        public MovedType Renamed(List<Operation> renames)
        {
            string old = OldNs, now = NewNs;
            foreach (Operation o in renames)
            {
                var pattern = new Regex(@"(?<![\w.])" + Regex.Escape(o.A) + @"(?!\w)");
                old = pattern.Replace(old ?? "", m => o.B);
                now = pattern.Replace(now ?? "", m => o.B);
            }
            return new MovedType { Name = Name, OldNs = old, NewNs = now, File = File };
        }
    }

    // Puts the types of moved files into their new namespaces.
    internal static class Namespaces
    {
        // Sets the namespace of the file's top-level types to the given one (unchanged where null) and returns
        // those types with the namespace they came from; the file is written only where the namespace changes.
        public static List<MovedType> Set(string root, string path, string newNs, HashSet<string> changed)
        {
            string full = Path.Combine(root, path);
            string text = TextFile.Read(full, out bool bom);
            SyntaxTree tree = CSharpSyntaxTree.ParseText(text);
            var result = new List<MovedType>();
            foreach (NamespaceDeclarationSyntax ns in tree.GetRoot().DescendantNodes().OfType<NamespaceDeclarationSyntax>().Where(n => !(n.Parent is NamespaceDeclarationSyntax)))
            {
                string old = ns.Name.ToString();
                foreach (MemberDeclarationSyntax member in ns.Members)
                {
                    string name = member switch
                    {
                        BaseTypeDeclarationSyntax t => t.Identifier.Text,
                        DelegateDeclarationSyntax d => d.Identifier.Text,
                        _ => null,
                    };
                    if (name != null) result.Add(new MovedType { Name = name, OldNs = old, NewNs = newNs ?? old, File = path });
                }
                if (newNs != null && newNs != old)
                {
                    text = text.Substring(0, ns.Name.SpanStart) + newNs + text.Substring(ns.Name.Span.End);
                    TextFile.Write(full, text, bom);
                    changed.Add(path);
                    // One namespace per file is the repository's rule; a second one would need its own edit.
                    break;
                }
            }
            return result;
        }
    }

    // Renames an identifier without touching strings: the identifier tokens and the comments of a C# file.
    internal static class Renames
    {
        // The source with every identifier token and every comment word equal to the old name renamed.
        public static string InCSharp(string text, string old, string now)
        {
            if (!text.Contains(old)) return text;
            SyntaxTree tree = CSharpSyntaxTree.ParseText(text);
            var edits = new List<(int start, int length, string replacement)>();
            foreach (SyntaxToken token in tree.GetRoot().DescendantTokens())
                if (token.IsKind(SyntaxKind.IdentifierToken) && token.ValueText == old)
                    edits.Add((token.SpanStart, token.Span.Length, now));
            var word = new Regex(@"\b" + Regex.Escape(old) + @"\b");
            foreach (SyntaxTrivia trivia in tree.GetRoot().DescendantTrivia())
            {
                bool comment = trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia);
                if (!comment) continue;
                string original = trivia.ToString();
                string replaced = word.Replace(original, m => now);
                if (replaced != original) edits.Add((trivia.SpanStart, trivia.Span.Length, replaced));
            }
            var result = new StringBuilder(text);
            foreach (var e in edits.OrderByDescending(e => e.start))
                result.Remove(e.start, e.length).Insert(e.start, e.replacement);
            return result.ToString();
        }
    }

    // Adds the usings a move makes necessary: for a moved type, to every file that names it from outside its
    // new namespace; for a moved file, the old namespace, whose types it used without saying so.
    internal static class Usings
    {
        // What the pass needs to know of one C# file.
        private sealed class FileInfo
        {
            // The file's path, its text and whether it began with a byte order mark.
            public string Path;
            public string Text;
            public bool Bom;
            // The namespaces the file declares, those it imports, every identifier in it and its top-level type names.
            public readonly HashSet<string> Declared = new HashSet<string>();
            public readonly HashSet<string> Imported = new HashSet<string>();
            public readonly HashSet<string> Identifiers = new HashSet<string>();
            public readonly List<string> TypeNames = new List<string>();
        }

        // Adds the usings to the files and returns how many were added; the files changed are added to `changed`.
        public static int Add(string root, List<string> csFiles, List<MovedType> moved, HashSet<string> changed)
        {
            var movedByName = moved.Where(t => t.OldNs != t.NewNs).GroupBy(t => t.Name)
                                   .ToDictionary(g => g.Key, g => g.Select(t => t.NewNs).Distinct().ToList());
            var oldOfFile = moved.Where(t => t.OldNs != t.NewNs).GroupBy(t => t.File)
                                 .ToDictionary(g => g.Key, g => g.First().OldNs);
            var infos = new List<FileInfo>();
            var declaredIn = new Dictionary<string, HashSet<string>>();
            foreach (string path in csFiles)
            {
                if (!File.Exists(Path.Combine(root, path))) continue;
                FileInfo info = Read(root, path);
                infos.Add(info);
                foreach (string ns in info.Declared)
                {
                    if (!declaredIn.TryGetValue(ns, out HashSet<string> set)) declaredIn[ns] = set = new HashSet<string>();
                    foreach (string type in info.TypeNames) set.Add(type);
                }
            }
            int added = 0;
            foreach (FileInfo info in infos)
            {
                var needed = new SortedSet<string>(StringComparer.Ordinal);
                foreach (string id in info.Identifiers)
                    if (movedByName.TryGetValue(id, out List<string> targets))
                        foreach (string ns in targets)
                            if (!Visible(info, ns)) needed.Add(ns);
                if (oldOfFile.TryGetValue(info.Path, out string old) && declaredIn.TryGetValue(old, out HashSet<string> siblings)
                    && !Visible(info, old) && info.Identifiers.Any(id => siblings.Contains(id) && !info.TypeNames.Contains(id)))
                    needed.Add(old);
                if (needed.Count == 0) continue;
                string text = info.Text;
                foreach (string ns in needed) text = Insert(text, ns);
                TextFile.Write(Path.Combine(root, info.Path), text, info.Bom);
                changed.Add(info.Path);
                added += needed.Count;
            }
            return added;
        }

        // Whether a namespace's types can be named in the file: it declares the namespace or a child of it, or imports it.
        private static bool Visible(FileInfo info, string ns)
        {
            return info.Imported.Contains(ns) || info.Declared.Any(d => d == ns || d.StartsWith(ns + ".", StringComparison.Ordinal));
        }

        // The namespaces, usings, top-level types and identifiers of a file.
        private static FileInfo Read(string root, string path)
        {
            var info = new FileInfo { Path = path };
            info.Text = TextFile.Read(Path.Combine(root, path), out bool bom);
            info.Bom = bom;
            SyntaxTree tree = CSharpSyntaxTree.ParseText(info.Text);
            SyntaxNode top = tree.GetRoot();
            foreach (NamespaceDeclarationSyntax ns in top.DescendantNodes().OfType<NamespaceDeclarationSyntax>())
            {
                string full = ns.Name.ToString();
                for (SyntaxNode p = ns.Parent; p is NamespaceDeclarationSyntax outer; p = p.Parent) full = outer.Name + "." + full;
                info.Declared.Add(full);
                foreach (MemberDeclarationSyntax member in ns.Members)
                {
                    if (member is BaseTypeDeclarationSyntax t) info.TypeNames.Add(t.Identifier.Text);
                    else if (member is DelegateDeclarationSyntax d) info.TypeNames.Add(d.Identifier.Text);
                }
            }
            foreach (UsingDirectiveSyntax u in top.DescendantNodes().OfType<UsingDirectiveSyntax>())
                if (u.Alias == null && u.Name != null) info.Imported.Add(u.Name.ToString());
            foreach (SyntaxToken token in top.DescendantTokens())
                if (token.IsKind(SyntaxKind.IdentifierToken)) info.Identifiers.Add(token.ValueText);
            return info;
        }

        // The text with `using ns;` added among the file's usings: in order among the ones of the same
        // project, otherwise after the last one, otherwise at the top.
        private static string Insert(string text, string ns)
        {
            string newline = Repo.NewLine(text);
            SyntaxTree tree = CSharpSyntaxTree.ParseText(text);
            var usings = tree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>()
                             .Where(u => u.Alias == null && u.StaticKeyword.IsKind(SyntaxKind.None) && u.Name != null && u.Parent is CompilationUnitSyntax).ToList();
            string line = "using " + ns + ";" + newline;
            if (usings.Count == 0) return line + (text.StartsWith("namespace") || text.StartsWith("//") ? newline : "") + text;
            var own = usings.Where(u => u.Name.ToString().StartsWith("ReDefinition", StringComparison.Ordinal)).ToList();
            UsingDirectiveSyntax before = own.FirstOrDefault(u => string.CompareOrdinal(u.Name.ToString(), ns) > 0);
            if (before != null)
                return text.Insert(tree.GetText().Lines[tree.GetLineSpan(before.Span).StartLinePosition.Line].Start, line);
            UsingDirectiveSyntax last = own.Count > 0 ? own[own.Count - 1] : usings[usings.Count - 1];
            return text.Insert(tree.GetText().Lines[tree.GetLineSpan(last.Span).EndLinePosition.Line].EndIncludingLineBreak, line);
        }
    }
}
