using System.Collections.Generic;

namespace ReDefinition.Settings
{
    // The bindings ReDefinition keeps for a mod that has no member of its own: a
    // KEY without `member` in a registration. The mod asks ReDefinition.Api whether
    // its binding is pressed, and never holds a key itself.
    //
    // The value is kept like any other setting of a mod that keeps nothing
    // (SettingsSaving.AtEveryStart): it stands in ReDefinition's bundled.cfg and is
    // set here at every start.
    internal static class KeptBindings
    {
        private static readonly Dictionary<string, string> bindings = new Dictionary<string, string>();

        internal static string Get(string key, string fallback)
        {
            string text;
            return bindings.TryGetValue(key, out text) ? text : fallback;
        }

        internal static void Set(string key, string text)
        {
            bindings[key] = text ?? KeyCombination.NoneText;
        }
    }
}
