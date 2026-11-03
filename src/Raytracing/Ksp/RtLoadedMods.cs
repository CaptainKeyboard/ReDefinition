using System;
using System.Reflection;

namespace ReDefinition.Raytracing
{
    // Walks the loaded assemblies for the mod the raytracing reaches into by reflection.
    internal static class RtLoadedMods
    {
        // Calls `read` with each loaded assembly of that name. A mod of another version whose members are not
        // found makes `read` throw; that mod is left out and the raytracing goes on without it.
        internal static void Each(string assemblyName, StringComparison comparison, Action<Assembly> read)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (string.Equals(assembly.GetName().Name, assemblyName, comparison)) read(assembly);
                }
                catch (Exception)
                {
                    // A mod of another version: raytracing goes on without it.
                }
            }
        }
    }
}
