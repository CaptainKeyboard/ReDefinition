using FidelityFX.FSR3;
using ReDefinition.Proxy;
using ReDefinition.Core;
using ReDefinition.Settings;
using ReDefinition.Upscaler;
using UnityEngine;

namespace ReDefinition.Modules
{
    // The settings' setters: how each change takes effect, on the rig or by building it anew.
    // The settings views and the diagnostics window change them through these.
    internal sealed partial class SettingsController
    {
        // Switched on only with a graphics profile chosen.
        internal void SetEnabled(bool value)
        {
            if (value && !ProfileChosen())
            {
                NeedsProfile();
                return;
            }
            WantEnabled = value;
            rigs.ForgetSetupFailures();
            // Tick takes the rig down (RigLifecycle.WantsRig), for the reason Reattach waits.
            rigs.AttachSoon();
            Debug.Log(Log.Tag + " Upscaler " + (WantEnabled ? "on" : "off"));
        }

        // The upscaler's hotkey: on when it is off, off when it is on.
        internal void ToggleEnabled()
        {
            SetEnabled(!WantEnabled);
        }

        // Said on screen when something asks for the upscaler or frame generation
        // without a graphics profile chosen.
        private static void NeedsProfile()
        {
            ScreenMessages.PostScreenMessage("ReDefinition is off until a graphics profile is chosen in its window,"
                                             + " under Profiles.", 5f);
        }

        // Sharpness takes effect immediately: SetupDispatch reads it every frame,
        // the context does not need rebuilding for it.
        internal void SetSharpness(float value)
        {
            bool wasSharpening = Sharpness > 0f;
            Sharpness = Mathf.Clamp(value, 0f, UpscalerRig.MaximumSharpness);
            if (Rig != null) Rig.Sharpness = Sharpness;
            // 0 switches RCAS off, which takes the other accumulate shader: a new rig.
            if (Rig != null && wasSharpening != (Sharpness > 0f)) rigs.Reattach();
            Debug.Log(Log.Tag + " Sharpness " + Sharpness.ToString("0.00")
                      + (Sharpness > 1f ? "  (beyond FidelityFX' maximum)" : ""));
        }

        // Live: each instrument asks for it as it runs.
        internal void SetDebugMode(bool value)
        {
            DebugMode = value;
            Debug.Log(Log.Tag + " Diagnostic measurements " + (value ? "on" : "off") + ".");
        }

        // Built into the rig: the picture before the upscaler instead of its result.
        internal void SetBypass(bool value)
        {
            Bypass = value;
            rigs.Reattach();
        }

        // Live: the rig runs the motion vector check on fast turns from the next frame.
        internal void SetRecordFastTurns(bool value)
        {
            RecordFastTurns = value;
            if (Rig != null) Rig.RecordFastTurns = RecordFastTurns;
            Debug.Log(Log.Tag + " Motion vector check on fast turns " + (RecordFastTurns ? "on" : "off") + ".");
        }

#if DEVELOPMENT_BUILD
        // Live: the rig draws FSR's debug view pass, which development builds compile.
        internal void SetDebugView(bool value)
        {
            DebugView = value;
            if (Rig != null) Rig.DebugView = DebugView;
            Debug.Log(Log.Tag + " FSR debug view " + (DebugView ? "on" : "off"));
        }
#endif

        // Built into the rig's mipmap bias: a new rig.
        internal void SetMipmapBias(bool value)
        {
            MipmapBias = value;
            rigs.Reattach();
        }

        // Live: the rig sets the LOD bias in QualitySettings anew at once.
        internal void SetCompensateLodBias(bool value)
        {
            CompensateLodBias = value;
            if (Rig != null) { Rig.CompensateLodBias = CompensateLodBias; Rig.RefreshQualityOverrides(); }
            Debug.Log(Log.Tag + " LOD bias compensation " + (CompensateLodBias ? "on" : "off"));
        }

