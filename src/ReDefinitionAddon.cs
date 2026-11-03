using System;
using ReDefinition.Api.Internal;
using ReDefinition.Proxy;
using ReDefinition.Core;
using ReDefinition.Diagnostics;
using ReDefinition.Settings.Behaviours;
using ReDefinition.Settings;
using ReDefinition.Ui;
using ReDefinition.Upscaler;
using ReDefinition.Window;
using UnityEngine;

namespace ReDefinition
{
    // The mod's main add-on, which starts with the game. It wires the hooks into KSP and other mods
    // and calls the parts it owns once per frame, in a fixed order: the rig's lifecycle
    // (RigLifecycle), the hotkeys (AddonHotkeys), the frame rates (FrameRateReadout) and the
    // diagnostics window (DiagnosticsWindow). It also drives frame generation's capture and the
    // raytracing (RtKsp) from its Update.
    //
    // Everything goes through the windows -- the settings
    // window the toolbar button opens (SettingsWindow), and the diagnostics
    // window this add-on draws -- so that no further key combination has to be
    // claimed for each setting.
    //
    // The settings and how a change takes effect are in ReDefinitionAddon.*.cs.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public partial class ReDefinitionAddon : MonoBehaviour
    {
        // The rig's lifecycle: attached, rebuilt and detached as the scene, the camera and the settings ask.
        private RigLifecycle rigs;

        // Update's steps into other mods, made once: a method group makes a new
        // delegate each time.
        private Action enforceProfileStep;
        private Action syncHostStackStep;
        private Action autoStep;
        private static readonly Action ReassertHostStackStep = HostStack.Reassert;

        // The toolbar button that opens the settings window and shows whether it is open.
        private ToolbarButton toolbarButton;

        // The frame rates, with the upscaler on and off, that the diagnostics window shows.
        private readonly FrameRateReadout rates = new FrameRateReadout();

        // The diagnostics window this add-on draws, and the screen recording it arms.
        private DiagnosticsWindow diagnostics;
        private readonly ScreenRecordingTimer recording = new ScreenRecordingTimer();

        // The hotkeys, asked once per frame.
        private AddonHotkeys hotkeys;

        // The one instance, for KSP's settings dialog (KspSettingsSection),
        // which reads and changes the same settings as the diagnostics window.
        internal static ReDefinitionAddon Instance { get; private set; }

        // Set up once at startup: the settings, the hooks into KSP and other mods, the toolbar button, and both shader
        // sets of the bundle, whose check the log reports.
        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            Instance = this;
            rigs = new RigLifecycle(this, rates);
            diagnostics = new DiagnosticsWindow(this, rigs, rates, recording, GetInstanceID());
            hotkeys = new AddonHotkeys(settings, diagnostics, ToggleUpscaler);
            enforceProfileStep = EnforceProfile;
            syncHostStackStep = SyncHostStack;
            autoStep = ResolveAutoOnNewScreen;
            // What KSP's V-Sync row allows while DLSS frame generation runs (KSP.cfg).
            KspBehaviour.DlssFrameGenerationRuns = () => frameGeneration && FrameGenerationBridge.DlssFrameGenerationRuns;
            KspBehaviour.DlssFrameGenerationVsync = () => FrameGenerationBridge.DlssFrameGenerationVsync;
            // And while Intel's XeLL lowers the latency, with XeSS frame generation or alone.
            KspBehaviour.XellEveryRefresh = () => FrameGenerationBridge.XellEveryRefresh;
            KspBehaviour.SettingsApplied = SettingsWindow.FollowModSoon;
            TufxBehaviour.ProfileApplied = HostStack.ReassertNow;

            Debug.Log(Log.Tag + " Upscaler ready."
                      + " Toolbar button for the settings window; its Keys tab holds the hotkeys.");

            LoadSettings();
            // The frame's state for every mod (ReDefinition.Api), with or without a rig; a
            // failure there costs the interface, not the rest of the add-on.
            Guarded("shared-frame", SharedFrame.Install);
            FrameGenerationBridge.RegisterMainThread();
            // Another build of Scatterer or EVE costs its own hook, not the rest of the add-on.
            Guarded("scatterer-hook", ScattererCompatibility.InstallHook);
            Guarded("eve-hook", EveCloudMotion.InstallHook);
            Guarded("scatterer-cloud-light", ReDefinition.Raytracing.EveCloudMoonlight.InstallHook);
            Guarded("eve-cloud-relight", ReDefinition.Raytracing.EveCloudRelight.InstallHook);
            UnityMouseEvents.Install();
            ReflexFrames.Install();
            EscapeGuard.Install();
            GameEvents.OnGameSettingsApplied.Add(OnGameSettingsApplied);
            GameEvents.OnMapEntered.Add(OnMapToggled);
            GameEvents.OnMapExited.Add(OnMapToggled);
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;

            // The button opens the settings window; the diagnostics window this
            // add-on draws opens from there and by hotkey.
            toolbarButton = new ToolbarButton(SettingsWindow.Toggle);
            toolbarButton.Register();

            // Both sets, with and without HDR_COLOR_INPUT, right at startup: then the
            // log already says whether AssetBundle and plugin match. Which one a rig
            // takes is known once its camera is (RigLifecycle.TryAttach).
            if (ShaderBundle.Load(true) == null || ShaderBundle.Load(false) == null)
            {
                string problem = ShaderBundle.LastError;
                rigs.NoteProblem(problem);
                Debug.LogWarning(Log.Tag + " Upscaler not operational: " + problem);
            }
            else
            {
                Debug.Log(Log.Tag + " All ten required compute shaders found."
                          + " Compute support: " + SystemInfo.supportsComputeShaders
                          + ", graphics API: " + SystemInfo.graphicsDeviceType
                          + ", reversed depth: " + SystemInfo.usesReversedZBuffer);
            }
        }

