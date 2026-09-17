using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using FidelityFX.FSR3;
using ReDefinition.Framework;
using UnityEngine;

namespace ReDefinition
{
    // User interface and lifecycle of the upscaler.
    //
    // The hotkeys avoid Alt: that is KSP's modifier key, and every
    // Alt combination triggers game functions on the side. Left Ctrl and Shift
    // are bound to throttle. What remains is the right-hand side of the
    // keyboard, which KSP does not bind -- and three keys at once are not hit by
    // accident.
    //
    // Apart from on/off everything goes through the windows -- the settings
    // window the toolbar button opens (SettingsWindow), and the diagnostics
    // window this add-on draws -- so that no further key combination has to be
    // claimed for each setting.
    //
    // Here the rig's lifecycle: attached, rebuilt and detached as the scene, the
    // camera and the settings ask. The settings, the diagnostics window and the
    // frame rates are in UpscalerAddon.*.cs.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public partial class UpscalerAddon : MonoBehaviour
    {


        // Camera the rig attaches to. It finds the remaining ones itself.
        private static readonly string[] CameraPreference = { "Camera 00", "Main Camera" };

        private UpscalerRig rig;
        private Camera attachedTo;

        private GameScenes lastScene = (GameScenes)(-1);
        private bool wantEnabled;
        private string problem;
        private float nextAttachAttempt;
        private CameraManager.CameraMode lastCameraMode;
        private bool cameraModeKnown;

        // Why the chosen technique cannot run and FSR 3 runs instead, as TryAttach
        // last found; the reason last logged.
        private string fallbackReason;
        private string loggedFallback;

        // A new rig asked for (Reattach), built by the next Update.
        private bool reattachPending;

        // Techniques the proxy could not run, and why, at the output size they
        // failed at: FSR 3 runs in their place until something that decides it
        // changes -- the technique, its mode or preset, the output size, the
        // scene -- or the player asks for another try.
        private readonly Dictionary<UpscalerBackend, string> nativeFailures = new Dictionary<UpscalerBackend, string>();
        private Vector2Int nativeFailureOutput;
        // Those the proxy called standing -- no RTX GPU, no DLL, no D3D12 path:
        // only a new choice of technique or the player's retry forgets them.
        private readonly HashSet<UpscalerBackend> stableFailures = new HashSet<UpscalerBackend>();

        // What setting up the rig -- the upscaler's, or frame generation's capture
        // alone -- last failed with, logged once, and how often each failed: after
        // three, not again before the next scene or a switch of frame generation or
        // the upscaler, and the upscaler once more after a change of its settings.
        // The player's settings stay as they are; an upscaler that gave up leaves
        // frame generation its capture alone.
        private string loggedSetupProblem;
        private const int SetupAttempts = 3;
        private int setupFailures;
        private int captureFailures;

        // Update's steps into other mods, made once: a method group makes a new
        // delegate each time.
        private Action enforceProfileStep;
        private Action syncHostStackStep;
        private static readonly Action ReassertHostStackStep = HostStack.Reassert;

        // Setup attempts that may fail without switching the upscaler off: a rig
        // the old one asked for can meet a device that is not back yet.
        private const int RebuildAttachRetries = 10;
        private int attachRetries;

        // DLSS's render sizes, asked of the proxy before the first rig at an output
        // size (TryAttach), and how many frames the rig has waited for them.
        private const int DlssSizeWaitFrames = 30;
        private Vector2Int dlssSizesAskedFor;
        private int dlssSizeWaits;

        private UpscalerToolbarButton toolbarButton;

        // The one instance, for KSP's settings dialog (KspSettingsSection),
        // which reads and changes the same settings as the diagnostics window.
        internal static UpscalerAddon Instance { get; private set; }

        // The rig in place, or null; for ScattererCompatibility's hook and
        // UnityMouseEvents.
        internal UpscalerRig CurrentRig { get { return rig; } }

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            Instance = this;
            windowId = GetInstanceID();
            enforceProfileStep = EnforceProfile;
            syncHostStackStep = SyncHostStack;
            // What KSP's V-Sync row allows while DLSS frame generation runs (KSP.cfg).
            KspBehaviour.DlssFrameGenerationRuns = () => frameGeneration && FrameGenerationBridge.DlssFrameGenerationRuns;
            KspBehaviour.DlssFrameGenerationVsync = () => FrameGenerationBridge.DlssFrameGenerationVsync;

            Debug.Log(UpscalerProbe.Tag + " Upscaler ready."
                      + " Toolbar button for the settings window; its Keys tab holds the hotkeys.");

            LoadSettings();
            // The frame's state for every mod (ReDefinition.Api), with or without a rig; a
            // failure there costs the interface, not the rest of the add-on.
            Guarded("shared-frame", SharedFrame.Install);
            FrameGenerationBridge.RegisterMainThread();
            ScattererCompatibility.InstallHook();
            EveCloudMotion.InstallHook();
            UnityMouseEvents.Install();
            GameEvents.OnGameSettingsApplied.Add(OnGameSettingsApplied);
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;

            // The button opens the settings window; the diagnostics window this
            // add-on draws opens from there and by hotkey.
            toolbarButton = new UpscalerToolbarButton(SettingsWindow.Toggle);
            toolbarButton.Register();

            // Both sets, with and without HDR_COLOR_INPUT, right at startup: then the
            // log already says whether AssetBundle and plugin match. Which one a rig
            // takes is known once its camera is (TryAttach).
            if (FsrShaderBundle.Load(true) == null || FsrShaderBundle.Load(false) == null)
            {
                problem = FsrShaderBundle.LastError;
                Debug.LogWarning(UpscalerProbe.Tag + " Upscaler not operational: " + problem);
            }
            else
            {
                Debug.Log(UpscalerProbe.Tag + " All eleven required compute shaders found."
                          + " Compute support: " + SystemInfo.supportsComputeShaders
                          + ", graphics API: " + SystemInfo.graphicsDeviceType
                          + ", reversed depth: " + SystemInfo.usesReversedZBuffer);
            }
        }

