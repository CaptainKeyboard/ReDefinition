using System;
using FidelityFX.FSR3;
using ReDefinition.Framework;
using UnityEngine;

namespace ReDefinition
{
    // The add-on's part for its settings: what they are, how a change takes effect,
    // and how they are loaded, saved and applied from a settings view.
    public partial class UpscalerAddon
    {
        // NativeAA: on the development machine, the 3D scene rendered with 89 %
        // fewer pixels made the frame longer -- the pixel shading costs under 0.2 ms
        // of a 10.3 ms frame, while the GPU at 90-97 % and the CPU at 30-40 % work on
        // what does not scale with output resolution: shadow cascades, cloud
        // volumes, reflection probes (docs/development/upscaler.md). At NativeAA the
        // upscaler is the temporal antialiasing.
        private Fsr3Upscaler.QualityMode quality = Fsr3Upscaler.QualityMode.NativeAA;
        // RCAS maps sharpness to 2^(-2*(1-s)): 1.0 is the full effect, 0.4 about
        // 0.44 of it.
        private float sharpness = 1.0f;
        private bool mipmapBias = true;
        private bool bypass;
        private bool jitter = true;
        private bool compensateLodBias = true;
        private bool disableMsaa = true;
        private bool forceAnisotropic = true;
        // FSR's own exposure for its internal tonemapping, as AMD recommends
        // (UpscalerRig.AutoExposure).
        private bool autoExposure = true;
        // TUFX's bloom, tonemapping and the rest of its image effects after the
        // upscaler rather than before it (TufxPostProcessing); a switch to compare the two.
        private bool tufxAfterUpscaling = true;
        // The masks FSR 3 takes beside colour, depth and motion vectors
        // (UpscalerMasks); off by default.
        private bool transparencyMask;
        private UpscalerMasks.ReactiveSource reactiveMask = UpscalerMasks.ReactiveSource.Off;
#if DEVELOPMENT_BUILD
        private bool debugView;
#endif
        // Not saved, off at every start: the motion vector check on fast turns
        // (UpscalerRig.RecordFastTurns).
        private bool recordFastTurns;
        private bool frameGeneration;
        // FSR 3 or DLSS (DlssBridge), and DLSS's preset.
        private UpscalerBackend backend = UpscalerBackend.Fsr3;
        private DlssPreset dlssPreset = DlssPreset.Default;

        private bool skinnedMotionVectors = true;

        private readonly UpscalerSettings settings = new UpscalerSettings();
        private string savedSnapshot;
        private float nextSettingsCheck;
        private bool saveFailureLogged;

        private void LoadSettings()
        {
            UpscalerSettings loaded = UpscalerSettings.Load();
            string asSaved = loaded.Snapshot();
            // Nothing runs without a graphics profile chosen, whatever the file
            // says; what is corrected here is saved at the next check.
            if (!ProfileChosen())
            {
                loaded.Enabled = false;
                loaded.FrameGeneration = false;
            }

            wantEnabled = loaded.Enabled;
            quality = loaded.Quality;
            sharpness = loaded.Sharpness;
            autoExposure = loaded.AutoExposure;
            mipmapBias = loaded.MipmapBias;
            compensateLodBias = loaded.CompensateLodBias;
            disableMsaa = loaded.DisableMsaa;
            forceAnisotropic = loaded.ForceAnisotropic;
            tufxAfterUpscaling = loaded.TufxAfterUpscaling;
            transparencyMask = loaded.TransparencyMask;
            reactiveMask = loaded.ReactiveMask;
            jitter = loaded.Jitter;
            skinnedMotionVectors = loaded.SkinnedMotionVectors;
            frameGeneration = loaded.FrameGeneration;
            backend = loaded.Backend;
            dlssPreset = loaded.DlssPreset;

            savedSnapshot = asSaved;
        }

        // Whether a graphics profile is chosen: only then is ReDefinition active.
        private static bool ProfileChosen()
        {
            return BundledSettings.ProfileChosen;
        }

