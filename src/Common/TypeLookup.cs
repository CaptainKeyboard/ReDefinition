using System;
using System.Collections.Generic;
using System.Reflection;

namespace ReDefinition.Core
{
    // Another mod's type by its full name, from whatever assemblies are loaded.
    // Its own class, free of
    // Unity: HostStack's statics call into the engine, and the registered mods
    // are built outside the game too (tools/checks/check_bundled_mods.ps1).
    internal static class TypeLookup
    {
        // Every member, public or not, static or not: what the lookups into other
        // mods ask for.
        internal const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static
                                       | BindingFlags.Public | BindingFlags.NonPublic;

        // The types found so far, by full name.
        private static readonly Dictionary<string, Type> cache = new Dictionary<string, Type>();

        // The full names no loaded assembly had when they were asked for. Find runs every
        // second for mods that are often not installed, and every miss would scan all
        // assemblies and allocate their array; Reload forgets the misses whenever another
        // assembly arrives, so a mod loaded later is still found.
        private static readonly HashSet<string> missed = new HashSet<string>();

        // Counts the assemblies that arrived: a miss recorded after one arrived during its
        // own scan could have missed that assembly, so it is not recorded.
        private static int arrivals;

        // Guards the collections and the count: assemblies load on any thread, Find runs on the main one.
        private static readonly object gate = new object();

        static TypeLookup()
        {
            AppDomain.CurrentDomain.AssemblyLoad += (sender, args) => Reload();
        }

        // Forgets the misses; the hits stay, as an assembly's types do not leave.
        internal static void Reload()
        {
            lock (gate)
            {
                arrivals++;
                missed.Clear();
            }
        }

        // The type of that full name from the first loaded assembly that has it; null when none has it.
        public static Type Find(string fullName)
        {
            int seen;
            lock (gate)
            {
                Type cached;
                if (cache.TryGetValue(fullName, out cached)) return cached;
                if (missed.Contains(fullName)) return null;
                seen = arrivals;
            }

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type type;
                try { type = assemblies[i].GetType(fullName, false); }
                catch { continue; }
                if (type != null)
                {
                    lock (gate) cache[fullName] = type;
                    return type;
                }
            }
            lock (gate)
            {
                if (seen == arrivals) missed.Add(fullName);
            }
            return null;
        }
    }
}