        // Each frame: a recording that is due, a new scene's reset, the hotkeys, the steps into other mods, the saving,
        // the frame-rate meters, and the rig attached, rebuilt or detached as the settings and the cameras ask.
        private void Update()
        {
            recording.Tick();
            rigs.FollowScene();

            hotkeys.Handle();
            ReflectSettingsWindow();
            raytracing.Update(rigs.PickCamera());

            // Each into other mods by reflection, and each on its own: one that
            // throws -- a getter of a scene being torn down -- must not keep the
            // saving or the rig below from running this frame.
            Guarded("profile", enforceProfileStep);
            Guarded("host-stack", syncHostStackStep);
            Guarded("host-stack-reassert", ReassertHostStackStep);
            Guarded("auto", autoStep);
            SaveSettingsIfChanged();
            SharedFrame.PollProfile();

            UpscalerRig rig = rigs.Rig;
            bool active = rig != null && rig.Ready;
            // Neither on nor off: a rig for frame generation alone carries the
            // capture's cost, or the native antialiasing's, which would skew the
            // upscaler's comparison.
            if (rig == null || (!rig.PassThrough && wantEnabled)) rates.Sample(active);
            rates.Update(diagnostics.Visible);
            if (diagnostics.Visible) diagnostics.RefreshLiveStrip();

            rigs.Tick();
        }

