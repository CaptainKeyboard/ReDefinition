using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Settings
{
    // KSP's own reset, as its settings screen's Reset does it
    // (GameSettings.ResetSettings, decompiled): every GameSettings field to its
    // default (SetDefaultValues), the key bindings of the keyboard layout KSP
    // detects, its terrain detail presets anew, then saved and applied. Run by
    // the settings window's *Reset to defaults*, before the bundled mods' rows
    // are reset, so that what KSP's reset covers and no row shows -- the
    // keyboard layout's bindings, the terrain presets -- is reset as well.
    //
    // One difference: the screen resolution and full screen stay as they are.
    // KSP's defaults are 1280 x 720 in a window, which would resize the game on
    // every reset; they belong to the monitor, not to the game's defaults.
    internal static class KspReset
    {
        internal static void Run()
        {
            int width = GameSettings.SCREEN_RESOLUTION_WIDTH;
            int height = GameSettings.SCREEN_RESOLUTION_HEIGHT;
            bool fullScreen = GameSettings.FULLSCREEN;

            GameSettings.SetDefaultValues();
            GameSettings.SCREEN_RESOLUTION_WIDTH = width;
            GameSettings.SCREEN_RESOLUTION_HEIGHT = height;
            GameSettings.FULLSCREEN = fullScreen;
            GameSettings.LoadLayoutKeyBindings(GameSettings.DetectKeyboardLayout(), true);
            PQSCache.CreateDefaultPresetList();
            GameSettings.SaveSettings();
            GameSettings.ApplySettings();
            // What listens to KSP's settings -- the music, the reflection probe,
            // the upscaler's hold on MSAA -- takes them as after KSP's own screen.
            GameEvents.OnGameSettingsApplied.Fire();
            Debug.Log(Log.Tag + " KSP's settings reset to KSP's defaults (Reset); the screen resolution"
                      + " stays " + width + " x " + height + (fullScreen ? ", full screen." : ", windowed."));
        }
    }
}
