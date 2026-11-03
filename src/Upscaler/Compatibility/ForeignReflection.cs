using System.Collections.Generic;
using System.Reflection;
using System;
using ReDefinition.Core;

namespace ReDefinition.Upscaler
{
    // Reads and writes the fields and properties of other mods' objects by name, with the lookups cached.
    internal static class ForeignReflection
    {
        // A member looked up by type, name and binding flags.
        private struct MemberKey : IEquatable<MemberKey>
        {
            private readonly Type type;
            private readonly string name;
            private readonly BindingFlags flags;

            internal MemberKey(Type type, string name, BindingFlags flags)
            {
                this.type = type;
                this.name = name;
                this.flags = flags;
            }

            public bool Equals(MemberKey other)
            {
                return type == other.type && flags == other.flags && name == other.name;
            }

            public override bool Equals(object other)
            {
                return other is MemberKey && Equals((MemberKey)other);
            }

            public override int GetHashCode()
            {
                return (type.GetHashCode() * 31 + name.GetHashCode()) * 31 + (int)flags;
            }
        }

        // Public and non-public, instance and static: what the reflection lookups here search.
        private const BindingFlags Any = TypeLookup.Any;

        // The flags of the static lookups.
        private const BindingFlags StaticOnly = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // The fields and properties the lookups below found, kept so that the once-a-second passes do not ask
        // reflection for the same names again. A name a type lacks is kept as null: a loaded type does not change.
        private static readonly Dictionary<MemberKey, FieldInfo> fieldCache = new Dictionary<MemberKey, FieldInfo>();
        private static readonly Dictionary<MemberKey, PropertyInfo> propertyCache = new Dictionary<MemberKey, PropertyInfo>();

        // The type's field of that name under those flags, or null.
        internal static FieldInfo FieldOf(Type type, string name, BindingFlags flags)
        {
            MemberKey key = new MemberKey(type, name, flags);
            FieldInfo field;
            if (!fieldCache.TryGetValue(key, out field)) fieldCache[key] = field = type.GetField(name, flags);
            return field;
        }

        // The type's property of that name under Any, or null.
        private static PropertyInfo PropertyOf(Type type, string name)
        {
            MemberKey key = new MemberKey(type, name, Any);
            PropertyInfo property;
            if (!propertyCache.TryGetValue(key, out property)) propertyCache[key] = property = type.GetProperty(name, Any);
            return property;
        }

        // Reads a field or readable property by name; false when the type has neither.
        internal static bool TryGet(object target, Type type, string name, out object value)
        {
            FieldInfo field = FieldOf(type, name, Any);
            if (field != null) { value = field.GetValue(target); return true; }
            PropertyInfo property = PropertyOf(type, name);
            if (property != null && property.CanRead) { value = property.GetValue(target, null); return true; }
            value = null;
            return false;
        }

        // Writes a field or writable property by name; false when the type has neither.
        internal static bool TrySet(object target, Type type, string name, object value)
        {
            FieldInfo field = FieldOf(type, name, Any);
            if (field != null) { field.SetValue(target, value); return true; }
            PropertyInfo property = PropertyOf(type, name);
            if (property != null && property.CanWrite) { property.SetValue(target, value, null); return true; }
            return false;
        }

        // Static fields only: a lookup that could also find an instance member
        // would throw on the null target. The type is checked before writing.
        internal static bool TryGetStatic(Type type, string name, out object value)
        {
            FieldInfo field = FieldOf(type, name, StaticOnly);
            if (field != null) { value = field.GetValue(null); return true; }
            value = null;
            return false;
        }

        // Writes a static field by name, only with a value of the field's type.
        internal static bool TrySetStatic(Type type, string name, object value)
        {
            FieldInfo field = FieldOf(type, name, StaticOnly);
            if (field == null || value == null || !field.FieldType.IsInstanceOfType(value)) return false;
            field.SetValue(null, value);
            return true;
        }
    }
}
