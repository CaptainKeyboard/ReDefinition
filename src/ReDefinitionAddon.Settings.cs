using System;
using FidelityFX.FSR3;
using ReDefinition.Bridges;
using ReDefinition.Core;
using ReDefinition.Settings;
using ReDefinition.Upscaler;
using UnityEngine;

namespace ReDefinition
{
    // The add-on's part for its settings: what they are, how a change takes effect,
    // and how they are loaded, saved and applied from a settings view.
    public partial class ReDefinitionAddon
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

        // The settings window's own: it holds the flight while it is open
        // (WindowPause). Not a graphics setting, so nothing is rebuilt for it.
        private bool pauseWhileOpen;

        // KSP's Settings buttons open ReDefinition's window (OwnSettings).
        private bool replaceKspSettings;

        // The diagnostic measurements (OwnSettings.DebugMode), read by the instruments
        // wherever they run.
        internal static bool DebugMode { get; private set; }
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
        // FSR 3 or DLSS (DlssBridge).
        private UpscalerBackend backend = UpscalerBackend.Fsr3;
        // DLSS's model in every mode (DlssModel).
        private DlssModel dlssModel = DlssModel.Default;

        private bool skinnedMotionVectors = true;

        private readonly OwnSettings settings = new OwnSettings();
        // The raytracing (src/Raytracing/Ksp/RtKsp.cs), which the add-on keeps on the
        // scene camera.
        private readonly ReDefinition.Raytracing.RtKsp raytracing = new ReDefinition.Raytracing.RtKsp();
        private int raytracingBounces = 2;
        private string savedSnapshot;
        private float nextSettingsCheck;
        private bool saveFailureLogged;

        private void LoadSettings()
        {
            OwnSettings loaded = OwnSettings.Load();
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
            pauseWhileOpen = loaded.PauseWhileOpen;
            DebugMode = loaded.DebugMode;
            replaceKspSettings = loaded.ReplaceKspSettings;
            forceAnisotropic = loaded.ForceAnisotropic;
            tufxAfterUpscaling = loaded.TufxAfterUpscaling;
            transparencyMask = loaded.TransparencyMask;
            reactiveMask = loaded.ReactiveMask;
            jitter = loaded.Jitter;
            skinnedMotionVectors = loaded.SkinnedMotionVectors;
            frameGeneration = loaded.FrameGeneration;
            raytracing.Apply(loaded.Raytracing, loaded.RaytracingBounces);
            raytracingBounces = loaded.RaytracingBounces;
            backend = loaded.Backend;
            dlssModel = loaded.DlssModel;
            // The hotkeys and the settings read live are read from the settings object
            // itself (HandleHotkeys and the views), so the file's values go there.
            CopyLiveSettings(loaded);

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

        // The hotkeys and the settings read live -- the flight interface's, the
        // raytraced moonlight and reflections -- as the player set them, into the settings
        // object the add-on reads and saves.
        private void CopyLiveSettings(OwnSettings from)
        {
            settings.CopyLive(from);
        }

        // What a view changed of those (CopyLiveSettings), and nothing else: KSP's
        // settings dialog holds a copy from when it opened, and its Apply must not bring
        // back a value this window set meanwhile.
        private void ChangeLiveSettings(OwnSettings before, OwnSettings after)
        {
            settings.ChangeLive(before, after);
        }

        // Where the navball stands; NavballDrag moves it there and writes it as
        // the navball is dragged. Saved at the next check, as every change is. On
        // a screen of another size than the one it was put on, the centre, and
        // that is kept.
        internal float NavballOffset
        {
            get
            {
                string screen = ScreenSize;
                if (settings.NavballScreen != screen)
                {
                    settings.NavballOffset = 0f;
                    settings.NavballScreen = screen;
                }
                return settings.NavballOffset;
            }
            set
            {
                settings.NavballOffset = value;
                settings.NavballScreen = ScreenSize;
            }
        }

        // Asked for with the navball's place a few times a second: made anew only
        // when the screen's size changes.
        private static int screenWidth, screenHeight;
        private static string screenSize = "";

        private static string ScreenSize
        {
            get
            {
                if (Screen.width != screenWidth || Screen.height != screenHeight)
                {
                    screenWidth = Screen.width;
                    screenHeight = Screen.height;
                    screenSize = screenWidth + "x" + screenHeight;
                }
                return screenSize;
            }
        }

        // The settings as they stand, for what reads them every frame without a
        // copy -- the UI scaling. Not to be written through.
        internal OwnSettings Live
        {
            get { return settings; }
        }

        private OwnSettings Collect()
        {
            settings.Enabled = wantEnabled;
            settings.Quality = quality;
            settings.Sharpness = sharpness;
            settings.AutoExposure = autoExposure;
            settings.MipmapBias = mipmapBias;
            settings.CompensateLodBias = compensateLodBias;
            settings.DisableMsaa = disableMsaa;
            settings.PauseWhileOpen = pauseWhileOpen;
            settings.DebugMode = DebugMode;
            settings.ReplaceKspSettings = replaceKspSettings;
            settings.ForceAnisotropic = forceAnisotropic;
            settings.TufxAfterUpscaling = tufxAfterUpscaling;
            settings.TransparencyMask = transparencyMask;
            settings.ReactiveMask = reactiveMask;
            settings.Jitter = jitter;
            settings.SkinnedMotionVectors = skinnedMotionVectors;
            settings.FrameGeneration = frameGeneration;
            settings.Raytracing = raytracing.Mode;
            settings.RaytracingBounces = raytracingBounces;
            settings.Backend = backend;
            settings.DlssModel = dlssModel;
            return settings;
        }

        // Compared with what was saved last, so no setter has to mark the settings
        // dirty.
        private void SaveSettingsIfChanged(bool now = false)
        {
            // The scene probe switches settings for its measurements: the player's stay
            // as they were on disk, however the probe ends.
            if (Probe.SceneProbe.Requested) return;
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
                    Debug.LogWarning(Log.Tag + " Settings not saved, retrying: " + e.Message);
                saveFailureLogged = true;
            }
        }

