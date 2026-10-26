using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System;
using ReDefinition.Core;

namespace ReDefinition.Settings
{
    // A member path of a registration (docs/modders/registering-a-mod.md): a type's full
    // name and the members from it.
    // The members are fields, properties, an indexer with a string
    // key, and a method without parameters last.
    // The path is resolved once, then read, written or
    // called through reflection whenever asked.
    //
    // The type is the longest leading part that names a type from the mod's folder
    // (ModFolder).
    // A static first member is where the path starts.
    // An instance first member of a Unity object is the one in the scene, found again once it is
    // destroyed.
    // A struct on the way holds a copy, so a write puts it back whole
    // into what holds it, as with Parallax's settings groups, and whatever holds a
    // struct must take it back.
    internal sealed class MemberPath
    {
        // What the first member may be: static, of any visibility, inherited ones included.
        private const BindingFlags Statics =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        // What every member after the first is: an instance member, of any visibility.
        private const BindingFlags Instances = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // One member of the path, resolved: a field, a property, an indexer with its key, or a method.
        private sealed class Segment
        {
            // How problems name it; for an indexer "name[key]".
            public string Name;
            // Exactly one of these is set, and Key with Indexer.
            public FieldInfo Field;
            public PropertyInfo Property;
            public PropertyInfo Indexer;
            public string Key;
            public MethodInfo Method;
            public Type ValueType;
        }

        // The path as the registration wrote it, and the type it starts from.
        public readonly string Text;
        public readonly Type Root;
        // The members after the type; whether the path starts at the scene's object of the type, and that
        // object, found once and again where it was destroyed.
        private readonly List<Segment> segments = new List<Segment>();
        private bool fromScene;
        private UnityEngine.Object found;

        // Made by Resolve only, which fills the segments.
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

        // Whether the path ends in a method, to be called rather than read.
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

        // The longest leading part that names a type of the mod's folder, and the rest as its members.
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
                    // name[key] is the indexer, as Resolve reads it: the member is
                    // what stands before the bracket.
                    string name = parts[i];
                    int open = name.IndexOf('[');
                    bool indexed = open > 0 && name.EndsWith("]", StringComparison.Ordinal);
                    if (indexed) name = name.Substring(0, open);
                    Segment segment = Member(type, name, true, !indexed && i == parts.Length - 1);
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

        // The members after the type's parts, into the segments; null where every one resolves and the end can
        // be written (or `readOnly`), else the reason.
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

        // Why the member cannot be written -- read-only, no setter --, or null where it can.
        private static string Unwritable(Segment segment)
        {
            if (segment.Field != null && (segment.Field.IsInitOnly || segment.Field.IsLiteral)) return "is read-only";
            if (segment.Property != null && segment.Property.GetSetMethod(true) == null) return "has no setter";
            if (segment.Indexer != null && segment.Indexer.GetSetMethod(true) == null) return "has no setter";
            return null;
        }

        // The type's field, property without index or, at the end, method without parameters of that name; null
        // where it has none.
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

        // The type's indexer with one string key; null where it has none.
        private static PropertyInfo Indexer(Type type)
        {
            foreach (PropertyInfo property in type.GetProperties(Instances))
            {
                ParameterInfo[] parameters = property.GetIndexParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string)) return property;
            }
            return null;
        }

        // Whether the member is static, as the first one of a path that does not start in the scene must be.
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

        // The value written at the end, and every struct on the way written back into what holds it. Throws where
        // something on the way is not there.
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

        // The method's result; null for a void one.
        public object Invoke()
        {
            object[] holders = Holders();
            Segment end = segments[segments.Count - 1];
            return end.Method.Invoke(end.Method.IsStatic ? null : holders[segments.Count - 1], null);
        }

        // What the method at the end returns; null where the path ends in a value.
        public Type ReturnType
        {
            get
            {
                MethodInfo method = segments[segments.Count - 1].Method;
                return method != null ? method.ReturnType : null;
            }
        }

        // Whether everything holding the end is there now -- the mod's object made,
        // its instance set -- without an exception for what is not: asked in every
        // frame a row shows.
        public bool Reachable()
        {
            object[] holders;
            return Holders(out holders) == null;
        }

        // What holds each member of the path, the root's first.
        private object[] Holders()
        {
            object[] holders;
            string missing = Holders(out holders);
            if (missing != null) throw new InvalidOperationException(missing);
            return holders;
        }

        // Null, or what is not there.
        private string Holders(out object[] holders)
        {
            holders = new object[segments.Count];
            holders[0] = RootObject();
            if (fromScene && holders[0] == null) return Text + ": " + Root.FullName + " is not in the scene.";
            for (int i = 1; i < segments.Count; i++)
            {
                holders[i] = Read(segments[i - 1], holders[i - 1]);
                if (Gone(holders[i])) return Text + ": " + segments[i - 1].Name + " is not there.";
            }
            return null;
        }

        // The scene's object the path starts from, found where none is held; null for a static path.
        private object RootObject()
        {
            if (!fromScene) return null;
            if (found == null) found = UnityEngine.Object.FindObjectOfType(Root);
            return found;
        }

        // The member's value in the holder; throws for a method.
        private static object Read(Segment segment, object holder)
        {
            if (segment.Field != null) return segment.Field.GetValue(holder);
            if (segment.Property != null) return segment.Property.GetValue(holder, null);
            if (segment.Indexer != null) return segment.Indexer.GetValue(holder, new object[] { segment.Key });
            throw new InvalidOperationException(segment.Name + " is a method, not a value.");
        }

        // The member set in the holder; throws for a method.
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
        // The folder's assemblies in the order they were loaded, and the types looked up by name.
        private readonly List<Assembly> assemblies;
        private readonly Dictionary<string, Type> types = new Dictionary<string, Type>();

        // Made by Of only.
        private ModFolder(List<Assembly> assemblies)
        {
            this.assemblies = assemblies;
        }

        // The assemblies loaded from the assembly's folder under GameData, in the order they were loaded; the
        // assembly alone where it lies in no mod's folder.
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

        // Whether the assembly is one of the folder's.
        public bool Contains(Assembly assembly)
        {
            return assemblies.Contains(assembly);
        }

        // Whether code of the folder's assemblies loads the string -- a shader
        // property or global the mod sets by name (ForeignCode).
        public bool Loads(string literal)
        {
            foreach (Assembly assembly in assemblies)
                if (ForeignCode.AssemblyLoads(assembly, literal)) return true;
            return false;
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

        // The assembly's location; null for one loaded from memory, or one that cannot say.
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
