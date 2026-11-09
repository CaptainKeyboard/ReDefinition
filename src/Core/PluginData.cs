namespace ReDefinition.Core
{
    // ReDefinition's settings folder, GameData/ReDefinition/PluginData: settings.cfg (OwnSettings),
    // bundled.cfg (BundledSettings), bindings.cfg (KeptBindings), and the files of the flight panel and the gauge.
    internal static class PluginData
    {
        // The full path of a file in that folder, from KSP's root folder.
        internal static string Path(string fileName)
        {
            return ModFolder.PluginFile("ReDefinition", fileName);
        }
    }
}