        internal bool ReplaceKspSettings
        {
            get { return replaceKspSettings; }
        }

        // Before KSP quits for a restart: the settings on disk now, not at the
        // next check.
        internal void SaveSettingsNow()
        {
            SaveSettingsIfChanged(true);
        }

        // The first start's answer to take control: KSP's Settings buttons open
        // ReDefinition's window from the next menu on. Saved at once, since the
        // main menu is loaded again right after.
        internal void TakeKspSettings()
        {
            if (replaceKspSettings) return;
            replaceKspSettings = true;
            Debug.Log(Log.Tag + " KSP's Settings buttons open ReDefinition's window (Replace original settings).");
            SaveSettingsIfChanged(true);
        }

        internal bool PauseWhileOpen
        {
            get { return pauseWhileOpen; }
            set { pauseWhileOpen = value; }
        }

        // The settings as they are now, as a copy: KSP's settings dialog edits
        // it and hands it back through Apply only on Apply or Accept.
        internal OwnSettings Current()
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
        internal void Apply(OwnSettings before, OwnSettings after)
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

        private void ApplyChanges(OwnSettings before, OwnSettings after)
        {
            if (before.Enabled != after.Enabled && !after.Enabled) SetEnabled(false);
            if (before.Quality != after.Quality) SetQuality(after.Quality);
            if (before.Sharpness != after.Sharpness) SetSharpness(after.Sharpness);
            if (before.Backend != after.Backend) SetBackend(after.Backend);
            if (before.DlssModel != after.DlssModel) SetDlssModel(after.DlssModel);
            if (before.AutoExposure != after.AutoExposure) SetAutoExposure(after.AutoExposure);
            if (before.FrameGeneration != after.FrameGeneration) SetFrameGeneration(after.FrameGeneration);
            if (before.Raytracing != after.Raytracing || before.RaytracingBounces != after.RaytracingBounces)
            {
                raytracingBounces = after.RaytracingBounces;
                raytracing.Apply(after.Raytracing, after.RaytracingBounces);
                Debug.Log(Log.Tag + " Raytracing set to " + after.Raytracing + ", " + after.RaytracingBounces + " bounce(s).");
            }
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
            if (before.ReplaceKspSettings != after.ReplaceKspSettings)
            {
                replaceKspSettings = after.ReplaceKspSettings;
                Debug.Log(Log.Tag + " KSP's Settings buttons open " + (replaceKspSettings ? "ReDefinition's window" : "KSP's"
                          + " own screens") + " from the next time a menu is built.");
            }
            ChangeLiveSettings(before, after);
        }
    }
}
