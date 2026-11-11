using System;
using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // Marks a static class that holds ForeignAssumption values (public static fields) about code the atmospheres rely on. The catalogue
    // finds the class by reflection; a package adds its own file, Assumptions<Id>.cs, and edits no shared one.
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    internal sealed class AtmospheresAssumptionsAttribute : Attribute
    {
    }

    // The catalogue of what the atmospheres take for granted about other code, built on Common's ForeignAssumption (the same type the
    // main mod's catalogue ForeignAssumptions uses): each entry names what it relies on, the fingerprints of the code it was checked
    // against, and what happens where that code has changed. The entries of every class marked [AtmospheresAssumptions] in this
    // assembly are collected once, ordered by id. A package that reads another mod's code through reflection adds one entry for it.
    internal static class AtmospheresAssumptions
    {
        private static List<ForeignAssumption> all;

        // Every entry, ordered by id.
        internal static IList<ForeignAssumption> All
        {
            get { return all ?? (all = Collect(typeof(AtmospheresAssumptions).Assembly)); }
        }

        // The entries of the marked classes of an assembly, by id; a duplicate id keeps the first and is left out.
        internal static List<ForeignAssumption> Collect(Assembly assembly)
        {
            var list = new List<ForeignAssumption>();
            var seen = new HashSet<string>();
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = Array.FindAll(e.Types, t => t != null);
            }
            Array.Sort(types, (a, b) => string.CompareOrdinal(a.FullName, b.FullName));
            foreach (Type type in types)
            {
                if (!type.IsClass || Attribute.GetCustomAttribute(type, typeof(AtmospheresAssumptionsAttribute), false) == null) continue;
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    var assumption = field.GetValue(null) as ForeignAssumption;
                    if (assumption != null && seen.Add(assumption.Id)) list.Add(assumption);
                }
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return list;
        }

        // The entries whose mod is installed (the others are Absent and need nothing).
        internal static List<ForeignAssumption> Installed()
        {
            var installed = new List<ForeignAssumption>();
            foreach (ForeignAssumption assumption in All)
            {
                if (assumption.State != ForeignState.Absent) installed.Add(assumption);
            }
            return installed;
        }

        // The entry with that id, or null.
        internal static ForeignAssumption Find(string id)
        {
            foreach (ForeignAssumption assumption in All)
            {
                if (assumption.Id == id) return assumption;
            }
            return null;
        }

        // Whether what rests on the entry may go on: the entry holds, or its mod is absent and the caller's own check says so. An
        // unknown id does not hold.
        internal static bool Holds(string id)
        {
            ForeignAssumption assumption = Find(id);
            return assumption != null && assumption.Holds;
        }
    }
}
