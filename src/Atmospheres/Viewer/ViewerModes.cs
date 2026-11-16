using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres.Viewer
{
    // One cloud mode or quality switch the viewer offers: a setting of the Clouds group, when a change of it shows (read from the code that reads it, Where says which), and a rule that
    // greys it with the reason.
    internal sealed class ViewerMode
    {
        internal ViewerMode(string key, KeyMarker marker, string where)
        {
            Key = key;
            Marker = marker;
            Where = where;
        }

        internal string Key { get; }
        internal KeyMarker Marker { get; }
        // The reader that decides the marker, shown in the tooltip.
        internal string Where { get; }
    }

    // The cloud modes (ownClouds, cloudSource, layerCap, rtOwnClouds, the ray layer cap) and the quality switches the viewer lists in its Modes group. Each carries the marker the code
    // gives it; the list is the one place a mode is added (a test checks that every key is a setting of the Clouds group and that none is left unchecked).
    internal static class ViewerModes
    {
        internal static readonly IReadOnlyList<ViewerMode> All = new List<ViewerMode>
        {
            new ViewerMode(AtmospheresSettings.Keys.OwnClouds, KeyMarker.Live, "OwnCloudModule.Run reads it every frame; the pass is hidden or drawn at once"),
            new ViewerMode(AtmospheresSettings.Keys.CloudSource, KeyMarker.Live,
                "OwnCloudModule.Run and EveCloudModule.Tick read it every frame (EVE's layers are released or taken at once); a body whose config states its own source ignores it"),
            new ViewerMode(AtmospheresSettings.Keys.LayerCap, KeyMarker.Live, "OwnCloudModule.EnsureStack checks the cap once a second; a changed cap builds the stack again and fetches the maps again"),
            new ViewerMode(AtmospheresSettings.Keys.RtOwnClouds, KeyMarker.Live,
                "RtOwnClouds.Tick reads it every frame; the pack is baked over some frames and is drawn only while ray tracing asks for clouds"),
            new ViewerMode(AtmospheresSettings.Keys.RtOwnCloudsLayerCap, KeyMarker.Live, "RtOwnClouds.LiveSettings reads it every frame; the pack is baked again with the new cap"),
            new ViewerMode(AtmospheresSettings.Keys.CloudSamples, KeyMarker.Live, "OwnCloudOptions.Apply(LayerFrame) every frame"),
            new ViewerMode(AtmospheresSettings.Keys.CloudSoftness, KeyMarker.Live, "OwnCloudOptions.Apply(LayerFrame) every frame (CloudSoftDensity.Softness)"),
            new ViewerMode(AtmospheresSettings.Keys.LayerStepQuality, KeyMarker.Live, "OwnCloudOptions.Apply(LayerFrame) every frame"),
            new ViewerMode(AtmospheresSettings.Keys.CloudResolution, KeyMarker.Live, "OwnCloudOptions.Apply(ReconSettings) every frame; the viewer drops the history with the change"),
            new ViewerMode(AtmospheresSettings.Keys.CloudReconMode, KeyMarker.Live, "OwnCloudOptions.Apply(ReconSettings) every frame; the viewer drops the history with the change"),
            new ViewerMode(AtmospheresSettings.Keys.CloudLightQuality, KeyMarker.Live, "OwnCloudOptions.Apply(CloudLightSettings) every frame"),
            new ViewerMode(AtmospheresSettings.Keys.CloudShadows, KeyMarker.Live, "OwnCloudOptions.ShadowActive every frame"),
            new ViewerMode(AtmospheresSettings.Keys.CloudShadowQuality, KeyMarker.Live, "OwnCloudOptions.Apply(CloudShadowFrame) every frame"),
            new ViewerMode(AtmospheresSettings.Keys.CloudNoiseTier, KeyMarker.Live,
                "OwnCloudModule applies the switches every frame; LayerNoise bakes the noise volumes of the new size on a worker, so the old ones draw for a few seconds"),
            new ViewerMode(AtmospheresSettings.Keys.CloudFarHaze, KeyMarker.Live, "OwnCloudOptions far-haze plan every frame (cloud.farHaze overrides it)")
        };

        // The setting keys of the modes, in order.
        internal static List<string> Keys()
        {
            var keys = new List<string>();
            foreach (ViewerMode m in All) keys.Add(m.Key);
            return keys;
        }

        internal static ViewerMode Find(string key)
        {
            foreach (ViewerMode m in All) if (m.Key == key) return m;
            return null;
        }

        // The reason a mode is greyed out, or null where it takes a change: the settings window's own rule (Dependencies) for the row, plus the body's stated cloud source for the
        // cloudSource row (statesSource: the body's config names its source, so the setting is not read for it).
        internal static string GreyReason(SettingDef def, Func<string, double> value, WindowEnvironment env, bool bodyStatesSource)
        {
            if (def == null) return null;
            if (def.Key == AtmospheresSettings.Keys.CloudSource && bodyStatesSource) return "The Body's Config States Its Own Cloud Source";
            RowState state = Dependencies.Evaluate(def, value, env);
            return state.Enabled ? null : state.Reason;
        }

        // The words of a mode's marker for the window.
        internal static string MarkerText(ViewerMode mode)
        {
            return CloudKeyRegistry.MarkerText(mode.Marker);
        }
    }
}
