using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace ReDefinition.Framework
{
    // A member path of a registration (docs/modders/registering-a-mod.md): a type's full
    // name and the members from it -- fields, properties, an indexer with a string
    // key, a method without parameters last -- resolved once, then read, written or
    // called through reflection whenever asked.
    //
    // The type is the longest leading part that names a type from the mod's folder
    // (ModFolder). The first member static: the path starts there; an instance
    // member of a Unity object: the one in the scene, found again once it is
    // destroyed. A struct on the way holds a copy, so a write puts it back whole
    // into what holds it -- Parallax's settings groups -- and whatever holds a
    // struct must take it back.
    internal sealed class MemberPath
    {
        private const BindingFlags Statics =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        private const BindingFlags Instances = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private sealed class Segment
        {
            public string Name;
            public FieldInfo Field;
            public PropertyInfo Property;
            public PropertyInfo Indexer;
            public string Key;
            public MethodInfo Method;
            public Type ValueType;
        }

        public readonly string Text;
        public readonly Type Root;
        private readonly List<Segment> segments = new List<Segment>();
        private bool fromScene;
        private UnityEngine.Object found;

        private MemberPath(string text, Type root)
        {
            Text = text;
            Root = root;
        }

        // The type of the value at the end; null for a method.
        public Type ValueType
        {
            get { return segments[segments.Count - 1].ValueType; }
        }

        public bool IsMethod
        {
            get { return segments[segments.Count - 1].Method != null; }
        }

        // The field at the end, where the path ends in one; null for a property,
        // an indexer or a method.
        public FieldInfo EndField
        {
            get { return segments[segments.Count - 1].Field; }
        }

        // Null where the path does not resolve, with the reason in `problem`.
        public static MemberPath Resolve(string text, ModFolder folder, out string problem)
        {
            return Resolve(text, folder, false, out problem);
        }

        // A path that is only read -- `ready` -- may end in a member without a
        // setter.
        public static MemberPath ResolveReadable(string text, ModFolder folder, out string problem)
        {
            return Resolve(text, folder, true, out problem);
        }

        private static MemberPath Resolve(string text, ModFolder folder, bool readOnly, out string problem)
        {
            problem = null;
            string[] parts = Split(text);
            for (int count = parts.Length - 1; count >= 1; count--)
            {
                string typeName = string.Join(".", parts, 0, count);
                if (typeName.IndexOf('[') >= 0) continue;
                Type type = FindType(typeName, folder);
                if (type == null) continue;
                MemberPath path = new MemberPath(text, type);
                problem = path.Resolve(parts, count, readOnly);
                return problem == null ? path : null;
            }
            problem = "no type from the mod's folder starts " + text;
            return null;
        }

        // Whether a type of that name comes from the mod's folder, and has the
        // members that follow it -- static or not: a build is told by what it has,
        // not by what can be reached from outside (BUILD, needs).
        public static bool Exists(string text, ModFolder folder)
        {
            string[] parts = Split(text);
            for (int count = parts.Length; count >= 1; count--)
            {
                Type type = FindType(string.Join(".", parts, 0, count), folder);
                if (type == null) continue;
                for (int i = count; i < parts.Length; i++)
                {
                    Segment segment = Member(type, parts[i], true, i == parts.Length - 1);
                    if (segment == null) return false;
                    type = segment.ValueType;
                    if (type == null && i < parts.Length - 1) return false;
                }
                return true;
            }
            return false;
        }

        // A type of that name from the mod's folder -- not merely the first loaded:
        // another copy of a mod's DLL elsewhere must not stand in for its own.
        // Without a folder, the first loaded.
        internal static Type FindType(string name, ModFolder folder)
        {
            return folder != null ? folder.FindType(name) : TypeLookup.Find(name);
        }

        // At the dots outside brackets: an indexer's key may hold a dot.
        private static string[] Split(string text)
        {
            List<string> parts = new List<string>();
            text = text ?? "";
            int depth = 0;
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '[') depth++;
                else if (text[i] == ']') depth = Math.Max(0, depth - 1);
                else if (text[i] == '.' && depth == 0)
                {
                    parts.Add(text.Substring(start, i - start));
                    start = i + 1;
                }
            }
            parts.Add(text.Substring(start));
            return parts.ToArray();
        }

        private string Resolve(string[] parts, int start, bool readOnly)
        {
            Type current = Root;
            for (int i = start; i < parts.Length; i++)
            {
                string name = parts[i];
                string key = null;
                int open = name.IndexOf('[');
                if (open >= 0)
                {
                    if (open == 0 || !name.EndsWith("]", StringComparison.Ordinal)) return "'" + name + "' is not name[key]";
                    key = name.Substring(open + 1, name.Length - open - 2);
                    name = name.Substring(0, open);
                }
                bool first = segments.Count == 0;
                bool last = i == parts.Length - 1;
                if (segments.Count > 0 && segments[segments.Count - 1].Method != null)
                    return "nothing follows the method " + segments[segments.Count - 1].Name;

                Segment segment = Member(current, name, first, last && key == null);
                if (segment == null) return current.FullName + " has no member " + name;
                if (first && !IsStatic(segment))
                {
                    if (!typeof(UnityEngine.Object).IsAssignableFrom(Root))
                        return name + " is an instance member of " + Root.FullName + ", which is no object in the scene";
                    fromScene = true;
                }
                segments.Add(segment);
                current = segment.ValueType;

                if (key == null) continue;
                PropertyInfo indexer = current != null ? Indexer(current) : null;
                if (indexer == null) return name + " has no indexer with a string key";
                segments.Add(new Segment { Name = name + "[" + key + "]", Indexer = indexer, Key = key, ValueType = indexer.PropertyType });
                current = indexer.PropertyType;
            }

            // A method writes nothing back: `save` and `after` may be called on a
            // struct's copy.
            Segment end = segments[segments.Count - 1];
            if (end.Method != null || readOnly) return null;
            string why = Unwritable(end);
            if (why != null) return end.Name + " " + why;
            // A struct on the way is written back into what holds it: that must take
            // it.
            for (int i = segments.Count - 2; i >= 0; i--)
            {
                if (segments[i].ValueType == null || !segments[i].ValueType.IsValueType) break;
                string holds = Unwritable(segments[i]);
                if (holds != null) return segments[i].Name + " holds a struct and " + holds;
            }
            return null;
        }

        private static string Unwritable(Segment segment)
        {
            if (segment.Field != null && (segment.Field.IsInitOnly || segment.Field.IsLiteral)) return "is read-only";
            if (segment.Property != null && segment.Property.GetSetMethod(true) == null) return "has no setter";
            if (segment.Indexer != null && segment.Indexer.GetSetMethod(true) == null) return "has no setter";
            return null;
        }

        private static Segment Member(Type type, string name, bool first, bool mayBeMethod)
        {
            BindingFlags flags = first ? Statics | Instances : Instances;
            FieldInfo field = type.GetField(name, flags);
            if (field != null) return new Segment { Name = name, Field = field, ValueType = field.FieldType };
            PropertyInfo property;
            try
            {
                property = type.GetProperty(name, flags);
            }
            catch (AmbiguousMatchException)
            {
                property = null;
            }
            if (property != null && property.GetIndexParameters().Length == 0)
                return new Segment { Name = name, Property = property, ValueType = property.PropertyType };
            if (!mayBeMethod) return null;
            MethodInfo method = type.GetMethod(name, flags, null, Type.EmptyTypes, null);
            return method != null ? new Segment { Name = name, Method = method } : null;
        }

        private static PropertyInfo Indexer(Type type)
        {
            foreach (PropertyInfo property in type.GetProperties(Instances))
            {
                ParameterInfo[] parameters = property.GetIndexParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string)) return property;
            }
            return null;
        }

        private static bool IsStatic(Segment segment)
        {
            if (segment.Field != null) return segment.Field.IsStatic;
            if (segment.Method != null) return segment.Method.IsStatic;
            MethodInfo accessor = segment.Property.GetGetMethod(true) ?? segment.Property.GetSetMethod(true);
            return accessor != null && accessor.IsStatic;
        }

        // A Unity object destroyed is gone wherever on the path it is held -- a
        // plain reference to it still answers.
        private static bool Gone(object value)
        {
            if (value == null) return true;
            UnityEngine.Object unity = value as UnityEngine.Object;
            return !ReferenceEquals(unity, null) && unity == null;
        }

        // The value, or null where something on the way is not there -- the mod's
        // object not made yet, a settings object not loaded.
        public object Get()
        {
            object holder = RootObject();
            if (fromScene && holder == null) return null;
            for (int i = 0; i < segments.Count; i++)
            {
                if (i > 0 && Gone(holder)) return null;
                holder = Read(segments[i], holder);
            }
            return holder;
        }

        public void Set(object value)
        {
            object[] holders = Holders();
            int last = segments.Count - 1;
            Write(segments[last], holders[last], value);
            for (int i = last - 1; i >= 0; i--)
            {
                if (segments[i].ValueType == null || !segments[i].ValueType.IsValueType) break;
                Write(segments[i], holders[i], holders[i + 1]);
            }
        }

        public void Invoke()
        {
            object[] holders = Holders();
            Segment end = segments[segments.Count - 1];
            end.Method.Invoke(end.Method.IsStatic ? null : holders[segments.Count - 1], null);
        }

        // What holds each member of the path, the root's first.
        private object[] Holders()
        {
            object[] holders = new object[segments.Count];
            holders[0] = RootObject();
            if (fromScene && holders[0] == null)
                throw new InvalidOperationException(Text + ": " + Root.FullName + " is not in the scene.");
            for (int i = 1; i < segments.Count; i++)
            {
                holders[i] = Read(segments[i - 1], holders[i - 1]);
                if (Gone(holders[i])) throw new InvalidOperationException(Text + ": " + segments[i - 1].Name + " is not there.");
            }
            return holders;
        }

        private object RootObject()
        {
            if (!fromScene) return null;
            if (found == null) found = UnityEngine.Object.FindObjectOfType(Root);
            return found;
        }

        private static object Read(Segment segment, object holder)
        {
            if (segment.Field != null) return segment.Field.GetValue(holder);
            if (segment.Property != null) return segment.Property.GetValue(holder, null);
            if (segment.Indexer != null) return segment.Indexer.GetValue(holder, new object[] { segment.Key });
            throw new InvalidOperationException(segment.Name + " is a method, not a value.");
        }

        private static void Write(Segment segment, object holder, object value)
        {
            if (segment.Field != null) segment.Field.SetValue(holder, value);
            else if (segment.Property != null) segment.Property.SetValue(holder, value, null);
            else if (segment.Indexer != null) segment.Indexer.SetValue(holder, value, new object[] { segment.Key });
            else throw new InvalidOperationException(segment.Name + " is a method, not a value.");
        }
    }

    // Which assemblies a registration may reach: those loaded from the same folder
    // under GameData as the type it detects. A type in the game's Managed folder or
    // in GameData itself reaches its own assembly only -- mscorlib, UnityEngine or
    // every mod are no mod's folder. KSP loads a plugin with Assembly.LoadFrom
    // (AssemblyLoader, decompiled), so each has its location. Taken once, as the
    // mod is built -- every plugin is loaded by then -- and a type looked up among
    // them once per name, not across every assembly for every path.
    internal sealed class ModFolder
    {
        private readonly List<Assembly> assemblies;
        private readonly Dictionary<string, Type> types = new Dictionary<string, Type>();

        private ModFolder(List<Assembly> assemblies)
        {
            this.assemblies = assemblies;
        }

        public static ModFolder Of(Assembly assembly)
        {
            List<Assembly> found = new List<Assembly>();
            string root = Root(assembly);
            foreach (Assembly other in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (other == assembly)
                {
                    found.Add(other);
                    continue;
                }
                if (root == null) continue;
                string location = Location(other);
                if (location != null && location.StartsWith(root, StringComparison.OrdinalIgnoreCase)) found.Add(other);
            }
            if (!found.Contains(assembly)) found.Insert(0, assembly);
            return new ModFolder(found);
        }

        public bool Contains(Assembly assembly)
        {
            return assemblies.Contains(assembly);
        }

        // In the order the assemblies were loaded; null where none has it.
        internal Type FindType(string name)
        {
            Type type;
            if (types.TryGetValue(name, out type)) return type;
            foreach (Assembly assembly in assemblies)
            {
                try
                {
                    type = assembly.GetType(name, false);
                }
                catch (Exception)
                {
                    continue;
                }
                if (type != null) break;
            }
            types[name] = type;
            return type;
        }

        // The mod's folder, GameData\<folder>\; null for anything not in one.
        private static string Root(Assembly assembly)
        {
            string location = Location(assembly);
            if (location == null) return null;
            string directory = Path.GetDirectoryName(Path.GetFullPath(location));
            if (directory == null) return null;
            string[] parts = directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            int gameData = Array.FindLastIndex(parts, part => string.Equals(part, "GameData", StringComparison.OrdinalIgnoreCase));
            if (gameData < 0 || gameData == parts.Length - 1) return null;
            return string.Join(Path.DirectorySeparatorChar.ToString(), parts, 0, gameData + 2) + Path.DirectorySeparatorChar;
        }

        private static string Location(Assembly assembly)
        {
            try
            {
                string location = assembly.Location;
                return string.IsNullOrEmpty(location) ? null : location;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
