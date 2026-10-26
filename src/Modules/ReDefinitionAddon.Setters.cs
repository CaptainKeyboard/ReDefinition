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
        private void SetEnabled(bool value)
        {
            if (value && !ProfileChosen())
            {
                NeedsProfile();
                return;
            }
            wantEnabled = value;
            setupFailures = 0;
            captureFailures = 0;
            // Update takes the rig down (WantsRig), for the reason Reattach waits.
            nextAttachAttempt = 0f;
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
        private void SetSharpness(float value)
        {
            bool wasSharpening = sharpness > 0f;
            sharpness = Mathf.Clamp(value, 0f, UpscalerRig.MaximumSharpness);
            if (rig != null) rig.Sharpness = sharpness;
            // 0 switches RCAS off, which takes the other accumulate shader: a new rig.
            if (rig != null && wasSharpening != (sharpness > 0f)) Reattach();
            Debug.Log(Log.Tag + " Sharpness " + sharpness.ToString("0.00")
                      + (sharpness > 1f ? "  (beyond FidelityFX' maximum)" : ""));
        }

        // Live: each instrument asks for it as it runs.
        private void SetDebugMode(bool value)
        {
            DebugMode = value;
            Debug.Log(Log.Tag + " Diagnostic measurements " + (value ? "on" : "off") + ".");
        }

        // Built into the rig's mipmap bias: a new rig.
        private void SetMipmapBias(bool value)
        {
            mipmapBias = value;
            Reattach();
        }

        // Live: the rig sets the LOD bias in QualitySettings anew at once.
        private void SetCompensateLodBias(bool value)
        {
            compensateLodBias = value;
            if (rig != null) { rig.CompensateLodBias = compensateLodBias; rig.RefreshQualityOverrides(); }
            Debug.Log(Log.Tag + " LOD bias compensation " + (compensateLodBias ? "on" : "off"));
        }

        // Live: the rig sets the MSAA override in QualitySettings anew at once.
        private void SetDisableMsaa(bool value)
        {
            disableMsaa = value;
            if (rig != null) { rig.DisableMsaa = disableMsaa; rig.RefreshQualityOverrides(); }
            Debug.Log(Log.Tag + " MSAA override " + (disableMsaa ? "on" : "off"));
        }

        // Live: the rig sets the anisotropic filtering override in QualitySettings anew at once.
        private void SetForceAnisotropic(bool value)
        {
            forceAnisotropic = value;
            if (rig != null) { rig.ForceAnisotropic = forceAnisotropic; rig.RefreshQualityOverrides(); }
            Debug.Log(Log.Tag + " Anisotropic override " + (forceAnisotropic ? "on" : "off"));
        }

        // Built into the FSR context: a new rig.
        private void SetAutoExposure(bool value)
        {
            autoExposure = value;
            Debug.Log(Log.Tag + " FSR auto exposure " + (autoExposure ? "on" : "off"));
            Reattach();
        }

        // Live: the rig reads it every frame, and TUFX's next blend of its settings
        // puts every effect back on its own layer once it is off.
        private void SetTufxAfterUpscaling(bool value)
        {
            tufxAfterUpscaling = value;
            if (rig != null) rig.TufxAfterUpscaling = tufxAfterUpscaling;
            Debug.Log(Log.Tag + " TUFX effects " + (tufxAfterUpscaling ? "after" : "before") + " FSR");
        }

        // Live: the rig records the masks and hands them to FSR in every frame.
        private void SetTransparencyMask(bool value)
        {
            transparencyMask = value;
            if (rig != null) rig.TransparencyMask = transparencyMask;
            Debug.Log(Log.Tag + " Transparency mask " + (transparencyMask ? "on" : "off"));
        }

        // Live: the rig takes the reactive mask's source in the next frame.
        private void SetReactiveMask(UpscalerMasks.ReactiveSource value)
        {
            reactiveMask = value;
            if (rig != null) rig.ReactiveMask = reactiveMask;
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
        private void SetBackend(UpscalerBackend value)
        {
            if (backend == value) return;
            backend = value;
            ForgetNativeFailures(true);
            Debug.Log(Log.Tag + " Upscaler technique: " + UpscalerBackends.Name(backend));
            Reattach();
        }

        // Live: the proxy creates DLSS's feature anew when the preset differs.
        private void SetDlssModel(DlssModel value)
        {
            if (dlssModel == value) return;
            dlssModel = value;
            if (rig != null) rig.DlssPreset = DlssModels.Preset(dlssModel);
            Debug.Log(Log.Tag + " DLSS model: " + DlssModels.Name(dlssModel) + ".");
        }

        // NVIDIA's download placed nvngx_dlss.dll. A DLSS failure that stood for its
        // lack stands no more once NGX's search has changed (Dlss::Status). DLSS
        // is tried at once where it is chosen, not only after the next scene change.
        internal void NvidiaFilesPlaced()
        {
            ForgetNativeFailures(false);
            RetryIfFallenBack(UpscalerBackend.Dlss);
        }

        // Intel's download placed libxess.dll: as with nvngx_dlss.dll, XeSS is tried at
        // once where it is chosen (XessUpscaler loads it from the folder once it
        // is there).
        internal void IntelFilesPlaced()
        {
            ForgetNativeFailures(false);
            RetryIfFallenBack(UpscalerBackend.Xess);
        }

        // The upscaler chosen is this one, FSR 3 running in its place, and no failure
        // left against it: the rig is built again for it.
        private void RetryIfFallenBack(UpscalerBackend wanted)
        {
            if (backend == wanted && wantEnabled && rig != null && rig.Backend != backend
                && !nativeFailures.ContainsKey(backend))
                Reattach();
        }

        // Live: the rig reads it every frame.
        private void SetJitter(bool value)
        {
            jitter = value;
            if (rig != null) rig.EnableJitter = jitter;
            Debug.Log(Log.Tag + " Jitter " + (jitter ? "on" : "off"));
        }

        // Live: the rig sweeps the skinned renderers once a second while this is
        // on, and puts back what they had once it is off (SkinnedMotionVectors).
        private void SetSkinnedMotionVectors(bool value)
        {
            skinnedMotionVectors = value;
            if (rig != null) rig.ForceSkinnedMotionVectors = skinnedMotionVectors;
            Debug.Log(Log.Tag + " Skinned motion vectors "
                      + (skinnedMotionVectors ? "forced on" : "as the game set them"));
        }

        // Live: the rig reads it every frame. Needs the dxgi.dll proxy next to
        // KSP_x64.exe with frameGeneration=1 in its ini. The rig supplies depth,
        // motion vectors and the HUD-less image; with the upscaler off it runs for
        // that alone (UpscalerRig.PassThrough), attached by Update.
        // Without the proxy the switch is inert and the diagnostics say so.
        private void SetFrameGeneration(bool value)
        {
            if (value && !ProfileChosen())
            {
                NeedsProfile();
                return;
            }
            frameGeneration = value;
            setupFailures = 0;
            captureFailures = 0;
            loggedSetupProblem = null;
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
            if (rig != null) rig.FrameGeneration = frameGeneration;
            else if (frameGeneration) nextAttachAttempt = 0f;
            // Off at once, rig or not: without a rig nobody dispatches, and a rig on
            // its way down (Reattach, WantsRig) may not dispatch again, while its
            // teardown only switches the proxy off when frame generation is on.
            if (!frameGeneration) FrameGenerationBridge.SetEnabled(false);
            Debug.Log(Log.Tag + " Frame generation " + (frameGeneration ? "on" : "off"));
        }

        // Live: the proxy takes the multiplier -- 2x to 6x, or Dynamic where DLSS-G chooses -- at the
        // next Present, within what the technique that runs allows (FrameGenerationBridge.Limits);
        // a step above it is the most it allows.
        private void SetFrameGenerationMultiplier(string choice)
        {
            if (!FrameGenerationChoice.Valid(choice) || choice == frameGenerationMultiplier) return;
            frameGenerationMultiplier = choice;
            FrameGenerationBridge.SetMultiplier(choice);
            Debug.Log(Log.Tag + " Frame generation multiplier " + choice);
        }

        // A new rig at the new mode; a technique that failed at the old one is tried again.
        private void SetQuality(Fsr3Upscaler.QualityMode mode)
        {
            if (quality == mode) return;
            quality = mode;
            ForgetNativeFailures(false);
            Reattach();
        }
    }
}
