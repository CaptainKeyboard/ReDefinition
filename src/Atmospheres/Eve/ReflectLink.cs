using System;
using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // The lookups of the links into other mods (ParallaxLink, EveSnowLink, EveRainLink): a field, property or method of a type by
    // name, null where either is missing, and the names that were missing for the log. A type that is null (the mod is not there)
    // counts every member asked of it as missing. TypeLookup finds the types.
    internal sealed class ReflectLink
    {
        private readonly List<string> missing = new List<string>();

        // The names of the members that were not found, as "Type.member".
        internal IList<string> Missing
        {
            get { return missing; }
        }

        internal bool Complete
        {
            get { return missing.Count == 0; }
        }

        internal Type Type(string fullName)
        {
            Type type = TypeLookup.Find(fullName);
            if (type == null) missing.Add(fullName);
            return type;
        }

        // A field; with expected, a field of another type counts as missing.
        internal FieldInfo Field(Type type, string typeName, string name, BindingFlags flags, Type expected = null)
        {
            FieldInfo field = type != null ? type.GetField(name, flags) : null;
            if (field != null && expected != null && field.FieldType != expected) field = null;
            if (field == null) missing.Add(typeName + "." + name);
            return field;
        }

        internal PropertyInfo Property(Type type, string typeName, string name, BindingFlags flags)
        {
            PropertyInfo property = type != null ? type.GetProperty(name, flags) : null;
            if (property == null) missing.Add(typeName + "." + name);
            return property;
        }

        // A method with those parameter types; null for parameters takes the method of that name whatever it takes.
        internal MethodInfo Method(Type type, string typeName, string name, BindingFlags flags, Type[] parameters)
        {
            MethodInfo method = null;
            if (type != null) method = parameters == null ? type.GetMethod(name, flags) : type.GetMethod(name, flags, null, parameters, null);
            if (method == null) missing.Add(typeName + "." + name);
            return method;
        }

        // The names that were missing joined for a log line, "none" if all were found.
        internal string MissingText()
        {
            return missing.Count == 0 ? "none" : string.Join(", ", missing.ToArray());
        }
    }
}
