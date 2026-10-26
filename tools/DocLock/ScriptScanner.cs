using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace DocLock
{
    // The script languages' elements are read line by line. They are Python's module, classes and functions (at
    // the top level and in classes), PowerShell's script and functions, and the dashboard's JavaScript -- its file,
    // named functions at any depth, functions held in variables, classes and their methods, and top-level values. Their
    // documentation is the comment block directly above (decorators may stand between), Python's docstring, or a
    // PowerShell script's opening comment.
    internal static class ScriptScanner
    {
        // The lines that declare: Python's def and class, PowerShell's function, JavaScript's function, a function
        // held in a value, a class, a method, a top-level value.
        private static readonly Regex PyDef = new Regex(@"^(\s*)(async\s+def|def|class)\s+(\w+)");
        private static readonly Regex PsFunction = new Regex(@"^\s*function\s+([\w-]+)", RegexOptions.IgnoreCase);
        private static readonly Regex JsFunction = new Regex(@"^\s*(?:export\s+)?(?:async\s+)?function\s*\*?\s*(\w+)\s*\(");
        private static readonly Regex JsHeld = new Regex(@"^\s*(?:export\s+)?(?:const|let|var)\s+(\w+)\s*=\s*(?:async\s+)?(?:function\b|\([^)]*\)\s*=>|\w+\s*=>)");
        private static readonly Regex JsClass = new Regex(@"^\s*(?:export\s+)?class\s+(\w+)");
        private static readonly Regex JsMethod = new Regex(@"^\s+(?:static\s+|async\s+|get\s+|set\s+)*(\w+)\s*\([^)]*\)\s*\{\s*$");
        private static readonly Regex JsTopValue = new Regex(@"^(?:export\s+)?(?:const|let|var)\s+(\w+)\s*=");

        // A Python file's module, its top-level classes and functions, and the methods of its classes.
        public static void Python(string path, string text, List<Element> into)
        {
            string[] lines = text.Replace("\r", "").Split('\n');
            into.Add(new Element { Path = path, Language = "py", Kind = "module", Name = path, Line = 1, Doc = PyModuleDoc(lines), Code = "module" });
            // Open blocks: their indentation and whether a class.
            var stack = new List<(int indent, bool isClass)>();
            for (int l = 0; l < lines.Length; l++)
            {
                Match m = PyDef.Match(lines[l]);
                if (!m.Success) continue;
                int indent = m.Groups[1].Value.Replace("\t", "    ").Length;
                while (stack.Count > 0 && stack[stack.Count - 1].indent >= indent) stack.RemoveAt(stack.Count - 1);
                bool isClass = m.Groups[2].Value == "class";
                bool topOrMethod = stack.Count == 0 || stack.Count == 1 && stack[0].isClass;
                stack.Add((indent, isClass));
                if (!topOrMethod) continue;
                string name = (stack.Count == 2 ? ClassName(lines, stack[0].indent, l) + "." : "") + m.Groups[3].Value;
                string doc = PyDocstring(lines, l);
                if (doc.Length == 0) doc = HashAbove(lines, l, true);
                into.Add(new Element
                {
                    Path = path, Language = "py", Kind = isClass ? "class" : "function", Name = name, Line = l + 1,
                    Doc = doc, Code = isClass ? Element.Normalise(StripHash(lines[l])) : PyBody(lines, l, indent),
                });
            }
        }

        // The name of the class a method stands in: the nearest class line above at its indentation.
        private static string ClassName(string[] lines, int indent, int before)
        {
            for (int l = before - 1; l >= 0; l--)
            {
                Match m = PyDef.Match(lines[l]);
                if (m.Success && m.Groups[2].Value == "class" && m.Groups[1].Value.Length == indent) return m.Groups[3].Value;
            }
            return "";
        }

        // A Python module's documentation: its docstring, or the comment block at its top.
        private static string PyModuleDoc(string[] lines)
        {
            int l = 0;
            while (l < lines.Length && (lines[l].StartsWith("#!") || lines[l].StartsWith("# -*-") || lines[l].Trim().Length == 0)) l++;
            if (l < lines.Length && (lines[l].TrimStart().StartsWith("\"\"\"") || lines[l].TrimStart().StartsWith("'''")))
                return TripleQuoted(lines, l);
            var parts = new List<string>();
            while (l < lines.Length && lines[l].TrimStart().StartsWith("#")) parts.Add(lines[l++].TrimStart().TrimStart('#').Trim());
            return string.Join("\n", parts).Trim();
        }

        // The docstring: the first statement of the body, a string.
        private static string PyDocstring(string[] lines, int defLine)
        {
            int l = defLine;
            // The header may run over several lines, to its colon.
            while (l < lines.Length && !lines[l].TrimEnd().EndsWith(":")) l++;
            l++;
            while (l < lines.Length && lines[l].Trim().Length == 0) l++;
            if (l >= lines.Length) return "";
            string t = lines[l].TrimStart();
            if (t.StartsWith("\"\"\"") || t.StartsWith("'''") || t.StartsWith("r\"\"\"")) return TripleQuoted(lines, l);
            return "";
        }

        // A triple-quoted string from its first line, without its quotes.
        private static string TripleQuoted(string[] lines, int l)
        {
            string first = lines[l].TrimStart();
            if (first.StartsWith("r")) first = first.Substring(1);
            string quote = first.Substring(0, 3);
            var b = new StringBuilder(first.Substring(3));
            if (b.ToString().Contains(quote)) return b.ToString().Substring(0, b.ToString().IndexOf(quote, StringComparison.Ordinal)).Trim();
            for (l++; l < lines.Length; l++)
            {
                int end = lines[l].IndexOf(quote, StringComparison.Ordinal);
                if (end >= 0) { b.Append('\n').Append(lines[l].Substring(0, end)); break; }
                b.Append('\n').Append(lines[l]);
            }
            return b.ToString().Trim();
        }

        // A def's or class's code: its lines as far as its indentation reaches, comments left out.
        private static string PyBody(string[] lines, int l, int indent)
        {
            var b = new StringBuilder();
            b.Append(StripHash(lines[l]));
            for (int k = l + 1; k < lines.Length; k++)
            {
                string line = lines[k];
                if (line.Trim().Length == 0) continue;
                int own = line.Length - line.TrimStart().Length;
                if (own <= indent) break;
                b.Append(' ').Append(StripHash(line));
            }
            return Element.Normalise(b.ToString());
        }

        // A Python line without its comment.
        private static string StripHash(string line)
        {
            // A '#' outside quotes starts a comment.
            bool single = false, dbl = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\'' && !dbl) single = !single;
                else if (c == '"' && !single) dbl = !dbl;
                else if (c == '#' && !single && !dbl) return line.Substring(0, i);
            }
            return line;
        }

        // The block of '#' comments directly above, decorators allowed between.
        private static string HashAbove(string[] lines, int index, bool decorators)
        {
            int l = index - 1;
            while (decorators && l >= 0 && lines[l].TrimStart().StartsWith("@")) l--;
            var parts = new List<string>();
            for (; l >= 0; l--)
            {
                string t = lines[l].TrimStart();
                if (!t.StartsWith("#")) break;
                parts.Insert(0, t.TrimStart('#').Trim());
            }
            return string.Join("\n", parts).Trim();
        }

        // A PowerShell file's script and its functions.
        public static void PowerShell(string path, string text, List<Element> into)
        {
            string[] lines = text.Replace("\r", "").Split('\n');
            into.Add(new Element { Path = path, Language = "ps1", Kind = "script", Name = path, Line = 1, Doc = PsScriptDoc(lines), Code = "script" });
            for (int l = 0; l < lines.Length; l++)
            {
                Match m = PsFunction.Match(lines[l]);
                if (!m.Success) continue;
                into.Add(new Element
                {
                    Path = path, Language = "ps1", Kind = "function", Name = m.Groups[1].Value, Line = l + 1,
                    Doc = HashAbove(lines, l, false), Code = Braced(lines, l),
                });
            }
        }

        // A PowerShell script's documentation: its opening <# #> block or # lines.
        private static string PsScriptDoc(string[] lines)
        {
            int l = 0;
            while (l < lines.Length && lines[l].Trim().Length == 0) l++;
            if (l < lines.Length && lines[l].TrimStart().StartsWith("<#"))
            {
                var b = new StringBuilder();
                for (; l < lines.Length; l++)
                {
                    b.Append(lines[l].Replace("<#", "").Replace("#>", "")).Append('\n');
                    if (lines[l].Contains("#>")) break;
                }
                return b.ToString().Trim();
            }
            var parts = new List<string>();
            while (l < lines.Length && lines[l].TrimStart().StartsWith("#")) parts.Add(lines[l++].TrimStart().TrimStart('#').Trim());
            return string.Join("\n", parts).Trim();
        }

        // A JavaScript file's script, its functions, classes, methods and top-level values.
        public static void JavaScript(string path, string text, List<Element> into)
        {
            string[] lines = text.Replace("\r", "").Split('\n');
            string fileDoc = SlashesAbove(lines, FirstCodeLine(lines) , true);
            into.Add(new Element { Path = path, Language = "js", Kind = "script", Name = path, Line = 1, Doc = fileDoc, Code = "script" });
            string currentClass = null;
            int classDepth = -1;
            int depth = 0;
            for (int l = 0; l < lines.Length; l++)
            {
                string line = lines[l];
                string code = line.Split(new[] { "//" }, StringSplitOptions.None)[0];
                Match m;
                string kind = null, name = null;
                if ((m = JsClass.Match(code)).Success) { kind = "class"; name = m.Groups[1].Value; currentClass = name; classDepth = depth; }
                else if ((m = JsFunction.Match(code)).Success) { kind = "function"; name = m.Groups[1].Value; }
                else if ((m = JsHeld.Match(code)).Success) { kind = "function"; name = m.Groups[1].Value; }
                else if (currentClass != null && depth == classDepth + 1 && (m = JsMethod.Match(code)).Success
                         && m.Groups[1].Value != "if" && m.Groups[1].Value != "for" && m.Groups[1].Value != "while"
                         && m.Groups[1].Value != "switch" && m.Groups[1].Value != "catch")
                {
                    kind = "method";
                    name = currentClass + "." + m.Groups[1].Value;
                }
                else if (depth == 0 && (m = JsTopValue.Match(code)).Success) { kind = "variable"; name = m.Groups[1].Value; }
                if (kind != null)
                {
                    string doc = SlashesAbove(lines, l, false);
                    if (doc.Length == 0 && kind == "variable")
                    {
                        // A run of top-level values shares the comment above its first.
                        int k = l - 1;
                        while (k >= 0 && JsTopValue.IsMatch(lines[k])) k--;
                        if (k < l - 1) doc = SlashesAbove(lines, k + 1, false);
                    }
                    into.Add(new Element { Path = path, Language = "js", Kind = kind, Name = name, Line = l + 1, Doc = doc, Code = Braced(lines, l) });
                }
                foreach (char c in StripStrings(code))
                {
                    if (c == '{') depth++;
                    else if (c == '}')
                    {
                        depth--;
                        if (currentClass != null && depth == classDepth) currentClass = null;
                    }
                }
            }
        }

        // The first line that is neither blank nor a comment.
        private static int FirstCodeLine(string[] lines)
        {
            for (int l = 0; l < lines.Length; l++)
            {
                string t = lines[l].Trim();
                if (t.Length > 0 && !t.StartsWith("//") && !t.StartsWith("/*") && !t.StartsWith("*")) return l;
            }
            return lines.Length;
        }

        // A line without its strings, so that braces in them are not counted.
        private static string StripStrings(string code)
        {
            var b = new StringBuilder();
            char quote = '\0';
            foreach (char c in code)
            {
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c == '"' || c == '\'' || c == '`') { quote = c; continue; }
                b.Append(c);
            }
            return b.ToString();
        }

        // The block of '//' comments (or a '/* */' block) directly above; for the file, any above its first code.
        private static string SlashesAbove(string[] lines, int index, bool file)
        {
            var parts = new List<string>();
            int l = index - 1;
            if (l >= 0 && lines[l].Trim().EndsWith("*/"))
            {
                while (l >= 0)
                {
                    parts.Insert(0, lines[l].Trim().TrimStart('/').TrimStart('*').TrimEnd('/').TrimEnd('*').Trim());
                    if (lines[l].Trim().StartsWith("/*")) break;
                    l--;
                }
                return string.Join("\n", parts).Trim();
            }
            for (; l >= 0; l--)
            {
                string t = lines[l].Trim();
                if (file && t.Length == 0) continue;
                if (!t.StartsWith("//")) break;
                parts.Insert(0, t.Substring(2).Trim());
            }
            return string.Join("\n", parts).Trim();
        }

        // From a line to its braces' end (or the line alone), comments left out.
        private static string Braced(string[] lines, int start)
        {
            var b = new StringBuilder();
            int depth = 0;
            bool opened = false;
            for (int l = start; l < lines.Length; l++)
            {
                string code = lines[l].Split(new[] { "//" }, StringSplitOptions.None)[0];
                if (code.TrimStart().StartsWith("#")) code = "";
                // A line of nothing but a comment adds nothing: a comment written into the function leaves its code as it was.
                string kept = Element.Normalise(code);
                if (kept.Length > 0) b.Append(kept).Append(' ');
                foreach (char c in StripStrings(code))
                {
                    if (c == '{') { depth++; opened = true; }
                    else if (c == '}') depth--;
                }
                if (!opened && l == start && !code.Contains("{")) break;
                if (opened && depth <= 0) break;
            }
            return b.ToString();
        }
    }
}
