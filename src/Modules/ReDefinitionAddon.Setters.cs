using FidelityFX.FSR3;
using ReDefinition.Proxy;
using ReDefinition.Core;
using ReDefinition.Settings;
using ReDefinition.Upscaler;
using UnityEngine;

namespace ReDefinition
{
    // The add-on's setters: how each change to its own settings takes effect, on
    // the rig or by building it anew. The settings views and the diagnostics
    // window change them through these.
    public partial class ReDefinitionAddon
    {
        // Switched on only with a graphics profile chosen.
        internal void SetEnabled(bool value)
        {
            if (value && !ProfileChosen())
            {
                NeedsProfile();
                return;
            }
            wantEnabled = value;
            rigs.ForgetSetupFailures();
            // Tick takes the rig down (RigLifecycle.WantsRig), for the reason Reattach waits.
            rigs.AttachSoon();
            Debug.Log(Log.Tag + " Upscaler " + (wantEnabled ? "on" : "off"));
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
            bool wasSharpening = sharpness > 0f;
            sharpness = Mathf.Clamp(value, 0f, UpscalerRig.MaximumSharpness);
            if (rigs.Rig != null) rigs.Rig.Sharpness = sharpness;
            // 0 switches RCAS off, which takes the other accumulate shader: a new rig.
            if (rigs.Rig != null && wasSharpening != (sharpness > 0f)) rigs.Reattach();
            Debug.Log(Log.Tag + " Sharpness " + sharpness.ToString("0.00")
                      + (sharpness > 1f ? "  (beyond FidelityFX' maximum)" : ""));
        }

        // Live: each instrument asks for it as it runs.
        internal void SetDebugMode(bool value)
        {
            DebugMode = value;
            Debug.Log(Log.Tag + " Diagnostic measurements " + (value ? "on" : "off") + ".");
        }

        // Built into the rig's mipmap bias: a new rig.
        internal void SetMipmapBias(bool value)
        {
            mipmapBias = value;
            rigs.Reattach();
        }

        // Live: the rig sets the LOD bias in QualitySettings anew at once.
        internal void SetCompensateLodBias(bool value)
        {
            compensateLodBias = value;
            if (rigs.Rig != null) { rigs.Rig.CompensateLodBias = compensateLodBias; rigs.Rig.RefreshQualityOverrides(); }
            Debug.Log(Log.Tag + " LOD bias compensation " + (compensateLodBias ? "on" : "off"));
        }

        // Live: the rig sets the MSAA override in QualitySettings anew at once.
        internal void SetDisableMsaa(bool value)
        {
            disableMsaa = value;
            if (rigs.Rig != null) { rigs.Rig.DisableMsaa = disableMsaa; rigs.Rig.RefreshQualityOverrides(); }
            Debug.Log(Log.Tag + " MSAA override " + (disableMsaa ? "on" : "off"));
        }

        // Live: the rig sets the anisotropic filtering override in QualitySettings anew at once.
        internal void SetForceAnisotropic(bool value)
        {
            forceAnisotropic = value;
            if (rigs.Rig != null) { rigs.Rig.ForceAnisotropic = forceAnisotropic; rigs.Rig.RefreshQualityOverrides(); }
            Debug.Log(Log.Tag + " Anisotropic override " + (forceAnisotropic ? "on" : "off"));
        }

        // Built into the FSR context: a new rig.
        internal void SetAutoExposure(bool value)
        {
            autoExposure = value;
            Debug.Log(Log.Tag + " FSR auto exposure " + (autoExposure ? "on" : "off"));
            rigs.Reattach();
        }

        // Live: the rig reads it every frame, and TUFX's next blend of its settings
        // puts every effect back on its own layer once it is off.
        internal void SetTufxAfterUpscaling(bool value)
        {
            tufxAfterUpscaling = value;
            if (rigs.Rig != null) rigs.Rig.TufxAfterUpscaling = tufxAfterUpscaling;
            Debug.Log(Log.Tag + " TUFX effects " + (tufxAfterUpscaling ? "after" : "before") + " FSR");
        }

        // Live: the rig records the masks and hands them to FSR in every frame.
        internal void SetTransparencyMask(bool value)
        {
            transparencyMask = value;
            if (rigs.Rig != null) rigs.Rig.TransparencyMask = transparencyMask;
            Debug.Log(Log.Tag + " Transparency mask " + (transparencyMask ? "on" : "off"));
        }

