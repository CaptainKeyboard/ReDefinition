using System;
using System.Collections.Generic;
using ReDefinition.Diagnostics;
using ReDefinition.Modules;
using ReDefinition.Settings;
using ReDefinition.Ui;
using ReDefinition.Upscaler;
using ReDefinition.Window;

namespace ReDefinition
{
    // The add-on's hotkeys as the player set them (OwnSettings, the Keys tab): the upscaler's
    // switch, the settings window, the diagnostics window and the camera list. The add-on
    // asks it once per frame.
    internal sealed class AddonHotkeys
    {
        // The settings the keys are read from, the diagnostics window the keys open, and the upscaler's switch.
        private readonly OwnSettings settings;
        private readonly DiagnosticsWindow diagnostics;
        private readonly Action toggleUpscaler;

        // Parsed once per text: asked every frame, and parsing allocates.
        private readonly Dictionary<string, KeyCombination> combinations = new Dictionary<string, KeyCombination>();

        // Hotkeys that read their keys from the settings and press the add-on's switches given.
        public AddonHotkeys(OwnSettings settings, DiagnosticsWindow diagnostics, Action toggleUpscaler)
        {
            this.settings = settings;
            this.diagnostics = diagnostics;
            this.toggleUpscaler = toggleUpscaler;
        }

        // Fires the hotkeys pressed this frame. While a row is listening for a key, none of them fires.
        public void Handle()
        {
            KeyCapture.Poll();
            AxisCapture.Poll();
            // Nor while a text field has the keyboard, KSP's or a mod's: a key
            // typed there is text.
            if (KeyCapture.Busy || UiHygiene.Typing || diagnostics.Typing) return;

            if (Combination(settings.UpscalerKey).Pressed()) toggleUpscaler();
            if (Combination(settings.SettingsWindowKey).Pressed()) SettingsWindow.Toggle();
            if (Combination(settings.DiagnosticsKey).Pressed()) diagnostics.Toggle();
            if (Combination(settings.CameraListKey).Pressed()) CameraSurvey.Write();
        }

        // The combination for a hotkey's text, parsed on its first ask.
        private KeyCombination Combination(string text)
        {
            if (text == null) return KeyCombination.None;
            KeyCombination combination;
            if (combinations.TryGetValue(text, out combination)) return combination;
            combination = KeyCombination.Parse(text);
            combinations[text] = combination;
            return combination;
        }
    }
}