        private void Update()
        {
            if (HighLogic.LoadedScene != lastScene)
            {
                lastScene = HighLogic.LoadedScene;
                Detach();
                ForgetNativeFailures(false);
                setupFailures = 0;
                captureFailures = 0;
                // What made the context fail -- memory, most likely -- may have
                // gone with the old scene.
                FrameGenerationBridge.RetryContext();
            }

            HandleHotkeys();
            ReflectSettingsWindow();

            // Each into other mods by reflection, and each on its own: one that
            // throws -- a getter of a scene being torn down -- must not keep the
            // saving or the rig below from running this frame.
            Guarded("profile", enforceProfileStep);
            Guarded("host-stack", syncHostStackStep);
            Guarded("host-stack-reassert", ReassertHostStackStep);
            SaveSettingsIfChanged();
            SharedFrame.PollProfile();

            bool active = rig != null && rig.Ready;
            // Neither on nor off: a rig for frame generation alone carries the
            // capture's cost, which would skew the upscaler's comparison.
            if (rig == null || !rig.PassThrough)
                (active ? meterOn : meterOff).Sample(Time.unscaledDeltaTime);
            UpdateFrameRates();

            RebuildOnCameraModeChange();

            // The new rig settings asked for (Reattach) -- after the hotkeys and the
            // profile check, which may have switched the upscaler off since.
            if (reattachPending) RebuildRig();

            // The rig asked for it: its textures lost, DLSS asking for another
            // render size, or a technique that stopped and gives way to FSR 3.
            // Attached again by the loop below, with retries that leave the
            // upscaler on while Setup cannot succeed yet.
            if (rig != null && rig.RebuildWanted)
            {
                if (rig.FallbackReason != null)
                {
                    nativeFailures[rig.Backend] = rig.FallbackReason;
                    nativeFailureOutput = rig.DisplaySize;
                    if (rig.FallbackStable) stableFailures.Add(rig.Backend);
                    else stableFailures.Remove(rig.Backend);
                }
                Detach();
                attachRetries = RebuildAttachRetries;
                nextAttachAttempt = 0f;
            }

            // The rig follows what is wanted of it: the upscaler, or with the
            // upscaler off only frame generation's inputs (UpscalerRig.PassThrough).
            if (rig != null && (!WantsRig() || rig.PassThrough != PassThroughWanted))
            {
                Detach();
                nextAttachAttempt = 0f;
            }

            if (WantsRig() && rig == null && Time.unscaledTime >= nextAttachAttempt)
            {
                nextAttachAttempt = Time.unscaledTime + 1.0f;
                TryAttach();
            }

            // If the camera loses validity the rig has to be rebuilt, otherwise
            // it keeps computing on a dead camera -- and so does a rig its camera
            // tore down: switched off and on again within one frame, the camera
            // looks valid while the rig has nothing left (UpscalerRig.OnDisable).
            if (rig != null && (rig.TornDown || attachedTo == null || !attachedTo.isActiveAndEnabled))
                Detach();
        }

