using System;
using System.Collections;
using System.Collections.Generic;
using FidelityFX.FSR3;
using ReDefinition.Core;
using ReDefinition.Diagnostics;
using ReDefinition.Modules;
using ReDefinition.Proxy;
using ReDefinition.Upscaler;
using UnityEngine;

namespace ReDefinition
{
    // The upscaler rig's lifecycle: attached, rebuilt and detached as the scene, the camera and the
    // settings ask. It also keeps what went wrong (the techniques the proxy could not run, the
    // set-ups that failed) and sizes the other mods anew when the rig changes the cameras'
    // texture. The add-on calls Tick once per frame, after everything else it does.
    internal sealed class RigLifecycle
    {
        // Camera the rig attaches to. It finds the remaining ones itself.
        private static readonly string[] CameraPreference = { "Camera 00", "Main Camera" };

        // Setup attempts that may fail without switching the upscaler off: a rig
        // the old one asked for can meet a device that is not back yet.
        private const int RebuildAttachRetries = 10;

        // After three failures the rig is not tried again before the next scene or a switch of
        // frame generation or the upscaler.
        private const int SetupAttempts = 3;

        // DLSS's render sizes, asked of the proxy before the first rig at an output size
        // (TryAttach), and how many frames the rig has waited for them at most.
        private const int DlssSizeWaitFrames = 30;

        // The add-on, which hosts the coroutines, the settings the rig is built from, and the
        // frame rates, whose means start afresh with the rig.
        private readonly MonoBehaviour host;
        private readonly SettingsController config;
        private readonly FrameRateReadout rates;

        // The rig running, null while none is, and the camera it is attached to.
        private UpscalerRig rig;
        private Camera attachedTo;

        // The scene the last Update saw, which a new one detaches from; when TryAttach may try next;
        // and the camera mode last seen (RebuildOnCameraModeChange).
        private GameScenes lastScene = (GameScenes)(-1);
        private float nextAttachAttempt;
        private CameraManager.CameraMode lastCameraMode;
        private bool cameraModeKnown;

        // Why the chosen technique cannot run and FSR 3 runs instead, as TryAttach
        // last found; the reason last logged.
        private string fallbackReason;
        private string loggedFallback;

        // A new rig asked for (Reattach), built by the next Tick.
        private bool reattachPending;

        // Techniques the proxy could not run, and why, at the output size they
        // failed at. FSR 3 runs in their place until something that decides it
        // changes (the technique, its mode or preset, the output size, the
        // scene) or the player asks for another try.
        private readonly Dictionary<UpscalerBackend, string> nativeFailures = new Dictionary<UpscalerBackend, string>();
        private Vector2Int nativeFailureOutput;
        // Those the proxy called standing -- no RTX GPU, no DLL, no D3D12 path:
        // only a new choice of technique or the player's retry forgets them.
        private readonly HashSet<UpscalerBackend> stableFailures = new HashSet<UpscalerBackend>();

        // What setting up the rig (the upscaler's, or frame generation's capture
        // alone) last failed with, logged once, and how often each failed. The upscaler is
        // tried once more after a change of its settings. The player's settings stay as they
        // are; an upscaler that gave up leaves frame generation its capture alone.
        private string loggedSetupProblem;
        private int setupFailures;
        private int captureFailures;
        private int attachRetries;

        // DLSS's render sizes the proxy was asked for, and how many frames the rig has waited for them.
        private Vector2Int dlssSizesAskedFor;
        private int dlssSizeWaits;

        // In the main menu and its settings screen the mod only takes settings:
        // they take effect in flight and in the editors, and the window is
        // there to change them before a game is loaded (ToolbarButton).
        // The settings screen has a "Main Camera" of its own, which the
        // upscaler would attach to for nothing.
        // So nothing is attempted in either, and a camera problem left over
        // from an earlier scene is dropped rather than shown.
        private bool problemIsCamera;

        // Other mods size some of their buffers once, by the camera or the
        // screen (EveCompatibility, ScattererCompatibility,
        // DeferredCompatibility). Whenever the rig changes the texture the
        // cameras render into they are sized anew, at
        // the end of the frame, once however many changes the frame made -- a
        // mode change detaches and attaches -- and with the rig then in place.
        // Not while the addon itself goes: a coroutine would not run.
        private bool followUpPending;
        private string followUpReason;
        private bool destroying;

