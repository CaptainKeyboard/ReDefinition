namespace ReDefinition.Core
{
    // A mod's settings folder, GameData/<mod>/PluginData, where <mod> is a path below GameData such as
    // "ReDefinition/Main" or "ReDefinition/Planner". Each package keeps its own files in its own folder.
    internal static class ModFolder
    {
        // The full path of a file in that mod's folder, from KSP's root folder.
        internal static string PluginFile(string mod, string fileName)
        {
            return PluginFile(KSPUtil.ApplicationRootPath, mod, fileName);
        }

        // The same from a given root, so that the shape of the path can be tested without the game.
        internal static string PluginFile(string root, string mod, string fileName)
        {
            return System.IO.Path.Combine(root,
                System.IO.Path.Combine("GameData", System.IO.Path.Combine(mod,
                    System.IO.Path.Combine("PluginData", fileName))));
        }
    }
}
