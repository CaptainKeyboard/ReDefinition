namespace ReDefinition.Core
{
    // A mod's settings folder, GameData/<mod>/PluginData. Each package keeps its own files in its own folder.
    internal static class ModFolder
    {
        // The full path of a file in that mod's folder, from KSP's root folder.
        internal static string PluginFile(string mod, string fileName)
        {
            return System.IO.Path.Combine(KSPUtil.ApplicationRootPath,
                System.IO.Path.Combine("GameData", System.IO.Path.Combine(mod,
                    System.IO.Path.Combine("PluginData", fileName))));
        }
    }
}
