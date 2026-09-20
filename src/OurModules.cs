using System.Collections.Generic;
using System.Globalization;
using FidelityFX.FSR3;
using ReDefinition.Bridges;
using ReDefinition.Settings;
using ReDefinition.Upscaler;
using ReDefinition.Window;

namespace ReDefinition
{
    // ReDefinition's own features as modules (docs/development/architecture.md):
    // the upscaler and frame generation, their settings over ReDefinition's
    // settings file (OwnSettings). What a profile's MODULE node names comes from
    // here (ProfileApplier), and so do the rows under General in the settings
    // window and in KSP's settings dialog (KspSettingsSection). Both edit a copy
    // and commit it through the add-on's setters (ReDefinitionAddon.Apply); the FSR rig
    // stays as it is.
    //
    // A profile sets their quality settings only, as it does the other mods': the
    // upscaler and its mode. Its sharpness is taste, and frame generation -- which
    // needs the dxgi.dll proxy a profile cannot see -- the player's switch. None of
    // them can be changed without a graphics profile chosen: only a profile makes
    // ReDefinition active (ReDefinitionAddon).
    internal static class OurModules
    {
        public static readonly IGraphicsModule Upscaler = new GraphicsModule("upscaler", "Upscaler",
            new ModuleSetting
            {
                Key = "enabled",
                Title = "Upscaler",
                Kind = SettingKind.Quality,
                Order = 10,
                Control = SettingControl.Toggle,
                // KSP's tooltip draws its text as one line across the screen, so
                // the texts break their own lines.
                Tooltip = "The upscaler on the 3D scene, FSR 3 or the chosen technique: temporal antialiasing,\n"
                          + "and in every mode but AA only also upscaling.",
                Read = settings => SettingValues.Text(settings.Enabled),
                Write = (settings, value) => settings.Enabled = bool.Parse(value),
            },
            // Not a profile's: it depends on the GPU and on a file the player adds.
            new ModuleSetting
            {
                Key = "technique",
                Title = "Technique",
                Kind = SettingKind.Other,
                Order = 15,
                Control = SettingControl.Choice,
                Choices = new[]
                {
                    UpscalerBackend.Fsr3.ToString(), UpscalerBackend.Dlss.ToString(), UpscalerBackend.Amd.ToString(),
                },
                Label = value => UpscalerBackends.Name(Backend(value)),
                Tooltip = "FSR 3 runs on every GPU and comes with ReDefinition.\n"
                          + "DLSS needs an NVIDIA RTX GPU and NVIDIA's nvngx_dlss.dll, which NVIDIA DLSS files\n"
                          + "in ReDefinition's settings window downloads from NVIDIA.\n"
                          + "AMD FSR (DLL) runs AMD's amd_fidelityfx_upscaler_dx12.dll, the player's own copy next to KSP_x64.exe:\n"
                          + "FSR 4 where the DLL and the GPU have it, otherwise the FSR 3.1 the DLL carries.\n"
                          + "Both need the dxgi.dll proxy next to KSP_x64.exe. Without their DLL, FSR 3 runs.",
                Read = settings => settings.Backend.ToString(),
                Write = (settings, value) => settings.Backend = Backend(value),
                Interactable = settings => DlssBridge.Offered || AmdUpscalerBridge.Offered
                                           || settings.Backend != UpscalerBackend.Fsr3,
            },
            // AA only at the right end, smaller render sizes to its left.
            new ModuleSetting
            {
                Key = "quality",
                Title = "Mode",
                Kind = SettingKind.Quality,
                Order = 20,
                Control = SettingControl.Choice,
                Choices = ModesRightToLeft(),
                Label = value => KspSettingsSection.ModeName(Mode(value)),
                Tooltip = "Right end: AA only, full resolution, the upscaler as antialiasing.\n"
                          + "Further left: rendered smaller by the factor shown, and reconstructed.\n"
                          + "DLSS renders at the size it asks for in each mode; 1.2x, which DLSS does not have, as its Quality.\n"
                          + "The diagnostics window shows the frame rate it gives.\n"
                          + "With V-Sync on and frame generation, the rendered rate is held at half the refresh rate,\n"
                          + "or lower where DLSS generates several frames from each rendered one.",
                Read = settings => settings.Quality.ToString(),
                Write = (settings, value) => settings.Quality = Mode(value),
            },
            new ModuleSetting
            {
                Key = "dlssPreset",
                Title = "DLSS preset",
                Kind = SettingKind.Taste,
                Order = 25,
                Control = SettingControl.Choice,
                Choices = DlssBridge.PresetNames(),
                Label = value => value == DlssPreset.Default.ToString() ? "Default" : "Preset " + value,
                Tooltip = "Which of NVIDIA's DLSS models to use; Default lets the DLSS library choose for each mode.\n"
                          + "Which presets a library has depends on its version.",
                Read = settings => settings.DlssPreset.ToString(),
                Write = (settings, value) => settings.DlssPreset =
                    (DlssPreset)System.Enum.Parse(typeof(DlssPreset), value, true),
                Interactable = settings => settings.Backend == UpscalerBackend.Dlss,
            },
            new ModuleSetting
            {
                Key = "sharpness",
                Title = "Sharpness",
                Kind = SettingKind.Taste,
                Order = 30,
                Control = SettingControl.Slider,
                Min = 0f,
                Max = UpscalerRig.MaximumSharpness,
                StepsPerUnit = 20f,
                Label = value => float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)
                    .ToString("0.00", CultureInfo.InvariantCulture),
                Tooltip = "RCAS sharpening after the upscaler -- FSR 3, DLSS and AMD's DLL alike; 0 switches it off.\n"
                          + "1.0 is FidelityFX's maximum; above it is beyond what FidelityFX intends,\n"
                          + "and AMD's DLL stops there.",
                Read = settings => SettingValues.Text(settings.Sharpness),
                Write = (settings, value) =>
                    settings.Sharpness = float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture),
            });

        public static readonly IGraphicsModule FrameGeneration = new GraphicsModule("frameGeneration", "Frame generation",
            new ModuleSetting
            {
                Key = "enabled",
                Title = "Frame generation",
                Kind = SettingKind.Other,
                Order = 40,
                Control = SettingControl.Toggle,
                // Which one runs, DLSS or FSR 3.
                Label = value => FrameGenerationLabel(bool.Parse(value)),
                Tooltip = "Generated frames between the rendered ones, from KSP's own depth and motion vectors.\n"
                          + "DLSS frame generation where NVIDIA's Streamline 2.14.1 DLLs lie next to KSP_x64.exe\n"
                          + "and the GPU runs it (RTX 40 and newer), as many frames as the GPU offers;\n"
                          + "FSR 3 frame generation otherwise, one frame between every two. The switch says which runs.\n"
                          + "Needs the dxgi.dll proxy next to KSP_x64.exe. With the upscaler off,\n"
                          + "the scene is still captured for it, at full resolution.\n"
                          + "With FSR 3, turn V-Sync on in KSP's settings for even frames, with G-Sync or FreeSync too:\n"
                          + "in full screen, frames can tear without it even on a variable refresh rate (AMD).",
                Read = settings => SettingValues.Text(settings.FrameGeneration),
                Write = (settings, value) => settings.FrameGeneration = bool.Parse(value),
                // Offered where the proxy can generate, and always while it is on,
                // so that a setting saved with the proxy installed can still be
                // switched off after it has gone.
                Interactable = settings => FrameGenerationBridge.CanGenerate || settings.FrameGeneration,
            });

        // The technique that runs, "DLSS 2x" or "FSR 3", in place of "Enabled" --
        // the ticked box says it is on, and the control is too narrow for both --
        // and the plain state while none runs.
        private static string FrameGenerationLabel(bool on)
        {
            string technique = on ? FrameGenerationBridge.TechniqueName() : null;
            return technique ?? KspSettingsSection.StateText(on);
        }

        // ReDefinition's own hotkeys, in the Keys tab beside the mods' and KSP's.
        // A profile never sets a binding, so none of them is a quality setting.
        public static readonly IGraphicsModule Hotkeys = new GraphicsModule("hotkeys", "ReDefinition",
            new ModuleSetting
            {
                Key = "upscalerKey",
                Title = "Upscaler on or off",
                Order = 10,
                Row = SettingCategory.Keys,
                Control = SettingControl.Binding,
                Tooltip = "Switches the upscaler on or off without opening a window.",
                Read = settings => settings.UpscalerKey,
                Write = (settings, value) => settings.UpscalerKey = value,
            },
            new ModuleSetting
            {
                Key = "settingsWindowKey",
                Title = "Settings window",
                Order = 20,
                Row = SettingCategory.Keys,
                Control = SettingControl.Binding,
                Tooltip = "Opens and closes this window; it also opens from ReDefinition's toolbar button.",
                Read = settings => settings.SettingsWindowKey,
                Write = (settings, value) => settings.SettingsWindowKey = value,
            },
            new ModuleSetting
            {
                Key = "diagnosticsKey",
                Title = "Diagnostics window",
                Order = 30,
                Row = SettingCategory.Keys,
                Control = SettingControl.Binding,
                Tooltip = "Opens and closes the diagnostics window.",
                Read = settings => settings.DiagnosticsKey,
                Write = (settings, value) => settings.DiagnosticsKey = value,
            },
            new ModuleSetting
            {
                Key = "cameraListKey",
                Title = "Camera list to the log",
                Order = 40,
                Row = SettingCategory.Keys,
                Control = SettingControl.Binding,
                Tooltip = "Writes the scene's cameras, with their depth and what they render, into KSP.log.",
                Read = settings => settings.CameraListKey,
                Write = (settings, value) => settings.CameraListKey = value,
            });

        public static readonly IList<IGraphicsModule> All =
            new List<IGraphicsModule> { Upscaler, FrameGeneration, Hotkeys }.AsReadOnly();

        public static IGraphicsModule Find(string name)
        {
            foreach (IGraphicsModule module in All)
                if (module.Name == name) return module;
            return null;
        }

        // Every module's settings in the order their rows stand, in that tab.
        public static List<ModuleSetting> Rows()
        {
            return Rows(SettingCategory.General);
        }

        public static List<ModuleSetting> Rows(SettingCategory tab)
        {
            List<ModuleSetting> rows = new List<ModuleSetting>();
            foreach (IGraphicsModule module in All)
                foreach (ModuleSetting setting in module.Settings)
                    if (setting.Row == tab) rows.Add(setting);
            // Stable: equal orders keep the modules' order.
            List<ModuleSetting> sorted = new List<ModuleSetting>();
            foreach (ModuleSetting setting in rows)
            {
                int at = sorted.FindIndex(other => other.Order > setting.Order);
                if (at < 0) sorted.Add(setting);
                else sorted.Insert(at, setting);
            }
            return sorted;
        }

        // The modes from UltraPerformance on the left to AA only on the right: they
        // run from NativeAA = 0 to UltraPerformance = 5, so the row shows them
        // mirrored.
        private static string[] ModesRightToLeft()
        {
            int last = (int)Fsr3Upscaler.QualityMode.UltraPerformance;
            string[] names = new string[last + 1];
            for (int i = 0; i <= last; i++) names[i] = ((Fsr3Upscaler.QualityMode)(last - i)).ToString();
            return names;
        }

        private static UpscalerBackend Backend(string name)
        {
            return (UpscalerBackend)System.Enum.Parse(typeof(UpscalerBackend), name, true);
        }

        private static Fsr3Upscaler.QualityMode Mode(string name)
        {
            return (Fsr3Upscaler.QualityMode)System.Enum.Parse(typeof(Fsr3Upscaler.QualityMode), name, true);
        }
    }
}
