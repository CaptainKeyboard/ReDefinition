using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DocLock
{
    // The configuration files' elements, read line by line: an ini file, its sections and its keys
    // (ReDefinitionProxy.ini), and a KSP ConfigNode file and its nodes (the registrations, profiles and
    // Module Manager patches under GameData/ReDefinition). Their documentation: the comment block directly
    // above, or a run of keys or nodes of one name standing together under the comment of the first.
    internal static class ConfigScanner
    {
        // An ini section header and an ini key.
        private static readonly Regex IniSection = new Regex(@"^\s*\[([^\]]+)\]\s*$");
        private static readonly Regex IniKey = new Regex(@"^\s*([A-Za-z_][\w.]*)\s*=");

        // An ini file, its sections and its keys; a key's code is its line, so a changed default marks its comment
        // dirty.
        public static void Ini(string path, string text, List<Element> into)
        {
            string[] lines = text.Replace("\r", "").Split('\n');
            into.Add(new Element { Path = path, Language = "ini", Kind = "config", Name = path, Line = 1, Doc = Leading(lines, ';'), Code = "config" });
            string section = "";
            Element runFirst = null;
            for (int l = 0; l < lines.Length; l++)
            {
                string line = lines[l];
                Match s = IniSection.Match(line);
                if (s.Success)
                {
                    section = s.Groups[1].Value;
                    into.Add(new Element { Path = path, Language = "ini", Kind = "section", Name = "[" + section + "]", Line = l + 1, Doc = Above(lines, l, ';'), Code = "[" + section + "]" });
                    runFirst = null;
                    continue;
                }
                Match k = IniKey.Match(line);
                if (!k.Success)
                {
                    if (line.Trim().Length == 0 || line.TrimStart().StartsWith(";")) runFirst = null;
                    continue;
                }
                var e = new Element
                {
                    Path = path, Language = "ini", Kind = "key", Name = (section.Length > 0 ? section + "." : "") + k.Groups[1].Value,
                    Line = l + 1, Doc = Above(lines, l, ';'), Code = Element.Normalise(line),
                };
                // A key straight under the one before, with no comment of its own, stands in that key's run.
                if (e.Doc.Length == 0 && runFirst != null && Rules.Enough(runFirst.Doc, runFirst.Name)) e.SharesDoc = true;
                if (e.Doc.Length > 0 || runFirst == null) runFirst = e;
                into.Add(e);
            }
        }

        // A ConfigNode file and its nodes. A node is named by its path of headers, each with its name or key
        // value where it has one: MOD_SETTINGS[eve]/SETTING[quality]. Its code is its own values, not its child
        // nodes', so a changed setting marks only that setting's comment dirty.
        public static void Cfg(string path, string text, List<Element> into)
        {
            string[] lines = text.Replace("\r", "").Split('\n');
            var file = new Element { Path = path, Language = "cfg", Kind = "config", Name = path, Line = 1, Doc = Leading(lines, '/'), Code = "config" };
            into.Add(file);
            // The nodes at the top level, for the file comment that describes a file of one.
            var top = new List<Element>();
            // The nodes inside a Module Manager patch, which are its body rather than elements of their own.
            var body = new HashSet<Element>();
            // The open nodes: their element, their values so far, and the header of the last child closed, for runs.
            var stack = new List<(Element node, StringBuilder code, string lastChild, Element runFirst)>();
            string topLast = null;
            Element topRun = null;
            for (int l = 0; l < lines.Length; l++)
            {
                string line = StripComment(lines[l]).Trim();
                if (line.Length == 0) continue;
                if (line == "}")
                {
                    if (stack.Count == 0) continue;
                    var closed = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                    // A node in a Module Manager patch is the patch's body: its code is the patch's.
                    if (body.Contains(closed.node))
                    {
                        stack[stack.Count - 1].code.Append(closed.code).Append("}\n");
                        continue;
                    }
                    closed.node.Code = Element.Normalise(closed.code.ToString());
                    // A setting the registration format describes itself: its title and tooltip are what the player
                    // reads, and its documentation.
                    if (closed.node.Doc.Length == 0) closed.node.Doc = Described(closed.code.ToString());
                    continue;
                }
                int equals = line.IndexOf('='), bracket = line.IndexOf('[');
                if (equals >= 0 && (bracket < 0 || equals < bracket) && !line.EndsWith("{"))
                {
                    if (stack.Count > 0) stack[stack.Count - 1].code.Append(line).Append('\n');
                    continue;
                }
                // A header: the node opens on this line or the next.
                string header = line.TrimEnd('{').Trim();
                bool opensHere = line.EndsWith("{");
                int headerLine = l;
                if (!opensHere)
                {
                    int next = l + 1;
                    while (next < lines.Length && StripComment(lines[next]).Trim().Length == 0) next++;
                    if (next >= lines.Length || !StripComment(lines[next]).Trim().StartsWith("{")) continue;
                    l = next;
                }
                string named = header + NodeName(lines, l + 1);
                string full = string.Join("/", stack.Select(f => f.node.Name.Substring(f.node.Name.LastIndexOf('/') + 1)).Concat(new[] { named }));
                var e = new Element { Path = path, Language = "cfg", Kind = "node", Name = full, Line = headerLine + 1, Doc = Above(lines, headerLine, '/') };
                if (stack.Count > 0 && (body.Contains(stack[stack.Count - 1].node) || IsPatch(stack[0].node.Name)))
                {
                    if (opensHere && line.EndsWith("}") && line.IndexOf('{') < line.LastIndexOf('}'))
                        stack[stack.Count - 1].code.Append(line).Append('\n');
                    else
                    {
                        body.Add(e);
                        stack.Add((e, new StringBuilder(named + "\n"), null, null));
                    }
                    continue;
                }
                // A node of the same header straight after its sibling, with no comment of its own, stands in that run.
                string last = stack.Count > 0 ? stack[stack.Count - 1].lastChild : topLast;
                Element run = stack.Count > 0 ? stack[stack.Count - 1].runFirst : topRun;
                if (e.Doc.Length == 0 && last == header && run != null && Rules.Enough(run.Doc, run.Name)) e.SharesDoc = true;
                if (e.Doc.Length > 0 || last != header) run = e;
                if (stack.Count > 0) stack[stack.Count - 1] = (stack[stack.Count - 1].node, stack[stack.Count - 1].code, header, run);
                else { topLast = header; topRun = run; }
                into.Add(e);
                stack.Add((e, new StringBuilder(header + "\n"), null, null));
                // A one-line node: NAME { key = value }.
                if (opensHere && line.EndsWith("}") && line.IndexOf('{') < line.LastIndexOf('}'))
                {
                    stack.RemoveAt(stack.Count - 1);
                    e.Code = Element.Normalise(line);
                }
                if (stack.Count == 0 || stack.Count == 1 && stack[0].node == e) top.Add(e);
            }
            // A file of one top-level node, a registration or a patch, has that node described by the file's comment.
            if (top.Count == 1 && top[0].Doc.Length == 0 && Rules.Enough(file.Doc, path)) top[0].DocumentedElsewhere = true;
        }

        // A node's name in brackets: its first name or key value, read from its own lines; empty where it has none.
        private static string NodeName(string[] lines, int from)
        {
            int depth = 0;
            for (int l = from; l < lines.Length; l++)
            {
                string line = StripComment(lines[l]).Trim();
                if (line.StartsWith("{")) { depth++; continue; }
                if (line == "}") { if (depth == 0) break; depth--; continue; }
                if (depth > 0) continue;
                Match m = Regex.Match(line, @"^(name|key|id)\s*=\s*(.+)$");
                if (m.Success) return "[" + m.Groups[2].Value.Trim() + "]";
            }
            return "";
        }

        // Whether a header is a Module Manager operation on nodes of others (@, +, $, %, !, -, #), not a node of its
        // own.
        private static bool IsPatch(string header) => header.Length > 0 && "@+$%!-#".IndexOf(header[0]) >= 0;

        // A node's own description in its values: its title and its tooltip (the registrations' SETTING and KEY), or
        // its description; empty where it has none.
        private static string Described(string values)
        {
            string title = "", tooltip = "";
            foreach (string line in values.Split('\n'))
            {
                Match m = Regex.Match(line, @"^\s*(title|tooltip|description)\s*=\s*(.+)$");
                if (!m.Success) continue;
                if (m.Groups[1].Value == "title") title = m.Groups[2].Value.Trim();
                else tooltip = m.Groups[2].Value.Trim().Replace("\\n", " ");
            }
            return tooltip.Length == 0 ? "" : (title.Length > 0 ? title + ": " : "") + tooltip;
        }

        // A line with its comment taken off: ";" in an ini, "//" in a ConfigNode file.
        private static string StripComment(string line)
        {
            int at = line.IndexOf("//", System.StringComparison.Ordinal);
            return at >= 0 ? line.Substring(0, at) : line;
        }

        // Whether a line is a comment of the file's kind: ";" or "//".
        private static bool IsComment(string line, char marker)
        {
            string t = line.TrimStart();
            return marker == ';' ? t.StartsWith(";") : t.StartsWith("//");
        }

        // A comment line's text, its marker taken off.
        private static string CommentText(string line, char marker)
        {
            string t = line.TrimStart();
            return (marker == ';' ? t.TrimStart(';') : t.Substring(2)).Trim();
        }

        // The comment block directly above a line, or the comment behind it on its line.
        private static string Above(string[] lines, int index, char marker)
        {
            var parts = new List<string>();
            for (int l = index - 1; l >= 0 && IsComment(lines[l], marker); l--) parts.Insert(0, CommentText(lines[l], marker));
            string doc = string.Join("\n", parts).Trim();
            if (doc.Length > 0) return doc;
            int behind = marker == ';' ? -1 : lines[index].IndexOf("//", System.StringComparison.Ordinal);
            return behind > 0 ? lines[index].Substring(behind + 2).Trim() : "";
        }

        // The file's own comment: the comment block it starts with.
        private static string Leading(string[] lines, char marker)
        {
            var parts = new List<string>();
            int l = 0;
            while (l < lines.Length && lines[l].Trim().Length == 0) l++;
            for (; l < lines.Length && IsComment(lines[l], marker); l++) parts.Add(CommentText(lines[l], marker));
            return string.Join("\n", parts).Trim();
        }
    }
}
