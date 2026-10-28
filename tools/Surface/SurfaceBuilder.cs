using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Surface
{
    // The surface of a ReDefinition build: everything a player or another mod can see of it that a release must not change
    // unnoticed, as sorted lines "key = value". It comes from the build's ReDefinition.dll: the settings' defaults and the
    // catalogue of other mods' code it was checked against. It also comes from the files of its GameData folder (the bundled mods'
    // registrations and the graphics profiles) and from its proxy's ini. Text for people (titles, tooltips) is left out, as is the
    // order of rows: only what changes what a setting means or holds.
    public static class SurfaceBuilder
    {
        // The keys of a node that are for people to read.
        private static readonly HashSet<string> Words = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "title", "tooltip", "description", "summary", "text", "order",
        };

        // The surface of the build whose assembly is `build` and whose files are under `repo`.
        public static SortedDictionary<string, string> Build(Assembly build, string repo)
        {
            var lines = new SortedDictionary<string, string>(StringComparer.Ordinal);
            OwnSettings(build, repo, lines);
            Foreign(build, lines);
            string gameData = Path.Combine(repo, "GameData", "ReDefinition");
            if (Directory.Exists(gameData))
                foreach (string file in Directory.GetFiles(gameData, "*.cfg", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
                    Config(file, gameData, lines);
            Ini(Path.Combine(repo, "src", "DxgiProxy", "ReDefinitionProxy.ini"), lines);
            return lines;
        }

        // The text of a surface, one line per entry.
        public static string Text(SortedDictionary<string, string> lines)
        {
            var text = new StringBuilder();
            text.Append("# The surface of a ReDefinition build, made by tools/Surface. Do not edit by hand.\n");
            foreach (KeyValuePair<string, string> line in lines) text.Append(line.Key).Append(" = ").Append(line.Value).Append('\n');
            return text.ToString();
        }

        // A surface read back from its text.
        public static SortedDictionary<string, string> Parse(string text)
        {
            var lines = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (raw.Length == 0 || raw[0] == '#') continue;
                int at = raw.IndexOf(" = ", StringComparison.Ordinal);
                if (at > 0) lines[raw.Substring(0, at)] = raw.Substring(at + 3);
            }
            return lines;
        }

        // ReDefinition's own settings: each key of settings.cfg with the text a fresh settings object writes for it.
        private static void OwnSettings(Assembly build, string repo, SortedDictionary<string, string> lines)
        {
            Type own = build.GetType("ReDefinition.Modules.OwnSettings");
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo fields = own == null ? null : own.GetField("Fields", any);
            MethodInfo valueOf = own == null ? null : own.GetMethod("ValueOf", any);
            if (fields == null || valueOf == null)
            {
                OwnSettingsFromSource(repo, lines);
                return;
            }
            object fresh = Activator.CreateInstance(own);
            foreach (object field in (IEnumerable)fields.GetValue(null))
            {
                string key = (string)field.GetType().GetField("Key", any).GetValue(field);
                lines["own." + key] = Flat((string)valueOf.Invoke(fresh, new object[] { key }));
            }
        }

        // The same for a build from before the settings were a table (v0.1.3): Save wrote each key from a field with `node.AddValue`, and the
        // default is the field's initializer. Only that shape is read: a bool, a number written with ToString("0.00"), an enum or the
        // unbound key.
        private static void OwnSettingsFromSource(string repo, SortedDictionary<string, string> lines)
        {
            string file = Path.Combine(repo, "src", "OwnSettings.cs");
            if (!File.Exists(file)) return;
            string text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match add in System.Text.RegularExpressions.Regex.Matches(
                         text, @"node\.AddValue\(""(\w+)"",\s*(\w+)(\.ToString\(""([^""]+)""[^)]*\))?"))
            {
                string key = add.Groups[1].Value, member = add.Groups[2].Value, format = add.Groups[4].Value;
                System.Text.RegularExpressions.Match field = System.Text.RegularExpressions.Regex.Match(
                    text, @"public\s+([\w.]+)\s+" + member + @"\s*(=\s*([^;]+))?;");
                if (!field.Success) continue;
                string type = field.Groups[1].Value, initial = field.Groups[3].Value.Trim();
                string value;
                if (type == "bool") value = initial == "true" ? "True" : "False";
                else if (type == "float") value = (initial.Length == 0 ? 0f : float.Parse(initial.TrimEnd('f'), CultureInfo.InvariantCulture)).ToString(format.Length > 0 ? format : "0.###", CultureInfo.InvariantCulture);
                else if (initial.EndsWith("NoneText", StringComparison.Ordinal)) value = "None";
                else value = initial.Substring(initial.LastIndexOf('.') + 1);
                lines["own." + key] = value;
            }
        }

        // The catalogue of other mods' code the build was checked against: the mods and what each is used for.
        private static void Foreign(Assembly build, SortedDictionary<string, string> lines)
        {
            Type catalogue = build.GetType("ReDefinition.Core.ForeignAssumptions");
            FieldInfo all = catalogue == null ? null : catalogue.GetField("All", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (all == null) return;
            foreach (object assumption in (IEnumerable)all.GetValue(null))
            {
                Type type = assumption.GetType();
                string id = Convert.ToString(Member(type, "Id", assumption), CultureInfo.InvariantCulture);
                string mod = Convert.ToString(Member(type, "Mod", assumption), CultureInfo.InvariantCulture);
                lines["foreign." + id] = mod;
            }
        }

        // The value of a field or property of an object, by name.
        private static object Member(Type type, string name, object target)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo field = type.GetField(name, any);
            if (field != null) return field.GetValue(target);
            PropertyInfo property = type.GetProperty(name, any);
            return property != null ? property.GetValue(target, null) : null;
        }

        // A registration, profile or patch file: every value of every node, under the path of the nodes it is in.
        private static void Config(string file, string root, SortedDictionary<string, string> lines)
        {
            string relative = file.Substring(root.Length + 1).Replace('\\', '/');
            string prefix = relative.StartsWith("Mods/", StringComparison.Ordinal) ? "mod"
                          : relative.StartsWith("Profiles/", StringComparison.Ordinal) ? "profile" : "cfg." + Path.GetFileNameWithoutExtension(file);
            if (relative.StartsWith("Patches/", StringComparison.Ordinal))
            {
                lines[prefix + ".sha"] = Flat(Sha(File.ReadAllText(file)));
                return;
            }
            foreach (Node node in ParseConfig(File.ReadAllLines(file)).Children) Walk(node, prefix, lines);
        }

        // One node and its children under `path`: the key of each value, and for a node with a name that name in brackets.
        private static void Walk(Node node, string path, SortedDictionary<string, string> lines)
        {
            string name = node.Values.Where(v => v.Key == "name").Select(v => v.Value).FirstOrDefault();
            string here = path + "." + node.Name + (name != null ? "[" + name + "]" : "");
            lines[here + ".@exists"] = "True";
            foreach (KeyValuePair<string, string> value in node.Values)
            {
                if (Words.Contains(value.Key) || value.Key == "name") continue;
                string key = here + "." + value.Key;
                for (int copy = 2; lines.ContainsKey(key); copy++) key = here + "." + value.Key + "#" + copy;
                lines[key] = Flat(value.Value);
            }
            var seen = new Dictionary<string, int>();
            foreach (Node child in node.Children)
            {
                string label = child.Name + "|" + child.Values.Where(v => v.Key == "name").Select(v => v.Value).FirstOrDefault();
                int number;
                seen.TryGetValue(label, out number);
                seen[label] = number + 1;
                Walk(child, number == 0 ? here : here + "#" + (number + 1), lines);
            }
        }

        // The proxy's ini: each key under its section, the comments left out.
        private static void Ini(string file, SortedDictionary<string, string> lines)
        {
            if (!File.Exists(file)) return;
            string section = "";
            foreach (string raw in File.ReadAllLines(file))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[' && line[line.Length - 1] == ']') { section = line.Substring(1, line.Length - 2); continue; }
                int at = line.IndexOf('=');
                if (at <= 0) continue;
                lines["proxy." + section + "." + line.Substring(0, at).Trim()] = Flat(line.Substring(at + 1).Trim());
            }
        }

        // A node of a ConfigNode file.
        private sealed class Node
        {
            public string Name = "";
            public readonly List<KeyValuePair<string, string>> Values = new List<KeyValuePair<string, string>>();
            public readonly List<Node> Children = new List<Node>();
        }

        // A ConfigNode file read the way KSP reads it: NAME, then a brace, with key = value lines and nodes inside.
        private static Node ParseConfig(string[] fileLines)
        {
            var root = new Node();
            var stack = new Stack<Node>();
            stack.Push(root);
            string pending = null;
            foreach (string raw in fileLines)
            {
                string line = Uncomment(raw).Trim();
                if (line.Length == 0) continue;
                if (line == "{")
                {
                    var child = new Node { Name = pending ?? "" };
                    stack.Peek().Children.Add(child);
                    stack.Push(child);
                    pending = null;
                }
                else if (line == "}")
                {
                    if (stack.Count > 1) stack.Pop();
                }
                else if (line.EndsWith("{", StringComparison.Ordinal))
                {
                    var child = new Node { Name = line.Substring(0, line.Length - 1).Trim() };
                    stack.Peek().Children.Add(child);
                    stack.Push(child);
                }
                else
                {
                    int at = line.IndexOf('=');
                    if (at > 0) stack.Peek().Values.Add(new KeyValuePair<string, string>(line.Substring(0, at).Trim(), line.Substring(at + 1).Trim()));
                    else pending = line;
                }
            }
            return root;
        }

        // A line without its // comment; a // inside a word such as a URL stays.
        private static string Uncomment(string line)
        {
            for (int i = 0; i + 1 < line.Length; i++)
                if (line[i] == '/' && line[i + 1] == '/' && (i == 0 || char.IsWhiteSpace(line[i - 1]))) return line.Substring(0, i);
            return line;
        }

        // A value on one line.
        private static string Flat(string value)
        {
            return (value ?? "").Replace("\r", "").Replace("\n", " ");
        }

        // A short hash of a text with its line ends and the white space round its lines left out.
        private static string Sha(string text)
        {
            string normal = string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()));
            using (var sha = System.Security.Cryptography.SHA1.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(normal)).Take(6).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }
    }
}
