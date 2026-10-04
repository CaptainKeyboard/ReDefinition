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
    // the upscaler, frame generation, raytracing, the hotkeys, the flight interface
    // and the scaling of mods' windows, their settings over ReDefinition's
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
            // Fast on the left, High on the right. Low takes Fast: NVIDIA states it is the
            // quicker of the two.
            new ModuleSetting
            {
                Key = "dlssModel",
                Title = "DLSS model",
                Kind = SettingKind.Quality,
                Order = 25,
                Control = SettingControl.Choice,
                Choices = new[] { DlssModel.Fast.ToString(), DlssModel.High.ToString() },
                Label = value => DlssModels.Name(DlssModelOf(value)),
                Tooltip = "Which of NVIDIA's DLSS models reconstructs the image, in every mode; DLSS only.\n"
                          + "High (preset L): the sharper, more stable image with less ghosting, at more GPU time.\n"
                          + "Fast (preset M): similar in quality, closer in speed to DLSS's standard models.",
                Read = settings => settings.DlssModel.ToString(),
                Write = (settings, value) => settings.DlssModel = DlssModelOf(value),
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

        // Raytracing: how much of the lighting, and how often its light bounces
        // (docs/development/raytracing.md). Not a quality a profile sets: it asks for a GPU
        // with raytracing and costs what the player decides to spend.
        public static readonly IGraphicsModule Raytracing = new GraphicsModule("raytracing", "Raytracing",
            new ModuleSetting
            {
                Key = "mode",
                Title = "Raytracing",
                Kind = SettingKind.Other,
                Order = 50,
                Control = SettingControl.Choice,
                Choices = new[]
                {
                    ReDefinition.Raytracing.RaytracingMode.Off.ToString(), ReDefinition.Raytracing.RaytracingMode.Shadows.ToString(),
                    ReDefinition.Raytracing.RaytracingMode.Light.ToString(), ReDefinition.Raytracing.RaytracingMode.Full.ToString(),
                },
                Label = value => RaytracingLabel(value),
                Tooltip = "Lighting from rays traced through the scene, on the proxy's Direct3D 12 device.\n"
                          + "Shadows: the sun's shadows, as far as the scene reaches.\n"
                          + "Light: also the light bouncing off surfaces and the sky, and ambient occlusion.\n"
                          + "Full: also reflections.\n"
                          + "Needs the dxgi.dll proxy next to KSP_x64.exe and a GPU with DirectX Raytracing 1.1\n"
                          + "(GeForce RTX 20 and newer, Radeon RX 6000 and newer).",
                Read = settings => settings.Raytracing.ToString(),
                Write = (settings, value) => settings.Raytracing = RaytracingModeOf(value),
            },
            new ModuleSetting
            {
                Key = "bounces",
                Title = "Light bounces",
                Kind = SettingKind.Other,
                Order = 60,
                Control = SettingControl.Slider,
                Min = 1f,
                Max = 4f,
                StepsPerUnit = 1f,
                Label = value => ((int)float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture),
                Tooltip = "How often the raytraced light bounces between surfaces. More bounces light rooms and\n"
                          + "shadowed sides more fully, and cost more GPU time.",
                Read = settings => settings.RaytracingBounces.ToString(CultureInfo.InvariantCulture),
                Write = (settings, value) =>
                    settings.RaytracingBounces = (int)float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture),
                Interactable = TracesDiffuseLight,
            },
            new ModuleSetting
            {
                Key = "moonlight",
                Title = "Moonlight",
                Kind = SettingKind.Other,
                Order = 70,
                Control = SettingControl.Slider,
                Min = 0f,
                Max = 300f,
                StepsPerUnit = 0.2f,
                Label = PercentLabel,
                Tooltip = "How bright the light of moons and planets is that the sun shines on: the night side under\n"
                          + "the Mun, and the planet's light in orbit. It falls only where the body stands in the sky,\n"
                          + "with its shadows. Real moonlight would be too faint to see; 100 % is ReDefinition's scale.",
                Read = settings => settings.RaytracingMoonlight.ToString("0", CultureInfo.InvariantCulture),
                Write = (settings, value) =>
                    settings.RaytracingMoonlight = float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture),
                // The bodies' light comes with the traced diffuse light.
                Interactable = TracesDiffuseLight,
            },
            new ModuleSetting
            {
                Key = "moonReflection",
                Title = "Moon reflection",
                Kind = SettingKind.Other,
                Order = 75,
                Control = SettingControl.Slider,
                Min = 0f,
                Max = 500f,
                StepsPerUnit = 0.2f,
                Label = PercentLabel,
                Tooltip = "How strongly water mirrors the moons. 100 % is the moon as bright in the water as the eye\n"
                          + "sees it at night; it lights nothing. Only with Full, which traces reflections.",
                Read = settings => settings.RaytracingMoonReflection.ToString("0", CultureInfo.InvariantCulture),
                Write = (settings, value) =>
                    settings.RaytracingMoonReflection = float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture),
                Interactable = settings => settings.Raytracing == ReDefinition.Raytracing.RaytracingMode.Full,
            },
            new ModuleSetting
            {
                Key = "sunReflection",
                Title = "Sun reflection",
                Kind = SettingKind.Other,
                Order = 77,
                Control = SettingControl.Slider,
                Min = 0f,
                Max = 300f,
                StepsPerUnit = 0.2f,
                Label = PercentLabel,
                Tooltip = "How strongly water mirrors the sun: the raytraced sun on the sea, or Scatterer's own\n"
                          + "glint without raytraced reflections. 100 % is the sun's own brightness and Scatterer's.",
                Read = settings => settings.SunReflection.ToString("0", CultureInfo.InvariantCulture),
                Write = (settings, value) =>
                    settings.SunReflection = float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture),
            },
            new ModuleSetting
            {
                Key = "nightAmbient",
                Title = "Night light",
                Kind = SettingKind.Other,
                Order = 80,
                Control = SettingControl.Slider,
                Min = 0f,
                Max = 100f,
                StepsPerUnit = 0.2f,
                Label = PercentLabel,
                Tooltip = "How much of KSP's own night light stays: the least light anything gets where neither\n"
                          + "the sun nor a moon reaches it. 100 % is KSP's night; lower makes the night darker\n"
                          + "and the moonlight's shadows stand out.",
                Read = settings => settings.RaytracingNightAmbient.ToString("0", CultureInfo.InvariantCulture),
                Write = (settings, value) =>
                    settings.RaytracingNightAmbient = float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture),
                Interactable = TracesDiffuseLight,
            });

        private static string PercentLabel(string value)
        {
            return float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture).ToString("0", CultureInfo.InvariantCulture) + " %";
        }

        // Whether the raytracing traces the diffuse light: in Light and Full.
        private static bool TracesDiffuseLight(OwnSettings settings)
        {
            return settings.Raytracing == ReDefinition.Raytracing.RaytracingMode.Light
                   || settings.Raytracing == ReDefinition.Raytracing.RaytracingMode.Full;
        }

        private static ReDefinition.Raytracing.RaytracingMode RaytracingModeOf(string value)
        {
            ReDefinition.Raytracing.RaytracingMode mode;
            return System.Enum.TryParse(value, out mode) ? mode : ReDefinition.Raytracing.RaytracingMode.Off;
        }

        private static string RaytracingLabel(string value)
        {
            switch (RaytracingModeOf(value))
            {
                case ReDefinition.Raytracing.RaytracingMode.Shadows: return "Shadows";
                case ReDefinition.Raytracing.RaytracingMode.Light: return "Shadows and light";
                case ReDefinition.Raytracing.RaytracingMode.Full: return "Full";
                default: return "Off";
            }
        }

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

        // The flight UI's own, in the Gameplay tab with KSP's flight settings.
        public static readonly IGraphicsModule FlightUi = new GraphicsModule("flightUi", "Flight",
            new ModuleSetting
            {
                Key = "flightPanelAuto",
                Title = "Flight panel follows the situation",
                Order = 104,
                Row = SettingCategory.Gameplay,
                Section = "Flight",
                Control = SettingControl.Toggle,
                NeedsProfile = false,
                Tooltip = "The flight panel in the maneuver mode switches to the profile for what the vessel is doing: ascent, "
                          + "orbit, rendezvous, landing, docking or flight on wings. A profile picked by hand stays until the "
                          + "situation changes.",
                Read = settings => SettingValues.Text(settings.FlightPanelAuto),
                Write = (settings, value) => settings.FlightPanelAuto = bool.Parse(value),
            },
            new ModuleSetting
            {
                Key = "autoAltimeter",
                Title = "Altimeter switches to the ground",
                Order = 103,
                Row = SettingCategory.Gameplay,
                Section = "Flight",
                Control = SettingControl.Toggle,
                NeedsProfile = false,
                Tooltip = "The altimeter shows the height above the ground below 5 km over it and on the ground, and the height "
                          + "above sea level above 6 km. A click on its switch still changes it by hand, until the height crosses "
                          + "the other way.",
                Read = settings => SettingValues.Text(settings.AutoAltimeter),
                Write = (settings, value) => settings.AutoAltimeter = bool.Parse(value),
            },
            new ModuleSetting
            {
                Key = "flightStartsInManeuverMode",
                Title = "Start flight in maneuver mode",
                Order = 105,
                Row = SettingCategory.Gameplay,
                Section = "Flight",
                Control = SettingControl.Toggle,
                NeedsProfile = false,
                Tooltip = "A flight starts in KSP's maneuver mode, with its orbit and maneuver panel at the bottom left, rather "
                          + "than in the staging mode. The mode buttons switch it as before.",
                Read = settings => SettingValues.Text(settings.FlightStartsInManeuverMode),
                Write = (settings, value) => settings.FlightStartsInManeuverMode = bool.Parse(value),
            },
            new ModuleSetting
            {
                Key = "worldButtons",
                Title = "Buttons over the buildings",
                Order = 106,
                Row = SettingCategory.Gameplay,
                Section = "Flight",
                Control = SettingControl.Toggle,
                NeedsProfile = false,
                Tooltip = "Buttons over the space centre's buildings while the vessel stands near them -- back to the "
                          + "assembly building, and before the launch to every other building -- and over the launch pad and "
                          + "the runway in the editors.",
                Read = settings => SettingValues.Text(settings.WorldButtons),
                Write = (settings, value) => settings.WorldButtons = bool.Parse(value),
            });

        // What KSP leaves unscaled (Scaling/UiScaling).
        public static readonly IGraphicsModule Scaling = new GraphicsModule("uiScaling", "Scaling",
            new ModuleSetting
            {
                Key = "scaleModWindows",
                Title = "Other mods' windows",
                Order = 210,
                Row = SettingCategory.Interface,
                Section = ScalingSection,
                Control = SettingControl.Toggle,
                NeedsProfile = false,
                Tooltip = "Draws other mods' windows at KSP's UI scale. Windows of mods that scale themselves, such as "
                          + "MechJeb's, stay as they are.",
                Read = settings => SettingValues.Text(settings.ScaleModWindows),
                Write = (settings, value) => settings.ScaleModWindows = bool.Parse(value),
            });

        private const string ScalingSection = "Scaling";

        public static readonly IList<IGraphicsModule> All =
            new List<IGraphicsModule> { Upscaler, FrameGeneration, Raytracing, Hotkeys, FlightUi, Scaling }.AsReadOnly();

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

        private static DlssModel DlssModelOf(string name)
        {
            return (DlssModel)System.Enum.Parse(typeof(DlssModel), name, true);
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
