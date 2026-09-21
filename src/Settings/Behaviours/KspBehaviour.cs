using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Settings.Behaviours
{
    // KSP's own settings (KSP 1.12.5, decompiled) -- what their registration
    // cannot say in data.
    //
    // They are static fields of GameSettings. KSP hands the quality ones to Unity
    // in GameSettings.ApplySettings -- the quality level, the texture mipmap limit,
    // V-Sync, the pixel light count, the shadow cascades -- and the frame limit
    // through Application.targetFrameRate. The others are read where they matter:
    // the terrain shader quality and the terrain detail preset when a planet's
    // surface is built, terrain scatter likewise, surface FX when a part's module
    // starts, aerodynamic FX in the FX camera, the reflection probe on
    // OnGameSettingsApplied. A value set here is saved as KSP's own settings
    // screens save it -- GameSettings.SaveSettings, the registration's `save`.
    //
    // Here: what follows a change (FollowUp), the terrain detail preset, the
    // screen resolution, the two bits of the temperature gauges' mode, Making
    // History's settings only with the expansion, and what another mod holds for
    // itself -- the reflection refresh while Deferred caps it, the terrain shader
    // quality while Kopernicus enforces or warns about a level.
    //
    // The engine settings take effect as GameSettings.ApplySettings sets them:
    // the physics step limit, running in the background, the UI scale, the
    // master volume, the highlighting, and the resolution where it differs from
    // the screen's. Antialiasing is kept for the reset only: while the upscaler runs it
    // switches MSAA off again whenever KSP's settings are applied
    // (ReDefinitionAddon.OnGameSettingsApplied), which FollowUp fires after every
    // change here.
    internal sealed class KspBehaviour : ModBehaviour
    {
        private enum Follow
        {
            // Nothing to do now: read where it is used, at the next scene or start.
            None,
            // Unity's quality settings from GameSettings, then the event.
            Quality,
            // OnGameSettingsApplied alone: the reflection probe and terrain
            // scatter's shadows listen to it.
            Event,
            // The engine settings of GameSettings.ApplySettings, then the event.
            Engine,
        }

        private static readonly string[] QualityFollow =
        {
            "QUALITY_PRESET", "TEXTURE_QUALITY", "LIGHT_QUALITY", "SYNC_VBL", "FRAMERATE_LIMIT", "SHADOWS_QUALITY",
            "ANTI_ALIASING",
        };

        private static readonly string[] NoFollow =
        {
            "terrainDetail", "TERRAIN_SHADER_QUALITY", "PLANET_SCATTER", "PLANET_SCATTER_FACTOR", "AERO_FX_QUALITY",
            "SURFACE_FX",
        };

        private static readonly string[] EngineFollow =
        {
            "PHYSICS_FRAME_DT_LIMIT", "SIMULATE_IN_BACKGROUND", "UI_SCALE", "MASTER_VOLUME", "HIGHLIGHT_FX",
            "screenResolution", "FULLSCREEN",
        };

        // Making History's settings, which KSP's own screen shows only with the
        // expansion installed (SettingsExpansion).
        private static readonly string[] MakingHistory =
        {
            "MISSION_GAP_CAMERA_VAB_CONTROLS", "MISSION_MINIMUM_CANVAS_ZOOM",
        };

        // Whether the end-of-frame follow-up sets Unity's quality, or the engine
        // settings, again besides firing the event.
        private static bool qualityDue;
        private static bool engineDue;

        // KSP.cfg's REQUIRES on V-Sync while DLSS frame generation runs: every
        // refresh at most -- "SyncInterval > 1: Not supported" -- and none where its
        // build does not support V-Sync -- "hide or disable VSync toggle"
        // (ProgrammingGuideDLSS_G.md 22.1, 22.2). The proxy holds the same at
        // Present, until a value set in KSP's own screen is put right.
        private const string DlssEveryRefreshCheck = "DlssFrameGenerationEveryRefresh";
        private const string DlssVsyncCheck = "DlssFrameGenerationVSync";

        // Set by the upscaler's add-on in the game: whether DLSS frame generation
        // presents ReDefinition's frame generation now, and whether its build
        // presents with V-Sync. Unset outside the game, where every value passes.
        // KSP's settings applied, with the id of KSP's registration: the settings
        // window reads them all at its next sync (SettingsWindow.FollowModSoon).
        internal static Action<string> SettingsApplied;

        internal static Func<bool> DlssFrameGenerationRuns;
        internal static Func<bool> DlssFrameGenerationVsync;

        private bool deferred;
        private bool capRefresh;
        private bool capResolution;
        private int kopernicusLevel = -1;

        public override bool Attach(RegisteredMod mod)
        {
            deferred = TypeLookup.Find("Deferred.Deferred") != null;
            if (deferred) DeferredCaps(out capRefresh, out capResolution);
            kopernicusLevel = KopernicusShaderLevel();
            return true;
        }

        // Versioning asks the game's own object, which is there only in the game.
        public override string Version(RegisteredMod mod)
        {
            try
            {
                return Versioning.version_major + "." + Versioning.version_minor + "." + Versioning.Revision;
            }
            catch (Exception)
            {
                return "";
            }
        }

        // KSP's own settings screen is no window the settings window's sync sees
        // open: all of KSP's settings are read at its next tick once they are
        // applied.
        public override void InstallHooks(RegisteredMod mod)
        {
            string id = mod.Id;
            GameEvents.OnGameSettingsApplied.Add(() =>
            {
                Action<string> follow = SettingsApplied;
                if (follow != null) follow(id);
            });
        }

        public override bool Reach(RegisteredMod mod, SettingRegistration setting, out Func<string> read,
                                   out Action<string> write, out Type type)
        {
            read = null;
            write = null;
            type = null;
            switch (setting.Name)
            {
                case "terrainDetail":
                    // KSP's terrain detail: a preset by name in PQSCache's list. Kopernicus
                    // copies the live preset into its own config as the game quits, and
                    // says so in the main menu when it changed: the player's choice, like
                    // one made in KSP's own screen.
                    read = () => PQSCache.PresetList != null ? PQSCache.PresetList.preset : null;
                    write = text =>
                    {
                        if (PQSCache.PresetList != null) KspPresets.Select(text);
                    };
                    type = typeof(string);
                    return true;

                // KSP keeps the resolution in two fields; the row chooses both.
                case "screenResolution":
                    read = () => ResolutionText(GameSettings.SCREEN_RESOLUTION_WIDTH, GameSettings.SCREEN_RESOLUTION_HEIGHT);
                    write = text =>
                    {
                        int width;
                        int height;
                        if (!ParseResolution(text, out width, out height))
                            throw new ArgumentException("'" + text + "' is no resolution.");
                        GameSettings.SCREEN_RESOLUTION_WIDTH = width;
                        GameSettings.SCREEN_RESOLUTION_HEIGHT = height;
                    };
                    type = typeof(string);
                    return true;

                // Two switches in one int, as KSP's own screen splits it
                // (GameplaySettingsScreen): 1 the gauges, 2 the thermal highlights.
                case "temperatureGauges":
                    read = () => Bit(1);
                    write = text => SetBit(1, text);
                    type = typeof(bool);
                    return true;
                case "thermalHighlights":
                    read = () => Bit(2);
                    write = text => SetBit(2, text);
                    type = typeof(bool);
                    return true;

                case "TERRAIN_SHADER_QUALITY":
                    if (kopernicusLevel < 0) return false;
                    mod.Drop(setting.Name, "Kopernicus holds it at level " + kopernicusLevel
                                                 + " (its EnforceShaders or WarnShaders)");
                    return true;

                // Deferred sets the refresh to Low at every scene load where it is
                // off, and where it is higher with its cap on; and the resolution
                // down to 256 where it is higher with its cap on (HandleStockProbe).
                // A value beyond that would not stand past the next scene load, so it
                // is not offered.
                case "REFLECTION_PROBE_REFRESH_MODE":
                    if (!capRefresh) return false;
                    mod.Drop(setting.Name, "Deferred holds it at Low (its capReflectionProbeRefreshRate)");
                    return true;

                default:
                    if (Array.IndexOf(MakingHistory, setting.Name) < 0 || MakingHistoryInstalled()) return false;
                    mod.Drop(setting.Name, "Making History is not installed");
                    return true;
            }
        }

        private static bool MakingHistoryInstalled()
        {
            try
            {
                return Expansions.ExpansionsLoader.IsExpansionInstalled("MakingHistory");
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Bit(int bit)
        {
            return (GameSettings.TEMPERATURE_GAUGES_MODE & bit) != 0 ? "True" : "False";
        }

        private static void SetBit(int bit, string text)
        {
            bool on;
            if (!bool.TryParse(text, out on)) throw new ArgumentException("'" + text + "' is no switch.");
            GameSettings.TEMPERATURE_GAUGES_MODE = on
                ? GameSettings.TEMPERATURE_GAUGES_MODE | bit
                : GameSettings.TEMPERATURE_GAUGES_MODE & ~bit;
        }

        internal static string ResolutionText(int width, int height)
        {
            return width.ToString(CultureInfo.InvariantCulture) + " x " + height.ToString(CultureInfo.InvariantCulture);
        }

        internal static bool ParseResolution(string text, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Split('x', 'X');
            return parts.Length == 2
                   && int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out width)
                   && int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out height)
                   && width > 0 && height > 0;
        }

        // The resolutions this monitor offers, each once, smallest first, with
        // the one KSP holds among them -- a window dragged to a size of its own.
        private static string[] Resolutions()
        {
            List<string> list = new List<string>();
            List<KeyValuePair<int, int>> sizes = new List<KeyValuePair<int, int>>();
            try
            {
                foreach (Resolution resolution in Screen.resolutions)
                    sizes.Add(new KeyValuePair<int, int>(resolution.width, resolution.height));
            }
            catch (Exception)
            {
                // Outside the game: only what KSP holds.
            }
            sizes.Add(new KeyValuePair<int, int>(GameSettings.SCREEN_RESOLUTION_WIDTH, GameSettings.SCREEN_RESOLUTION_HEIGHT));
            sizes.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Value.CompareTo(b.Value));
            foreach (KeyValuePair<int, int> size in sizes)
            {
                if (size.Key <= 0 || size.Value <= 0) continue;
                string text = ResolutionText(size.Key, size.Value);
                if (!list.Contains(text)) list.Add(text);
            }
            return list.ToArray();
        }

        public override bool? Check(RegisteredMod mod, string check, string value)
        {
            if (check != DlssEveryRefreshCheck && check != DlssVsyncCheck) return null;
            int interval;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out interval)) return null;
            Func<bool> runs = DlssFrameGenerationRuns;
            if (runs == null || !runs()) return true;
            if (check == DlssEveryRefreshCheck) return interval <= 1;
            Func<bool> vsync = DlssFrameGenerationVsync;
            return interval == 0 || vsync == null || vsync();
        }

        public override bool Provides(string check)
        {
            return check == DlssEveryRefreshCheck || check == DlssVsyncCheck;
        }

        public override void Finish(RegisteredMod mod, BundledSetting setting, SettingRegistration registration)
        {
            switch (registration.Name)
            {
                case "terrainDetail":
                    setting.Control = SettingControl.Choice;
                    setting.ValueType = null;
                    setting.ChoicesSource = KspPresets.Names;
                    break;
                case "screenResolution":
                    setting.Control = SettingControl.Choice;
                    setting.ValueType = null;
                    setting.ChoicesSource = Resolutions;
                    break;
                case "REFLECTION_PROBE_REFRESH_MODE":
                    if (!deferred) break;
                    setting.Tooltip += " Off is not offered: Deferred raises it to Low.";
                    setting.Choices = new[] { "1", "2", "3" };
                    setting.ChoiceLabels = new[] { "Low", "Medium", "Every frame" };
                    break;
                case "REFLECTION_PROBE_TEXTURE_RESOLUTION":
                    if (!capResolution) break;
                    setting.Tooltip += " Deferred holds it at 256 at most.";
                    setting.Choices = new[] { "0", "1" };
                    setting.ChoiceLabels = new[] { "128", "256" };
                    break;
            }

            Follow follow = Array.IndexOf(QualityFollow, registration.Name) >= 0 ? Follow.Quality
                : Array.IndexOf(EngineFollow, registration.Name) >= 0 ? Follow.Engine
                : Array.IndexOf(NoFollow, registration.Name) >= 0 ? Follow.None : Follow.Event;
            if (follow == Follow.None) return;
            Action<string> write = setting.Write;
            setting.Write = text =>
            {
                write(text);
                FollowUp(follow);
            };
        }

        // Deferred's two caps as its loader reads them -- the first Deferred_config
        // node of a Deferred config (Settings.LoadSettings) -- each on where it
        // cannot be read: its own default, and outside the game.
        private static void DeferredCaps(out bool refresh, out bool resolution)
        {
            refresh = true;
            resolution = true;
            try
            {
                ConfigNode node = DeferredConfig();
                if (node == null) return;
                bool value;
                if (bool.TryParse(node.GetValue("capReflectionProbeRefreshRate"), out value)) refresh = value;
                if (bool.TryParse(node.GetValue("capReflectionProbeResolution"), out value)) resolution = value;
            }
            catch (Exception)
            {
                // Its default stands.
            }
        }

        // Apart, so that a GameDatabase missing outside the game is an exception
        // the caller catches, not one thrown as the caller is compiled.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ConfigNode DeferredConfig()
        {
            if (GameDatabase.Instance == null) return null;
            foreach (UrlDir.UrlConfig url in GameDatabase.Instance.GetConfigs("Deferred"))
            {
                ConfigNode[] nodes = url.config.GetNodes("Deferred_config");
                if (nodes.Length > 0) return nodes[0];
            }
            return null;
        }

        // Kopernicus sets the terrain shader quality back to its level in every
        // frame, and saves KSP's settings as it does, where its config says
        // EnforceShaders; and posts a warning in every frame outside a game where
        // it says WarnShaders (TerrainQualitySetter, from its source). Its settings
        // window switches neither, so they are read once. The level, or -1 where it
        // holds none or cannot be asked.
        private static int KopernicusShaderLevel()
        {
            try
            {
                Type utility = TypeLookup.Find("Kopernicus.RuntimeUtility.RuntimeUtility");
                if (utility == null) return -1;
                const BindingFlags Statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                FieldInfo field = utility.GetField("KopernicusConfig", Statics);
                PropertyInfo property = field == null ? utility.GetProperty("KopernicusConfig", Statics) : null;
                object config = field != null ? field.GetValue(null) : property != null ? property.GetValue(null, null) : null;
                if (config == null) return -1;
                FieldInfo enforce = config.GetType().GetField("EnforceShaders", TypeLookup.Any);
                FieldInfo warn = config.GetType().GetField("WarnShaders", TypeLookup.Any);
                FieldInfo level = config.GetType().GetField("EnforcedShaderLevel", TypeLookup.Any);
                if (level == null) return -1;
                bool holds = (enforce != null && (bool)enforce.GetValue(config))
                             || (warn != null && (bool)warn.GetValue(config));
                return holds ? (int)level.GetValue(config) : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        // As KSP's own settings screens finish (SettingsScreen, MiniSettings,
        // decompiled): Unity's quality settings from GameSettings, then
        // OnGameSettingsApplied -- the event the reflection probe, terrain scatter,
        // the FX camera and this mod's own quality overrides listen to.
        //
        // Once at the end of the frame however many settings change, with one event:
        // every listener runs once -- CommNet builds its network anew on it. The
        // event fires even where the quality part throws, since it is what puts this
        // mod's quality overrides back.
        private static void FollowUp(Follow follow)
        {
            if (follow == Follow.None) return;
            if (follow == Follow.Quality) qualityDue = true;
            if (follow == Follow.Engine) engineDue = true;
            BundledSettingsAddon.AtEndOfFrame("ksp-settings-applied", () =>
            {
                bool quality = qualityDue;
                bool engine = engineDue;
                qualityDue = false;
                engineDue = false;
                try
                {
                    if (quality) ApplyQuality();
                }
                catch (Exception e)
                {
                    Debug.LogWarning(Log.Tag + " KSP's quality settings could not be applied: " + e);
                }
                try
                {
                    if (engine) ApplyEngine();
                }
                catch (Exception e)
                {
                    Debug.LogWarning(Log.Tag + " KSP's engine settings could not be applied: " + e);
                }
                finally
                {
                    GameEvents.OnGameSettingsApplied.Fire();
                }
            });
        }

        // As GameSettings.ApplySettings and KSP's start (ApplyEngineSettings) set
        // them. The resolution only where it differs from the screen's, as there:
        // a change resizes the swapchain.
        private static void ApplyEngine()
        {
            Time.maximumDeltaTime = GameSettings.PHYSICS_FRAME_DT_LIMIT;
            Application.runInBackground = GameSettings.SIMULATE_IN_BACKGROUND;
            AudioListener.volume = GameSettings.MASTER_VOLUME;
            Highlighting.HighlightingSystem.FxEnabled = GameSettings.HIGHLIGHT_FX;
            if (KSP.UI.UIMasterController.Instance != null)
            {
                KSP.UI.UIMasterController.Instance.SetScale(GameSettings.UI_SCALE);
                KSP.UI.UIMasterController.Instance.SetAppScale(GameSettings.UI_SCALE_APPS * GameSettings.UI_SCALE);
            }
            if (Screen.width != GameSettings.SCREEN_RESOLUTION_WIDTH || Screen.height != GameSettings.SCREEN_RESOLUTION_HEIGHT
                || Screen.fullScreen != GameSettings.FULLSCREEN)
            {
                Debug.Log(Log.Tag + " Screen resolution set to " + ResolutionText(GameSettings.SCREEN_RESOLUTION_WIDTH,
                    GameSettings.SCREEN_RESOLUTION_HEIGHT) + (GameSettings.FULLSCREEN ? ", full screen." : ", windowed."));
                Screen.SetResolution(GameSettings.SCREEN_RESOLUTION_WIDTH, GameSettings.SCREEN_RESOLUTION_HEIGHT,
                    GameSettings.FULLSCREEN);
            }
        }

        private static void ApplyQuality()
        {
            QualitySettings.SetQualityLevel(GameSettings.QUALITY_PRESET, true);
            QualitySettings.antiAliasing = GameSettings.ANTI_ALIASING;
            QualitySettings.masterTextureLimit = GameSettings.TEXTURE_QUALITY;
            QualitySettings.vSyncCount = GameSettings.SYNC_VBL;
            QualitySettings.pixelLightCount = GameSettings.LIGHT_QUALITY;
            QualitySettings.shadowCascades = GameSettings.SHADOWS_QUALITY;
            Application.targetFrameRate = GameSettings.FRAMERATE_LIMIT;
        }
    }

    // KSP's terrain detail presets, by name: Low, Default and High unless something
    // has added more.
    internal static class KspPresets
    {
        // Where the first preset of that name stands -- the one a name selects, as
        // Parallax's requirement judges it by position among the names -- or -1.
        internal static int Index(string name)
        {
            PQSCache.PQSGlobalPresetList list = PQSCache.PresetList;
            if (list == null || list.presets == null) return -1;
            for (int i = 0; i < list.presets.Count; i++)
                if (list.presets[i] != null && list.presets[i].name == name) return i;
            return -1;
        }

        internal static string[] Names()
        {
            List<string> names = new List<string>();
            if (PQSCache.PresetList != null && PQSCache.PresetList.presets != null)
            {
                foreach (PQSCache.PQSPreset preset in PQSCache.PresetList.presets)
                    if (preset != null && !string.IsNullOrEmpty(preset.name)) names.Add(preset.name);
            }
            return names.ToArray();
        }

        // By its index: SetPreset(string) in KSP 1.12.5 compares the current name
        // rather than the one asked for and never moves presetIndex, which KSP's
        // settings screens read and write back.
        internal static void Select(string name)
        {
            PQSCache.PQSGlobalPresetList list = PQSCache.PresetList;
            if (list == null || list.presets == null) return;
            int index = Index(name);
            if (index < 0) throw new ArgumentException("KSP has no terrain detail preset named '" + name + "'.");
            list.SetPreset(index);
        }
    }
}
