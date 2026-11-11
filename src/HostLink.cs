using System;
using System.Reflection;
using ReDefinition.Api.Internal;
using ReDefinition.Core;
using ReDefinition.Hud;
using ReDefinition.Proxy;
using ReDefinition.Upscaler;
using ReDefinition.Scaling;
using UnityEngine;

namespace ReDefinition
{
    // What ReDefinition gives the shared code (ReDefinition.Common) when it is installed: its settings, its scale, and the space centre's buttons.
    // Whatever uses the seams works without any of it. The assignment is unconditional, so it wins whichever add-on ran first.
    internal static class HostLink
    {
        // From the addon's Awake.
        internal static void Connect()
        {
            string problem = CommonVersionCheck.Problem("ReDefinition", typeof(HostLink).Assembly);
            if (problem != null) Debug.LogWarning(Log.Tag + " " + problem);
            Fill();
        }

        // The line for a loaded Common that is older than the one this build was made against; null where it is not older.
        internal static string CommonProblem(Version built, Version loaded)
        {
            return CommonVersionCheck.Problem("ReDefinition", built, loaded);
        }

        // Every frame, from SharedFrame.PollProfile: the caps the atmospheres read (WeatherHooks.Caps). The profile is the one the
        // last poll saw.
        internal static void FillCaps(string profile)
        {
            UpscalerRig rig = UpscalerRig.Current;
            if (rig != null && rig.TornDown) rig = null;
            WeatherCapsWriter.Fill(WeatherHooks.Caps, SharedFrame.UpscalerActive, rig != null ? rig.Backend : UpscalerBackend.Fsr3,
                                   SharedFrame.FrameGenerationActive, profile);
            EveOpticalDepthLink.Refresh();
        }

        private static void Fill()
        {
            HostSeams.StandsAlone = () => false;
            HostSeams.WorldScale = () => UiScaling.World;
            HostSeams.BackgroundRoute = () => ReDefinitionAddon.Instance == null || ReDefinitionAddon.Instance.Live.BackgroundRoute;
            HostSeams.WorldButtons = () => ReDefinitionAddon.Instance == null || ReDefinitionAddon.Instance.Live.WorldButtons;
            HostSeams.DashboardNetwork = () => ReDefinitionAddon.Instance == null || ReDefinitionAddon.Instance.Live.DashboardNetwork;
            HostSeams.DashboardKey = () => ReDefinitionAddon.Instance == null ? null : ReDefinitionAddon.Instance.Live.DashboardKey;
            HostSeams.SetDashboardKey = key =>
            {
                if (ReDefinitionAddon.Instance != null) ReDefinitionAddon.Instance.Live.DashboardKey = key;
            };
            HostSeams.SpaceCentreButton = name =>
            {
                KscButtons.Look held = KscButtons.Get(name);
                return held == null ? null : new SpaceCentreLook
                {
                    Normal = held.Normal, Highlighted = held.Highlighted, Pressed = held.Pressed, Disabled = held.Disabled,
                };
            };
        }
    }
}