        // Without a graphics profile chosen ReDefinition is not active: the upscaler
        // and frame generation go off whichever way the profile went -- the reset,
        // Restore, the bundling switched off. Also for a view about to read the
        // settings back, which must not see them still on.
        internal void EnforceProfile()
        {
            if (ProfileChosen()) return;
            if (frameGeneration) SetFrameGeneration(false);
            if (wantEnabled) SetEnabled(false);
        }

        private UpscalerSettings Collect()
        {
            settings.Enabled = wantEnabled;
            settings.Quality = quality;
            settings.Sharpness = sharpness;
            settings.AutoExposure = autoExposure;
            settings.MipmapBias = mipmapBias;
            settings.CompensateLodBias = compensateLodBias;
            settings.DisableMsaa = disableMsaa;
            settings.ForceAnisotropic = forceAnisotropic;
            settings.TufxAfterUpscaling = tufxAfterUpscaling;
            settings.TransparencyMask = transparencyMask;
            settings.ReactiveMask = reactiveMask;
            settings.Jitter = jitter;
            settings.SkinnedMotionVectors = skinnedMotionVectors;
            settings.FrameGeneration = frameGeneration;
            settings.Backend = backend;
            settings.DlssPreset = dlssPreset;
            return settings;
        }

        // Compared with what was saved last, so no setter has to mark the settings
        // dirty.
        private void SaveSettingsIfChanged(bool now = false)
        {
            if (!now && Time.unscaledTime < nextSettingsCheck) return;
            nextSettingsCheck = Time.unscaledTime + 2f;

            string snapshot = Collect().Snapshot();
            if (snapshot == savedSnapshot) return;

            // Remembered only once it is on disk, so a failed write is tried
            // again at the next check instead of being taken for done.
            try
            {
                settings.Save();
                savedSnapshot = snapshot;
                saveFailureLogged = false;
            }
            catch (System.Exception e)
            {
                if (!saveFailureLogged)
                    Debug.LogWarning(UpscalerProbe.Tag + " Settings not saved, retrying: " + e.Message);
                saveFailureLogged = true;
            }
        }

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
            Debug.Log(UpscalerProbe.Tag + " Upscaler " + (wantEnabled ? "on" : "off"));
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
        // Up to 2: values above FidelityFX's range of 1 stay numerically sound;
        // above about 1.2 artefacts appear.
        internal const float MaximumSharpness = 2f;

        private void SetSharpness(float value)
        {
            bool wasSharpening = sharpness > 0f;
            sharpness = Mathf.Clamp(value, 0f, MaximumSharpness);
            if (rig != null) rig.Sharpness = sharpness;
            // 0 switches RCAS off, which takes the other accumulate shader: a new rig.
            if (rig != null && wasSharpening != (sharpness > 0f)) Reattach();
            Debug.Log(UpscalerProbe.Tag + " Sharpness " + sharpness.ToString("0.00")
                      + (sharpness > 1f ? "  (beyond FidelityFX' maximum)" : ""));
        }

        private void SetMipmapBias(bool value)
        {
            mipmapBias = value;
            Reattach();
        }

        private void SetCompensateLodBias(bool value)
        {
            compensateLodBias = value;
            if (rig != null) { rig.CompensateLodBias = compensateLodBias; rig.RefreshQualityOverrides(); }
            Debug.Log(UpscalerProbe.Tag + " LOD bias compensation " + (compensateLodBias ? "on" : "off"));
        }

        private void SetDisableMsaa(bool value)
        {
            disableMsaa = value;
            if (rig != null) { rig.DisableMsaa = disableMsaa; rig.RefreshQualityOverrides(); }
            Debug.Log(UpscalerProbe.Tag + " MSAA override " + (disableMsaa ? "on" : "off"));
        }

        private void SetForceAnisotropic(bool value)
        {
            forceAnisotropic = value;
            if (rig != null) { rig.ForceAnisotropic = forceAnisotropic; rig.RefreshQualityOverrides(); }
            Debug.Log(UpscalerProbe.Tag + " Anisotropic override " + (forceAnisotropic ? "on" : "off"));
        }

        // Built into the FSR context: a new rig.
        private void SetAutoExposure(bool value)
        {
            autoExposure = value;
            Debug.Log(UpscalerProbe.Tag + " FSR auto exposure " + (autoExposure ? "on" : "off"));
            Reattach();
        }

