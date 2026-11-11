using System;

namespace ReDefinition.Atmospheres
{
    // The path by which a change in the settings window reaches the parts that keep a copy of a setting (the live refresh). Every part that
    // reads a setting in each frame sees a change on its own; the parts that build something from a setting (a map, a texture, a
    // shell) subscribe here and rebuild. The key is the setting that changed; "*" says that Reset changed all of them.
    internal static class SettingChanges
    {
        internal const string All = "*";

        internal static event Action<string> Changed;

        // Where a failure is told; the tests assign their own.
        internal static Action<string> Report = AtmospheresLog.WarnOnce;

        // Raises the event; a subscriber that throws is reported and does not keep the others from hearing it.
        internal static void Raise(string key)
        {
            Action<string> handlers = Changed;
            if (handlers == null) return;
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<string>)handler)(key);
                }
                catch (Exception e)
                {
                    Report("a part could not follow the change of " + key + " (" + ReDefinition.Core.CompatibilityLog.Reason(e) + ")");
                }
            }
        }
    }
}
