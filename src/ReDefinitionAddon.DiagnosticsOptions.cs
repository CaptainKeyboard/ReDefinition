using System;
using System.Collections.Generic;
using ReDefinition.Bridges;
using ReDefinition.Core;
using ReDefinition.Upscaler;
using ReDefinition.Window;
using UnityEngine;

namespace ReDefinition
{
    // The diagnostics window's options as data (DebugOption): what each is, where it is
    // shown, how it is read and set, where it does not apply and whether it is saved.
    // The setters are the ones the settings views use, so a click here does what it
    // always did, log line included.
    public partial class ReDefinitionAddon
    {
        // Every option of the window, made on its first draw, and the options pinned to its foot.
        private DebugOptionSet debugOptions;
        private readonly DebugPins pins = new DebugPins();
        // Why a row is greyed out, as the last layout pass found it: the rows must stay the
        // ones that pass counted, whatever a click changes meanwhile.
        private readonly Dictionary<string, string> optionReasons = new Dictionary<string, string>();
        private int changedCount;

        // Why a row is greyed out: no graphics profile chosen, or a technique that takes no masks running.
        private const string NeedsProfileText = "needs a graphics profile (ReDefinition's window, Profiles)";
        private const string NeedsMasksText = "needs FSR 3 or AMD";

        // Makes the options on the window's first draw, with the pins, the page and the place it had last session.
        private void EnsureOptions()
        {
            if (debugOptions != null) return;
            debugOptions = BuildDebugOptions();
            pins.Load(settings.DiagnosticsPins, debugOptions);
            // The last page, except Previews: it reads the image back from the GPU on every
            // frame it is shown, and a window that opens on it costs frame rate unasked.
            category = settings.DiagnosticsPage == DebugCategory.Previews ? DebugCategory.Overview : settings.DiagnosticsPage;
            if (!float.IsNaN(settings.DiagnosticsX) && !float.IsInfinity(settings.DiagnosticsX)) window.x = settings.DiagnosticsX;
            if (!float.IsNaN(settings.DiagnosticsY) && !float.IsInfinity(settings.DiagnosticsY)) window.y = settings.DiagnosticsY;
        }

        // At the start of each layout pass.
        private void RefreshOptionStates()
        {
            changedCount = 0;
            RefreshOfferedBackends();
            foreach (DebugOption option in debugOptions.All)
            {
                optionReasons[option.Id] = option.Unavailable();
                if (option.Changed) changedCount++;
            }
        }

        // The technique the rig runs, the chosen one where no rig runs.
        private UpscalerBackend RunningBackend
        {
            get { return rig != null ? rig.Backend : backend; }
        }

        // FSR 3 and AMD's upscaler take the masks (UpscalerRig.Native, the AMD packet); DLSS and
        // XeSS none.
        private bool TakesMasks
        {
            get
            {
                UpscalerBackend running = RunningBackend;
                return running == UpscalerBackend.Fsr3 || running == UpscalerBackend.Amd;
            }
        }

        // An on/off option.
        private static DebugOption Switch(string id, DebugCategory category, string group, string label,
                                          DebugPersistence persistence, bool defaultOn, Func<bool> get, Action<bool> set)
        {
            return new DebugOption(id, category, label, DebugKind.Switch, persistence)
            {
                Group = group,
                Default = defaultOn ? 1 : 0,
                Get = () => get() ? 1 : 0,
                Set = value => set(value != 0),
            };
        }

        // An option of several values, the index of the chosen one its value.
        private static DebugOption Choice(string id, DebugCategory category, string group, string label,
                                          DebugPersistence persistence, string[] choices, int defaultChoice,
                                          Func<int> get, Action<int> set)
        {
            return new DebugOption(id, category, label, DebugKind.Choice, persistence)
            {
                Group = group,
                Choices = choices,
                Default = defaultChoice,
                Get = get,
                Set = set,
            };
        }