        // Runs one step of Update; an exception is a warning in the log, a few per kind, and the frame goes on.
        internal static void Guarded(string kind, Action body)
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("update-" + kind, "ReDefinition's " + kind + " step failed this frame ("
                                                        + CompatibilityLog.Reason(e) + ").");
            }
        }

        // The upscaler's hotkey: on when it is off, off when it is on.
        private void ToggleUpscaler()
        {
            SetEnabled(!wantEnabled);
        }

        // Set where a camera's antialiasing changes at once: the map view entered or left.
        private bool duplicatesNow;

        // What the last HostStack.Apply or Restore said, for the diagnostics window's page of other mods.
        internal string hostStackMessage;

        // Entering or leaving the map view: the next SyncHostStack re-takes the mods' antialiasing at once.
        private void OnMapToggled()
        {
            duplicatesNow = true;
        }

        // The other mods' antialiasing, and what Scatterer's TAA leaves behind, are
        // taken while the upscaler runs and handed back when it stops (HostStack). It also
        // runs with frame generation alone, as antialiasing at the screen's size
        // (RigLifecycle.NativeForFrameGeneration). Without either, the mods' own antialiasing is the
        // only one there is, one per camera (HostStack.Duplicates). Taken where the
        // cameras it configures exist: once a rig runs, or in flight and the editors.
        // Taken earlier, at the main menu say, HostStack would record Scatterer's state
        // before Scatterer has set itself up. A later scene's new parts are taken
        // each second, with their own record (HostStack.Reassert).
        private void SyncHostStack()
        {
            UpscalerRig rig = rigs.Rig;
            bool upscaling = ProfileChosen() && !rigs.PassThroughWanted;
            // Given back before HostStack takes everything.
            HostStack.SyncDuplicates(!upscaling && rig == null && ProfileChosen() && Live.OneAntialiasing, duplicatesNow);
            duplicatesNow = false;
            if (upscaling && !HostStack.Applied
                && (rig != null || HighLogic.LoadedSceneIsFlight || HighLogic.LoadedSceneIsEditor))
                hostStackMessage = HostStack.Apply();
            else if (!upscaling && HostStack.Applied)
                hostStackMessage = HostStack.Restore();
        }

        // Draws the diagnostics window.
        private void OnGUI()
        {
            diagnostics.OnGUI();
        }

        // From the settings window's Diagnostics button: the diagnostics window opens, on the
        // page of the upscaler's options where it was closed.
        internal void ShowDiagnostics()
        {
            diagnostics.Show();
        }

        // Whether the diagnostics window's search field has the keyboard.
        internal bool DiagnosticsTyping
        {
            get { return diagnostics.Typing; }
        }

        // The scene probe's way in (-redefinitionProbeDiagnostics): commands separated by commas for
        // the diagnostics window, answered with what was done (DiagnosticsWindow.RunProbeCommands).
        internal string ProbeDiagnostics(string commands)
        {
            return diagnostics.RunProbeCommands(commands);
        }

        // The toolbar's front, asked for again whenever the launcher is looked at
        // (ModWindowsAddon).
        internal void KeepToolbarButtonFirst()
        {
            if (toolbarButton != null) toolbarButton.KeepFirst();
        }

        // The toolbar button shows whether the settings window is open, which
        // closes by its own buttons as well.
        private bool settingsShown;

        // Lights the toolbar button where the settings window opened or closed since the last frame.
        private void ReflectSettingsWindow()
        {
            bool shown = SettingsWindow.Visible;
            if (shown == settingsShown) return;
            settingsShown = shown;
            if (toolbarButton != null) toolbarButton.Reflect(shown);
        }

        // Every scene load Unity reports, additive ones included -- a revert, a
        // quickload, the editors' switch between VAB and SPH. The rig's sweep of
        // skinned renderers counts these.
        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene,
                                   UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            UpscalerRig.NoteSceneLoad();
            ReDefinition.Raytracing.RtKsp.NewScene();
        }

        // KSP's own settings screens write the quality level and MSAA into
        // QualitySettings when the player applies them, over what the rig set
        // there. Both fire this event once they are done.
        private void OnGameSettingsApplied()
        {
            rigs.ReassertQualityOverrides();
        }

        // Takes everything back as the add-on goes: the raytracing, the event handlers, the toolbar button, the other
        // mods' state, the settings saved, the rig, the frame's state for other mods and the shader bundle.
        private void OnDestroy()
        {
            rigs.NoteDestroying();
            raytracing.Detach("ReDefinition's add-on goes");
            if (Instance == this)
            {
                Instance = null;
                UpscalerRig.Current = null;
            }
            GameEvents.OnGameSettingsApplied.Remove(OnGameSettingsApplied);
            GameEvents.OnMapEntered.Remove(OnMapToggled);
            GameEvents.OnMapExited.Remove(OnMapToggled);
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;

            if (toolbarButton != null)
            {
                toolbarButton.Unregister();
                toolbarButton = null;
            }
            // What was changed in other mods is taken back on teardown, each step
            // on its own, so the rig still comes down when one throws.
            Guarded("host-stack-restore", () =>
            {
                if (HostStack.Applied) HostStack.Restore();
                HostStack.SyncDuplicates(false, false);
            });

            // The throttled check may not have run since the last change. Not after a scene
            // probe, whose settings were its measurements' (SaveSettingsIfChanged).
            if (!Probe.SceneProbe.Requested) Guarded("settings-save", () => Collect().Save());

            rigs.Detach();
            KeyCapture.Stop();
            SharedFrame.Uninstall();
            ShaderBundle.Unload();
        }
    }
}
