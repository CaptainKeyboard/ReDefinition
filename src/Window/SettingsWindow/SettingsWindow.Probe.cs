using ReDefinition.Modules;

namespace ReDefinition.Window
{
    // For the scene probe's window steps (Probe.WindowProbe): a row set as the player's click sets it, and Accept
    // as its button runs it.
    internal static partial class SettingsWindow
    {
        // Writes a value into one of ReDefinition's rows of the open window, as a click would; false where there is no
        // such row or no window.
        internal static bool ProbeSet(string module, string key, string value)
        {
            IGraphicsModule found = OurModules.Find(module);
            ModuleSetting setting = found != null ? found.Setting(key) : null;
            if (setting == null || edit == null || edit.After == null) return false;
            setting.Write(edit.After, value);
            return true;
        }

        // Accepts what the window holds, as the Accept button does.
        internal static void ProbeAccept()
        {
            ApplyAndAskRestart();
        }
    }
}
