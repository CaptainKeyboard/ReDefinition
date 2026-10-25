using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Window
{
    // The windows around the bundled settings: close buttons on the mods' own
    // windows (ModWindowClose), the toolbar buttons taken over (ToolbarTakeover),
    // and the open settings window following a change made in another window.
    // The toolbar is looked at once a scene's GUI is ready, once the launcher is,
    // and on a tick for buttons a mod adds later.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class ModWindowsAddon : MonoBehaviour
    {
        // Seconds between two looks at the toolbar for buttons a mod added later.
        private const float TickInterval = 2f;
        // How often the open settings window looks for changes made in another.
        private const float SyncInterval = 0.25f;
        private float nextSync;
        private float nextTick;

        // Kept across scenes: the close buttons' hooks installed, and the scene and launcher events followed.
        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            try
            {
                ModWindowClose.Install();
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("own-window-close-hooks", "The bundled mods' own windows get no close button ("
                                                                + CompatibilityLog.Reason(e) + ").");
            }
            GameEvents.onLevelWasLoaded.Add(OnLevelLoaded);
            GameEvents.onLevelWasLoadedGUIReady.Add(OnLevelReady);
            GameEvents.onGUIApplicationLauncherReady.Add(OnLauncherReady);
        }

        // Stops following the scene and launcher events.
        private void OnDestroy()
        {
            GameEvents.onLevelWasLoaded.Remove(OnLevelLoaded);
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnLevelReady);
            GameEvents.onGUIApplicationLauncherReady.Remove(OnLauncherReady);
        }

        // A new scene: the mods' windows of the last one are forgotten.
        private void OnLevelLoaded(GameScenes scene)
        {
            ModWindowClose.Forget();
        }

        // A scene's GUI is ready: the settings window's waiting changes of the last scene drop, and the toolbar is looked at.
        private void OnLevelReady(GameScenes scene)
        {
            SettingsWindow.SceneChanged();
            ToolbarTakeover.Refresh();
            KeepOurButtonFirst();
        }

        // The launcher is ready: the toolbar is looked at.
        private void OnLauncherReady()
        {
            ToolbarTakeover.Refresh();
            KeepOurButtonFirst();
        }

        // A mod that adds its button after this scene's launcher was ready puts
        // itself behind ours, but one that rebuilds the row can move ours along,
        // so the front is asked for again whenever the toolbar is looked at.
        private static void KeepOurButtonFirst()
        {
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            if (addon != null) addon.KeepToolbarButtonFirst();
        }

        // Four times a second the open settings window follows changes made elsewhere; the openings are reported; and the
        // toolbar is looked at on its tick.
        private void Update()
        {
            if (SettingsWindow.Visible && Time.unscaledTime >= nextSync)
            {
                nextSync = Time.unscaledTime + SyncInterval;
                try
                {
                    SettingsWindow.SyncFromMods();
                }
                catch (Exception e)
                {
                    CompatibilityLog.Warn("settings-window-sync", "ReDefinition's window could not follow a change made in"
                                                                  + " another window (" + CompatibilityLog.Reason(e) + ").");
                }
            }

            ModWindowClose.ReportOpening();

            if (Time.unscaledTime < nextTick) return;
            nextTick = Time.unscaledTime + TickInterval;
            ToolbarTakeover.Refresh();
        }
    }
}