        private static void Guarded(string kind, Action body)
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

        // A change of camera mode changes which cameras render. Entering IVA,
        // KSP disables every game camera, enables the flight cameras again and
        // then InternalCamera (CameraManager.SetCameraIVA, decompiled). The rig
        // therefore stays on Camera 00, while InternalCamera, which it never
        // redirected, draws past the upscaler -- and, with frame generation, after the
        // HUD-less snapshot. So the rig is rebuilt for the new set of cameras,
        // and a new rig starts without history, which is the reset the cut
        // needs anyway. Only on an actual change: KSP also calls
        // SetCameraFlight when the view stays in flight.
        private void RebuildOnCameraModeChange()
        {
            CameraManager manager = CameraManager.Instance;
            if (manager == null)
            {
                cameraModeKnown = false;
                return;
            }

            CameraManager.CameraMode mode = manager.currentCameraMode;
            if (cameraModeKnown && mode != lastCameraMode && rig != null)
            {
                Debug.Log(UpscalerProbe.Tag + " Camera mode " + lastCameraMode + " -> " + mode
                          + ": rebuilding the upscaler for the new set of cameras.");
                Detach();
                nextAttachAttempt = 0f;
            }
            lastCameraMode = mode;
            cameraModeKnown = true;
        }

        // The other mods' antialiasing, and what Scatterer's TAA leaves behind, are
        // taken for the upscaler while a graphics profile is chosen, and handed back when it
        // goes (HostStack) -- with the upscaler switched off too, and not at every
        // toggle of it, which would compare the frame rate against another set of
        // effects. Taken where the cameras it configures exist: once a rig runs, or
        // in flight and the editors. Taken earlier, at the main menu say, HostStack
        // would record Scatterer's state before Scatterer has set itself up. What a
        // later scene builds anew is taken once a second, each with its own record
        // (HostStack.Reassert).
        private void SyncHostStack()
        {
            bool chosen = ProfileChosen();
            if (chosen && !HostStack.Applied
                && (rig != null || HighLogic.LoadedSceneIsFlight || HighLogic.LoadedSceneIsEditor))
                hostStackMessage = HostStack.Apply();
            else if (!chosen && HostStack.Applied)
                hostStackMessage = HostStack.Restore();
        }

        // The hotkeys as the player set them (UpscalerSettings, the Keys tab).
        // While a row is listening for a key, none of them fires.
        private void HandleHotkeys()
        {
            KeyCapture.Poll();
            if (KeyCapture.Busy) return;

            if (KeyCombination.Parse(settings.UpscalerKey).Pressed()) SetEnabled(!wantEnabled);
            if (KeyCombination.Parse(settings.SettingsWindowKey).Pressed()) SettingsWindow.Toggle();
            if (KeyCombination.Parse(settings.DiagnosticsKey).Pressed()) ToggleWindow();
            if (KeyCombination.Parse(settings.CameraListKey).Pressed()) UpscalerProbe.LogCameraSurvey();
        }

        // The toolbar button shows whether the settings window is open, which
        // closes by its own buttons as well.
        private bool settingsShown;

        private void ReflectSettingsWindow()
        {
            bool shown = SettingsWindow.Visible;
            if (shown == settingsShown) return;
            settingsShown = shown;
            if (toolbarButton != null) toolbarButton.Reflect(shown);
        }

        // A rig runs for the upscaler, and with the upscaler off for frame
        // generation where the proxy can generate.
        private bool WantsRig()
        {
            // Not for a context that cannot be made: the proxy tries that again
            // only after a new swapchain, an ini change or the player switching
            // frame generation on, which need no rig.
            return PassThroughWanted
                ? frameGeneration && FrameGenerationBridge.InputsUsable && captureFailures < SetupAttempts
                : true;
        }

        // The rig for frame generation's capture alone: with the upscaler off, and
        // with an upscaler that could not be set up.
        private bool PassThroughWanted
        {
            get { return !wantEnabled || setupFailures >= SetupAttempts; }
        }