        // What the other mods were last sized for. EVE's volumetric clouds are
        // rebuilt only when that changed: a rebuild throws their history away,
        // and every Flight and IVA toggle detaches and attaches at the same
        // size. EVE's cloud shadows are checked every time and sized only where
        // they differ -- EVE makes the IVA camera's shadow renderer as the view
        // changes to IVA, before the rig has redirected that camera.
        private bool followedAttached;
        private Vector2Int followedSize;

        // Asked every frame: the cameras into a buffer kept, as Camera.allCameras is a
        // new array on every read.
        private Camera[] pickBuffer = new Camera[16];

        // The first active camera of each of CameraPreference's names, for PickCamera.
        private readonly Camera[] pickFirst = new Camera[CameraPreference.Length];

        // A lifecycle that builds its rigs from the settings given, and runs its coroutines on the host given.
        public RigLifecycle(MonoBehaviour host, SettingsController config, FrameRateReadout rates)
        {
            this.host = host;
            this.config = config;
            this.rates = rates;
        }

        // The rig running, null while none is.
        public UpscalerRig Rig
        {
            get { return rig; }
        }

        // Why the upscaler does not run, for the windows; null while nothing is wrong.
        public string Problem { get; private set; }

        // Why the chosen technique cannot run and FSR 3 runs instead; null where it runs.
        public string FallbackReason
        {
            get { return fallbackReason; }
        }

        // The rig for frame generation's capture alone: with the upscaler off, and
        // with an upscaler that could not be set up.
        public bool PassThroughWanted
        {
            get { return (!config.WantEnabled && !NativeForFrameGeneration) || setupFailures >= SetupAttempts; }
        }

        // Whether the proxy could not run this technique at the output size, and FSR 3 runs in its place.
        public bool HasNativeFailure(UpscalerBackend backend)
        {
            return nativeFailures.ContainsKey(backend);
        }

        // Says why the upscaler does not run, as the startup found it.
        public void NoteProblem(string problem)
        {
            Problem = problem;
        }

        // Whether the scene is the main menu or its settings screen, where no rig is attached.
        public static bool OnlySettingsHere()
        {
            return HighLogic.LoadedScene == GameScenes.MAINMENU
                   || HighLogic.LoadedScene == GameScenes.SETTINGS;
        }

        // A new scene detaches the rig and lets what failed be tried again.
        public void FollowScene()
        {
            if (HighLogic.LoadedScene == lastScene) return;
            lastScene = HighLogic.LoadedScene;
            Detach();
            ForgetNativeFailures(false);
            setupFailures = 0;
            captureFailures = 0;
            // What made the context fail -- memory, most likely -- may have
            // gone with the old scene.
            FrameGenerationBridge.RetryContext();
        }

