namespace ReDefinition.Window
{
    // For the scene probe's window steps (Probe.WindowProbe): a row set as the player's click sets it, and Accept
    // as its button runs it.
    internal static partial class SettingsWindow
    {
        internal static bool ProbeSet(string module, string key, string value)
        {
            IGraphicsModule found = OurModules.Find(module);
            ModuleSetting setting = found != null ? found.Setting(key) : null;
            if (setting == null || edit == null || edit.After == null) return false;
            setting.Write(edit.After, value);
            return true;
        }

        internal static void ProbeAccept()
        {
            ApplyAndAskRestart();
        }
    }
}
