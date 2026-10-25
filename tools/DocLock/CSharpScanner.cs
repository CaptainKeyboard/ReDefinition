using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DocLock
{
    // C#'s elements, read with Roslyn: types, delegates, methods, constructors, finalisers, operators,
    // properties, indexers, events, fields, constants, enum members. Their documentation: the comments
    // directly above the declaration (its attributes may stand between), or after it on its line. A type's
    // code is its declaration's head, not its members, which are elements of their own.
    internal static class CSharpScanner
    {
        // Every member declaration of a C# file, with its documentation, into the list and the index.
        public static void Scan(string path, string text, List<Element> into, CSharpIndex index)
        {
            SyntaxTree tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Latest));
            SyntaxNode root = tree.GetRoot();
            foreach (MemberDeclarationSyntax member in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
            {
                if (member is BaseNamespaceDeclarationSyntax || member is GlobalStatementSyntax) continue;
                // Members of a method's local scope are not reached: local functions are statements.
                Element e = Make(path, member, tree);
                if (e == null) continue;
                into.Add(e);
                index.Add(e, member);
            }
        }

        // The element of one declaration: its kind, qualified name, signature, code and documentation, or null for
        // what is no element.
        private static Element Make(string path, MemberDeclarationSyntax member, SyntaxTree tree)
        {
            string kind, name, signature = "";
            string code;
            switch (member)
            {
                case EnumDeclarationSyntax en:
                    kind = "enum"; name = en.Identifier.Text; code = Head(en, en.OpenBraceToken); break;
                case TypeDeclarationSyntax type:
                    kind = type.Keyword.Text; name = type.Identifier.Text + Arity(type.TypeParameterList);
                    code = Head(type, type.OpenBraceToken); break;
                case DelegateDeclarationSyntax d:
                    kind = "delegate"; name = d.Identifier.Text; code = Tokens(d); break;
                case MethodDeclarationSyntax m:
                    kind = "method"; name = m.Identifier.Text; signature = Parameters(m.ParameterList); code = Tokens(m); break;
                case ConstructorDeclarationSyntax c:
                    kind = "constructor"; name = c.Identifier.Text; signature = Parameters(c.ParameterList); code = Tokens(c); break;
                case DestructorDeclarationSyntax f:
                    kind = "finaliser"; name = "~" + f.Identifier.Text; code = Tokens(f); break;
                case OperatorDeclarationSyntax o:
                    kind = "operator"; name = "operator " + o.OperatorToken.Text; signature = Parameters(o.ParameterList); code = Tokens(o); break;
                case ConversionOperatorDeclarationSyntax co:
                    kind = "operator"; name = co.ImplicitOrExplicitKeyword.Text + " operator " + co.Type; signature = Parameters(co.ParameterList);
                    code = Tokens(co); break;
                case PropertyDeclarationSyntax p:
                    kind = "property"; name = p.Identifier.Text; code = Tokens(p); break;
                case IndexerDeclarationSyntax ix:
                    kind = "indexer"; name = "this[]"; signature = Parameters(ix.ParameterList); code = Tokens(ix); break;
                case EventDeclarationSyntax ev:
                    kind = "event"; name = ev.Identifier.Text; code = Tokens(ev); break;
                case EventFieldDeclarationSyntax ef:
                    kind = "event"; name = string.Join(", ", ef.Declaration.Variables.Select(v => v.Identifier.Text)); code = Tokens(ef); break;
                case FieldDeclarationSyntax fd:
                    kind = fd.Modifiers.Any(SyntaxKind.ConstKeyword) ? "constant" : "field";
                    name = string.Join(", ", fd.Declaration.Variables.Select(v => v.Identifier.Text)); code = Tokens(fd); break;
                case EnumMemberDeclarationSyntax em:
                    kind = "enum member"; name = em.Identifier.Text; code = Tokens(em); break;
                default:
                    return null;
            }
            var e = new Element
            {
                Path = path,
                Language = "cs",
                Kind = kind,
                Name = Scope(member) + name,
                Signature = signature,
                Line = tree.GetLineSpan(member.Span).StartLinePosition.Line + 1,
                Doc = Leading(member) ?? "",
                Code = code,
            };
            if (e.Doc.Length == 0) e.Doc = Trailing(member) ?? "";
            // The members of an enum written on one line (enum Curve { Linear, S, Auto }) share its comment.
            if (e.Doc.Length == 0 && member is EnumMemberDeclarationSyntax && member.Parent is EnumDeclarationSyntax oneLine
                && tree.GetLineSpan(oneLine.OpenBraceToken.Span).StartLinePosition.Line == tree.GetLineSpan(oneLine.CloseBraceToken.Span).StartLinePosition.Line
                && Rules.Enough(Leading(oneLine) ?? Trailing(oneLine) ?? "", oneLine.Identifier.Text))
                e.SharesDoc = true;
            if (e.Doc.Length == 0 && Groupable(member))
            {
                // A run of fields, constants or enum members without a blank line shares the comment above its first.
                MemberDeclarationSyntax previous = Previous(member);
                while (previous != null && previous.GetType() == member.GetType() && !BlankLineBefore(member, previous))
                {
                    if ((Leading(previous) ?? Trailing(previous)) != null)
                    {
                        e.SharesDoc = true;
                        break;
                    }
                    member = previous;
                    previous = Previous(previous);
                }
            }
            return e;
        }

        // Whether the declaration may share the comment of a run: fields, enum members, event fields.
        private static bool Groupable(MemberDeclarationSyntax member)
        {
            return member is FieldDeclarationSyntax || member is EnumMemberDeclarationSyntax || member is EventFieldDeclarationSyntax;
        }

        // The declaration before this one in its type or enum, or null for the first.
        private static MemberDeclarationSyntax Previous(MemberDeclarationSyntax member)
        {
            SyntaxNode parent = member.Parent;
            IEnumerable<MemberDeclarationSyntax> siblings = parent switch
            {
                TypeDeclarationSyntax t => t.Members,
                EnumDeclarationSyntax en => en.Members,
                _ => null,
            };
            if (siblings == null) return null;
            MemberDeclarationSyntax before = null;
            foreach (MemberDeclarationSyntax s in siblings)
            {
                if (s == member) return before;
                before = s;
            }
            return null;
        }

        // A blank line between two members: the previous one's line end is in its own trailing trivia, so a
        // line end in the trivia before this one's first token is an empty line.
        private static bool BlankLineBefore(MemberDeclarationSyntax member, MemberDeclarationSyntax previous)
        {
            return member.GetFirstToken().LeadingTrivia.Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
        }

        // The comment block directly above: comment lines in the leading trivia with no blank line between
        // them and the declaration.
        public static string Leading(SyntaxNode node)
        {
            SyntaxTriviaList trivia = node.GetFirstToken().LeadingTrivia;
            var lines = new List<string>();
            int i = trivia.Count - 1;
            // Whitespace before the declaration on its own line.
            while (i >= 0 && trivia[i].IsKind(SyntaxKind.WhitespaceTrivia)) i--;
            while (i >= 0)
            {
                SyntaxTrivia t = trivia[i];
                if (t.IsKind(SyntaxKind.EndOfLineTrivia))
                {
                    // Two line ends in a row (only whitespace between): a blank line ends the block.
                    int j = i - 1;
                    while (j >= 0 && trivia[j].IsKind(SyntaxKind.WhitespaceTrivia)) j--;
                    if (j >= 0 && trivia[j].IsKind(SyntaxKind.EndOfLineTrivia)) break;
                    i = j;
                    continue;
                }
                if (t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia)
                    || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                {
                    lines.Insert(0, Strip(t.ToFullString()));
                    i--;
                    continue;
                }
                if (t.IsKind(SyntaxKind.WhitespaceTrivia)) { i--; continue; }
                // A preprocessor line or anything else ends the block.
                break;
            }
            string doc = string.Join("\n", lines).Trim();
            return doc.Length > 0 ? doc : null;
        }

        // A comment after the declaration on its own last line.
        private static string Trailing(SyntaxNode node)
        {
            SyntaxToken last = node.GetLastToken();
            // An enum member's comment follows the comma after it.
            SyntaxToken next = last.GetNextToken();
            if (next.IsKind(SyntaxKind.CommaToken)) last = next;
            foreach (SyntaxTrivia t in last.TrailingTrivia)
            {
                if (t.IsKind(SyntaxKind.EndOfLineTrivia)) break;
                if (t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia))
                    return Strip(t.ToFullString());
            }
            return null;
        }

        // A comment's text without its markers (//, ///, /* */ and leading stars), line by line.
        public static string Strip(string comment)
        {
            var b = new StringBuilder();
            foreach (string raw in comment.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("///")) line = line.Substring(3);
                else if (line.StartsWith("//")) line = line.Substring(2);
                else if (line.StartsWith("/*")) line = line.Substring(2);
                if (line.EndsWith("*/")) line = line.Substring(0, line.Length - 2);
                if (line.StartsWith("*")) line = line.Substring(1);
                b.Append(line.Trim()).Append('\n');
            }
            return b.ToString().Trim();
        }

        // The tokens of a node, comments and spacing left out.
        public static string Tokens(SyntaxNode node)
        {
            var b = new StringBuilder();
            foreach (SyntaxToken token in node.DescendantTokens())
                b.Append(token.Text).Append(' ');
            return b.ToString();
        }

        // The tokens of a declaration up to a token, its opening brace: a type's head without its members.
        private static string Head(SyntaxNode node, SyntaxToken stop)
        {
            var b = new StringBuilder();
            foreach (SyntaxToken token in node.DescendantTokens())
            {
                if (token == stop) break;
                b.Append(token.Text).Append(' ');
            }
            return b.ToString();
        }

        // A generic type's arity as `n, which tells overloads by type parameters apart.
        private static string Arity(TypeParameterListSyntax list) => list == null ? "" : "`" + list.Parameters.Count;

        // The parameters' types, comma-separated: the signature that tells overloads apart.
        private static string Parameters(BaseParameterListSyntax list)
        {
            return list == null ? "" : string.Join(",", list.Parameters.Select(p => p.Type?.ToString().Replace(" ", "") ?? ""));
        }

        // The containing namespaces and types, as a prefix.
        private static string Scope(SyntaxNode node)
        {
            var parts = new List<string>();
            for (SyntaxNode p = node.Parent; p != null; p = p.Parent)
            {
                if (p is BaseTypeDeclarationSyntax t) parts.Insert(0, t.Identifier.Text);
                else if (p is BaseNamespaceDeclarationSyntax n) parts.Insert(0, n.Name.ToString());
            }
            return parts.Count > 0 ? string.Join(".", parts) + "." : "";
        }
    }

    // Our C# types and their members by simple name, for the references in comments (References, in Program.cs), and
    // the partial types' parts, of which one documented part documents all.
    internal sealed class CSharpIndex
    {
        // Per referable type, the names of its members and nested types; every namespace part; per qualified type
        // name, its partial parts.
        public readonly Dictionary<string, HashSet<string>> Members = new Dictionary<string, HashSet<string>>();
        public readonly HashSet<string> Namespaces = new HashSet<string>();
        public readonly Dictionary<string, List<Element>> Types = new Dictionary<string, List<Element>>();

        // Records a declaration: its namespaces, a type and its parts, a member under its type.
        public void Add(Element e, MemberDeclarationSyntax member)
        {
            for (SyntaxNode p = member.Parent; p != null; p = p.Parent)
                if (p is BaseNamespaceDeclarationSyntax n)
                    foreach (string part in n.Name.ToString().Split('.')) Namespaces.Add(part);
            if (member is BaseTypeDeclarationSyntax type)
            {
                string simple = type.Identifier.Text;
                if (!Members.ContainsKey(simple) && Referable(e) && !(type.Parent is BaseTypeDeclarationSyntax)) Members[simple] = new HashSet<string>();
                if (!Types.TryGetValue(e.Name, out List<Element> parts)) Types[e.Name] = parts = new List<Element>();
                parts.Add(e);
                // A nested type is a member of the type it stands in.
                if (type.Parent is BaseTypeDeclarationSyntax outer && Members.TryGetValue(outer.Identifier.Text, out HashSet<string> outerMembers))
                    outerMembers.Add(simple);
                return;
            }
            if (member.Parent is BaseTypeDeclarationSyntax owner)
            {
                string simple = owner.Identifier.Text;
                if (!Members.TryGetValue(simple, out HashSet<string> set)) return;
                string last = e.Name.Substring(e.Name.LastIndexOf('.') + 1);
                foreach (string n in last.Split(',')) set.Add(n.Trim());
            }
        }

        // The types a comment may name by Type.Member: the mod's and the tools', not the tests' stand-ins for
        // KSP's and Unity's types (Part, Input), which comments mean as the game's; top-level types only, as a
        // nested one's name (AmperePackage.Part) may be one of KSP's too.
        private static bool Referable(Element e) => e.Path.StartsWith("src/") || e.Path.StartsWith("tools/");

        // A partial type documented in one of its parts is documented in all.
        public void SharePartials()
        {
            foreach (List<Element> parts in Types.Values)
            {
                if (parts.Count < 2) continue;
                bool any = parts.Any(p => Rules.Enough(p.Doc, p.Name));
                if (any)
                    foreach (Element p in parts)
                        if (!Rules.Enough(p.Doc, p.Name)) p.DocumentedElsewhere = true;
            }
        }
    }
}