        // The rig attached, rebuilt or detached as the settings and the cameras ask.
        public void Tick()
        {
            RebuildOnCameraModeChange();

            // The new rig settings asked for (Reattach) -- after the hotkeys and the
            // profile check, which may have switched the upscaler off since.
            RebuildPending();

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

            // The rig follows what is wanted of it: the upscaler, at the player's
            // quality or with the upscaler off at the screen's size for frame
            // generation, or only frame generation's inputs (UpscalerRig.PassThrough).
            if (rig != null && (!WantsRig() || rig.PassThrough != PassThroughWanted
                                || (!rig.PassThrough && rig.QualityMode != EffectiveQuality)))
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

        // The first active camera by CameraPreference's names; null where there is none. Each active camera's name is
        // read once, as Object.name makes a new string on every read.
        public Camera PickCamera()
        {
            if (pickBuffer.Length < Camera.allCamerasCount) pickBuffer = new Camera[Camera.allCamerasCount];
            int count = Camera.GetAllCameras(pickBuffer);
            Array.Clear(pickFirst, 0, pickFirst.Length);
            for (int i = 0; i < count; i++)
            {
                Camera cam = pickBuffer[i];
                if (!cam.isActiveAndEnabled) continue;
                string name = cam.name;
                for (int rank = 0; rank < CameraPreference.Length; rank++)
                    if (pickFirst[rank] == null && name == CameraPreference[rank])
                    {
                        pickFirst[rank] = cam;
                        break;
                    }
            }
            foreach (Camera first in pickFirst)
                if (first != null) return first;
            return null;
        }

        // A new rig for settings built into it -- by the next Tick, or at the end
        // of Apply. Built from a control inside the window, it would change the rows
        // after that control within the same IMGUI event, against what the layout
        // pass counted; and however many settings change in one step, it is built
        // once.
        public void Reattach()
        {
            reattachPending = true;
        }

        // Builds the rig asked for by Reattach, if one was.
        public void RebuildPending()
        {
            if (reattachPending) RebuildRig();
        }

        // What changes the outcome of a failure that need not stand forgets it;
        // stableToo for a new choice of technique and the player's retry.
        public void ForgetNativeFailures(bool stableToo)
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

        // The upscaler chosen is this one, FSR 3 running in its place, and no failure
        // left against it: the rig is built again for it.
        public void RetryIfFallenBack(UpscalerBackend wanted)
        {
            if (config.Backend == wanted && config.WantEnabled && rig != null && rig.Backend != config.Backend
                && !nativeFailures.ContainsKey(config.Backend))
                Reattach();
        }

        // Forgets the failed set-ups of the upscaler and of frame generation's capture, so that both are tried again.
        public void ForgetSetupFailures()
        {
            setupFailures = 0;
            captureFailures = 0;
        }

        // Lets the set-up problem that was logged be logged again.
        public void ForgetLoggedSetupProblem()
        {
            loggedSetupProblem = null;
        }

        // Lets the next Tick attach a rig at once.
        public void AttachSoon()
        {
            nextAttachAttempt = 0f;
        }

        // Tears the rig down and removes it at once, and asks the other mods to be sized anew.
        public void Detach()
        {
            // Retries belong to the rebuild that set them (Tick), not to a
            // later attempt that fails for another reason.
            attachRetries = 0;
            if (rig != null)
            {
                rig.Teardown();
                // Immediately rather than at end of frame: a setup following
                // straight after must not meet a half torn down instance.
                UnityEngine.Object.DestroyImmediate(rig);
                rig = null;
                UpscalerRig.Current = null;
                RequestRenderSizeFollowUp("upscaler detached");
            }
            attachedTo = null;
            rates.ResetOff();
        }

        // Says that the add-on goes: no more coroutines for the other mods' resize.
        public void NoteDestroying()
        {
            destroying = true;
        }

        // KSP's own settings screens write the quality level and MSAA into
        // QualitySettings when the player applies them, over what the rig set
        // there.
        public void ReassertQualityOverrides()
        {
            if (rig != null) rig.ReassertQualityOverrides();
        }

        // A rig runs for the upscaler, and with the upscaler off for frame
        // generation where the proxy can generate.
        private bool WantsRig()
        {
            // Not for a context that cannot be made: the proxy tries that again
            // only after a new swapchain, an ini change or the player switching
            // frame generation on, which need no rig.
            return PassThroughWanted
                ? config.FrameGeneration && FrameGenerationBridge.InputsUsable && captureFailures < SetupAttempts
                : true;
        }

        // With the upscaler off and frame generation on, the upscaler runs all the same, at
        // the screen's size (NativeAA: DLAA, FSR's and XeSS's native AA). It is then the one
        // antialiasing there is, as while it upscales, with the mods' own taken
        // (HostStack) and with a jitter frame generation is told of. Measured without
        // ReDefinition, 2026-10-23: in the space centre and in flight Scatterer's TAA and
        // SMAA and TUFX's TAA all ran on the scene camera at once. Where the upscaler
        // cannot be set up, frame generation's capture alone, the mods' antialiasing theirs.
        private bool NativeForFrameGeneration
        {
            get { return !config.WantEnabled && config.FrameGeneration && FrameGenerationBridge.InputsUsable; }
        }

        // The quality the rig is built with: the player's, or NativeAA for frame
        // generation alone.
        private Fsr3Upscaler.QualityMode EffectiveQuality
        {
            get { return !config.WantEnabled ? Fsr3Upscaler.QualityMode.NativeAA : config.Quality; }
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
                Debug.Log(Log.Tag + " Camera mode " + lastCameraMode + " -> " + mode
                          + ": rebuilding the upscaler for the new set of cameras.");
                Detach();
                nextAttachAttempt = 0f;
            }
            lastCameraMode = mode;
            cameraModeKnown = true;
        }

        // Builds the rig asked for by Reattach: detaches the old one and attaches a new one at once, or leaves frame
        // generation's capture alone to Tick.
        private void RebuildRig()
        {
            reattachPending = false;
            // A changed setting may be what the upscaler could not be set up with:
            // one more try, in place of frame generation's capture if that ran for
            // it, which is back the frame after a failure.
            if (config.WantEnabled && setupFailures >= SetupAttempts) setupFailures = SetupAttempts - 1;
            // A rig for frame generation alone depends on none of the upscaler's
            // settings; Tick rebuilds it once the upscaler comes on.
            if (rig != null && rig.PassThrough && !config.WantEnabled) return;
            Detach();
            // Without the upscaler, Tick attaches frame generation's rig at once.
            if (PassThroughWanted)
            {
                nextAttachAttempt = 0f;
                return;
            }
            // A failed attempt is not repeated by Tick in the same frame.
            nextAttachAttempt = Time.unscaledTime + 1f;
            TryAttach();
        }

