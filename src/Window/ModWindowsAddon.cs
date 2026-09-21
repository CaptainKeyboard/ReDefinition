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
        private const float TickInterval = 2f;
        // How often the open settings window looks for changes made in another.
        private const float SyncInterval = 0.25f;
        private float nextSync;
        private float nextTick;

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

        private void OnDestroy()
        {
            GameEvents.onLevelWasLoaded.Remove(OnLevelLoaded);
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnLevelReady);
            GameEvents.onGUIApplicationLauncherReady.Remove(OnLauncherReady);
        }

        private void OnLevelLoaded(GameScenes scene)
        {
            ModWindowClose.Forget();
        }

        private void OnLevelReady(GameScenes scene)
        {
            SettingsWindow.SceneChanged();
            ToolbarTakeover.Refresh();
            KeepOurButtonFirst();
        }

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

            if (Time.unscaledTime < nextTick) return;
            nextTick = Time.unscaledTime + TickInterval;
            ToolbarTakeover.Refresh();
        }
    }
}