        // Live: the rig reads it every frame, and TUFX's next blend of its settings
        // puts every effect back on its own layer once it is off.
        private void SetTufxAfterUpscaling(bool value)
        {
            tufxAfterUpscaling = value;
            if (rig != null) rig.TufxAfterUpscaling = tufxAfterUpscaling;
            Debug.Log(UpscalerProbe.Tag + " TUFX effects " + (tufxAfterUpscaling ? "after" : "before") + " FSR");
        }

        // Live: the rig records the masks and hands them to FSR in every frame.
        private void SetTransparencyMask(bool value)
        {
            transparencyMask = value;
            if (rig != null) rig.TransparencyMask = transparencyMask;
            Debug.Log(UpscalerProbe.Tag + " Transparency mask " + (transparencyMask ? "on" : "off"));
        }

        private void SetReactiveMask(UpscalerMasks.ReactiveSource value)
        {
            reactiveMask = value;
            if (rig != null) rig.ReactiveMask = reactiveMask;
            Debug.Log(UpscalerProbe.Tag + " Reactive mask: " + ReactiveLabel(reactiveMask));
        }

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
            Debug.Log(UpscalerProbe.Tag + " Upscaler technique: " + UpscalerBackends.Name(backend));
            Reattach();
        }

        // Live: the proxy creates DLSS's feature anew when the preset differs.
        private void SetDlssPreset(DlssPreset value)
        {
            dlssPreset = value;
            if (rig != null) rig.DlssPreset = dlssPreset;
            Debug.Log(UpscalerProbe.Tag + " DLSS preset " + dlssPreset);
            // A preset the DLSS library lacks may be what made FSR 3 take over:
            // forgotten in any case, and tried at once where DLSS is to run now.
            if (!stableFailures.Contains(UpscalerBackend.Dlss) && nativeFailures.Remove(UpscalerBackend.Dlss))
                RetryDlssIfFallenBack();
        }

        // NVIDIA's download placed nvngx_dlss.dll: a DLSS failure that stood for its
        // lack stands no more once NGX's search has changed (Dlss::Status), and DLSS
        // is tried at once where it is chosen, not only after the next scene change.
        internal void NvidiaFilesPlaced()
        {
            ForgetNativeFailures(false);
            RetryDlssIfFallenBack();
        }

        // DLSS chosen, FSR 3 running in its place, and no failure left against DLSS:
        // the rig is built again for it.
        private void RetryDlssIfFallenBack()
        {
            if (backend == UpscalerBackend.Dlss && wantEnabled && rig != null && rig.Backend != backend
                && !nativeFailures.ContainsKey(backend))
                Reattach();
        }

        // Takes effect immediately, no rebuild needed -- Update reads the value
        // every frame.
        private void SetJitter(bool value)
        {
            jitter = value;
            if (rig != null) rig.EnableJitter = jitter;
            Debug.Log(UpscalerProbe.Tag + " Jitter " + (jitter ? "on" : "off"));
        }

