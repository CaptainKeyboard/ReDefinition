using System;
using System.Reflection;

namespace ReDefinition.Core
{
    // The game keeps one ReDefinition.Common.dll. Where the one loaded is older than the one a mod was built against, members are missing at
    // the first call that needs them; each mod names that at its start.
    internal static class CommonVersionCheck
    {
        // The line to log, null where the loaded copy is not older than the one the mod was built against.
        internal static string Problem(string mod, Version built, Version loaded)
        {
            if (built == null || loaded == null || loaded >= built) return null;
            return "ReDefinition.Common " + loaded + " is loaded but this " + mod + " was made against " + built
                   + ": another mod ships an older copy. Calls into it can fail until it is updated or removed.";
        }

        // The same for a mod's assembly: the version it references, against the one that is loaded.
        internal static string Problem(string mod, Assembly modAssembly)
        {
            Version built = null;
            foreach (AssemblyName referenced in modAssembly.GetReferencedAssemblies())
                if (referenced.Name == "ReDefinition.Common") built = referenced.Version;
            return Problem(mod, built, typeof(CommonVersionCheck).Assembly.GetName().Version);
        }
    }
}