        // A button that runs an action; nothing to save.
        private static DebugOption Button(string id, DebugCategory category, string group, string label, string text, Action run)
        {
            return new DebugOption(id, category, label, DebugKind.Action, DebugPersistence.SessionOnly)
            {
                Group = group,
                ActionText = text,
                Run = run,
            };
        }

        // Every option of every page: the switches, choices and buttons, each with its setter, default, saving and the
        // reason it may be greyed out.
        private DebugOptionSet BuildDebugOptions()
        {
            // The defaults the code starts from: the settings' own for what is saved.
            OwnSettings saved = new OwnSettings();
            const DebugPersistence Saved = DebugPersistence.Saved;
            const DebugPersistence Session = DebugPersistence.SessionOnly;
            DebugOptionSet set = new DebugOptionSet();

            // Overview: the player's two main switches. Not debug switches, so neither is reset.
            DebugOption upscaler = Switch("upscaler", DebugCategory.Overview, "", "Upscaler", Saved, false,
                () => wantEnabled, value => SetEnabled(value));
            upscaler.Resettable = false;
            upscaler.Tooltip = "Turn Upscaler On Or Off";
            upscaler.Keywords = new[] { "enable", "disable", "fsr", "dlss", "xess" };
            // Switching on needs a graphics profile (SetEnabled).
            upscaler.Blocked = () => !wantEnabled && !ProfileChosen() ? NeedsProfileText : null;
            set.Add(upscaler);

            DebugOption generation = Switch("frameGeneration", DebugCategory.Overview, "", "Frame generation", Saved, false,
                () => frameGeneration, value => SetFrameGeneration(value));
            generation.Also = DebugCategory.FrameGeneration;
            generation.Resettable = false;
            generation.Tooltip = "Turn Frame Generation On Or Off";
            generation.Keywords = new[] { "fg", "dlss", "xess", "fsr", "interpolation" };
            generation.Blocked = () => !frameGeneration && !ProfileChosen() ? NeedsProfileText : null;
            set.Add(generation);

            // The multiplier, as the settings window's Frame generation row sets it: 2x to 6x, or
            // Dynamic where DLSS-G chooses; what the technique does not allow is the most it allows.
            string[] multipliers = new string[FrameGenerationChoice.Steps.Length + 1];
            FrameGenerationChoice.Steps.CopyTo(multipliers, 0);
            multipliers[multipliers.Length - 1] = FrameGenerationChoice.Dynamic;
            DebugOption multiplier = Choice("frameGenerationMultiplier", DebugCategory.FrameGeneration, "",
                "Multiplier", Saved, multipliers, 0,
                () => Math.Max(0, Array.IndexOf(multipliers, frameGenerationMultiplier)),
                index => SetFrameGenerationMultiplier(multipliers[index]));
            multiplier.Resettable = false;
            multiplier.Tooltip = "Frames Shown Per Rendered Frame";
            multiplier.Keywords = new[] { "2x", "3x", "4x", "dynamic", "mfg", "dlss", "frames" };
            // Where the technique does not allow the step: what runs instead.
            multiplier.DynamicLabel = () =>
            {
                FrameGenerationChoice.Limits limits = FrameGenerationBridge.Limits();
                return FrameGenerationChoice.Offered(frameGenerationMultiplier, limits) ? "Multiplier"
                    : "Multiplier (" + FrameGenerationChoice.Effective(frameGenerationMultiplier, limits) + " here)";
            };
            set.Add(multiplier);

            // Upscaler inputs.
            const string motion = "Motion and depth";
            DebugOption jitterOption = Switch("jitter", DebugCategory.UpscalerInputs, motion, "Jitter", Saved, saved.Jitter,
                () => jitter, value => SetJitter(value));
            jitterOption.Keywords = new[] { "taa", "subpixel" };
            set.Add(jitterOption);

            DebugOption vessel = Choice("vesselMotionVectors", DebugCategory.UpscalerInputs, motion,
                "Vessel depth and motion vectors", Session, new[] { "Unity's", "Written" }, 1,
                () => VesselMotionVectors.RepairEnabled ? 1 : 0,
                value =>
                {
                    VesselMotionVectors.RepairEnabled = value != 0;
                    Debug.Log(Log.Tag + " Vessel depth and motion vectors "
                              + (VesselMotionVectors.RepairEnabled ? "written by ReDefinition." : "as Unity writes them."));
                });
            vessel.Tooltip = "Written By ReDefinition Or Unity";
            set.Add(vessel);

            DebugOption shadow = Choice("vesselShadow", DebugCategory.UpscalerInputs, motion,
                "Vessel shadow in frame generation", Session, new[] { "Moved", "Kept in place" }, 1,
                () => VesselShadowLayer.Enabled ? 1 : 0,
                value =>
                {
                    VesselShadowLayer.Enabled = value != 0;
                    Debug.Log(Log.Tag + " Vessel shadow in frame generation "
                              + (VesselShadowLayer.Enabled ? "kept in place where that is the smaller error." : "moved with the ground."));
                });
            shadow.Tooltip = "Kept Out Of The Ground's Motion";
            shadow.Keywords = new[] { "fg" };
            set.Add(shadow);

            DebugOption skinned = Choice("skinnedMotionVectors", DebugCategory.UpscalerInputs, motion,
                "Skinned motion vectors", Saved, new[] { "As set", "Forced" }, saved.SkinnedMotionVectors ? 1 : 0,
                () => skinnedMotionVectors ? 1 : 0, value => SetSkinnedMotionVectors(value != 0));
            skinned.Tooltip = "Forced On Or As The Game Set Them";
            skinned.Keywords = new[] { "animation", "kerbals" };
            set.Add(skinned);

            DebugOption repair = Switch("motionVectorRepair", DebugCategory.UpscalerInputs, motion, "Motion vector repair", Session, true,
                () => UnityMotionRepair.Enabled,
                value =>
                {
                    UnityMotionRepair.Enabled = value;
                    Debug.Log(Log.Tag + " Motion vector repair " + (UnityMotionRepair.Enabled ? "on." : "off."));
                });
            repair.Tooltip = "The Camera's Move In Unity's Vectors";
            set.Add(repair);

            DebugOption interpolation = Switch("renderInterpolation", DebugCategory.UpscalerInputs, motion, "Render interpolation",
                Session, true,
                () => RenderInterpolation.Enabled,
                value =>
                {
                    RenderInterpolation.Enabled = value;
                    Debug.Log(Log.Tag + " Render interpolation " + (RenderInterpolation.Enabled ? "on." : "off."));
                });
            interpolation.Tooltip = "Vessel And Camera Between Physics Steps";
            set.Add(interpolation);

            DebugOption clouds = Switch("eveClouds", DebugCategory.UpscalerInputs, "Clouds", "EVE clouds: jitter and motion vectors",
                Session, true,
                () => EveCloudMotion.Enabled,
                value =>
                {
                    EveCloudMotion.Enabled = value;
                    Debug.Log(Log.Tag + " EVE clouds with jitter and motion vectors "
                              + (EveCloudMotion.Enabled ? "on" : "off") + ".");
                });
            clouds.Keywords = new[] { "environmental visual enhancements" };
            clouds.PutBack = false;
            clouds.Blocked = () => EveCloudMotion.Present ? null : "EVE is not installed";
            set.Add(clouds);

            DebugOption mipmap = Switch("mipmapBias", DebugCategory.UpscalerInputs, "Textures", "Mipmap bias", Saved, saved.MipmapBias,
                () => mipmapBias, value => SetMipmapBias(value));
            mipmap.Keywords = new[] { "texture", "sharpness" };
            set.Add(mipmap);

            DebugOption tufx = Switch("tufxAfterUpscaling", DebugCategory.UpscalerInputs, "Image", "TUFX after upscaling", Saved,
                saved.TufxAfterUpscaling, () => tufxAfterUpscaling, value => SetTufxAfterUpscaling(value));
            tufx.Keywords = new[] { "bloom", "tonemapping", "post-processing" };
            set.Add(tufx);

            DebugOption exposure = Switch("autoExposure", DebugCategory.UpscalerInputs, "Image", "Auto exposure", Saved,
                saved.AutoExposure, () => autoExposure, value => SetAutoExposure(value));
            exposure.Tooltip = "FSR 3, AMD And XeSS";
            // DLSS always measures its exposure itself.
            exposure.Blocked = () => RunningBackend == UpscalerBackend.Dlss ? "DLSS measures its exposure itself" : null;
            set.Add(exposure);

            DebugOption transparency = Switch("transparencyMask", DebugCategory.UpscalerInputs, "Masks", "Transparency mask", Saved,
                saved.TransparencyMask, () => transparencyMask, value => SetTransparencyMask(value));
            transparency.Tooltip = "FSR 3 And AMD";
            transparency.Keywords = new[] { "eve", "scatterer", "ocean" };
            transparency.Blocked = () => TakesMasks ? null : NeedsMasksText;
            set.Add(transparency);

            DebugOption reactive = Choice("reactiveMask", DebugCategory.UpscalerInputs, "Masks", "Reactive mask", Saved,
                new[] { "Off", "Renderers", "FSR's estimate" }, (int)saved.ReactiveMask,
                () => (int)reactiveMask, value => SetReactiveMask((UpscalerMasks.ReactiveSource)value));
            reactive.Tooltip = "FSR 3 And AMD";
            reactive.Keywords = new[] { "particles", "plumes", "re-entry", "transparent renderers" };
            reactive.Blocked = () => TakesMasks ? null : NeedsMasksText;
            set.Add(reactive);

            // Frame generation and latency.
            DebugOption fastTurns = Switch("motionVectorCheck", DebugCategory.FrameGeneration, "", "Motion vector check on fast turns",
                Session, false,
                () => recordFastTurns,
                value =>
                {
                    recordFastTurns = value;
                    if (rig != null) rig.RecordFastTurns = recordFastTurns;
                    Debug.Log(Log.Tag + " Motion vector check on fast turns " + (recordFastTurns ? "on" : "off") + ".");
                });
            fastTurns.Keywords = new[] { "fg", "camera" };
            // Needs frame generation, whose inputs the check reads.
            fastTurns.Blocked = () => frameGeneration ? null : "needs frame generation";
            set.Add(fastTurns);

            // Game settings, all three at once: a rebuild would reset the upscaler's history.
            DebugOption lod = Switch("lodBias", DebugCategory.GameSettings, "", "LOD bias compensation", Saved, saved.CompensateLodBias,
                () => compensateLodBias, value => SetCompensateLodBias(value));
            lod.Keywords = new[] { "level of detail" };
            set.Add(lod);

            DebugOption msaa = Switch("disableMsaa", DebugCategory.GameSettings, "", "MSAA forced off", Saved, saved.DisableMsaa,
                () => disableMsaa, value => SetDisableMsaa(value));
            msaa.Keywords = new[] { "antialiasing", "multisampling" };
            set.Add(msaa);

            DebugOption anisotropic = Switch("forceAnisotropic", DebugCategory.GameSettings, "", "Anisotropic filtering forced", Saved,
                saved.ForceAnisotropic, () => forceAnisotropic, value => SetForceAnisotropic(value));
            anisotropic.Keywords = new[] { "texture filtering" };
            set.Add(anisotropic);

            // Capture and log.
            const string log = "Log";
            DebugOption measurements = Switch("debugMode", DebugCategory.Capture, log, "Measurements into the log", Saved, false,
                () => DebugMode, value => SetDebugMode(value));
            measurements.Tooltip = "Audits And Read-Backs";
            measurements.Keywords = new[] { "debug", "diagnostic" };
            set.Add(measurements);

            DebugOption diagnostics = Button("writeDiagnostics", DebugCategory.Capture, log, "Write diagnostics to log", "Write",
                () =>
                {
                    if (rig != null) rig.LogDiagnostics();
                    else Debug.Log(Log.Tag + " Diagnostics: upscaler is not active.");
                });
            set.Add(diagnostics);

            // Never saved: a probe, not a setting. It registers a handler on each hook and writes one
            // line per scene and hook into KSP.log, so that the path a mod's handler takes is
            // walked in a normal game (HookProbe).
            DebugOption hooks = Switch("hookProbe", DebugCategory.Capture, "Interface for mods", "Hook probe, into the log", Session, false,
                () => HookProbe.On, value => HookProbe.Set(value));
            hooks.Tooltip = "One Log Line Per Hook";
            hooks.Keywords = new[] { "api", "mods" };
            set.Add(hooks);

            const string views = "Views";
            // Each surface's shine as its material means it, as colours over the picture
            // (RtCamera.SurfaceView; ReDefinitionDeferredReflections.shader). Needs the raytracing
            // with Deferred.
            DebugOption surface = Switch("surfaceShine", DebugCategory.Capture, views, "Surface shine overlay", Session, false,
                () => ReDefinition.Raytracing.RtCamera.SurfaceView, value => ReDefinition.Raytracing.RtCamera.SurfaceView = value);
            surface.Tooltip = "Needs Raytracing With Deferred";
            surface.Keywords = new[] { "raytracing", "reflections" };
            set.Add(surface);

            // The rendered picture as it is, through the same redirect: what the upscaler starts from.
            DebugOption without = Switch("bypass", DebugCategory.Capture, views, "Without upscaler (bypass)", Session, false,
                () => bypass,
                value =>
                {
                    bypass = value;
                    Reattach();
                });
            without.Tooltip = "The Picture Before The Upscaler";
            set.Add(without);

#if DEVELOPMENT_BUILD
            // FSR's debug view pass is compiled into development builds only.
            DebugOption view = Switch("fsrDebugView", DebugCategory.Capture, views, "FSR's debug view", Session, false,
                () => debugView,
                value =>
                {
                    debugView = value;
                    if (rig != null) rig.DebugView = debugView;
                    Debug.Log(Log.Tag + " FSR debug view " + (debugView ? "on" : "off"));
                });
            view.Tooltip = "Development Builds Only";
            view.Blocked = () => RunningBackend == UpscalerBackend.Fsr3 ? null : "needs FSR 3";
            set.Add(view);
#endif

            // 120 frames of what the monitor shows, frame generation's frames included, into
            // ReDefinitionCaptures beside KSP_x64.exe (ScreenRecordingBridge). Five seconds after the
            // click, time to close the windows and turn the camera. With the dumps of the same turn
            // for comparing, or without, as nothing else slows the frames it shows.
            const string screen = "Screen recording";
            DebugOption recordWithDumps = Button("recordWithDumps", DebugCategory.Capture, screen,
                "Record the screen in 5 s (120 frames)", "Record",
                () =>
                {
                    recordingAt = Time.unscaledTime + 5f;
                    recordingDumps = true;
                });
            recordWithDumps.DynamicLabel = () => recordingAt > 0f
                ? "Recording in " + Mathf.CeilToInt(recordingAt - Time.unscaledTime) + " s"
                : "Record the screen in 5 s (120 frames)";
            recordWithDumps.Tooltip = "With Dumps For Comparing";
            recordWithDumps.Keywords = new[] { "video", "capture", "dump" };
            set.Add(recordWithDumps);

            DebugOption recordPlain = Button("recordWithoutDumps", DebugCategory.Capture, screen,
                "Record the screen in 5 s, without dumps", "Record",
                () =>
                {
                    recordingAt = Time.unscaledTime + 5f;
                    recordingDumps = false;
                });
            recordPlain.Tooltip = "Nothing Else Slows The Frames";
            recordPlain.Keywords = new[] { "video", "capture" };
            recordPlain.Blocked = () => recordingAt > 0f ? "a recording is about to start" : null;
            set.Add(recordPlain);

            return set;
        }
    }
}