        // Live: the rig sets the MSAA override in QualitySettings anew at once.
        internal void SetDisableMsaa(bool value)
        {
            DisableMsaa = value;
            if (Rig != null) { Rig.DisableMsaa = DisableMsaa; Rig.RefreshQualityOverrides(); }
            Debug.Log(Log.Tag + " MSAA override " + (DisableMsaa ? "on" : "off"));
        }

        // Live: the rig sets the anisotropic filtering override in QualitySettings anew at once.
        internal void SetForceAnisotropic(bool value)
        {
            ForceAnisotropic = value;
            if (Rig != null) { Rig.ForceAnisotropic = ForceAnisotropic; Rig.RefreshQualityOverrides(); }
            Debug.Log(Log.Tag + " Anisotropic override " + (ForceAnisotropic ? "on" : "off"));
        }

        // Built into the FSR context: a new rig.
        internal void SetAutoExposure(bool value)
        {
            AutoExposure = value;
            Debug.Log(Log.Tag + " FSR auto exposure " + (AutoExposure ? "on" : "off"));
            rigs.Reattach();
        }

        // Live: the rig reads it every frame, and TUFX's next blend of its settings
        // puts every effect back on its own layer once it is off.
        internal void SetTufxAfterUpscaling(bool value)
        {
            TufxAfterUpscaling = value;
            if (Rig != null) Rig.TufxAfterUpscaling = TufxAfterUpscaling;
            Debug.Log(Log.Tag + " TUFX effects " + (TufxAfterUpscaling ? "after" : "before") + " FSR");
        }

        // Live: the rig records the masks and hands them to FSR in every frame.
        internal void SetTransparencyMask(bool value)
        {
            TransparencyMask = value;
            if (Rig != null) Rig.TransparencyMask = TransparencyMask;
            Debug.Log(Log.Tag + " Transparency mask " + (TransparencyMask ? "on" : "off"));
        }

        // Live: the rig takes the reactive mask's source in the next frame.
        internal void SetReactiveMask(UpscalerMasks.ReactiveSource value)
        {
            ReactiveMask = value;
            if (Rig != null) Rig.ReactiveMask = ReactiveMask;
            Debug.Log(Log.Tag + " Reactive mask: " + ReactiveLabel(ReactiveMask));
        }

        // The reactive mask's source in the log's words.
        private static string ReactiveLabel(UpscalerMasks.ReactiveSource source)
        {
            switch (source)
            {
                case UpscalerMasks.ReactiveSource.Renderers: return "transparent renderers";
                case UpscalerMasks.ReactiveSource.Automatic: return "FSR's estimate";
                default: return "off";
            }
        }

        // The technique picked by hand: no longer Auto's choice, and Auto is resolved afresh.
        internal void PickBackend(UpscalerBackend value)
        {
            settings.BackendAuto = false;
            resolvedFor = null;
            SetBackend(value);
        }

        // A new rig: the technique decides what it builds.
        internal void SetBackend(UpscalerBackend value)
        {
            if (Backend == value) return;
            Backend = value;
            rigs.ForgetNativeFailures(true);
            Debug.Log(Log.Tag + " Upscaler technique: " + UpscalerBackends.Name(Backend));
            rigs.Reattach();
        }

        // Live: the proxy creates DLSS's feature anew when the preset differs.
        private void SetDlssModel(DlssModel value)
        {
            if (DlssModel == value) return;
            DlssModel = value;
            if (Rig != null) Rig.DlssPreset = DlssModels.Preset(DlssModel);
            Debug.Log(Log.Tag + " DLSS model: " + DlssModels.Name(DlssModel) + ".");
        }

        // NVIDIA's download placed nvngx_dlss.dll. A DLSS failure that stood for its
        // lack stands no more once NGX's search has changed (Dlss::Status). DLSS
        // is tried at once where it is chosen, not only after the next scene change.
        internal void NvidiaFilesPlaced()
        {
            rigs.ForgetNativeFailures(false);
            rigs.RetryIfFallenBack(UpscalerBackend.Dlss);
        }

