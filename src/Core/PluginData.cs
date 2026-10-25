namespace ReDefinition.Core
{
    // ReDefinition's settings folder, GameData/ReDefinition/PluginData: its own
    // settings.cfg (OwnSettings) and bundled.cfg (BundledSettings).
    internal static class PluginData
    {
        // The full path of a file in that folder, from KSP's root folder.
        internal static string Path(string fileName)
        {
            return System.IO.Path.Combine(KSPUtil.ApplicationRootPath,
                System.IO.Path.Combine("GameData", System.IO.Path.Combine("ReDefinition",
                    System.IO.Path.Combine("PluginData", fileName))));
        }
    }
}
