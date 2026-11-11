using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // Late-bound access for the reconnaissance probe: the Atmospheres assembly does not reference the engine's audio and physics modules or
    // the other mods, and the probe must run (and say what is missing) where they are absent. Every call returns null or false on a
    // missing member instead of throwing; the caller decides what a miss means.
    internal static class ReconDyn
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        // A type by its full name from any loaded assembly.
        internal static Type Type(string fullName)
        {
            return TypeLookup.Find(fullName);
        }

        internal static object Get(object target, string name)
        {
            return Get(target.GetType(), target, name);
        }

        internal static object GetStatic(Type type, string name)
        {
            return Get(type, null, name);
        }

        private static object Get(Type type, object target, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                PropertyInfo p = t.GetProperty(name, All | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0 && p.CanRead) return p.GetValue(target, null);
                FieldInfo f = t.GetField(name, All | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(target);
            }
            return null;
        }

        // Whether a property or field of that name exists.
        internal static bool Has(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                if (t.GetProperty(name, All | BindingFlags.DeclaredOnly) != null) return true;
                if (t.GetField(name, All | BindingFlags.DeclaredOnly) != null) return true;
            }
            return false;
        }

        internal static bool Set(object target, string name, object value)
        {
            Type type = target.GetType();
            for (Type t = type; t != null; t = t.BaseType)
            {
                PropertyInfo p = t.GetProperty(name, All | BindingFlags.DeclaredOnly);
                if (p != null && p.CanWrite)
                {
                    p.SetValue(target, Convert(value, p.PropertyType), null);
                    return true;
                }
                FieldInfo f = t.GetField(name, All | BindingFlags.DeclaredOnly);
                if (f != null)
                {
                    f.SetValue(target, Convert(value, f.FieldType));
                    return true;
                }
            }
            return false;
        }

        // Calls an instance method (or a static one when target is null and type is given) whose parameter count and types fit.
        internal static object Call(object target, string name, params object[] args)
        {
            return Invoke(target.GetType(), target, name, args);
        }

        internal static object CallStatic(Type type, string name, params object[] args)
        {
            return Invoke(type, null, name, args);
        }

        private static object Invoke(Type type, object target, string name, object[] args)
        {
            MethodInfo best = null;
            foreach (MethodInfo m in type.GetMethods(All))
            {
                if (m.Name != name) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length < args.Length) continue;
                bool fits = true;
                for (int i = 0; i < ps.Length && fits; i++)
                {
                    if (i < args.Length) fits = Accepts(ps[i].ParameterType, args[i]);
                    else fits = ps[i].IsOptional;
                }
                if (!fits) continue;
                best = m;
                if (ps.Length == args.Length) break;
            }
            if (best == null) throw new MissingMethodException(type.FullName, name);
            ParameterInfo[] parameters = best.GetParameters();
            var full = new object[parameters.Length];
            for (int i = 0; i < full.Length; i++)
            {
                full[i] = i < args.Length ? Convert(args[i], parameters[i].ParameterType) : parameters[i].DefaultValue;
            }
            return best.Invoke(target, full);
        }

        private static bool Accepts(Type parameter, object value)
        {
            if (value == null) return !parameter.IsValueType;
            Type vt = value.GetType();
            if (parameter.IsAssignableFrom(vt)) return true;
            if (parameter.IsEnum && value is int) return true;
            if (IsNumber(parameter) && IsNumber(vt)) return true;
            return false;
        }

        private static bool IsNumber(Type t)
        {
            return t == typeof(int) || t == typeof(float) || t == typeof(double) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
                   || t == typeof(uint);
        }

        private static object Convert(object value, Type to)
        {
            if (value == null) return null;
            if (to.IsAssignableFrom(value.GetType())) return value;
            if (to.IsEnum) return Enum.ToObject(to, value);
            if (IsNumber(to)) return System.Convert.ChangeType(value, to);
            return value;
        }

        // Creates an instance with a default constructor.
        internal static object New(Type type)
        {
            return Activator.CreateInstance(type);
        }

        // The names of the fields and properties of a type (public and not) whose declared type name contains the text.
        internal static List<string> MembersOfType(Type type, string typeNamePart)
        {
            var found = new List<string>();
            for (Type t = type; t != null; t = t.BaseType)
            {
                foreach (FieldInfo f in t.GetFields(All | BindingFlags.DeclaredOnly))
                {
                    if (f.FieldType.Name.Contains(typeNamePart)) found.Add(f.Name + ":" + f.FieldType.Name);
                }
                foreach (PropertyInfo p in t.GetProperties(All | BindingFlags.DeclaredOnly))
                {
                    if (p.PropertyType.Name.Contains(typeNamePart)) found.Add(p.Name + ":" + p.PropertyType.Name);
                }
            }
            return found;
        }

        // Every element of an IEnumerable as an object list; empty for null.
        internal static List<object> List(object enumerable)
        {
            var list = new List<object>();
            var e = enumerable as IEnumerable;
            if (e == null) return list;
            foreach (object o in e) list.Add(o);
            return list;
        }
    }
}
