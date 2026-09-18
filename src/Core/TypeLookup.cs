using System;
using System.Collections.Generic;
using System.Reflection;

namespace ReDefinition.Core
{
    // Another mod's type by its full name, from whatever assemblies are loaded.
    // Its own class, free of
    // Unity: HostStack's statics call into the engine, and the registered mods
    // are built outside the game too (tools/check_bundled_mods.ps1).
    internal static class TypeLookup
    {
        private static readonly Dictionary<string, Type> cache = new Dictionary<string, Type>();

        public static Type Find(string fullName)
        {
            // Hits are cached permanently, misses are not: the scan is cheap, and an
            // assembly loaded later is found.
            Type cached;
            if (cache.TryGetValue(fullName, out cached)) return cached;

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type type;
                try { type = assemblies[i].GetType(fullName, false); }
                catch { continue; }
                if (type != null)
                {
                    cache[fullName] = type;
                    return type;
                }
            }
            return null;
        }
    }
}