        // What changes the outcome of a failure that need not stand forgets it;
        // stableToo for a new choice of technique and the player's retry.
        private void ForgetNativeFailures(bool stableToo)
        {
            if (nativeFailures.Count == 0) return;
            if (stableToo)
            {
                nativeFailures.Clear();
                stableFailures.Clear();
                return;
            }
            // One that stood stands no more once the proxy stops saying so: the
            // player copied the DLL or set its folder (Dlss::Status, AmdUpscaler::Status).
            List<UpscalerBackend> forgotten = new List<UpscalerBackend>();
            foreach (UpscalerBackend failed in nativeFailures.Keys)
            {
                NativeUpscalerLink link = UpscalerBackends.Link(failed);
                if (!stableFailures.Contains(failed) || link == null || link.State() != -2) forgotten.Add(failed);
            }
            foreach (UpscalerBackend failed in forgotten)
            {
                nativeFailures.Remove(failed);
                stableFailures.Remove(failed);
            }
        }

        // Every scene load Unity reports, additive ones included -- a revert, a
        // quickload, the editors' switch between VAB and SPH. The rig's sweep of
        // skinned renderers counts these.
        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene,
                                   UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            UpscalerRig.NoteSceneLoad();
        }

        // KSP's own settings screens write the quality level and MSAA into
        // QualitySettings when the player applies them, over what the rig set
        // there. Both fire this event once they are done.
        private void OnGameSettingsApplied()
        {
            if (rig != null) rig.ReassertQualityOverrides();
        }

        // A new rig for settings built into it -- by the next Update, or at the end
        // of Apply. Built from a control inside the window, it would change the rows
        // after that control within the same IMGUI event, against what the layout
        // pass counted; and however many settings change in one step, it is built
        // once.
        private void Reattach()
        {
            reattachPending = true;
        }

        private void RebuildRig()
        {
            reattachPending = false;
            // A changed setting may be what the upscaler could not be set up with:
            // one more try, in place of frame generation's capture if that ran for
            // it, which is back the frame after a failure.
            if (wantEnabled && setupFailures >= SetupAttempts) setupFailures = SetupAttempts - 1;
            // A rig for frame generation alone depends on none of the upscaler's
            // settings; Update rebuilds it once the upscaler comes on.
            if (rig != null && rig.PassThrough && !wantEnabled) return;
            Detach();
            // Without the upscaler, Update attaches frame generation's rig at once.
            if (PassThroughWanted)
            {
                nextAttachAttempt = 0f;
                return;
            }
            // A failed attempt is not repeated by Update in the same frame.
            nextAttachAttempt = Time.unscaledTime + 1f;
            TryAttach();
        }

        // In the main menu and its settings screen the mod only takes settings:
        // they take effect in flight and in the editors, and the window is
        // there to change them before a game is loaded (UpscalerToolbarButton).
        // The settings screen has a "Main Camera" of its own, which the
        // upscaler would attach to for nothing.
        // So nothing is attempted in either, and a camera problem left over
        // from an earlier scene is dropped rather than shown.
        private bool problemIsCamera;

        private static bool OnlySettingsHere()
        {
            return HighLogic.LoadedScene == GameScenes.MAINMENU
                   || HighLogic.LoadedScene == GameScenes.SETTINGS;
        }

        private void TryAttach()
        {
            if (OnlySettingsHere())
            {
                if (problemIsCamera) problem = null;
                problemIsCamera = false;
                return;
            }

            Camera target = PickCamera();
            if (target == null)
            {
                problem = "No suitable camera in scene " + HighLogic.LoadedScene + ".";
                problemIsCamera = true;
                return;
            }

            // Only now is it settled which shader set is needed: it has to match
            // whether the camera renders in HDR, which KSP decides -- without it
            // in the editor, with it in flight.
            // Without the upscaler, for frame generation alone: no FSR, no shaders.
            bool passThrough = PassThroughWanted;
            Fsr3UpscalerShaders shaders = passThrough ? null : FsrShaderBundle.Load(target.allowHDR, sharpness > 0f);
            if (!passThrough && shaders == null)
            {
                // What a loaded bundle lacks it lacks until KSP starts again: not
                // tried again before the player switches something, and the
                // player's setting stays as it is.
                problem = FsrShaderBundle.LastError ?? "Shaders not loaded.";
                problemIsCamera = false;
                if (loggedSetupProblem != problem) Debug.LogError(UpscalerProbe.Tag + " " + problem);
                loggedSetupProblem = problem;
                setupFailures = SetupAttempts;
                return;
            }

            // A technique this installation cannot run falls back to FSR 3, which
            // runs wherever the mod does; the setting stays the player's.
            UpscalerBackend runs = backend;
            Vector2Int output = new Vector2Int(Screen.width, Screen.height);
            // A failure at another output size says nothing about this one.
            if (output != nativeFailureOutput)
            {
                ForgetNativeFailures(false);
                nativeFailureOutput = output;
            }
            string failure;
            fallbackReason = bypass || passThrough
                ? null
                : UpscalerBackends.WhyNotOffered(backend)
                  ?? (nativeFailures.TryGetValue(backend, out failure) ? failure : null);
            if (fallbackReason != null)
            {
                runs = UpscalerBackend.Fsr3;
                if (loggedFallback != fallbackReason)
                    Debug.LogWarning(UpscalerProbe.Tag + " " + UpscalerBackends.Name(backend)
                                     + " cannot run, FSR 3 runs instead: " + fallbackReason);
                loggedFallback = fallbackReason;
            }
            if (passThrough) runs = UpscalerBackend.Fsr3;

            // DLSS renders at the size it asks for (UpscalerRig.Setup), which the
            // proxy knows once it has been asked. Asked here at every output size it
            // does not know yet, answered on the render thread within a frame or
            // two; the rig waits that long rather than being built at a size DLSS
            // would have it rebuilt from, and no longer if no answer comes. The
            // texture only carries Unity's device.
            Vector2Int known;
            if (runs == UpscalerBackend.Dlss && !bypass
                && !DlssBridge.RenderSize(output, DlssBridge.Quality(quality), out known))
            {
                if (dlssSizesAskedFor != output)
                {
                    dlssSizesAskedFor = output;
                    dlssSizeWaits = 0;
                    DlssBridge.RequestRenderSizes(Texture2D.whiteTexture.GetNativeTexturePtr(), output);
                }
                if (dlssSizeWaits < DlssSizeWaitFrames)
                {
                    dlssSizeWaits++;
                    nextAttachAttempt = 0f;
                    return;
                }
            }

            UpscalerRig created = target.gameObject.AddComponent<UpscalerRig>();
            created.QualityMode = quality;
            created.Sharpness = sharpness;
            created.Sharpening = sharpness > 0f;
            created.AutoExposure = autoExposure;
            created.EnableMipmapBias = mipmapBias;
            // Bypass is the upscaler's diagnostic; frame generation's capture has no
            // upscaler to bypass, and would send nothing with it.
            created.Bypass = bypass && !passThrough;
            created.EnableJitter = jitter;
            created.ForceSkinnedMotionVectors = skinnedMotionVectors;
            created.CompensateLodBias = compensateLodBias;
            created.DisableMsaa = disableMsaa;
            created.ForceAnisotropic = forceAnisotropic;
            created.TufxAfterUpscaling = tufxAfterUpscaling;
            created.TransparencyMask = transparencyMask;
            created.ReactiveMask = reactiveMask;
#if DEVELOPMENT_BUILD
            created.DebugView = debugView;
#endif
            created.FrameGeneration = frameGeneration;
            created.Backend = runs;
            created.PassThrough = passThrough;
            created.DlssPreset = dlssPreset;
            created.RecordFastTurns = recordFastTurns;

            if (!created.Setup(target, shaders))
            {
                problem = created.Status;
                problemIsCamera = false;
                DestroyImmediate(created);
                if (attachRetries > 0)
                {
                    attachRetries--;
                    Debug.LogWarning(UpscalerProbe.Tag + " Upscaler could not be set up yet, trying again in a second: "
                                     + problem);
                    return;
                }
                // Tried again later rather than turning the player's setting off,
                // which would be saved, for what may pass: a scene loading, VRAM.
                int failures = passThrough ? ++captureFailures : ++setupFailures;
                bool givingUp = failures >= SetupAttempts;
                if (loggedSetupProblem != problem || givingUp)
                    Debug.LogWarning(UpscalerProbe.Tag
                                     + (passThrough ? " Frame generation's capture" : " The upscaler")
                                     + " could not be set up"
                                     + (!givingUp
                                         ? ", trying again in five seconds: "
                                         : passThrough
                                             ? ", and is not tried again before the next scene or a switch of the upscaler or frame generation: "
                                             : ", and is not tried again before the next scene, a switch of the upscaler or frame"
                                               + " generation, or a change of its settings: ")
                                     + problem);
                loggedSetupProblem = problem;
                // Given up, frame generation's capture may take over at once.
                nextAttachAttempt = givingUp ? 0f : Time.unscaledTime + 5f;
                return;
            }

            rig = created;
            attachRetries = 0;
            // The upscaler's failures stand while frame generation's capture runs
            // in its place.
            if (passThrough) captureFailures = 0;
            else setupFailures = 0;
            loggedSetupProblem = null;
            // The next rig asks again if its sizes are not known by then.
            dlssSizesAskedFor = Vector2Int.zero;
            attachedTo = target;
            if (!passThrough || !wantEnabled) problem = null;
            // A rig for frame generation alone measures neither.
            if (!passThrough) meterOn.Reset();

            Debug.Log(UpscalerProbe.Tag + " Upscaler running on '" + target.name + "': "
                      + rig.RenderSize.x + "x" + rig.RenderSize.y + " -> "
                      + rig.DisplaySize.x + "x" + rig.DisplaySize.y + " (" + quality + ", "
                      + UpscalerBackends.Name(runs)
                      + (passThrough
                          ? (wantEnabled ? ", the upscaler not set up" : ", upscaler off") + ": frame generation's inputs only"
                          : "") + ")");
            RequestRenderSizeFollowUp("upscaler attached at " + rig.RenderSize.x + "x" + rig.RenderSize.y);
        }

        private void Detach()
        {
            // Retries belong to the rebuild that set them (Update), not to a
            // later attempt that fails for another reason.
            attachRetries = 0;
            if (rig != null)
            {
                rig.Teardown();
                // Immediately rather than at end of frame: a setup following
                // straight after must not meet a half torn down instance.
                DestroyImmediate(rig);
                rig = null;
                RequestRenderSizeFollowUp("upscaler detached");
            }
            attachedTo = null;
            meterOff.Reset();
        }

        // Other mods size some of their buffers once, by the camera or the
        // screen (EveCompatibility, ScattererCompatibility). Whenever the rig
        // changes the texture the cameras render into they are sized anew, at
        // the end of the frame, once however many changes the frame made -- a
        // mode change detaches and attaches -- and with the rig then in place.
        // Not while the addon itself goes: a coroutine would not run.
        private bool followUpPending;
        private string followUpReason;
        private bool destroying;

        private void RequestRenderSizeFollowUp(string reason)
        {
            if (destroying) return;
            if (!EveCompatibility.Present && !ScattererCompatibility.Present) return;
            followUpReason = reason;
            if (followUpPending) return;
            followUpPending = true;
            StartCoroutine(FollowRenderSizeAtEndOfFrame());
        }

        // What the other mods were last sized for. EVE's volumetric clouds are
        // rebuilt only when that changed: a rebuild throws their history away,
        // and every Flight and IVA toggle detaches and attaches at the same
        // size. EVE's cloud shadows are checked every time and sized only where
        // they differ -- EVE makes the IVA camera's shadow renderer as the view
        // changes to IVA, before the rig has redirected that camera.
        private bool followedAttached;
        private Vector2Int followedSize;

        private IEnumerator FollowRenderSizeAtEndOfFrame()
        {
            yield return new WaitForEndOfFrame();
            followUpPending = false;

            bool attached = rig != null;
            Vector2Int size = attached ? rig.RenderSize : Vector2Int.zero;
            bool sizeChanged = attached != followedAttached || size != followedSize;
            followedAttached = attached;
            followedSize = size;

            EveCompatibility.AfterRenderSizeChange(sizeChanged, followUpReason);
            // Every time, hook or not: godrays Scatterer makes between a detach
            // and the attach after it are sized by the hook against a camera
            // not redirected yet, and only this search finds them.
            ScattererCompatibility.AfterRenderSizeChange(followUpReason);
        }

        private Camera PickCamera()
        {
            foreach (string wanted in CameraPreference)
            {
                foreach (Camera cam in Camera.allCameras)
                {
                    if (cam.name == wanted && cam.isActiveAndEnabled) return cam;
                }
            }
            return null;
        }

        private void OnDestroy()
        {
            destroying = true;
            if (Instance == this) Instance = null;
            GameEvents.OnGameSettingsApplied.Remove(OnGameSettingsApplied);
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
            });

            // The throttled check may not have run since the last change.
            Guarded("settings-save", () => Collect().Save());

            Detach();
            KeyCapture.Stop();
            SharedFrame.Uninstall();
            FsrShaderBundle.Unload();
        }
    }
}
