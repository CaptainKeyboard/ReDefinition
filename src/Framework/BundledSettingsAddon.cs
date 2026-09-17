using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Framework
{
    // When the bundled settings reach their mods, and when the toolbar is
    // looked at.
    //
    // At every scene change: when the next scene is requested, before it loads
    // -- Scatterer reads its node in the new scene's Awake, Deferred sets up
    // its reflections as the scene loads; when it has loaded and when its GUI
    // is ready -- after the mods' own per-scene applies: EVE's puts its quality
    // values back, TUFX applies its own profile (onLevelWasLoaded, and at every
    // change of camera mode, OnCameraChange). KSP's GameEvents call their
    // handlers from the last subscribed to the first (EventData.Fire,
    // decompiled: while (count-- > 0)); these subscribe at the start of the
    // game, TUFX at the end of loading (its ModuleManagerPostLoad), so
    // ReDefinition's run after TUFX's, in the same frame -- no frame shows its
    // profile first.
    // Values already in place are left alone (BundledSettings).
    //
    // In the main menu the values are handed over again on the tick below: EVE
    // sets up that scene's managers late -- five physics frames after its
    // global manager starts there, for Kopernicus
    // (GlobalEVEManager.waitToRunLateSetup; GenericEVEManager: SceneLoad main
    // menu, DelayedLoad) -- and its apply puts EVE's own cloud quality back over
    // ReDefinition's. Physics frames are not seconds, so this asks again while the
    // menu is up; handing over values already in place costs nothing and says
    // nothing.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class BundledSettingsAddon : MonoBehaviour
    {
        private const float TickInterval = 2f;
        // How often the open settings window looks for changes made in another.
        private const float SyncInterval = 0.25f;
        private float nextSync;

        private static BundledSettingsAddon instance;
        private static readonly Dictionary<string, Action> endOfFrame = new Dictionary<string, Action>();
        private static readonly Dictionary<string, Action> beforeScene = new Dictionary<string, Action>();
        private bool endOfFrameScheduled;
        private float nextTick;

        private void Awake()
        {
            instance = this;
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
            GameEvents.onGameSceneLoadRequested.Add(OnSceneLoadRequested);
            GameEvents.onLevelWasLoaded.Add(OnLevelLoaded);
            GameEvents.onLevelWasLoadedGUIReady.Add(OnLevelReady);
            GameEvents.OnCameraChange.Add(OnCameraChange);
            GameEvents.onGUIApplicationLauncherReady.Add(OnLauncherReady);
            GameEvents.OnGameSettingsApplied.Add(OnGameSettingsApplied);
        }

        private void OnDestroy()
        {
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoadRequested);
            GameEvents.onLevelWasLoaded.Remove(OnLevelLoaded);
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnLevelReady);
            GameEvents.OnCameraChange.Remove(OnCameraChange);
            GameEvents.onGUIApplicationLauncherReady.Remove(OnLauncherReady);
            GameEvents.OnGameSettingsApplied.Remove(OnGameSettingsApplied);
            if (instance == this) instance = null;
        }

        // Once, at the end of this frame, however often it is asked for: EVE's
        // rebuild of its clouds, as EveCompatibility does it -- a renderer still
        // rendering in the frame would set itself up again just before it is
        // destroyed -- and Parallax's updates of its materials.
        internal static void AtEndOfFrame(string key, Action action)
        {
            if (instance == null || !instance.isActiveAndEnabled)
            {
                action();
                return;
            }

            endOfFrame[key] = action;
            instance.Schedule();
        }

        // Waiting for the end of the frame takes a coroutine; where none can
        // run, the queue is emptied at once rather than never.
        private void Schedule()
        {
            if (endOfFrameScheduled || endOfFrame.Count == 0) return;
            try
            {
                StartCoroutine(RunAtEndOfFrame());
                endOfFrameScheduled = true;
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("bundled-end-of-frame", "A bundled setting's follow-up could not wait for the end"
                                      + " of the frame (" + CompatibilityLog.Reason(e) + "); it runs at once.");
                Run(endOfFrame, "bundled-end-of-frame");
            }
        }

        // Unity stops a coroutine when this is disabled, so the flag goes with
        // it -- otherwise nothing would ever be scheduled again, and every
        // follow-up after that would only be queued.
        private void OnDisable()
        {
            endOfFrameScheduled = false;
        }

        // Once, as the next scene is asked for: Parallax's scatter
        // renormalisation, which its own window does with the planet rebuilt
        // and the game paused. A change made while a scene is already being
        // left runs with it -- these are emptied after the values are handed
        // over.
        internal static void AtNextSceneChange(string key, Action action)
        {
            // Queued even with no add-on to run it yet: it must not run at once.
            // Only the add-on's own scene handler empties this queue; the add-on
            // exists from the first frame of the game, before any mod can be
            // built, and where it does not the line below says so.
            if (instance == null)
                CompatibilityLog.Warn("bundled-before-scene", "A bundled setting's follow-up was queued with no add-on"
                                      + " to run it: it waits for the first scene change after the add-on starts, and"
                                      + " does not run at all if it never does.");
            beforeScene[key] = action;
        }

        private IEnumerator RunAtEndOfFrame()
        {
            yield return new WaitForEndOfFrame();
            endOfFrameScheduled = false;
            Run(endOfFrame, "bundled-end-of-frame");
        }

        private static void Run(Dictionary<string, Action> queue, string kind)
        {
            if (queue.Count == 0) return;
            List<KeyValuePair<string, Action>> due = new List<KeyValuePair<string, Action>>(queue);
            queue.Clear();
            foreach (KeyValuePair<string, Action> pair in due)
            {
                try
                {
                    pair.Value();
                }
                catch (Exception e)
                {
                    CompatibilityLog.Warn(kind + "-" + pair.Key, "A bundled setting's follow-up failed ("
                                                                 + CompatibilityLog.Reason(e) + ").");
                }
            }
        }

        private void OnSceneLoadRequested(GameScenes scene)
        {
            BundledSettings.ReapplyStored("before " + scene, sceneChange: true);
            Run(beforeScene, "bundled-before-scene");
        }

        private void OnLevelLoaded(GameScenes scene)
        {
            BundledSettings.NoteSceneLoaded();
            ModWindowClose.Forget();
            BundledSettings.ReapplyStored(scene + " loaded");
            Requirements.Enforce(scene + " loaded");
        }

        private void OnLevelReady(GameScenes scene)
        {
            BundledSettings.ReapplyStored(scene + " ready");
            Requirements.Enforce(scene + " ready");
            ToolbarTakeover.Refresh();
        }

        // KSP's own settings screen, or this mod's KSP rows, applied: what a
        // loaded mod requires is put back over a value set there. The settings
        // window follows KSP's settings through KSP's registration (KspBehaviour).
        private void OnGameSettingsApplied()
        {
            Requirements.Enforce("KSP's settings applied");
        }

        private void OnCameraChange(CameraManager.CameraMode mode)
        {
            BundledSettings.ReapplyStored("camera now " + mode);
        }

        private void OnLauncherReady()
        {
            ToolbarTakeover.Refresh();
        }

        private void Update()
        {
            Schedule();   // anything queued while this was disabled

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
            if (HighLogic.LoadedScene == GameScenes.MAINMENU) BundledSettings.ReapplyStored("main menu");
            // Elsewhere only to the mods a value could not reach before: Trajectories
            // from its first flight on, whenever in the scene's loading it makes its
            // settings.
            else BundledSettings.ReapplyWaiting("now that its mod can take it");
        }
    }
}