        // Live: the rig takes the reactive mask's source in the next frame.
        internal void SetReactiveMask(UpscalerMasks.ReactiveSource value)
        {
            reactiveMask = value;
            if (rigs.Rig != null) rigs.Rig.ReactiveMask = reactiveMask;
            Debug.Log(Log.Tag + " Reactive mask: " + ReactiveLabel(reactiveMask));
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

        // A new rig: the technique decides what it builds.
        internal void SetBackend(UpscalerBackend value)
        {
            if (backend == value) return;
            backend = value;
            rigs.ForgetNativeFailures(true);
            Debug.Log(Log.Tag + " Upscaler technique: " + UpscalerBackends.Name(backend));
            rigs.Reattach();
        }

        // Live: the proxy creates DLSS's feature anew when the preset differs.
        private void SetDlssModel(DlssModel value)
        {
            if (dlssModel == value) return;
            dlssModel = value;
            if (rigs.Rig != null) rigs.Rig.DlssPreset = DlssModels.Preset(dlssModel);
            Debug.Log(Log.Tag + " DLSS model: " + DlssModels.Name(dlssModel) + ".");
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
            jitter = value;
            if (rigs.Rig != null) rigs.Rig.EnableJitter = jitter;
            Debug.Log(Log.Tag + " Jitter " + (jitter ? "on" : "off"));
        }

        // Live: the rig sweeps the skinned renderers once a second while this is
        // on, and puts back what they had once it is off (SkinnedMotionVectors).
        internal void SetSkinnedMotionVectors(bool value)
        {
            skinnedMotionVectors = value;
            if (rigs.Rig != null) rigs.Rig.ForceSkinnedMotionVectors = skinnedMotionVectors;
            Debug.Log(Log.Tag + " Skinned motion vectors "
                      + (skinnedMotionVectors ? "forced on" : "as the game set them"));
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
            frameGeneration = value;
            rigs.ForgetSetupFailures();
            rigs.ForgetLoggedSetupProblem();
            // Switched on by the player: a context the proxy could not make gets
            // another try, a standing failure included. What it requires of KSP's
            // V-Sync is held at the end of the frame, and let go once it is off
            // (Requirements.Hold).
            if (frameGeneration)
            {
                FrameGenerationBridge.RetryContext();
                // Again with each switch on: where the proxy was not there yet when the settings loaded.
                FrameGenerationBridge.SetMultiplier(frameGenerationMultiplier);
            }
            string switched = frameGeneration ? "frame generation switched on" : "frame generation switched off";
            BundledSettingsAddon.AtEndOfFrame("frame-generation-requirements", () => Requirements.Enforce(switched));
            if (rigs.Rig != null) rigs.Rig.FrameGeneration = frameGeneration;
            else if (frameGeneration) rigs.AttachSoon();
            // Off at once, rig or not: without a rig nobody dispatches, and a rig on
            // its way down (Reattach, WantsRig) may not dispatch again, while its
            // teardown only switches the proxy off when frame generation is on.
            if (!frameGeneration) FrameGenerationBridge.SetEnabled(false);
            Debug.Log(Log.Tag + " Frame generation " + (frameGeneration ? "on" : "off"));
        }

        // Live: the proxy takes the multiplier -- 2x to 6x, or Dynamic where DLSS-G chooses -- at the
        // next Present, within what the technique that runs allows (FrameGenerationBridge.Limits);
        // a step above it is the most it allows.
        internal void SetFrameGenerationMultiplier(string choice)
        {
            if (!FrameGenerationChoice.Valid(choice) || choice == frameGenerationMultiplier) return;
            frameGenerationMultiplier = choice;
            FrameGenerationBridge.SetMultiplier(choice);
            Debug.Log(Log.Tag + " Frame generation multiplier " + choice);
        }

        // A new rig at the new mode; a technique that failed at the old one is tried again.
        internal void SetQuality(Fsr3Upscaler.QualityMode mode)
        {
            if (quality == mode) return;
            quality = mode;
            rigs.ForgetNativeFailures(false);
            rigs.Reattach();
        }
    }
}
