using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using ReDefinition.Core;
using ReDefinition.Hud;
using ReDefinition.Planner;
using ReDefinition.Planner.Hud;
using ReDefinition.Scaling;
using ReDefinition.Ui;
using UnityEngine;

namespace ReDefinition
{
    // What ReDefinition gives the planner (ReDefinition.Planner) when both are installed: its settings, its scale, and the space centre's buttons.
    // The planner works without any of it. The two are released apart, so the planner's seams carry a level, and a planner of another
    // level than this one is left to stand alone.
    internal static class PlannerLink
    {
        // The level of PlannerSeams this build fills; raised together with PlannerSeams.Level when a seam is added, removed or changes its meaning.
        internal const int Expected = 2;

        // From the addon's Awake.
        internal static void Connect()
        {
            Version built = null;
            foreach (AssemblyName referenced in typeof(PlannerLink).Assembly.GetReferencedAssemblies())
                if (referenced.Name == "ReDefinition.Common") built = referenced.Version;
            string problem = CommonProblem(built, typeof(Log).Assembly.GetName().Version);
            if (problem != null) Debug.LogWarning(Log.Tag + " " + problem);
            Connect(Compatible, Fill, Debug.LogWarning);
        }

        // Fills the seams when the planner names the level this build fills; true then. A planner without the member or with a type that
        // does not load (an older one) is the same as one of another level: it stands alone, and `warn` says so.
        internal static bool Connect(Func<bool> compatible, Action fill, Action<string> warn)
        {
            bool fits;
            try
            {
                fits = compatible();
            }
            catch (Exception e) when (e is MissingMemberException || e is TypeLoadException || e is TypeInitializationException)
            {
                fits = false;
            }
            if (!fits)
            {
                warn(Log.Tag + " The installed ReDefinition Planner is not the version this ReDefinition was made for;"
                     + " it stands alone, without ReDefinition's settings, scale and space centre buttons. Update both to the same release.");
                return false;
            }
            fill();
            return true;
        }

        // The game keeps one ReDefinition.Common.dll. Where the one loaded is older than the one this build was made against, members are
        // missing at the first call that needs them; the line says so at the start. Null where it is not older.
        internal static string CommonProblem(Version built, Version loaded)
        {
            if (built == null || loaded == null || loaded >= built) return null;
            return "ReDefinition.Common " + loaded + " is loaded but this ReDefinition was made against " + built
                   + ": another mod ships an older copy. Calls into it can fail until it is updated or removed.";
        }

        // NoInlining: the check is compiled alone, so that a planner without the member fails here and not in Fill.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool Compatible()
        {
            return PlannerSeams.Level == Expected;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Fill()
        {
            PlannerSeams.StandsAlone = () => false;
            PlannerSeams.WorldScale = () => UiScaling.World;
            PlannerSeams.BackgroundRoute = () => ReDefinitionAddon.Instance == null || ReDefinitionAddon.Instance.Live.BackgroundRoute;
            PlannerSeams.WorldButtons = () => ReDefinitionAddon.Instance == null || ReDefinitionAddon.Instance.Live.WorldButtons;
            PlannerSeams.SpaceCentreButton = name =>
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
