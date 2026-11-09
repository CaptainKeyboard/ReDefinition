using System;
using System.Reflection;
using ReDefinition.Core;
using ReDefinition.Hud;
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
            Version built = null;
            foreach (AssemblyName referenced in typeof(HostLink).Assembly.GetReferencedAssemblies())
                if (referenced.Name == "ReDefinition.Common") built = referenced.Version;
            string problem = CommonProblem(built, typeof(Log).Assembly.GetName().Version);
            if (problem != null) Debug.LogWarning(Log.Tag + " " + problem);
            Fill();
        }

        // The game keeps one ReDefinition.Common.dll. Where the one loaded is older than the one this build was made against, members are
        // missing at the first call that needs them; the line says so at the start. Null where it is not older.
        internal static string CommonProblem(Version built, Version loaded)
        {
            if (built == null || loaded == null || loaded >= built) return null;
            return "ReDefinition.Common " + loaded + " is loaded but this ReDefinition was made against " + built
                   + ": another mod ships an older copy. Calls into it can fail until it is updated or removed.";
        }

        private static void Fill()
        {
            HostSeams.StandsAlone = () => false;
            HostSeams.WorldScale = () => UiScaling.World;
            HostSeams.BackgroundRoute = () => ReDefinitionAddon.Instance == null || ReDefinitionAddon.Instance.Live.BackgroundRoute;
            HostSeams.WorldButtons = () => ReDefinitionAddon.Instance == null || ReDefinitionAddon.Instance.Live.WorldButtons;
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