        // Live: the rig sweeps the skinned renderers once a second while this is
        // on, and puts back what they had once it is off (SkinnedMotionVectors).
        private void SetSkinnedMotionVectors(bool value)
        {
            skinnedMotionVectors = value;
            if (rig != null) rig.ForceSkinnedMotionVectors = skinnedMotionVectors;
            Debug.Log(UpscalerProbe.Tag + " Skinned motion vectors "
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
            // another try, a standing failure included; and what the mods require
            // of KSP's V-Sync with it is put right at the end of the frame, as at a
            // scene change.
            if (frameGeneration)
            {
                FrameGenerationBridge.RetryContext();
                BundledSettingsAddon.AtEndOfFrame("frame-generation-requirements",
                                                  () => Requirements.Enforce("frame generation switched on"));
            }
            if (rig != null) rig.FrameGeneration = frameGeneration;
            else if (frameGeneration) nextAttachAttempt = 0f;
            // Off at once, rig or not: without a rig nobody dispatches, and a rig on
            // its way down (Reattach, WantsRig) may not dispatch again, while its
            // teardown only switches the proxy off when frame generation is on.
            if (!frameGeneration) FrameGenerationBridge.SetEnabled(false);
            Debug.Log(UpscalerProbe.Tag + " Frame generation " + (frameGeneration ? "on" : "off"));
        }

        // The settings as they are now, as a copy: KSP's settings dialog edits
        // it and hands it back through Apply only on Apply or Accept.
        internal UpscalerSettings Current()
        {
            return Collect().Clone();
        }

        // What a settings view changed between the copy it was given and the
        // one it hands back, through the same setters the diagnostics window uses,
        // so the two agree about what a change does. Only the differences are
        // applied: what the diagnostics window changed while the dialog was open
        // stays as that window left it.
        //
        // Off first and on last; however many of the changes need a new rig, it is
        // built once, before Apply returns: the settings views read back what came
        // of it -- a rig that cannot start switches the upscaler off. They are
        // uGUI dialogs, not the IMGUI window Reattach waits for.
        internal void Apply(UpscalerSettings before, UpscalerSettings after)
        {
            if (before == null || after == null) return;

            EnforceProfile();
            try
            {
                ApplyChanges(before, after);
            }
            finally
            {
                // Also after a setter threw: what changed before it has its rig.
                if (reattachPending) RebuildRig();
            }

            // Now rather than at the next throttled check: KSP saves its own
            // settings straight after this section's ApplySettings returns
            // (MiniSettings.ApplySettings), and a game closed right after
            // Accept keeps ReDefinition's settings too.
            SaveSettingsIfChanged(true);
        }

        private void ApplyChanges(UpscalerSettings before, UpscalerSettings after)
        {
            if (before.Enabled != after.Enabled && !after.Enabled) SetEnabled(false);
            if (before.Quality != after.Quality) SetQuality(after.Quality);
            if (before.Sharpness != after.Sharpness) SetSharpness(after.Sharpness);
            if (before.Backend != after.Backend) SetBackend(after.Backend);
            if (before.DlssPreset != after.DlssPreset) SetDlssPreset(after.DlssPreset);
            if (before.AutoExposure != after.AutoExposure) SetAutoExposure(after.AutoExposure);
            if (before.FrameGeneration != after.FrameGeneration) SetFrameGeneration(after.FrameGeneration);
            if (before.MipmapBias != after.MipmapBias) SetMipmapBias(after.MipmapBias);
            if (before.CompensateLodBias != after.CompensateLodBias) SetCompensateLodBias(after.CompensateLodBias);
            if (before.DisableMsaa != after.DisableMsaa) SetDisableMsaa(after.DisableMsaa);
            if (before.ForceAnisotropic != after.ForceAnisotropic) SetForceAnisotropic(after.ForceAnisotropic);
            if (before.TufxAfterUpscaling != after.TufxAfterUpscaling) SetTufxAfterUpscaling(after.TufxAfterUpscaling);
            if (before.TransparencyMask != after.TransparencyMask) SetTransparencyMask(after.TransparencyMask);
            if (before.ReactiveMask != after.ReactiveMask) SetReactiveMask(after.ReactiveMask);
            if (before.Jitter != after.Jitter) SetJitter(after.Jitter);
            if (before.SkinnedMotionVectors != after.SkinnedMotionVectors)
                SetSkinnedMotionVectors(after.SkinnedMotionVectors);
            if (before.Enabled != after.Enabled && after.Enabled) SetEnabled(true);
        }

        private static Fsr3Upscaler.QualityMode Step(Fsr3Upscaler.QualityMode mode, int direction)
        {
            int value = (int)mode + direction;
            if (value < (int)Fsr3Upscaler.QualityMode.NativeAA) value = (int)Fsr3Upscaler.QualityMode.NativeAA;
            if (value > (int)Fsr3Upscaler.QualityMode.UltraPerformance) value = (int)Fsr3Upscaler.QualityMode.UltraPerformance;
            return (Fsr3Upscaler.QualityMode)value;
        }

        private void SetQuality(Fsr3Upscaler.QualityMode mode)
        {
            if (quality == mode) return;
            quality = mode;
            ForgetNativeFailures(false);
            Reattach();
        }
    }
}