        // Attaches a rig to the scene's camera with the settings as they stand: the technique, or FSR 3 where it cannot
        // run; for DLSS, its render size waited for first. A failure leaves the reason in Problem and retries later.
        private void TryAttach()
        {
            if (OnlySettingsHere())
            {
                if (problemIsCamera) Problem = null;
                problemIsCamera = false;
                return;
            }

            Camera target = PickCamera();
            if (target == null)
            {
                Problem = "No suitable camera in scene " + HighLogic.LoadedScene + ".";
                problemIsCamera = true;
                return;
            }

            // Only now is it settled which shader set is needed: it has to match
            // whether the camera renders in HDR, which KSP decides -- without it
            // in the editor, with it in flight.
            // Without the upscaler, for frame generation alone: no FSR, no shaders.
            bool passThrough = PassThroughWanted;
            Fsr3UpscalerShaders shaders = passThrough ? null : ShaderBundle.Load(target.allowHDR, config.Sharpness > 0f);
            if (!passThrough && shaders == null)
            {
                // What a loaded bundle lacks it lacks until KSP starts again: not
                // tried again before the player switches something, and the
                // player's setting stays as it is.
                Problem = ShaderBundle.LastError ?? "Shaders not loaded.";
                problemIsCamera = false;
                if (loggedSetupProblem != Problem) Debug.LogError(Log.Tag + " " + Problem);
                loggedSetupProblem = Problem;
                setupFailures = SetupAttempts;
                return;
            }

            Vector2Int output = new Vector2Int(Screen.width, Screen.height);
            UpscalerBackend runs = ChooseBackendOrFallback(passThrough, output);
            Fsr3Upscaler.QualityMode builtWith = EffectiveQuality;
            if (WaitForDlssSizes(runs, output, builtWith)) return;

            UpscalerRig created = target.gameObject.AddComponent<UpscalerRig>();
            config.ConfigureRig(created, builtWith, runs, passThrough);

            if (!created.Setup(target, shaders))
            {
                OnSetupFailed(created, passThrough);
                return;
            }

            rig = created;
            UpscalerRig.Current = rig;
            attachRetries = 0;
            // The upscaler's failures stand while frame generation's capture runs
            // in its place.
            if (passThrough) captureFailures = 0;
            else setupFailures = 0;
            loggedSetupProblem = null;
            // The next rig asks again if its sizes are not known by then.
            dlssSizesAskedFor = Vector2Int.zero;
            attachedTo = target;
            if (!passThrough || !config.WantEnabled) Problem = null;
            // A rig for frame generation alone measures neither.
            if (!passThrough) rates.ResetOn();

            Debug.Log(Log.Tag + " Upscaler running on '" + target.name + "': "
                      + rig.RenderSize.x + "x" + rig.RenderSize.y + " -> "
                      + rig.DisplaySize.x + "x" + rig.DisplaySize.y + " (" + builtWith + ", "
                      + UpscalerBackends.Name(runs)
                      + (passThrough
                          ? (config.WantEnabled ? ", the upscaler not set up" : ", upscaler off") + ": frame generation's inputs only"
                          : !config.WantEnabled ? ", upscaler off: antialiasing at the screen's size for frame generation" : "") + ")");
            RequestRenderSizeFollowUp("upscaler attached at " + rig.RenderSize.x + "x" + rig.RenderSize.y);
        }

        // A technique this installation cannot run falls back to FSR 3, which runs wherever the mod does; the setting stays
        // the player's. This is the backend the rig is built for at the output size: the technique chosen, FSR 3 in its
        // place with the reason in fallbackReason, or FSR 3 for frame generation's capture alone.
        private UpscalerBackend ChooseBackendOrFallback(bool passThrough, Vector2Int output)
        {
            UpscalerBackend backend = config.Backend;
            UpscalerBackend runs = backend;
            // A failure at another output size says nothing about this one.
            if (output != nativeFailureOutput)
            {
                ForgetNativeFailures(false);
                nativeFailureOutput = output;
            }
            string failure;
            fallbackReason = config.Bypass || passThrough
                ? null
                : UpscalerBackends.WhyNotOffered(backend)
                  ?? (nativeFailures.TryGetValue(backend, out failure) ? failure : null);
            if (fallbackReason != null)
            {
                runs = UpscalerBackend.Fsr3;
                if (loggedFallback != fallbackReason)
                    Debug.LogWarning(Log.Tag + " " + UpscalerBackends.Name(backend)
                                     + " cannot run, FSR 3 runs instead: " + fallbackReason);
                loggedFallback = fallbackReason;
            }
            if (passThrough) runs = UpscalerBackend.Fsr3;
            return runs;
        }

