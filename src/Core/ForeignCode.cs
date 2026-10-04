using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;

namespace ReDefinition.Core
{
    // Another mod's compiled code read as instructions: a fingerprint of a method,
    // which changes where its instructions or what they refer to change -- tokens
    // are resolved to names, written without assemblies, so that it is the same in
    // the game's Mono and in .NET outside it, and a rebuild of unchanged code keeps
    // it, unless the compiler names its own helpers anew (closures, lambdas),
    // which then reads as changed -- and the string literals a method loads. Free
    // of Unity, like TypeLookup: the checks run outside the game too
    // (tools/check_foreign_code.ps1). Nothing here throws: what cannot be read
    // counts as not there.
    internal static class ForeignCode
    {
        private static readonly OpCode[] OneByte = new OpCode[0x100];
        private static readonly OpCode[] TwoByte = new OpCode[0x100];

        static ForeignCode()
        {
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                OpCode code = (OpCode)field.GetValue(null);
                ushort value = unchecked((ushort)code.Value);
                if (value < 0x100) OneByte[value] = code;
                else if ((value & 0xff00) == 0xfe00) TwoByte[value & 0xff] = code;
            }
        }

        // Twelve hex digits of a SHA-1 over the methods' instructions together, in
        // their order, each with what it refers to by name. Null where one of them
        // has no body to read.
        public static string Fingerprint(IList<MethodBase> methods)
        {
            StringBuilder text = new StringBuilder();
            foreach (MethodBase method in methods)
            {
                text.Append(method.DeclaringType != null ? TypeName(method.DeclaringType) : "?").Append("::")
                    .Append(method.Name).Append('\n');
                if (!Describe(method, text, null)) return null;
            }
            return Hash(text.ToString());
        }

        // Whether any method of the type -- constructors and static initializer
        // included -- loads the string.
        public static bool TypeLoads(Type type, string literal)
        {
            foreach (MethodBase method in Methods(type))
                if (Loads(method, literal)) return true;
            return false;
        }

        // Whether any method of the assembly loads the string. Each assembly is read
        // once, its strings kept.
        public static bool AssemblyLoads(Assembly assembly, string literal)
        {
            HashSet<string> strings;
            if (!AssemblyStrings.TryGetValue(assembly, out strings))
            {
                strings = new HashSet<string>(StringComparer.Ordinal);
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch (Exception) { types = new Type[0]; }
                foreach (Type type in types)
                {
                    if (type == null) continue;
                    foreach (MethodBase method in Methods(type))
                        Describe(method, null, strings);
                }
                AssemblyStrings[assembly] = strings;
            }
            return strings.Contains(literal);
        }

        private static readonly Dictionary<Assembly, HashSet<string>> AssemblyStrings =
            new Dictionary<Assembly, HashSet<string>>();

        public static bool Loads(MethodBase method, string literal)
        {
            HashSet<string> strings = new HashSet<string>(StringComparer.Ordinal);
            return Describe(method, null, strings) && strings.Contains(literal);
        }

        // The version the assembly says it is: its file version where it has one,
        // otherwise its assembly version.
        public static string Version(Assembly assembly)
        {
            try
            {
                if (!string.IsNullOrEmpty(assembly.Location))
                {
                    string file = FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion;
                    if (!string.IsNullOrEmpty(file)) return file;
                }
            }
            catch (Exception)
            {
                // A dynamic or unreadable assembly: its name's version below.
            }
            return assembly.GetName().Version.ToString();
        }

