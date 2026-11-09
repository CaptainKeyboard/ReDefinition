using System;
using TMPro;
using UnityEngine;

namespace ReDefinition.Planner
{
    // A facility button of the space centre: its sprites for the four states.
    internal sealed class SpaceCentreLook
    {
        internal Sprite Normal, Highlighted, Pressed, Disabled;
    }

    // What ReDefinition fills in when it is installed. Each has a default that works without it, so the planner stands alone.
    internal static class PlannerSeams
    {
        // The level of these seams, which ReDefinition compares with the one it fills (PlannerLink.Expected); raised when a seam is added, removed or changes its meaning.
        internal static int Level { get { return 2; } }

        // The factor ReDefinition scales the interface by; 1 without it.
        internal static Func<float> WorldScale = () => 1f;

        // Whether a route is flown by the vessel itself in the background (the player's setting); its own file without ReDefinition.
        internal static Func<bool> BackgroundRoute = () => { PlannerSettings.Load(); return PlannerSettings.BackgroundRoute; };

        // Whether the buttons standing in the world show (the player's setting).
        internal static Func<bool> WorldButtons = () => { PlannerSettings.Load(); return PlannerSettings.WorldButtons; };

        // Whether the planner stands alone, so that it offers its own settings; false with ReDefinition, whose settings window holds them.
        internal static Func<bool> StandsAlone = () => true;

        // The space centre's own button of that name with its sprites; null without it, and the planner draws its own.
        internal static Func<string, SpaceCentreLook> SpaceCentreButton = name => null;
    }
}