        // DLSS renders at the size it asks for (UpscalerRig.Setup), which the
        // proxy knows once it has been asked. It is asked here at every output size it
        // does not know yet, and answers on the render thread within a frame or two.
        // The rig waits that long, rather than being built at a size DLSS
        // would have it rebuilt from, and no longer if no answer comes. The
        // texture only carries Unity's device. True while the rig waits.
        private bool WaitForDlssSizes(UpscalerBackend runs, Vector2Int output, Fsr3Upscaler.QualityMode builtWith)
        {
            Vector2Int known;
            if (runs == UpscalerBackend.Dlss && !config.Bypass
                && !DlssBridge.RenderSize(output, DlssBridge.Quality(builtWith), out known))
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
                    return true;
                }
            }
            return false;
        }

        // A rig whose Setup failed is destroyed and the failure counted: tried again in a second while the retries of a rebuild
        // last, else every five seconds until SetupAttempts failures give it up.
        private void OnSetupFailed(UpscalerRig created, bool passThrough)
        {
            Problem = created.Status;
            problemIsCamera = false;
            UnityEngine.Object.DestroyImmediate(created);
            if (attachRetries > 0)
            {
                attachRetries--;
                Debug.LogWarning(Log.Tag + " Upscaler could not be set up yet, trying again in a second: "
                                 + Problem);
                return;
            }
            // Tried again later rather than turning the player's setting off,
            // which would be saved, for what may pass: a scene loading, VRAM.
            int failures = passThrough ? ++captureFailures : ++setupFailures;
            bool givingUp = failures >= SetupAttempts;
            if (loggedSetupProblem != Problem || givingUp)
                Debug.LogWarning(Log.Tag
                                 + (passThrough ? " Frame generation's capture" : " The upscaler")
                                 + " could not be set up"
                                 + (!givingUp
                                     ? ", trying again in five seconds: "
                                     : passThrough
                                         ? ", and is not tried again before the next scene or a switch of the upscaler or frame generation: "
                                         : ", and is not tried again before the next scene, a switch of the upscaler or frame"
                                           + " generation, or a change of its settings: ")
                                 + Problem);
            loggedSetupProblem = Problem;
            // Given up, frame generation's capture may take over at once.
            nextAttachAttempt = givingUp ? 0f : Time.unscaledTime + 5f;
        }

        // Asks for the other mods' resize at the end of this frame, once however often it is asked; nothing where none of
        // them is installed or the add-on goes.
        private void RequestRenderSizeFollowUp(string reason)
        {
            if (destroying) return;
            if (!EveCompatibility.Present && !ScattererCompatibility.Present && !DeferredCompatibility.Present) return;
            followUpReason = reason;
            if (followUpPending) return;
            followUpPending = true;
            host.StartCoroutine(FollowRenderSizeAtEndOfFrame());
        }

        // At the end of the frame: EVE, Scatterer and Deferred sized for the rig now in place, each on its own.
        private IEnumerator FollowRenderSizeAtEndOfFrame()
        {
            yield return new WaitForEndOfFrame();
            followUpPending = false;

            bool attached = rig != null;
            Vector2Int size = attached ? rig.RenderSize : Vector2Int.zero;
            bool sizeChanged = attached != followedAttached || size != followedSize;
            followedAttached = attached;
            followedSize = size;

            // Each mod on its own: one failing does not keep the others' resize.
            string reason = followUpReason;
            ReDefinitionAddon.Guarded("eve-resize", () => EveCompatibility.AfterRenderSizeChange(sizeChanged, reason));
            // Every time, hook or not: godrays Scatterer makes between a detach
            // and the attach after it are sized by the hook against a camera
            // not redirected yet, and only this search finds them.
            ReDefinitionAddon.Guarded("scatterer-resize", () => ScattererCompatibility.AfterRenderSizeChange(reason));
            ReDefinitionAddon.Guarded("deferred-resize", () => DeferredCompatibility.AfterRenderSizeChange(reason));
        }
    }
}
