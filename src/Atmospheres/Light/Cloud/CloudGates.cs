using System;

namespace ReDefinition.Atmospheres
{
    // The switches that decide whether the weather field exists and whether a cloud renderer draws, as functions of the settings (a delegate from a
    // key to its number: AtmospheresSettings.Number in the game, a fixture in a test). The modules call these functions, so a test that evaluates them
    // with the effective settings of a series start (the K-T2 gate audit) evaluates the real gate, not a copy. The rest of each gate (the scene, the
    // body's status, the field itself) is in the module and named in tests/ReDefinition.Tests/Atmospheres/T09/GateAuditTests.cs.
    internal static class CloudGates
    {
        private static bool On(Func<string, double> v, string key)
        {
            return v(key) != 0.0;
        }

        // GeneratorModule.Tick: the generator's field is put into WeatherHub.Field only while this is true. weatherEffects is off by default.
        internal static bool GeneratorInstalled(Func<string, double> v)
        {
            return On(v, AtmospheresSettings.Keys.Enabled) && On(v, AtmospheresSettings.Keys.WeatherEffects) && On(v, AtmospheresSettings.Keys.GenEnabled);
        }

        // EveCloudModule.Tick: the painter that feeds EVE's layers (TVC) works only while this is true.
        internal static bool EvePainterOn(Func<string, double> v)
        {
            return On(v, AtmospheresSettings.Keys.Enabled) && On(v, AtmospheresSettings.Keys.WeatherEffects);
        }

        // The settings half of OwnCloudModule's gate (OwnCloudPlan.Wanted): the mod is on and the own clouds are on with the source in force Ours.
        // eveBlocked is EveRenderCompat.Blocked (EVE cannot render: Eve and Both fall back to Ours).
        internal static bool OwnPassSwitches(Func<string, double> v, bool eveBlocked)
        {
            CloudSourceKind source = CloudModeModel.Effective((CloudSourceKind)(int)v(AtmospheresSettings.Keys.CloudSource), eveBlocked);
            return On(v, AtmospheresSettings.Keys.Enabled) && OwnCloudOptions.Active(v, source);
        }

        // The source in force for the settings.
        internal static CloudSourceKind EffectiveSource(Func<string, double> v, bool eveBlocked)
        {
            return CloudModeModel.Effective((CloudSourceKind)(int)v(AtmospheresSettings.Keys.CloudSource), eveBlocked);
        }

        // One line with the switches of the gates, for the log and the preflight.
        internal static string Describe(Func<string, double> v)
        {
            return "enabled " + v(AtmospheresSettings.Keys.Enabled).ToString("0") + ", weatherEffects " + v(AtmospheresSettings.Keys.WeatherEffects).ToString("0")
                   + ", genEnabled " + v(AtmospheresSettings.Keys.GenEnabled).ToString("0") + ", cloudSource " + (CloudSourceKind)(int)v(AtmospheresSettings.Keys.CloudSource)
                   + ", ownClouds " + v(AtmospheresSettings.Keys.OwnClouds).ToString("0");
        }
    }
}
