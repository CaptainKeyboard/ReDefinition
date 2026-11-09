using System;
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
    // What ReDefinition gives the planner (ReDefinition.Planner) when both are installed: its settings, its scale, the space centre's buttons and the
    // click shields. The planner works without any of it. The two are released apart, so the planner's seams carry a level, and a planner of another
    // level than this one is left to stand alone.
    internal static class PlannerLink
    {
        // The level of PlannerSeams this build fills; raised together with PlannerSeams.Level when a seam is added, removed or changes its meaning.
        internal const int Expected = 2;

        // From the addon's Awake.
        internal static void Connect()
        {
            bool fits;
            try
            {
                fits = Compatible();
            }
            catch (Exception e) when (e is MissingMemberException || e is TypeLoadException || e is TypeInitializationException)
            {
                fits = false;
            }
            if (!fits)
            {
                Debug.LogWarning(Log.Tag + " The installed ReDefinition Planner is not the version this ReDefinition was made for;"
                                 + " it stands alone, without ReDefinition's settings, scale and shields. Update both to the same release.");
                return;
            }
            Fill();
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