        // A type's name with its namespace and declaring types, and its type
        // arguments by the same rule -- not FullName, which names the arguments'
        // assemblies with their versions: mscorlib in Mono, System.Private.CoreLib in
        // .NET.
        public static string TypeName(Type type)
        {
            if (type.IsGenericParameter) return "!" + type.Name;
            if (type.HasElementType)
            {
                string element = TypeName(type.GetElementType());
                if (type.IsArray) return element + "[" + new string(',', type.GetArrayRank() - 1) + "]";
                return element + (type.IsByRef ? "&" : "*");
            }
            StringBuilder name = new StringBuilder();
            if (type.DeclaringType != null) name.Append(TypeName(type.DeclaringType)).Append('+');
            else if (!string.IsNullOrEmpty(type.Namespace)) name.Append(type.Namespace).Append('.');
            name.Append(type.Name);
            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                name.Append('[');
                Type[] arguments = type.GetGenericArguments();
                for (int i = 0; i < arguments.Length; i++) name.Append(i > 0 ? "," : "").Append(TypeName(arguments[i]));
                name.Append(']');
            }
            return name.ToString();
        }

        private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                                              | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        // Its methods, constructors and static initializer; none where the type
        // cannot be read -- a member naming a type from an assembly that is not
        // installed.
        private static List<MethodBase> Methods(Type type)
        {
            List<MethodBase> methods = new List<MethodBase>();
            try
            {
                methods.AddRange(type.GetMethods(Declared));
                methods.AddRange(type.GetConstructors(Declared & ~BindingFlags.Static));
                ConstructorInfo initializer = type.TypeInitializer;
                if (initializer != null) methods.Add(initializer);
            }
            catch (Exception)
            {
                methods.Clear();
            }
            return methods;
        }

        // Walks the method's instructions: appends each to text, and the strings it
        // loads to strings, where those are given. False where there is no body.
        private static bool Describe(MethodBase method, StringBuilder text, HashSet<string> strings)
        {
            byte[] il;
            Type[] typeArguments;
            Type[] methodArguments;
            try
            {
                MethodBody body = method.GetMethodBody();
                il = body != null ? body.GetILAsByteArray() : null;
                typeArguments = method.DeclaringType != null && method.DeclaringType.IsGenericType
                    ? method.DeclaringType.GetGenericArguments()
                    : null;
                methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
            }
            catch (Exception)
            {
                return false;
            }
            if (il == null) return false;
            try
            {
                Walk(il, method.Module, typeArguments, methodArguments, text, strings);
            }
            catch (Exception)
            {
                // Instructions that end inside an operand: what was read stands.
                if (text != null) text.Append("?\n");
            }
            return true;
        }

        private static void Walk(byte[] il, Module module, Type[] typeArguments, Type[] methodArguments, StringBuilder text,
                                 HashSet<string> strings)
        {
            int position = 0;
            while (position < il.Length)
            {
                OpCode code;
                byte first = il[position++];
                if (first == 0xfe && position < il.Length) code = TwoByte[il[position++]];
                else code = OneByte[first];
                if (code.Size == 0)
                {
                    // An opcode this table does not know: the rest is unreadable.
                    if (text != null) text.Append("?\n");
                    return;
                }
                if (text != null) text.Append(code.Name);
                switch (code.OperandType)
                {
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        if (text != null) text.Append(' ').Append(il[position]);
                        position += 1;
                        break;
                    case OperandType.InlineVar:
                        if (text != null) text.Append(' ').Append(BitConverter.ToUInt16(il, position));
                        position += 2;
                        break;
                    case OperandType.InlineBrTarget:
                    case OperandType.InlineI:
                    case OperandType.ShortInlineR:
                        if (text != null) text.Append(' ').Append(BitConverter.ToInt32(il, position));
                        position += 4;
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        if (text != null) text.Append(' ').Append(BitConverter.ToInt64(il, position));
                        position += 8;
                        break;
                    case OperandType.InlineSwitch:
                        int count = BitConverter.ToInt32(il, position);
                        position += 4;
                        if (text != null) text.Append(' ').Append(count);
                        for (int i = 0; i < count; i++)
                        {
                            if (text != null) text.Append(',').Append(BitConverter.ToInt32(il, position));
                            position += 4;
                        }
                        break;
                    case OperandType.InlineString:
                        string literal = Resolve(() => module.ResolveString(BitConverter.ToInt32(il, position)));
                        if (strings != null && literal != null) strings.Add(literal);
                        if (text != null) text.Append(" \"").Append(literal).Append('"');
                        position += 4;
                        break;
                    case OperandType.InlineSig:
                        // A calli's signature: its shape only.
                        if (text != null) text.Append(" sig");
                        position += 4;
                        break;
                    default:
                        // InlineField, InlineMethod, InlineType, InlineTok.
                        int token = BitConverter.ToInt32(il, position);
                        if (text != null)
                            text.Append(' ').Append(Resolve(() => Name(module.ResolveMember(token, typeArguments, methodArguments))));
                        position += 4;
                        break;
                }
                if (text != null) text.Append('\n');
            }
        }

        private static string Resolve(Func<string> resolve)
        {
            try { return resolve(); }
            catch (Exception) { return "?"; }
        }

        private static string Name(MemberInfo member)
        {
            if (member == null) return "?";
            Type type = member as Type;
            if (type != null) return TypeName(type);
            StringBuilder name = new StringBuilder();
            name.Append(member.DeclaringType != null ? TypeName(member.DeclaringType) : "?").Append("::").Append(member.Name);
            MethodBase method = member as MethodBase;
            if (method != null)
            {
                if (method.IsGenericMethod)
                {
                    name.Append('<');
                    Type[] arguments = method.GetGenericArguments();
                    for (int i = 0; i < arguments.Length; i++) name.Append(i > 0 ? "," : "").Append(TypeName(arguments[i]));
                    name.Append('>');
                }
                name.Append('(');
                ParameterInfo[] parameters = method.GetParameters();
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (i > 0) name.Append(',');
                    name.Append(TypeName(parameters[i].ParameterType));
                }
                name.Append(')');
            }
            return name.ToString();
        }

        private static string Hash(string text)
        {
            using (SHA1 sha = SHA1.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                StringBuilder hex = new StringBuilder(12);
                for (int i = 0; i < 6; i++) hex.Append(digest[i].ToString("x2"));
                return hex.ToString();
            }
        }
    }
}
