using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // What a row of the window shows: whether it can be changed, and if not, why. A greyed-out row keeps its tooltip, and the tooltip
    // names the reason (KSP shows none over a disabled button, so the window greys the text and keeps the control answering).
    internal sealed class RowState
    {
        internal static readonly RowState Open = new RowState(true, "", "");

        internal RowState(bool enabled, string reasonId, string note)
        {
            Enabled = enabled;
            ReasonId = reasonId ?? "";
            Note = note ?? "";
        }

        // False: the control does not take a change now.
        internal bool Enabled { get; }
        // The id of the reason (a GreyReasons id or one of SettingHelp's), empty where enabled.
        internal string ReasonId { get; }
        // A line about the value now in force that the row shows without being greyed (a choice that falls back), or empty.
        internal string Note { get; }

        internal string Reason
        {
            get { return ReasonId.Length == 0 ? "" : SettingHelp.ReasonText(ReasonId); }
        }
    }

    // The rules that grey a row or a choice out. Every rule is a pure function of the setting values and the environment. The order is
    // root first: a mod that is off, then the master switch, then the prerequisite of the row itself, so that the reason shown is the
    // first thing the player has to switch on.
    internal static class Dependencies
    {
        // The state of one row.
        internal static RowState Evaluate(SettingDef def, Func<string, double> value, WindowEnvironment env)
        {
            string key = def.Key;
            if (key != AtmospheresSettings.Keys.Enabled && value(AtmospheresSettings.Keys.Enabled) == 0.0)
                return new RowState(false, SettingHelp.ModOff, "");
            bool general = def.Category == AtmospheresSettings.Categories.General;
            if (!general && value(AtmospheresSettings.Keys.WeatherEffects) == 0.0)
                return new RowState(false, AtmospheresSettings.GreyReasons.NeedsMaster, "");

            string own = OwnReason(def, value, env);
            if (own != null) return new RowState(false, own, "");

            string note = NoteFor(def, value, env);
            return note.Length == 0 ? RowState.Open : new RowState(true, "", note);
        }

        // The reason of the row's own prerequisite, or null. The chain of a prerequisite is checked from its root.
        private static string OwnReason(SettingDef def, Func<string, double> value, WindowEnvironment env)
        {
            switch (def.GreyReason)
            {
                case AtmospheresSettings.GreyReasons.NeedsEve:
                    return env.EveInstalled ? null : def.GreyReason;
                case AtmospheresSettings.GreyReasons.NeedsHost:
                    return env.HostApi ? null : def.GreyReason;
                case AtmospheresSettings.GreyReasons.NeedsSim:
                    return value(AtmospheresSettings.Keys.GenEnabled) != 0.0 ? null : def.GreyReason;
                case AtmospheresSettings.GreyReasons.NeedsVolume:
                    return value(AtmospheresSettings.Keys.VolumePass) != 0.0 ? null : def.GreyReason;
                case AtmospheresSettings.GreyReasons.NeedsAudio:
                    if (value(AtmospheresSettings.Keys.WeatherAudio) == 0.0) return def.GreyReason;
                    // eveSound chooses between EVE's loop and ours: of no use without EVE.
                    if (def.Key == AtmospheresSettings.Keys.EveSound && !env.EveInstalled) return AtmospheresSettings.GreyReasons.NeedsEve;
                    return null;
                case AtmospheresSettings.GreyReasons.NeedsForecast:
                    return value(AtmospheresSettings.Keys.ForecastEnabled) != 0.0 ? null : def.GreyReason;
                case AtmospheresSettings.GreyReasons.NeedsAlerts:
                    // Alerts hang on the forecast: the root is named first.
                    if (value(AtmospheresSettings.Keys.ForecastEnabled) == 0.0) return AtmospheresSettings.GreyReasons.NeedsForecast;
                    if (def.Key == AtmospheresSettings.Keys.AlertKac && !env.KerbalAlarmClock && value(AtmospheresSettings.Keys.AlertsEnabled) != 0.0)
                        return SettingHelp.NeedsKac;
                    return value(AtmospheresSettings.Keys.AlertsEnabled) != 0.0 ? null : def.GreyReason;
                case AtmospheresSettings.GreyReasons.NeedsWindForces:
                    return value(AtmospheresSettings.Keys.WindForces) != 0.0 ? null : def.GreyReason;
                case AtmospheresSettings.GreyReasons.NeedsGroundCover:
                    if (value(AtmospheresSettings.Keys.GroundCover) == 0.0) return def.GreyReason;
                    if ((def.Key == AtmospheresSettings.Keys.VehicleCover || def.Key == AtmospheresSettings.Keys.VehicleCoverMethod) && !env.ShellShaders) return SettingHelp.NeedsShellShaders;
                    return null;
                case AtmospheresSettings.GreyReasons.NeedsSeaIce:
                    return value(AtmospheresSettings.Keys.SeaIce) != (double)(int)SeaIceMode.Off ? null : def.GreyReason;
                case AtmospheresSettings.GreyReasons.NeedsLightInMedium:
                    return value(AtmospheresSettings.Keys.LightInMedium) != 0.0 ? null : def.GreyReason;
                case AtmospheresSettings.GreyReasons.NeedsPrecip:
                    return value(AtmospheresSettings.Keys.PrecipDensity) > 0.0 ? null : def.GreyReason;
                case AtmospheresSettings.GreyReasons.NeedsSolarDimming:
                    return value(AtmospheresSettings.Keys.SolarDimming) != 0.0 ? null : def.GreyReason;
                case AtmospheresSettings.GreyReasons.NeedsLightning:
                    return value(AtmospheresSettings.Keys.Lightning) != 0.0 ? null : def.GreyReason;
                case AtmospheresSettings.GreyReasons.NeedsOwnSource:
                case AtmospheresSettings.GreyReasons.NeedsOwnClouds:
                case AtmospheresSettings.GreyReasons.NeedsCloudNoise:
                case AtmospheresSettings.GreyReasons.NeedsCloudShadows:
                case AtmospheresSettings.GreyReasons.NeedsAmbientTaps:
                case AtmospheresSettings.GreyReasons.NeedsEntryRefinement:
                    return OwnCloudReason(def, value, env);
                default:
                    return null;
            }
        }

        // The own cloud system's rows (package T10), root first: the system draws only where the source in force is Ours (EVE missing turns Eve and
        // Both into Ours, CloudModeModel.Effective), then ownClouds itself, then the branch switch the row hangs on.
        private static string OwnCloudReason(SettingDef def, Func<string, double> value, WindowEnvironment env)
        {
            var source = (CloudSourceKind)(int)value(AtmospheresSettings.Keys.CloudSource);
            bool ours = CloudModeModel.Effective(source, !env.EveInstalled) == CloudSourceKind.Ours;
            if (!ours) return AtmospheresSettings.GreyReasons.NeedsOwnSource;
            if (def.GreyReason == AtmospheresSettings.GreyReasons.NeedsOwnSource) return null;
            if (value(AtmospheresSettings.Keys.OwnClouds) == 0.0) return AtmospheresSettings.GreyReasons.NeedsOwnClouds;
            switch (def.GreyReason)
            {
                case AtmospheresSettings.GreyReasons.NeedsCloudNoise:
                    return value(AtmospheresSettings.Keys.CloudNoise) != 0.0 ? null : def.GreyReason;
                case AtmospheresSettings.GreyReasons.NeedsCloudShadows:
                    return value(AtmospheresSettings.Keys.CloudShadows) != 0.0 ? null : def.GreyReason;
                case AtmospheresSettings.GreyReasons.NeedsAmbientTaps:
                    return value(AtmospheresSettings.Keys.CloudAmbientTaps) != 0.0 ? null : def.GreyReason;
                case AtmospheresSettings.GreyReasons.NeedsEntryRefinement:
                    return value(AtmospheresSettings.Keys.CloudEntryRefinement) != 0.0 ? null : def.GreyReason;
                default:
                    return null;
            }
        }

        // Why a choice of a row is not offered here, or null. The row stays open and offers its other choices.
        internal static string ChoiceUnavailable(SettingDef def, int index, WindowEnvironment env)
        {
            if (def.Kind != SettingKind.Choice || index < 0 || index >= def.Choices.Length) return null;
            switch (def.Key)
            {
                case AtmospheresSettings.Keys.CloudSource:
                    // Eve and Both draw through EVE.
                    return (index == (int)CloudSourceKind.Eve || index == (int)CloudSourceKind.Both) && !env.EveInstalled
                        ? AtmospheresSettings.GreyReasons.NeedsEve : null;
                case AtmospheresSettings.Keys.WindInjection:
                    return index == (int)WindInjection.Aero && !env.ModularFlightIntegrator ? SettingHelp.NeedsModularFlightIntegrator : null;
                case AtmospheresSettings.Keys.AudioMuffling:
                    return index == (int)AudioMuffling.Rse && !env.RocketSoundEnhancement ? SettingHelp.NeedsRse : null;
                case AtmospheresSettings.Keys.WeatherCompose:
                    return index == (int)WeatherCompose.Host && !env.HostApi ? SettingHelp.NeedsHostForCompose : null;
                case AtmospheresSettings.Keys.VolumeCompose:
                    return index == (int)VolumeCompose.PostUpscale && !env.HostApi ? SettingHelp.NeedsHostForCompose : null;
                case AtmospheresSettings.Keys.Aurora:
                    return index == (int)AuroraMode.Volumetric && !env.EveInstalled ? AtmospheresSettings.GreyReasons.NeedsEve : null;
                case AtmospheresSettings.Keys.EveSound:
                    return index != (int)EveSoundPolicy.Auto && index != (int)EveSoundPolicy.Ours && !env.EveInstalled
                        ? AtmospheresSettings.GreyReasons.NeedsEve : null;
                case AtmospheresSettings.Keys.WindSource:
                    // Foreign means a wind mod's wind: with none installed there is nothing to hand the wind to.
                    return index == (int)WindSourceMode.Foreign && env.ForeignWindOwner.Length == 0 ? SettingHelp.NeedsForeignWind : null;
                default:
                    return null;
            }
        }

        // A line about what is in force where the chosen value cannot run here, so that the window never shows a choice that is not
        // the truth; empty where the value runs as chosen.
        private static string NoteFor(SettingDef def, Func<string, double> value, WindowEnvironment env)
        {
            if (def.Kind != SettingKind.Choice) return "";
            int index = (int)value(def.Key);
            string why = ChoiceUnavailable(def, index, env);
            switch (def.Key)
            {
                case AtmospheresSettings.Keys.CloudSource:
                    return why != null ? "EVE is not installed: the clouds come from the mod's own volume pass." : "";
                case AtmospheresSettings.Keys.WindInjection:
                    return why != null ? "ModularFlightIntegrator is not installed: the wind pushes the parts as a force." : "";
                case AtmospheresSettings.Keys.AudioMuffling:
                    return why != null ? "Rocket Sound Enhancement is not installed: the mod muffles the sound itself." : "";
                case AtmospheresSettings.Keys.WeatherCompose:
                    if (why != null) return "The main mod does not answer: the weather is drawn in the scene.";
                    return index == (int)WeatherCompose.Auto ? ComposeEvidence.AutoLine(env.GpuVendor, env.Upscaler, env.HostApi) : "";
                case AtmospheresSettings.Keys.VolumeCompose:
                    return why != null ? "The main mod does not answer: the volume is composed in the scene." : "";
                case AtmospheresSettings.Keys.Aurora:
                    return why != null ? "EVE is not installed: the aurora is drawn as a shell." : "";
                case AtmospheresSettings.Keys.WindSource:
                    return why != null ? "No other wind mod is installed: the wind is ours." : "";
                default:
                    return "";
            }
        }
    }
}
