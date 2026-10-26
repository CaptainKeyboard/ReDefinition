using System.Collections.Generic;
using System.Globalization;
using FidelityFX.FSR3;
using ReDefinition.Proxy;
using ReDefinition.Settings;
using ReDefinition.Upscaler;
using ReDefinition.Vendor;
using ReDefinition.Window;

namespace ReDefinition.Modules
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
    // A profile sets the graphics settings of these modules -- the upscaler, its mode, frame
    // generation, raytracing, Ray Reconstruction and the low latency mode -- by the class of the
    // graphics card (ProfileValue); a value may be Auto, which stands for what fits the card and
    // the screen (AutoSettings). Its sharpness is taste and stays the player's. None of them can
    // be changed without a graphics profile chosen: only a profile makes ReDefinition active
    // (ReDefinitionAddon).
    internal static class OurModules
    {
        // The upscaler: on or off, the technique and its mode, the render height, the DLSS model, the sharpness, and
        // whether the upscaler is the only antialiasing.
        public static readonly IGraphicsModule Upscaler = new GraphicsModule("upscaler", "Upscaler",
            new ModuleSetting
            {
                // The switch alone, for the hotkey and the diagnostics window; the row is "technique".
                Key = "enabled",
                Title = "Upscaler switch",
                Kind = SettingKind.Quality,
                OnlyActivates = true,
                Order = 10,
                Control = SettingControl.Toggle,
                // KSP's tooltip draws its text as one line across the screen, so
                // the texts break their own lines.
                Tooltip = "The upscaler on the 3D scene: temporal antialiasing,\n"
                          + "and in every mode but AA only also upscaling.",
                Read = settings => SettingValues.Text(settings.Enabled),
                Write = (settings, value) => settings.Enabled = bool.Parse(value),
                Present = () => false,
            },
            // Off, Auto or a technique; the switch and the technique in one row.
            new ModuleSetting
            {
                Key = "technique",
                Title = "Upscaler",
                Section = "Upscaling",
                Kind = SettingKind.Other,
                Order = 15,
                Control = SettingControl.Choice,
                Radio = true,
                ChoiceLabel = value => value == "Off" || value == AutoSettings.Auto ? value
                    : value == UpscalerBackend.Fsr3.ToString() ? "FSR 3"
                    : value == UpscalerBackend.Dlss.ToString() ? "DLSS"
                    : value == UpscalerBackend.Amd.ToString() ? "FSR 4" : "XeSS",
                Unavailable = (settings, value) => TechniqueAvailability.Upscaler(value, TechniqueAvailability.Current()),
                Files = (settings, value) => TechniqueFiles.For(
                    TechniqueAvailability.UpscalerNeeds(value, TechniqueAvailability.Current())),
                Choices = new[]
                {
                    "Off", AutoSettings.Auto, UpscalerBackend.Fsr3.ToString(), UpscalerBackend.Dlss.ToString(),
                    UpscalerBackend.Amd.ToString(), UpscalerBackend.Xess.ToString(),
                },
                // AMD's DLL by the FSR it runs, XeSS by its version, once they run.
                Label = value => value == "Off" ? "Off"
                    : value == AutoSettings.Auto ? "Auto (" + TechniqueLabel(AutoSettings.ResolvedBackend) + ")"
                    : TechniqueLabel(Backend(value)),
                Tooltip = "Renders the 3D scene smaller and reconstructs it at full size.",
                ChoiceTooltip = value => value == "Off" ? "No upscaling. With frame generation on, the upscaler last chosen still smooths\n"
                                                         + "the edges at full size (AA only), in place of the mods' antialiasing."
                    : value == AutoSettings.Auto ? "The best upscaler for this card: DLSS on an RTX, FSR 4 on a Radeon RX 7000 or 9000, XeSS on an Intel GPU, otherwise FSR 3."
                    : value == UpscalerBackend.Fsr3.ToString() ? "AMD FSR 3, built into ReDefinition. Runs on every card."
                    : value == UpscalerBackend.Dlss.ToString() ? "NVIDIA DLSS, on a GeForce RTX."
                    : value == UpscalerBackend.Amd.ToString() ? "AMD FSR 4, on a Radeon RX 7000 or 9000."
                    : "Intel XeSS. Runs on most cards.",
                Read = settings => !settings.Enabled ? "Off"
                    : settings.BackendAuto ? AutoSettings.Auto : settings.Backend.ToString(),
                Write = (settings, value) =>
                {
                    settings.Enabled = value != "Off";
                    if (value == "Off") return;
                    settings.BackendAuto = value == AutoSettings.Auto;
                    if (!settings.BackendAuto) settings.Backend = Backend(value);
                },
            },
            // AA only at the right end, smaller render sizes to its left.
            new ModuleSetting
            {
                Key = "quality",
                Title = "Mode",
                Section = "Upscaling",
                Kind = SettingKind.Quality,
                Order = 20,
                Control = SettingControl.Choice,
                Choices = ModesRightToLeft(),
                Label = value => value == AutoSettings.Auto ? "Auto (" + KspSettingsSection.ModeName(AutoSettings.ResolvedQuality) + ")"
                    : KspSettingsSection.ModeName(Mode(value)),
                Tooltip = "How much smaller the scene renders: AA only is full size, further left is faster and softer. "
                          + "Auto fits the profile to your screen.",
                Read = settings => settings.QualityAuto ? AutoSettings.Auto : settings.Quality.ToString(),
                Write = (settings, value) =>
                {
                    settings.QualityAuto = value == AutoSettings.Auto;
                    if (!settings.QualityAuto) settings.Quality = Mode(value);
                },
                Interactable = settings => settings.Enabled,
            },
            // What a profile aims for, which Auto turns into the mode for the screen: the height
            // the 3D scene is rendered at on a screen 1440 pixels high. No row: the profile's.
            new ModuleSetting
            {
                Key = "renderTargetHeight",
                Title = "Render height Auto aims for",
                Kind = SettingKind.Quality,
                Order = 21,
                Control = SettingControl.Slider,
                Min = 240f,
                Max = 4320f,
                WholeNumbers = true,
                StepsPerUnit = 1f,
                Tooltip = "The height the 3D scene is rendered at, in pixels, that Auto aims for in the mode row.",
                Read = settings => settings.RenderTargetHeight.ToString(CultureInfo.InvariantCulture),
                Write = (settings, value) =>
                    settings.RenderTargetHeight = (int)float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture),
                Present = () => false,
            },
            // DLSS's own choice first, then K, M and L, from the quickest to the sharpest.
            new ModuleSetting
            {
                Key = "dlssModel",
                Title = "DLSS model",
                Kind = SettingKind.Quality,
                Order = 25,
                Control = SettingControl.Choice,
                Choices = new[] { DlssModel.Default.ToString(), DlssModel.K.ToString(), DlssModel.M.ToString(), DlssModel.L.ToString() },
                Label = value => DlssModels.Name(DlssModelOf(value)),
                Tooltip = "Which of NVIDIA's DLSS models reconstructs the image; DLSS only.\n"
                          + "Default: the model NVIDIA's DLSS library chooses for each mode.\n"
                          + "Preset K: the transformer model before M and L.\n"
                          + "Preset M: close to L in quality, close to K in speed.\n"
                          + "Preset L: the sharpest, most stable image with the least ghosting, at the most GPU time.",
                Read = settings => settings.DlssModel.ToString(),
                Write = (settings, value) => settings.DlssModel = DlssModelOf(value),
                Interactable = settings => settings.Backend == UpscalerBackend.Dlss,
                // The profile's, by the card; not a row.
                Present = () => false,
            },
            new ModuleSetting
            {
                Key = "sharpness",
                Title = "Sharpness",
                Section = "Upscaling",
                Kind = SettingKind.Taste,
                Order = 30,
                Control = SettingControl.Slider,
                Min = 0f,
                Max = UpscalerRig.MaximumSharpness,
                StepsPerUnit = 20f,
                Label = value => float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)
                    .ToString("0.00", CultureInfo.InvariantCulture),
                Tooltip = "Sharpening after the upscaler. 0 is off, 1.0 is AMD's maximum.",
                Read = settings => SettingValues.Text(settings.Sharpness),
                Write = (settings, value) =>
                    settings.Sharpness = float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture),
            },
            // The mods' antialiasing with the upscaler and frame generation off (HostStack.Duplicates).
            new ModuleSetting
            {
                Key = "oneAntialiasing",
                Title = "One antialiasing per camera",
                Section = "Upscaling",
                Kind = SettingKind.Other,
                Order = 35,
                Control = SettingControl.Toggle,
                Tooltip = "With the upscaler and frame generation off: where Scatterer's TAA smooths a camera,\n"
                          + "TUFX's antialiasing is not run on it as well. With either on, the upscaler smooths alone.",
                Read = settings => SettingValues.Text(settings.OneAntialiasing),
                Write = (settings, value) => settings.OneAntialiasing = bool.Parse(value),
            });

        // Frame generation: the switch a profile sets, and the player's technique and multiplier rows.
        public static readonly IGraphicsModule FrameGeneration = new GraphicsModule("frameGeneration", "Frame generation",
            new ModuleSetting
            {
                // The switch a profile sets (MODULE frameGeneration, enabled = True); the player's row
                // is "mode" below, which holds this and the multiplier.
                Key = "enabled",
                Title = "Frame generation switch",
                Kind = SettingKind.Other,
                Order = 40,
                Control = SettingControl.Toggle,
                Tooltip = "Generated frames between the rendered ones. The row \"Frame generation\" sets this with the multiplier.",
                Read = settings => SettingValues.Text(settings.FrameGeneration),
                Write = (settings, value) => settings.FrameGeneration = bool.Parse(value),
                Interactable = settings => FrameGenerationBridge.CanGenerate || settings.FrameGeneration,
                Present = () => false,
            },
            new ModuleSetting
            {
                Key = "technique",
                Title = "Frame generation",
                Section = "Frame generation",
                Kind = SettingKind.Other,
                Order = 40,
                Control = SettingControl.Choice,
                Radio = true,
                Choices = FrameGenerationTechniqueSetting.All,
                ChoiceLabel = FrameGenerationTechniqueSetting.Label,
                Label = value => FrameGenerationTechniqueLabel(value),
                Tooltip = "Generated frames between the rendered ones.",
                ChoiceTooltip = value => value == FrameGenerationTechniqueSetting.Off ? "No generated frames."
                    : value == FrameGenerationTechniqueSetting.Auto
                        ? "The best frame generation that runs here: DLSS on an RTX, XeSS on an Intel GPU, otherwise FSR 3."
                    : value == FrameGenerationTechniqueSetting.Dlss
                        ? "NVIDIA DLSS frame generation, up to 6x and Dynamic, on a GeForce RTX. Takes effect after a restart."
                    : value == FrameGenerationTechniqueSetting.Fsr
                        ? "AMD FSR 3 frame generation, one frame between two, on every card. Turn V-Sync on for even frames. Takes effect after a restart."
                    : "Intel XeSS frame generation. Takes effect after a restart.",
                // Off and the technique that runs at once; another one at KSP's next start.
                EffectOf = (before, after) => FrameGenerationTakesEffect(after),
                Read = settings => settings.FrameGeneration ? settings.FrameGenerationTechnique : FrameGenerationTechniqueSetting.Off,
                Write = (settings, value) =>
                {
                    settings.FrameGeneration = value != FrameGenerationTechniqueSetting.Off;
                    if (FrameGenerationTechniqueSetting.Valid(value)) settings.FrameGenerationTechnique = value;
                },
                // Offered where the proxy is there -- a technique that does not generate now can from the next
                // start on, each button says -- and always while it is on, so that a setting saved with the
                // proxy installed can still be switched off after it has gone.
                Interactable = settings => FrameGenerationBridge.Available || settings.FrameGeneration,
                Unavailable = (settings, value) => TechniqueAvailability.FrameGeneration(value, TechniqueAvailability.Current()),
                Files = (settings, value) => TechniqueFiles.For(
                    TechniqueAvailability.FrameGenerationNeeds(value, TechniqueAvailability.Current())),
            },
            new ModuleSetting
            {
                Key = "mode",
                Title = "Multiplier",
                Section = "Frame generation",
                Kind = SettingKind.Other,
                Order = 41,
                Control = SettingControl.Choice,
                Radio = true,
                Choices = FrameGenerationChoice.Shown,
                Tooltip = "Frames shown per rendered frame.",
                ChoiceTooltip = value => value == FrameGenerationChoice.Dynamic
                    ? "DLSS frame generation picks the multiplier that reaches the display's refresh rate."
                    : value == "2x" ? "One generated frame per rendered frame."
                    : (FrameGenerationChoice.Multiplier(value) - 1) + " generated frames per rendered frame.",
                Read = settings => settings.FrameGenerationMultiplier,
                Write = (settings, value) =>
                {
                    if (FrameGenerationChoice.Valid(value)) settings.FrameGenerationMultiplier = value;
                },
                Interactable = settings => settings.FrameGeneration,
                Unavailable = (settings, value) => TechniqueAvailability.Step(value,
                    settings.FrameGenerationTechnique, FrameGenerationBridge.Limits()),
            });

        // The low latency mode (docs/development/low-latency.md): Auto, or Off. Written into the
        // proxy's ini, which reads it within a second.
        public static readonly IGraphicsModule Latency = new GraphicsModule("latency", "Low latency",
            new ModuleSetting
            {
                Key = "mode",
                Title = "Low latency",
                Section = "Frame generation",
                Kind = SettingKind.Other,
                Order = 45,
                Control = SettingControl.Choice,
                Radio = true,
                Choices = LowLatencySetting.All,
                // The card's technique behind Auto, once the proxy says which lowers the latency.
                Label = value => LowLatencyLabel(value),
                ChoiceLabel = value => LowLatencyLabel(value),
                Tooltip = "Lowers the delay between input and picture.",
                ChoiceTooltip = value => value == LowLatencySetting.Auto
                    ? "The card's own technique (NVIDIA Reflex, AMD Anti-Lag 2 or Intel XeLL) and ReDefinition's frame pacing."
                    : "No latency reduction.",
                Read = settings => settings.LowLatency,
                Write = (settings, value) => settings.LowLatency = value,
                Interactable = settings => FrameGenerationBridge.Available,
            });

        // Raytracing: how much of the lighting, and how often its light bounces
        // (docs/development/raytracing.md). Not a quality a profile sets: it asks for a GPU
        // with raytracing and costs what the player decides to spend.
        public static readonly IGraphicsModule Raytracing = new GraphicsModule("raytracing", "Raytracing",
            new ModuleSetting
            {
                Key = "mode",
                Title = "Raytracing",
                Section = "Raytracing",
                Kind = SettingKind.Other,
                Order = 50,
                Control = SettingControl.Choice,
                Choices = new[]
                {
                    ReDefinition.Raytracing.RaytracingMode.Off.ToString(), ReDefinition.Raytracing.RaytracingMode.Shadows.ToString(),
                    ReDefinition.Raytracing.RaytracingMode.Light.ToString(), ReDefinition.Raytracing.RaytracingMode.Full.ToString(),
                },
                Label = value => RaytracingLabel(value),
                Tooltip = "Raytraced lighting. Shadows: the sun's shadows. Shadows and light: plus bounced light and "
                          + "ambient occlusion. Full: plus reflections. Needs a card with DirectX Raytracing 1.1 (RTX 20, "
                          + "Radeon RX 6000 and newer).",
                Read = settings => settings.Raytracing.ToString(),
                Write = (settings, value) => settings.Raytracing = RaytracingModeOf(value),
                // Offered while the proxy runs raytracing here, and always while it is on, so that a
                // setting saved with the proxy installed can still be switched off after it has gone.
                Interactable = settings => RaytracingOffered() || settings.Raytracing != ReDefinition.Raytracing.RaytracingMode.Off,
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
                // The profile's; not a row.
                Present = () => false,
            },
            new ModuleSetting
            {
                Key = "rayReconstruction",
                Title = "Ray Reconstruction",
                Section = "Raytracing",
                Kind = SettingKind.Other,
                Order = 65,
                Control = SettingControl.Choice,
                Choices = new[] { AutoSettings.Auto, "Off", "On" },
                // Auto says whether it runs with the raytracing chosen now.
                LabelWith = (settings, value) => value != AutoSettings.Auto ? value
                    : "Auto (" + (AutoSettings.ResolvedRayReconstruction && settings.Raytracing != ReDefinition.Raytracing.RaytracingMode.Off
                                  && settings.Raytracing >= AutoSettings.ResolvedRayReconstructionFrom ? "On" : "Off") + ")",
                Tooltip = "NVIDIA's denoiser for the raytracing: sharper reflections, less flicker, more GPU time. Needs DLSS. "
                          + "Auto: on an RTX 40 or 50 always, on an RTX 20 or 30 with Full only.",
                Read = settings => settings.RayReconstructionAuto ? AutoSettings.Auto : settings.RayReconstruction ? "On" : "Off",
                Write = (settings, value) =>
                {
                    settings.RayReconstructionAuto = value == AutoSettings.Auto;
                    if (settings.RayReconstructionAuto) return;
                    settings.RayReconstruction = value == "On";
                    // On, by the player's choice, runs in every mode.
                    if (settings.RayReconstruction) settings.RayReconstructionFrom = ReDefinition.Raytracing.RaytracingMode.Shadows;
                },
                Interactable = settings => settings.Raytracing != ReDefinition.Raytracing.RaytracingMode.Off
                                           && settings.Backend == UpscalerBackend.Dlss,
            },
            new ModuleSetting
            {
                Key = "rayReconstructionFrom",
                Title = "Ray Reconstruction from",
                Kind = SettingKind.Other,
                Order = 66,
                Control = SettingControl.Choice,
                Choices = new[]
                {
                    ReDefinition.Raytracing.RaytracingMode.Shadows.ToString(), ReDefinition.Raytracing.RaytracingMode.Light.ToString(),
                    ReDefinition.Raytracing.RaytracingMode.Full.ToString(),
                },
                Label = value => RaytracingLabel(value),
                Tooltip = "The least raytracing that Ray Reconstruction takes the frame in. Below it ReDefinition's own\n"
                          + "denoiser and DLSS Super Resolution do. Ray Reconstruction costs about 9 ms of GPU time on an\n"
                          + "RTX A4500 in every mode; the own denoiser and Super Resolution together 2.2 ms with Shadows,\n"
                          + "4.2 ms with shadows and light and 5.2 ms with Full. Shadows: Ray Reconstruction in all of them.",
                Read = settings => settings.RayReconstructionFrom.ToString(),
                Write = (settings, value) => settings.RayReconstructionFrom = RaytracingModeOf(value),
                Interactable = settings => settings.RayReconstruction && !settings.RayReconstructionAuto
                                           && settings.Raytracing != ReDefinition.Raytracing.RaytracingMode.Off
                                           && settings.Backend == UpscalerBackend.Dlss,
                // Auto sets it by the card; On runs in every mode.
                Present = () => false,
            },
            new ModuleSetting
            {
                Key = "moonlight",
                Detail = true,
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
                Detail = true,
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
                Key = "moonGlow",
                Detail = true,
                Title = "Moon glow",
                Kind = SettingKind.Other,
                Order = 76,
                Control = SettingControl.Toggle,
                Tooltip = "The moons' discs a little brighter in the night sky, with a soft glow round them.\n"
                          + "It lights nothing.",
                Read = settings => SettingValues.Text(settings.MoonGlow),
                Write = (settings, value) => settings.MoonGlow = bool.Parse(value),
            },
            new ModuleSetting
            {
                Key = "sunReflection",
                Detail = true,
                Title = "Sun reflection",
                Kind = SettingKind.Other,
                Order = 77,
                Control = SettingControl.Slider,
                Min = 0f,
                Max = 300f,
                StepsPerUnit = 0.2f,
                Label = PercentLabel,
                Tooltip = "How strongly water mirrors the sun: the raytraced sun on the sea, or Scatterer's own\n"
                          + "glint without raytraced reflections. 100 % is three times the sun's own brightness and Scatterer's.",
                Read = settings => settings.SunReflection.ToString("0", CultureInfo.InvariantCulture),
                Write = (settings, value) =>
                    settings.SunReflection = float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture),
            },
            new ModuleSetting
            {
                Key = "nightAmbient",
                Detail = true,
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
            },
            new ModuleSetting
            {
                Key = "lightColour",
                Detail = true,
                Title = "Light colour",
                Kind = SettingKind.Other,
                Order = 85,
                Control = SettingControl.Choice,
                Choices = new[]
                {
                    ReDefinition.Raytracing.RaytracingLightColour.Raytraced.ToString(),
                    ReDefinition.Raytracing.RaytracingLightColour.AsWithout.ToString(),
                    ReDefinition.Raytracing.RaytracingLightColour.Neutral.ToString(),
                },
                Label = value => LightColourLabel(value),
                Tooltip = "The colour of the light from the sky and the surroundings; its strength is the rays' in each.\n"
                          + "Raytraced: the sky's own, blue by day, orange at dusk, blue at night.\n"
                          + "As without raytracing: the colour of the ambient light the game has without it.\n"
                          + "Neutral: grey, so that grass and ground in shadow keep their own colour.\n"
                          + "Only with Deferred, in whose lighting the colour is set.",
                Read = settings => settings.RaytracingLightColour.ToString(),
                Write = (settings, value) =>
                {
                    ReDefinition.Raytracing.RaytracingLightColour colour;
                    settings.RaytracingLightColour = System.Enum.TryParse(value, out colour)
                        ? colour : ReDefinition.Raytracing.RaytracingLightColour.Neutral;
                },
                // Set in Deferred's reflections pass, through ReDefinition's copy of its shader
                // (RtLighting); without Deferred the raytraced light goes in as it is.
                Interactable = settings => TracesDiffuseLight(settings) && ReDefinition.Core.ForeignAssumptions.DeferredReflections.Holds,
            });

        // Whether the proxy offers raytracing on this GPU (RtNative.Ready), asked once a second: a row asks
        // it in every frame.
        private static float raytracingAskedAt = float.MinValue;
        private static bool raytracingOffered;

        // The cached answer, asked of the proxy again once a second has passed.
        private static bool RaytracingOffered()
        {
            float now;
            try
            {
                now = UnityEngine.Time.realtimeSinceStartup;
            }
            catch (System.Exception)
            {
                now = 0f;
            }
            if (raytracingAskedAt != float.MinValue && now - raytracingAskedAt < 1f) return raytracingOffered;
            raytracingAskedAt = now;
            string why;
            raytracingOffered = RtNative.Ready(out why);
            return raytracingOffered;
        }

        // The light colour row's value as its row names it.
        private static string LightColourLabel(string value)
        {
            ReDefinition.Raytracing.RaytracingLightColour colour;
            if (!System.Enum.TryParse(value, out colour)) return value;
            switch (colour)
            {
                case ReDefinition.Raytracing.RaytracingLightColour.Raytraced: return "Raytraced";
                case ReDefinition.Raytracing.RaytracingLightColour.AsWithout: return "As without raytracing";
                default: return "Neutral";
            }
        }

        // A fraction's value as a whole percentage, e.g. "50 %".
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

        // The raytracing mode a value names; Off where it names none.
        private static ReDefinition.Raytracing.RaytracingMode RaytracingModeOf(string value)
        {
            ReDefinition.Raytracing.RaytracingMode mode;
            return System.Enum.TryParse(value, out mode) ? mode : ReDefinition.Raytracing.RaytracingMode.Off;
        }

        // The raytracing row's names for its modes.
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
                TakesEffect = ApplyWindow.NextScene,
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
            },
            new ModuleSetting
            {
                Key = "roundGauge",
                Title = "Round gauge",
                TakesEffect = ApplyWindow.NextScene,
                Order = 107,
                Row = SettingCategory.Gameplay,
                Section = "Flight",
                Control = SettingControl.Toggle,
                NeedsProfile = false,
                Tooltip = "KSP's vertical speed gauge shows the stage's burn time, the burn, the time to impact, the closing "
                          + "speed or the angle of attack, picked by a click or following the situation. From the next flight.\n"
                          + "Needs the speeds at the navball, which show the vertical speed in its place.",
                // Without them, the vertical speed would stand nowhere (RoundGauge.Start).
                Interactable = settings => settings.NavballSpeeds,
                Read = settings => SettingValues.Text(settings.RoundGauge),
                Write = (settings, value) => settings.RoundGauge = bool.Parse(value),
            },
            new ModuleSetting
            {
                Key = "navballSpeeds",
                Title = "Speeds at the navball",
                TakesEffect = ApplyWindow.NextScene,
                Order = 108,
                Row = SettingCategory.Gameplay,
                Section = "Flight",
                Control = SettingControl.Toggle,
                NeedsProfile = false,
                Tooltip = "The vertical and horizontal speed in two fields left of the navball, under its SAS modes. From the "
                          + "next flight.",
                Read = settings => SettingValues.Text(settings.NavballSpeeds),
                Write = (settings, value) => settings.NavballSpeeds = bool.Parse(value),
            },
            new ModuleSetting
            {
                Key = "navballDockingMarker",
                Title = "Docking marker on the navball",
                Order = 109,
                Row = SettingCategory.Gameplay,
                Section = "Flight",
                Control = SettingControl.Toggle,
                NeedsProfile = false,
                Tooltip = "While a docking port is the target, a marker on the navball shows where to point the nose so that "
                          + "the two ports face each other.",
                Read = settings => SettingValues.Text(settings.NavballDockingMarker),
                Write = (settings, value) => settings.NavballDockingMarker = bool.Parse(value),
            },
            new ModuleSetting
            {
                Key = "navballDrag",
                Title = "Navball moves by dragging",
                TakesEffect = ApplyWindow.NextScene,
                Order = 110,
                Row = SettingCategory.Gameplay,
                Section = "Flight",
                Control = SettingControl.Toggle,
                NeedsProfile = false,
                Tooltip = "Dragging the navball's show and hide button moves the navball sideways; it keeps clear of the mode "
                          + "buttons and the crew portraits and is drawn over the staging stack. From the next flight.",
                Read = settings => SettingValues.Text(settings.NavballDrag),
                Write = (settings, value) => settings.NavballDrag = bool.Parse(value),
            },
            new ModuleSetting
            {
                Key = "flightPanel",
                Title = "Flight panel profiles",
                TakesEffect = ApplyWindow.NextScene,
                Order = 111,
                Row = SettingCategory.Gameplay,
                Section = "Flight",
                Control = SettingControl.Toggle,
                NeedsProfile = false,
                Tooltip = "The maneuver mode's orbit tabs give way to the flight panel's profiles of readouts, with a pencil to "
                          + "edit them. From the next flight.",
                Read = settings => SettingValues.Text(settings.FlightPanel),
                Write = (settings, value) => settings.FlightPanel = bool.Parse(value),
            },
            new ModuleSetting
            {
                Key = "stageDetailColumns",
                Title = "Stage details in two columns",
                TakesEffect = ApplyWindow.NextScene,
                Order = 112,
                Row = SettingCategory.Gameplay,
                Section = "Flight",
                Control = SettingControl.Toggle,
                NeedsProfile = false,
                Tooltip = "More than four stage details stand in two columns, and the stage's part icons beside them, so that a "
                          + "stage grows wider rather than taller. From the next scene.",
                Read = settings => SettingValues.Text(settings.StageDetailColumns),
                Write = (settings, value) => settings.StageDetailColumns = bool.Parse(value),
            },
            new ModuleSetting
            {
                Key = "stageTwrRange",
                Title = "TWR at the stage's start and end",
                Order = 113,
                Row = SettingCategory.Gameplay,
                Section = "Flight",
                Control = SettingControl.Toggle,
                NeedsProfile = false,
                Tooltip = "The stage details' TWR gives the TWR at the end of the stage beside the one at its start.",
                Read = settings => SettingValues.Text(settings.StageTwrRange),
                Write = (settings, value) => settings.StageTwrRange = bool.Parse(value),
            },
            new ModuleSetting
            {
                Key = "deltaVTools",
                Title = "Delta-v tools in the staging stack",
                TakesEffect = ApplyWindow.NextScene,
                Order = 114,
                Row = SettingCategory.Gameplay,
                Section = "Flight",
                Control = SettingControl.Toggle,
                NeedsProfile = false,
                Tooltip = "The delta-v tools' button opens every stage's details with every value, and in the editors a compact "
                          + "situation window under the stages, in place of KSP's own window. From the next scene.",
                Read = settings => SettingValues.Text(settings.DeltaVTools),
                Write = (settings, value) => settings.DeltaVTools = bool.Parse(value),
            },
            new ModuleSetting
            {
                Key = "hudTooltips",
                Title = "Tooltips on the HUD's buttons",
                TakesEffect = ApplyWindow.NextScene,
                Order = 115,
                Row = SettingCategory.Gameplay,
                Section = "Flight",
                Control = SettingControl.Toggle,
                NeedsProfile = false,
                Tooltip = "Tooltips for the HUD's buttons that have none: the staging stack's, the alarm clock's, the "
                          + "altimeter's mode switch and the navball's speed field. From the next scene.",
                Read = settings => SettingValues.Text(settings.HudTooltips),
                Write = (settings, value) => settings.HudTooltips = bool.Parse(value),
            },
            new ModuleSetting
            {
                Key = "stagingResetAsks",
                Title = "Ask before resetting the staging",
                TakesEffect = ApplyWindow.NextScene,
                Order = 116,
                Row = SettingCategory.Gameplay,
                Section = "Flight",
                Control = SettingControl.Toggle,
                NeedsProfile = false,
                Tooltip = "The editors' button that resets the staging asks first. From the next scene.",
                Read = settings => SettingValues.Text(settings.StagingResetAsks),
                Write = (settings, value) => settings.StagingResetAsks = bool.Parse(value),
            },
            new ModuleSetting
            {
                Key = "crewSlotButtons",
                Title = "Crew slot buttons in the staging look",
                TakesEffect = ApplyWindow.NextScene,
                Order = 117,
                Row = SettingCategory.Gameplay,
                Section = "Flight",
                Control = SettingControl.Toggle,
                NeedsProfile = false,
                Tooltip = "The crew portraits' buttons that add and remove a slot look like the staging stack's add and delete "
                          + "buttons. From the next flight.",
                Read = settings => SettingValues.Text(settings.CrewSlotButtons),
                Write = (settings, value) => settings.CrewSlotButtons = bool.Parse(value),
            },
            new ModuleSetting
            {
                Key = "timeWarpPanelButton",
                Title = "Better Time Warp in the time panel",
                TakesEffect = ApplyWindow.NextScene,
                Order = 118,
                Row = SettingCategory.Gameplay,
                Section = "Flight",
                Control = SettingControl.Toggle,
                NeedsProfile = false,
                Tooltip = "With Better Time Warp installed, its button stands at the end of the time panel's warp row instead "
                          + "of where the mod draws it. From the next scene.",
                // Only with the mod, as its toolbar switch (SettingsWindow.ButtonRows).
                Present = () => ReDefinition.Flight.TimeWarpButton.Installed,
                Read = settings => SettingValues.Text(settings.TimeWarpPanelButton),
                Write = (settings, value) => settings.TimeWarpPanelButton = bool.Parse(value),
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

        // The heading the scaling rows stand under in their tab.
        private const string ScalingSection = "Scaling";

        // The main menu's scene (Menu/MenuFireworks).
        public static readonly IGraphicsModule MainMenuScene = new GraphicsModule("mainMenu", "Main menu",
            new ModuleSetting
            {
                Key = "menuFireworks",
                Title = "Fireworks",
                Order = 220,
                Row = SettingCategory.Interface,
                Section = "Main menu",
                Control = SettingControl.Toggle,
                NeedsProfile = false,
                Tooltip = "Small fireworks on Kerbin's night side behind the main menu.",
                Read = settings => SettingValues.Text(settings.MenuFireworks),
                Write = (settings, value) => settings.MenuFireworks = bool.Parse(value),
            },
            new ModuleSetting
            {
                Key = "menuKscZoom",
                Title = "Zoom to the space centre",
                Order = 221,
                Row = SettingCategory.Interface,
                Section = "Main menu",
                Control = SettingControl.Toggle,
                NeedsProfile = false,
                Tooltip = "From the main menu to the space centre as one camera flight: in to Kerbin, then down to\n"
                          + "the space centre. A click or a key ends it.",
                Read = settings => SettingValues.Text(settings.MenuKscZoom),
                Write = (settings, value) => settings.MenuKscZoom = bool.Parse(value),
            });

        // The space centre's look (Scenery/KscGrass).
        public static readonly IGraphicsModule SpaceCentre = new GraphicsModule("spaceCentre", "Space centre",
            new ModuleSetting
            {
                Key = "kscGrass",
                Title = "Grass on the lawns",
                Order = 230,
                Row = SettingCategory.Planets,
                Section = "Space centre",
                Control = SettingControl.Toggle,
                NeedsProfile = false,
                Tooltip = "Parallax's grass on the space centre's lawns, where Parallax itself grows it under them.\n"
                          + "Needs Parallax.",
                Interactable = settings => ReDefinition.Scenery.KscGrass.ParallaxInstalled,
                Read = settings => SettingValues.Text(settings.KscGrass),
                Write = (settings, value) => settings.KscGrass = bool.Parse(value),
            },
            new ModuleSetting
            {
                Key = "kscLawnTerrain",
                Title = "Lawns like the terrain",
                Order = 231,
                Row = SettingCategory.Planets,
                Section = "Space centre",
                Control = SettingControl.Toggle,
                NeedsProfile = false,
                Tooltip = "The space centre's lawns drawn with Parallax's terrain textures and colours, so that they\n"
                          + "look like the terrain beside them. Needs Parallax.",
                Interactable = settings => ReDefinition.Scenery.KscGrass.ParallaxInstalled,
                Read = settings => SettingValues.Text(settings.KscLawnTerrain),
                Write = (settings, value) => settings.KscLawnTerrain = bool.Parse(value),
            });

        // Firefly's re-entry effects (Scenery/FireflyIdle).
        public static readonly IGraphicsModule ReEntry = new GraphicsModule("reEntry", "Re-entry",
            new ModuleSetting
            {
                Key = "fireflyIdle",
                Title = "Skip unseen re-entry effects",
                Order = 240,
                Row = SettingCategory.Effects,
                Section = "Re-entry",
                Control = SettingControl.Toggle,
                NeedsProfile = false,
                Tooltip = "Firefly's re-entry effects are not drawn while they are too weak to be seen, as on the launch\n"
                          + "pad, which saves GPU time. They are drawn again as soon as they show.",
                // Only with the mod, as Better Time Warp's switch.
                Present = () => ReDefinition.Scenery.FireflyIdle.FireflyInstalled,
                Read = settings => SettingValues.Text(settings.FireflyIdle),
                Write = (settings, value) => settings.FireflyIdle = bool.Parse(value),
            });

        // The dashboard for phones, tablets and touch screens (Dashboard/DashboardServer): its server
        // listens on this computer always, on the network with this switch.
        public static readonly IGraphicsModule Dashboard = new GraphicsModule("dashboard", "Dashboard",
            new ModuleSetting
            {
                Key = "dashboardNetwork",
                Title = "Dashboard on other devices",
                Order = 250,
                Row = SettingCategory.Devices,
                Section = DashboardSection,
                Control = SettingControl.Toggle,
                NeedsProfile = false,
                Tooltip = "Phones and tablets in the same Wi-Fi may open the dashboard (Connect shows the QR code).\n"
                          + "On this computer, the iCUE widget for instance, it opens either way.",
                Read = settings => SettingValues.Text(settings.DashboardNetwork),
                Write = (settings, value) => settings.DashboardNetwork = bool.Parse(value),
            });

        // The heading the dashboard's rows stand under in the Devices tab.
        private const string DashboardSection = "Dashboard";

        // Every module, in the order their rows stand where Order ties; profiles find them here by name.
        public static readonly IList<IGraphicsModule> All =
            new List<IGraphicsModule>
                {
                    Upscaler, FrameGeneration, Latency, Raytracing, Hotkeys, FlightUi, Scaling, MainMenuScene, SpaceCentre, ReEntry,
                    Dashboard,
                }
                .AsReadOnly();

        // The rows that open a dialog, kept apart from the modules' settings (ModuleDialog).
        public static readonly IList<ModuleDialog> Dialogs =
            new List<ModuleDialog>
                {
                    new ModuleDialog
                    {
                        Key = "dashboard.connect",
                        Title = "Phone",
                        Row = SettingCategory.Devices,
                        Section = DashboardSection,
                        Button = "Connect",
                        Heading = "Connect a phone",
                        Tooltip = "Shows the QR code that opens the dashboard on a phone or tablet.",
                        Content = ReDefinition.Dashboard.ConnectDialog.Content,
                    },
                }
                .AsReadOnly();

        // The dialog rows that stand in that tab.
        public static List<ModuleDialog> DialogRows(SettingCategory tab)
        {
            List<ModuleDialog> rows = new List<ModuleDialog>();
            foreach (ModuleDialog dialog in Dialogs)
                if (dialog.Row == tab) rows.Add(dialog);
            return rows;
        }

        // A module by its name in a profile's MODULE node; null where there is none.
        public static IGraphicsModule Find(string name)
        {
            foreach (IGraphicsModule module in All)
                if (module.Name == name) return module;
            return null;
        }

        // Every module's settings under General in the order their rows stand; those
        // not present (ModuleSetting.Present) left out.
        public static List<ModuleSetting> Rows()
        {
            return Rows(SettingCategory.General);
        }

        // The rows of a tab that stand in Display (or the tab itself) or in Detail's group.
        public static List<ModuleSetting> Rows(SettingCategory tab, bool detail)
        {
            return Rows(tab).FindAll(setting => setting.Detail == detail);
        }

        // Every module's settings in that tab, the present ones only, sorted by Order; equal orders keep the modules'
        // order.
        public static List<ModuleSetting> Rows(SettingCategory tab)
        {
            List<ModuleSetting> rows = new List<ModuleSetting>();
            foreach (IGraphicsModule module in All)
                foreach (ModuleSetting setting in module.Settings)
                    if (setting.Row == tab && (setting.Present == null || setting.Present())) rows.Add(setting);
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

        // Auto, then the modes from UltraPerformance on the left to AA only on the right: they
        // run from NativeAA = 0 to UltraPerformance = 5, so the row shows them
        // mirrored.
        private static string[] ModesRightToLeft()
        {
            int last = (int)Fsr3Upscaler.QualityMode.UltraPerformance;
            string[] names = new string[last + 2];
            names[0] = AutoSettings.Auto;
            for (int i = 0; i <= last; i++) names[i + 1] = ((Fsr3Upscaler.QualityMode)(last - i)).ToString();
            return names;
        }

        // A DLSS model by its name, case aside.
        private static DlssModel DlssModelOf(string name)
        {
            return (DlssModel)System.Enum.Parse(typeof(DlssModel), name, true);
        }

        // An upscaler technique by its name, case aside.
        private static UpscalerBackend Backend(string name)
        {
            return (UpscalerBackend)System.Enum.Parse(typeof(UpscalerBackend), name, true);
        }

        // The frame generation row's value column: the technique, and for Auto what it makes of it; a
        // technique chosen that is not the one running yet says so.
        private static string FrameGenerationTechniqueLabel(string value)
        {
            if (value == FrameGenerationTechniqueSetting.Off) return value;
            FrameGenerationChoice.Limits limits = FrameGenerationBridge.Limits();
            // Auto names the technique it stands for here (the technique only: the multiplier row says how many
            // frames).
            string technique = AutoSettings.FrameGenerationTechnique(value, TechniqueAvailability.Current());
            int kind = technique == FrameGenerationTechniqueSetting.Fsr ? 1 : technique == FrameGenerationTechniqueSetting.Dlss ? 2 : 3;
            string name = FrameGenerationTechniqueSetting.Label(technique)
                          + (limits.Known && limits.Technique != kind ? ", after a restart" : "");
            return value == FrameGenerationTechniqueSetting.Auto ? "Auto (" + name + ")"
                : limits.Known && limits.Technique != kind ? FrameGenerationTechniqueSetting.Label(value) + " (after a restart)"
                : FrameGenerationTechniqueSetting.Label(value);
        }

        // Off, and the technique the proxy's swapchain was made for, take effect at once; another one when KSP
        // starts again (the window asks then whether to restart).
        private static ApplyWindow FrameGenerationTakesEffect(string after)
        {
            if (after == FrameGenerationTechniqueSetting.Off || !FrameGenerationBridge.Available) return ApplyWindow.Live;
            string technique = AutoSettings.FrameGenerationTechnique(after, TechniqueAvailability.Current());
            int kind = technique == FrameGenerationTechniqueSetting.Fsr ? 1 : technique == FrameGenerationTechniqueSetting.Dlss ? 2 : 3;
            return FrameGenerationBridge.Limits().Technique == kind ? ApplyWindow.Live : ApplyWindow.Restart;
        }

        // Auto with the technique that lowers the latency in brackets (ReflexFrames); any other value as it is.
        private static string LowLatencyLabel(string value)
        {
            if (value != LowLatencySetting.Auto) return value;
            string name = ReflexFrames.LowLatencyName();
            return name == null ? value : "Auto (" + name + ")";
        }

        // The technique's name; for AMD's and Intel's the version their DLL runs, e.g. "FSR 4.1.1", once it is known.
        internal static string TechniqueLabel(UpscalerBackend backend)
        {
            string running = backend == UpscalerBackend.Amd ? AmdUpscalerBridge.RunningName()
                : backend == UpscalerBackend.Xess ? XessUpscalerBridge.RunningName()
                : null;
            return running ?? UpscalerBackends.Name(backend);
        }

        // An upscaler mode by its name, case aside.
        private static Fsr3Upscaler.QualityMode Mode(string name)
        {
            return (Fsr3Upscaler.QualityMode)System.Enum.Parse(typeof(Fsr3Upscaler.QualityMode), name, true);
        }
    }
}
