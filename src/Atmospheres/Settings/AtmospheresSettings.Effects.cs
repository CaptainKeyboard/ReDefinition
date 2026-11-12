using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // The settings of the weather effects of stream T that are not clouds: the wet surfaces (T08), the window droplets (T08) and the lightning
    // (T07). Each is off until the owner agrees to its look and its cost (wetness darkens every lit surface through the G-buffer, the droplets draw
    // a pass on the camera, lightning adds lights and a mesh per flash). The keys are declared here (Keys is partial), the rows are appended to the
    // table by SettingsTable, and the typed readers follow. The renderers read the typed readers; LightningSwitches.Apply writes the lightning keys
    // into its fields.
    internal static partial class AtmospheresSettings
    {
        internal static partial class Keys
        {
            // Wet surfaces and window droplets (T08)
            internal const string WetSurfaces = "wetSurfaces";
            internal const string WindowDroplets = "windowDroplets";
            // Lightning (T07)
            internal const string Lightning = "lightning";
            internal const string LightningMaxPerSecond = "lightningMaxPerSecond";
            internal const string LightningBrightness = "lightningBrightness";
            internal const string LightningBolts = "lightningBolts";
            internal const string LightningTerrainLight = "lightningTerrainLight";
            internal const string LightningCloudFlash = "lightningCloudFlash";
            internal const string LightningThunder = "lightningThunder";
            internal const string LightningRealisticDelay = "lightningRealisticDelay";
        }

        internal static bool WetSurfaces => B(Keys.WetSurfaces);
        internal static bool WindowDroplets => B(Keys.WindowDroplets);

        internal static bool Lightning => B(Keys.Lightning);
        internal static int LightningMaxPerSecond => I(Keys.LightningMaxPerSecond);
        internal static float LightningBrightness => F(Keys.LightningBrightness);
        internal static bool LightningBolts => B(Keys.LightningBolts);
        internal static bool LightningTerrainLight => B(Keys.LightningTerrainLight);
        internal static bool LightningCloudFlash => B(Keys.LightningCloudFlash);
        internal static bool LightningThunder => B(Keys.LightningThunder);
        internal static bool LightningRealisticDelay => B(Keys.LightningRealisticDelay);
    }

    // The rows of the effects in the key table. The wet surfaces stand with the cover, the droplets and the lightning with the light. None is in a
    // quality profile: each is a switch whose default waits for the owner's agreement.
    internal static class EffectSettingRows
    {
        internal static IEnumerable<SettingDef> Rows()
        {
            const string C = AtmospheresSettings.Categories.Cover;
            const string L = AtmospheresSettings.Categories.Light;
            const string LIGHTNING = AtmospheresSettings.GreyReasons.NeedsLightning;

            return new[]
            {
                SettingDef.Bool("wetSurfaces", false, C, "Wet Surfaces"),
                SettingDef.Bool("windowDroplets", false, L, "Window Droplets"),

                SettingDef.Bool("lightning", false, L, "Lightning"),
                SettingDef.Int("lightningMaxPerSecond", 3, 1, 3, L, "Lightning Strokes Per Second", LIGHTNING),
                SettingDef.Float("lightningBrightness", 1.0, 0.0, 1.0, L, "Lightning Brightness", LIGHTNING),
                SettingDef.Bool("lightningBolts", true, L, "Lightning Bolts", LIGHTNING),
                SettingDef.Bool("lightningTerrainLight", true, L, "Lightning Ground Light", LIGHTNING),
                SettingDef.Bool("lightningCloudFlash", true, L, "Lightning Cloud Flash", LIGHTNING),
                SettingDef.Bool("lightningThunder", true, L, "Lightning Thunder", LIGHTNING),
                SettingDef.Bool("lightningRealisticDelay", true, L, "Lightning Thunder Delay", LIGHTNING)
            };
        }
    }
}
