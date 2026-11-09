using System.Collections.Generic;

namespace ReDefinition.Engineer
{
    // Looks at a part's modules without allocating, for the loops that run every frame or on every edit.
    // Part.FindModulesImplementing builds a new list on each call.
    internal static class PartModules
    {
        // Fills `into` (emptied first) with the part's modules that are a T, subclasses included and in module order.
        // The list is the caller's scratch: it holds the modules of this part until the next call with it.
        internal static List<T> Of<T>(Part part, List<T> into) where T : class
        {
            into.Clear();
            PartModuleList modules = part.Modules;
            for (int m = 0; m < modules.Count; m++)
            {
                T module = modules[m] as T;
                if (module != null) into.Add(module);
            }
            return into;
        }
    }
}
