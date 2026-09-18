using System.Collections.Generic;
using ReDefinition.Settings;
using ReDefinition.Window;

namespace ReDefinition.Api
{
    /// <summary>
    /// The key bindings ReDefinition holds, so that a mod does not have to keep one of its
    /// own. A binding is declared in the mod's registration (a <c>KEY</c> block in
    /// <c>MOD_SETTINGS</c>) and shows in the settings window's <i>Keys</i> tab, where the
    /// player sets it. Reference: docs/modders/registering-a-mod.md, "A key binding".
    /// </summary>
    public static class Keys
    {
        /// <summary>
        /// A binding as text, as a config file writes it: <c>LeftAlt+F10</c>, <c>F11</c>, or
        /// <c>None</c> where it is not bound. Null where no binding of that key is there --
        /// the mod is not registered, or the name is another.
        /// </summary>
        /// <param name="key">The binding's key: the mod's id, a dot, and the block's name.</param>
        public static string Binding(string key)
        {
            BundledSetting setting = Find(key);
            if (setting == null || setting.Read == null) return null;
            return setting.Read() ?? KeyCombination.NoneText;
        }

        /// <summary>
        /// Whether the binding's key went down this frame, with exactly its modifiers held:
        /// <c>F10</c> does not answer while <c>Alt+F10</c> is pressed. False while the player
        /// is setting a binding in the window.
        /// </summary>
        /// <param name="key">As for <see cref="Binding"/>.</param>
        public static bool Pressed(string key)
        {
            return !KeyCapture.Busy && Combination(key).Pressed();
        }

        /// <summary>
        /// Whether the binding's key is held, with exactly its modifiers.
        /// </summary>
        /// <param name="key">As for <see cref="Binding"/>.</param>
        public static bool Held(string key)
        {
            return !KeyCapture.Busy && Combination(key).Held();
        }

        /// <summary>
        /// Whether the binding's key went up this frame, with exactly its modifiers.
        /// </summary>
        /// <param name="key">As for <see cref="Binding"/>.</param>
        public static bool Released(string key)
        {
            return !KeyCapture.Busy && Combination(key).Released();
        }

        // The parsed binding, kept while its text stands: asked every frame, and
        // parsing allocates.
        private static readonly Dictionary<string, string> texts = new Dictionary<string, string>();
        private static readonly Dictionary<string, KeyCombination> parsed = new Dictionary<string, KeyCombination>();

        private static KeyCombination Combination(string key)
        {
            string text = Binding(key);
            if (text == null) return KeyCombination.None;
            string last;
            KeyCombination combination;
            if (texts.TryGetValue(key, out last) && last == text && parsed.TryGetValue(key, out combination))
                return combination;
            combination = KeyCombination.Parse(text);
            texts[key] = text;
            parsed[key] = combination;
            return combination;
        }

        // Through the store's own index, which it keeps by key: these are asked
        // every frame, and walking every mod's settings for each call is work per
        // frame a mod should not pay for.
        private static BundledSetting Find(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            BundledSetting setting = BundledSettings.Find(key);
            return setting != null && setting.Control == SettingControl.Binding ? setting : null;
        }
    }
}