        // Intel's download placed libxess.dll: as with nvngx_dlss.dll, XeSS is tried at
        // once where it is chosen (XessUpscaler loads it from the folder once it
        // is there).
        internal void IntelFilesPlaced()
        {
            rigs.ForgetNativeFailures(false);
            rigs.RetryIfFallenBack(UpscalerBackend.Xess);
        }

        // Live: the rig reads it every frame.
        internal void SetJitter(bool value)
        {
            Jitter = value;
            if (Rig != null) Rig.EnableJitter = Jitter;
            Debug.Log(Log.Tag + " Jitter " + (Jitter ? "on" : "off"));
        }

        // Live: the rig sweeps the skinned renderers once a second while this is
        // on, and puts back what they had once it is off (SkinnedMotionVectors).
        internal void SetSkinnedMotionVectors(bool value)
        {
            SkinnedMotionVectors = value;
            if (Rig != null) Rig.ForceSkinnedMotionVectors = SkinnedMotionVectors;
            Debug.Log(Log.Tag + " Skinned motion vectors "
                      + (SkinnedMotionVectors ? "forced on" : "as the game set them"));
        }

        // Live: the rig reads it every frame. Needs the dxgi.dll proxy next to
        // KSP_x64.exe with frameGeneration=1 in its ini. The rig supplies depth,
        // motion vectors and the HUD-less image; with the upscaler off it runs for
        // that alone (UpscalerRig.PassThrough), attached by RigLifecycle.Tick.
        // Without the proxy the switch is inert and the diagnostics say so.
        internal void SetFrameGeneration(bool value)
        {
            if (value && !ProfileChosen())
            {
                NeedsProfile();
                return;
            }
            FrameGeneration = value;
            rigs.ForgetSetupFailures();
            rigs.ForgetLoggedSetupProblem();
            // Switched on by the player: a context the proxy could not make gets
            // another try, a standing failure included. What it requires of KSP's
            // V-Sync is held at the end of the frame, and let go once it is off
            // (Requirements.Hold).
            if (FrameGeneration)
            {
                FrameGenerationBridge.RetryContext();
                // Again with each switch on: where the proxy was not there yet when the settings loaded.
                FrameGenerationBridge.SetMultiplier(FrameGenerationMultiplier);
            }
            string switched = FrameGeneration ? "frame generation switched on" : "frame generation switched off";
            BundledSettingsAddon.AtEndOfFrame("frame-generation-requirements", () => Requirements.Enforce(switched));
            if (Rig != null) Rig.FrameGeneration = FrameGeneration;
            else if (FrameGeneration) rigs.AttachSoon();
            // Off at once, rig or not: without a rig nobody dispatches, and a rig on
            // its way down (Reattach, WantsRig) may not dispatch again, while its
            // teardown only switches the proxy off when frame generation is on.
            if (!FrameGeneration) FrameGenerationBridge.SetEnabled(false);
            Debug.Log(Log.Tag + " Frame generation " + (FrameGeneration ? "on" : "off"));
        }

        // Live: the proxy takes the multiplier -- 2x to 6x, or Dynamic where DLSS-G chooses -- at the
        // next Present, within what the technique that runs allows (FrameGenerationBridge.Limits);
        // a step above it is the most it allows.
        internal void SetFrameGenerationMultiplier(string choice)
        {
            if (!FrameGenerationChoice.Valid(choice) || choice == FrameGenerationMultiplier) return;
            FrameGenerationMultiplier = choice;
            FrameGenerationBridge.SetMultiplier(choice);
            Debug.Log(Log.Tag + " Frame generation multiplier " + choice);
        }

        // The mode picked by hand: no longer Auto's choice, and Auto is resolved afresh.
        internal void PickQuality(Fsr3Upscaler.QualityMode mode)
        {
            settings.QualityAuto = false;
            resolvedFor = null;
            SetQuality(mode);
        }

        // A new rig at the new mode; a technique that failed at the old one is tried again.
        internal void SetQuality(Fsr3Upscaler.QualityMode mode)
        {
            if (Quality == mode) return;
            Quality = mode;
            rigs.ForgetNativeFailures(false);
            rigs.Reattach();
        }
    }
}
