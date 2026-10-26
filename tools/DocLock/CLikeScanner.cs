using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DocLock
{
    // The brace languages are C, C++, HLSL, the CG code in Unity's shaders, and compute shaders. Their elements
    // are read token by token at the scopes where declarations stand (the file, namespaces, structs, classes,
    // cbuffers), not inside function bodies. The elements are functions and their declarations, structs, classes,
    // unions, enums and their members, cbuffers, typedefs and using aliases, variables and fields, and macros (not
    // include guards). A function declared in one place and defined in another is documented where either is.
    internal static class CLikeScanner
    {
        // A token's kind.
        private enum T
        {
            Word,     // a name or a keyword
            Number,   // a number, with its suffix
            Text,     // a string or a character, quotes included
            Punct,    // one character of punctuation, or '::'
            Pre,      // a whole preprocessor line
        }

        // One token: its kind, its text, its line.
        private struct Token
        {
            // What it is, as written, and the line it stands on.
            public T Kind;
            public string Text;
            public int Line;
        }

        // A comment block: its first and last line, its text without markers, whether it stands on lines of its own.
        private sealed class Comment
        {
            public int First, Last;   // the lines it begins and ends on
            public string Text;       // its text, the markers taken off
            public bool OwnLine;      // nothing but the comment on its first line
        }

        // Every element of a file in a brace language into the list.
        public static void Scan(string path, string language, string text, List<Element> into)
        {
            var tokens = new List<Token>();
            var comments = new List<Comment>();
            Lex(text, tokens, comments);
            var byLast = new Dictionary<int, Comment>();
            foreach (Comment c in comments) byLast[c.Last] = c;
            var parser = new Parser { Path = path, Language = language, Tokens = tokens, Comments = comments, ByLast = byLast, Into = into };
            parser.Lines = text.Replace("\r", "").Split('\n');
            parser.ParseScope(0, tokens.Count, "", "file");
        }

        // The parse of one file's tokens: its scopes, the elements in them, their comments.
        private sealed class Parser
        {
            // The file and its language, its tokens and comments (and those by their last line), the elements found,
            // the file's lines.
            public string Path, Language;
            public List<Token> Tokens;
            public List<Comment> Comments;
            public Dictionary<int, Comment> ByLast;
            public List<Element> Into;
            public string[] Lines;
            // The last #ifndef's name, which tells an include guard's #define; the last element that may share its
            // comment with the next.
            private string lastIfndef;
            private Element previousGroupable;

            // The declarations between two tokens of a scope (the file, a namespace, a struct), each statement or
            // braced block in turn.
            public void ParseScope(int start, int end, string prefix, string scope)
            {
                int i = start;
                previousGroupable = null;
                while (i < end)
                {
                    Token t = Tokens[i];
                    if (t.Kind == T.Pre)
                    {
                        Preprocessor(t);
                        i++;
                        continue;
                    }
                    if (t.Text == ";" || t.Text == "}") { i++; continue; }
                    // Access labels.
                    if ((t.Text == "public" || t.Text == "private" || t.Text == "protected") && i + 1 < end && Tokens[i + 1].Text == ":")
                    {
                        i += 2;
                        continue;
                    }
                    int s = i;
                    int depth = 0;
                    while (i < end)
                    {
                        string x = Tokens[i].Text;
                        if (Tokens[i].Kind == T.Pre && depth == 0) break;
                        if (x == "(" || x == "[") depth++;
                        else if (x == ")" || x == "]") depth--;
                        else if (depth == 0 && (x == ";" || x == "{")) break;
                        i++;
                    }
                    if (i >= end || Tokens[i].Kind == T.Pre)
                        continue;
                    if (Tokens[i].Text == ";")
                    {
                        Statement(s, i, prefix, scope);
                        i++;
                        continue;
                    }
                    // A head and a brace.
                    int open = i;
                    int close = Match(open, end);
                    Block(s, open, close, prefix, scope, end);
                    i = close + 1;
                }
            }

            // A preprocessor line: #ifndef remembered, #define an element unless it is an include guard.
            private void Preprocessor(Token t)
            {
                string line = t.Text.TrimStart('#').Trim();
                if (line.StartsWith("ifndef "))
                {
                    lastIfndef = line.Substring(7).Trim();
                    return;
                }
                if (!line.StartsWith("define ")) return;
                string rest = line.Substring(7).Trim();
                int n = 0;
                while (n < rest.Length && (char.IsLetterOrDigit(rest[n]) || rest[n] == '_')) n++;
                string name = rest.Substring(0, n);
                if (name.Length == 0) return;
                bool guard = name == lastIfndef && rest.Substring(n).Trim().Length == 0;
                lastIfndef = null;
                if (guard) return;
                Element e = Make("macro", name, "", t.Line, t.Line, t.Text);
                Group(e, "macro");
            }

            // A declaration ending in a semicolon: a type alias, a function's declaration, or a variable or field.
            private void Statement(int s, int end, string prefix, string scope)
            {
                string first = Tokens[s].Text;
                if (first == "return" || first == "static_assert" || first == "friend" || first == "using" && Tokens.Count > s + 1 && Tokens[s + 1].Text == "namespace")
                    return;
                int line = Tokens[s].Line;
                int lastLine = Tokens[end].Line;
                string code = Join(s, end + 1);
                if (first == "typedef" || first == "using")
                {
                    // A using declaration (using A::B;) names nothing of its own; an alias (using X = ...;) does.
                    if (first == "using" && FindTop(s, end, "=") < 0) return;
                    string name = first == "using" && s + 1 < end ? Tokens[s + 1].Text : LastWordBefore(end, s);
                    Element type = Make("type", prefix + name, "", line, lastLine, code);
                    Group(type, "type");
                    return;
                }
                int paren = FindTopParen(s, end);
                int equals = FindTop(s, end, "=");
                // HLSL's semantics and registers (float4 x : RT_AT(...); Texture2D t : register(t0);) come
                // after a colon: a variable, not a function.
                int colon = FindTop(s, end, ":");
                if (paren >= 0 && (equals < 0 || paren < equals) && (colon < 0 || paren < colon) && paren > s
                    && Tokens[paren - 1].Kind == T.Word && !IsTypeOnly(s, paren))
                {
                    string name = Qualified(paren - 1, s);
                    Element f = Make("function", prefix + name, Signature(paren, end), line, lastLine, code);
                    f.Kind = "function";
                    previousGroupable = null;
                    return;
                }
                // A variable or a field: its name before '=', ':', '[' or the end.
                int stop = end;
                foreach (string mark in new[] { "=", ":", "[" })
                {
                    int at = FindTop(s, end, mark);
                    if (at >= 0 && at < stop) stop = at;
                }
                string varName = LastWordBefore(stop, s);
                if (varName == null) return;
                Element v = Make(scope == "struct" ? "field" : "variable", prefix + varName, "", line, lastLine, code);
                Group(v, v.Kind);
            }

            // A declaration with a braced body: a namespace or extern block (scanned into), a struct, class, union,
            // enum or cbuffer (an element, scanned into), a function's definition, or a variable with a braced
            // initialiser.
            private void Block(int s, int open, int close, string prefix, string scope, int end)
            {
                int line = Tokens[s].Line;
                string head = Join(s, open);
                var words = new List<string>();
                for (int k = s; k < open; k++) if (Tokens[k].Kind == T.Word) words.Add(Tokens[k].Text);
                if (words.Contains("namespace"))
                {
                    int at = words.IndexOf("namespace");
                    string name = at + 1 < words.Count ? words[at + 1] : "";
                    ParseScope(open + 1, close, prefix + (name.Length > 0 ? name + "::" : ""), "namespace");
                    previousGroupable = null;
                    return;
                }
                // extern "C" { ... } is a block of declarations; extern "C" HRESULT WINAPI F(...) { ... } a function.
                if (words.Count >= 1 && words[0] == "extern" && Tokens[s + 1].Kind == T.Text && open == s + 2)
                {
                    ParseScope(open + 1, close, prefix, scope);
                    return;
                }
                string kw = words.FirstOrDefault(w => w == "struct" || w == "class" || w == "union" || w == "enum" || w == "cbuffer");
                int paren = FindTopParen(s, open);
                if (kw != null && (paren < 0 || IndexOfWord(s, open, kw) < paren))
                {
                    int at = words.IndexOf(kw);
                    string name = at + 1 < words.Count ? words[at + 1] : "";
                    if (name == "class" || name == "struct") name = at + 2 < words.Count ? words[at + 2] : "";
                    if (name == "alignas" || name.Length == 0) name = "(unnamed)";
                    Element e = Make(kw == "cbuffer" ? "cbuffer" : kw, prefix + name, "", line, Tokens[open].Line, head);
                    previousGroupable = null;
                    if (kw == "enum") EnumMembers(open + 1, close, prefix + name + "::");
                    else ParseScope(open + 1, close, prefix + name + "::", "struct");
                    previousGroupable = null;
                    // Declarators after the brace (struct X { } x;) are not elements of their own.
                    return;
                }
                if (paren >= 0 && paren > s && Tokens[paren - 1].Kind == T.Word && FindTop(s, paren, "=") < 0)
                {
                    string name = Qualified(paren - 1, s);
                    if (name == "if" || name == "for" || name == "while" || name == "switch") return;
                    Make("function", prefix + name, Signature(paren, open), line, Tokens[close].Line, Join(s, close + 1));
                    previousGroupable = null;
                    return;
                }
                if (FindTop(s, open, "=") >= 0)
                {
                    // A variable with a braced initialiser.
                    string name = LastWordBefore(FindTop(s, open, "="), s);
                    if (name != null)
                    {
                        Element v = Make(scope == "struct" ? "field" : "variable", prefix + name, "", line, Tokens[close].Line, Join(s, close + 1));
                        Group(v, v.Kind);
                    }
                }
            }

            // The members of an enum's braces, separated by commas.
            private void EnumMembers(int start, int end, string prefix)
            {
                int i = start;
                previousGroupable = null;
                while (i < end)
                {
                    if (Tokens[i].Kind == T.Pre || Tokens[i].Text == ",") { i++; continue; }
                    int s = i;
                    int depth = 0;
                    while (i < end && !(depth == 0 && Tokens[i].Text == ","))
                    {
                        if (Tokens[i].Text == "(") depth++;
                        if (Tokens[i].Text == ")") depth--;
                        i++;
                    }
                    if (Tokens[s].Kind != T.Word) continue;
                    Element e = Make("enum member", prefix + Tokens[s].Text, "", Tokens[s].Line, Tokens[Math.Max(s, i - 1)].Line, Join(s, i));
                    Group(e, "enum member");
                }
            }

            // A run of fields, variables, macros or enum members on consecutive lines shares the comment above
            // its first.
            private void Group(Element e, string kind)
            {
                if (!Rules.Enough(e.Doc, e.Name) && previousGroupable != null && previousGroupable.Kind == e.Kind
                    && e.Line - previousLastLine <= 1 && (Rules.Enough(previousGroupable.Doc, previousGroupable.Name) || previousGroupable.SharesDoc))
                    e.SharesDoc = true;
                previousGroupable = e;
            }

            // The last line of the element made before, which tells whether the next one follows it directly.
            private int previousLastLine;

            // An element with its documentation, above its first line or after its last, into the list.
            private Element Make(string kind, string name, string signature, int line, int lastLine, string code)
            {
                var e = new Element
                {
                    Path = Path,
                    Language = Language,
                    Kind = kind,
                    Name = name,
                    Signature = signature,
                    Line = line,
                    Doc = Above(line) ?? Behind(lastLine, line) ?? "",
                    Code = code,
                };
                previousLastLine = lastLine;
                Into.Add(e);
                return e;
            }

            // The comment block directly above a line: comments ending on the line before, or before an
            // attribute line ([numthreads(...)], template<...>) there, with no blank line between.
            private string Above(int line)
            {
                int l = line - 1;
                while (l >= 1 && IsAttributeLine(l)) l--;
                var parts = new List<string>();
                while (l >= 1 && ByLast.TryGetValue(l, out Comment c) && c.OwnLine)
                {
                    parts.Insert(0, c.Text);
                    l = c.First - 1;
                }
                string doc = string.Join("\n", parts).Trim();
                return doc.Length > 0 ? doc : null;
            }

            // Whether a line may stand between a comment and its element: an attribute, a template head, a pragma.
            private bool IsAttributeLine(int line)
            {
                if (line < 1 || line > Lines.Length) return false;
                string text = Lines[line - 1].Trim();
                return text.StartsWith("[") && text.EndsWith("]") || text.StartsWith("template") || text.StartsWith("#pragma");
            }

            // A comment after the code on the element's last line.
            private string Behind(int lastLine, int line)
            {
                Comment c = Comments.FirstOrDefault(x => x.First == lastLine && !x.OwnLine);
                return c?.Text;
            }

            // The closing brace of an opening one.
            private int Match(int open, int end)
            {
                int depth = 0;
                for (int k = open; k < end; k++)
                {
                    if (Tokens[k].Text == "{") depth++;
                    else if (Tokens[k].Text == "}" && --depth == 0) return k;
                }
                return end - 1;
            }

            // The first parenthesis outside brackets and template heads, which a function's name stands before; -1
            // for none.
            private int FindTopParen(int s, int end)
            {
                int depth = 0;
                for (int k = s; k < end; k++)
                {
                    string x = Tokens[k].Text;
                    if (x == "<" && k > s && Tokens[k - 1].Text == "template") { k = SkipAngles(k, end); continue; }
                    if (x == "(" && depth == 0) return k;
                    if (x == "[" ) depth++;
                    if (x == "]") depth--;
                }
                return -1;
            }

            // The closing angle bracket of a template head.
            private int SkipAngles(int k, int end)
            {
                int depth = 0;
                for (; k < end; k++)
                {
                    if (Tokens[k].Text == "<") depth++;
                    else if (Tokens[k].Text == ">" && --depth == 0) return k;
                }
                return k;
            }

            // The first token with the text outside any brackets; -1 for none.
            private int FindTop(int s, int end, string mark)
            {
                int depth = 0;
                for (int k = s; k < end; k++)
                {
                    string x = Tokens[k].Text;
                    if (x == "(" || x == "[" || x == "{") depth++;
                    else if (x == ")" || x == "]" || x == "}") depth--;
                    else if (depth == 0 && x == mark)
                    {
                        // '::' is lexed as one token; a lone ':' is a semantic, a bit field or an initialiser list.
                        return k;
                    }
                }
                return -1;
            }

            // The first token with the text between two tokens; -1 for none.
            private int IndexOfWord(int s, int end, string word)
            {
                for (int k = s; k < end; k++) if (Tokens[k].Text == word) return k;
                return -1;
            }

            // A cast or a macro call standing alone (RT_AT(...), DECLARE(...)) is no function: a function's
            // name follows a type.
            private bool IsTypeOnly(int s, int paren) => paren - 1 == s && Tokens[s].Text.ToUpperInvariant() == Tokens[s].Text;

            // A name with its qualifiers (Class::Method) and a destructor's tilde.
            private string Qualified(int nameIndex, int s)
            {
                string name = Tokens[nameIndex].Text;
                int k = nameIndex - 1;
                while (k > s && Tokens[k].Text == "::" && Tokens[k - 1].Kind == T.Word)
                {
                    name = Tokens[k - 1].Text + "::" + name;
                    k -= 2;
                }
                if (k >= s && Tokens[k].Text == "~") name = "~" + name;
                return name;
            }

            // The words and separators of a function's parameter list: the signature that tells overloads apart.
            private string Signature(int paren, int end)
            {
                var b = new StringBuilder();
                int depth = 0;
                for (int k = paren; k < end; k++)
                {
                    string x = Tokens[k].Text;
                    if (x == "(") depth++;
                    if (x == ")" && --depth == 0) break;
                    if (k > paren && Tokens[k].Kind == T.Word || x == "," || x == "*" || x == "&") b.Append(x).Append(x == "," ? "" : " ");
                }
                return Element.Normalise(b.ToString());
            }

            // The last word before a token: a variable's or a typedef's name.
            private string LastWordBefore(int stop, int s)
            {
                for (int k = stop - 1; k >= s; k--)
                    if (Tokens[k].Kind == T.Word) return Tokens[k].Text;
                return null;
            }

            // The tokens between two indices, spaced: the code that the fingerprint is made of.
            private string Join(int s, int end)
            {
                var b = new StringBuilder();
                for (int k = s; k < end && k < Tokens.Count; k++) b.Append(Tokens[k].Text).Append(' ');
                return b.ToString();
            }
        }

        // Tokens and comments; strings and characters as one token each, preprocessor lines as one token.
        private static void Lex(string text, List<Token> tokens, List<Comment> comments)
        {
            int line = 1;
            int i = 0;
            int n = text.Length;
            bool lineHasCode = false;
            while (i < n)
            {
                char c = text[i];
                if (c == '\n') { line++; lineHasCode = false; i++; continue; }
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '/' && i + 1 < n && text[i + 1] == '/')
                {
                    int end = text.IndexOf('\n', i);
                    if (end < 0) end = n;
                    string body = text.Substring(i + 2, end - i - 2).Trim();
                    // Consecutive line comments on their own lines form one block.
                    Comment last = comments.Count > 0 ? comments[comments.Count - 1] : null;
                    if (!lineHasCode && last != null && last.OwnLine && last.Last == line - 1 && last.Text != null)
                    {
                        last.Last = line;
                        last.Text += "\n" + body;
                    }
                    else comments.Add(new Comment { First = line, Last = line, Text = body, OwnLine = !lineHasCode });
                    i = end;
                    continue;
                }
                if (c == '/' && i + 1 < n && text[i + 1] == '*')
                {
                    int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0) end = n - 2;
                    string body = text.Substring(i + 2, end - i - 2);
                    int first = line;
                    foreach (char ch in body) if (ch == '\n') line++;
                    comments.Add(new Comment { First = first, Last = line, Text = CSharpScanner.Strip("/*" + body + "*/"), OwnLine = !lineHasCode });
                    i = end + 2;
                    continue;
                }
                if (c == '#' && !lineHasCode)
                {
                    // To the line's end, with its continuations.
                    int s = i;
                    int start = line;
                    while (i < n && text[i] != '\n')
                    {
                        if (text[i] == '\\' && i + 1 < n && (text[i + 1] == '\n' || text[i + 1] == '\r')) { while (i < n && text[i] != '\n') i++; line++; }
                        // A comment after the directive belongs to it.
                        if (text[i] == '/' && i + 1 < n && (text[i + 1] == '/' || text[i + 1] == '*')) break;
                        i++;
                    }
                    tokens.Add(new Token { Kind = T.Pre, Text = text.Substring(s, i - s).Trim(), Line = start });
                    lineHasCode = true;
                    continue;
                }
                lineHasCode = true;
                if (char.IsLetter(c) || c == '_')
                {
                    int s = i;
                    while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    tokens.Add(new Token { Kind = T.Word, Text = text.Substring(s, i - s), Line = line });
                    continue;
                }
                if (char.IsDigit(c))
                {
                    int s = i;
                    while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '.' || text[i] == '\'')) i++;
                    tokens.Add(new Token { Kind = T.Number, Text = text.Substring(s, i - s), Line = line });
                    continue;
                }
                if (c == '"' || c == '\'' || c == '`')
                {
                    int s = i;
                    int startLine = line;
                    // C++ raw strings: R"tag( ... )tag"
                    if (c == '"' && i > 0 && text[i - 1] == 'R' && tokens.Count > 0 && tokens[tokens.Count - 1].Text.EndsWith("R"))
                    {
                        int paren = text.IndexOf('(', i);
                        string tag = text.Substring(i + 1, paren - i - 1);
                        int end = text.IndexOf(")" + tag + "\"", paren, StringComparison.Ordinal);
                        for (int k = i; k < end; k++) if (text[k] == '\n') line++;
                        i = end + tag.Length + 2;
                    }
                    else
                    {
                        i++;
                        while (i < n && text[i] != c)
                        {
                            if (text[i] == '\\') i++;
                            else if (text[i] == '\n') { if (c != '`') break; line++; }
                            i++;
                        }
                        i++;
                    }
                    tokens.Add(new Token { Kind = T.Text, Text = text.Substring(s, Math.Min(i, n) - s), Line = startLine });
                    continue;
                }
                if (c == ':' && i + 1 < n && text[i + 1] == ':')
                {
                    tokens.Add(new Token { Kind = T.Punct, Text = "::", Line = line });
                    i += 2;
                    continue;
                }
                tokens.Add(new Token { Kind = T.Punct, Text = c.ToString(), Line = line });
                i++;
            }
        }

        // A Unity shader: its CG and HLSL blocks as C (the rest blanked, its lines kept), and in ShaderLab the
        // shader itself and each pass.
        public static void ScanShaderLab(string path, string text, List<Element> into)
        {
            string[] lines = text.Replace("\r", "").Split('\n');
            var cg = new StringBuilder();
            bool inside = false;
            int passes = 0;
            for (int l = 0; l < lines.Length; l++)
            {
                string trimmed = lines[l].Trim();
                bool opens = trimmed.StartsWith("CGPROGRAM") || trimmed.StartsWith("CGINCLUDE") || trimmed.StartsWith("HLSLPROGRAM") || trimmed.StartsWith("HLSLINCLUDE");
                bool closes = trimmed.StartsWith("ENDCG") || trimmed.StartsWith("ENDHLSL");
                if (opens) { inside = true; cg.Append('\n'); continue; }
                if (closes) { inside = false; cg.Append('\n'); continue; }
                cg.Append(inside ? lines[l] : "").Append('\n');
                if (inside) continue;
                string bare = trimmed.Split(new[] { "//" }, StringSplitOptions.None)[0].Trim();
                if (bare.StartsWith("Shader \""))
                {
                    string name = bare.Substring(8, Math.Max(0, bare.IndexOf('"', 8) - 8));
                    into.Add(new Element { Path = path, Language = "shader", Kind = "shader", Name = name, Line = l + 1, Doc = CommentAbove(lines, l), Code = bare });
                }
                else if (bare == "Pass" || bare.StartsWith("Pass {") || bare.StartsWith("Pass{"))
                {
                    into.Add(new Element
                    {
                        Path = path, Language = "shader", Kind = "pass", Name = "Pass " + passes, Line = l + 1,
                        Doc = CommentAbove(lines, l), Code = PassCode(lines, l),
                    });
                    passes++;
                }
            }
            Scan(path, "shader", cg.ToString(), into);
        }

        // A pass's ShaderLab and the pragmas in it, as far as its closing brace.
        private static string PassCode(string[] lines, int start)
        {
            var b = new StringBuilder();
            int depth = 0;
            for (int l = start; l < lines.Length; l++)
            {
                string bare = lines[l].Split(new[] { "//" }, StringSplitOptions.None)[0];
                // A line of nothing but a comment adds nothing: a comment written into the pass leaves its code as it was.
                string kept = Element.Normalise(bare);
                if (kept.Length > 0) b.Append(kept).Append(' ');
                foreach (char c in bare)
                {
                    if (c == '{') depth++;
                    if (c == '}') depth--;
                }
                if (depth <= 0 && l > start && bare.Contains("}")) break;
            }
            return b.ToString();
        }

        // The // lines directly above a ShaderLab line.
        public static string CommentAbove(string[] lines, int index)
        {
            var parts = new List<string>();
            for (int l = index - 1; l >= 0; l--)
            {
                string t = lines[l].Trim();
                if (!t.StartsWith("//")) break;
                parts.Insert(0, t.Substring(2).Trim());
            }
            return string.Join("\n", parts).Trim();
        }
    }
}
