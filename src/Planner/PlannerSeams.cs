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
        // The factor ReDefinition scales the interface by; 1 without it.
        internal static Func<float> WorldScale = () => 1f;

        // Whether a route is flown by the vessel itself in the background (the player's setting); the way to go without a settings window.
        internal static Func<bool> BackgroundRoute = () => true;

        // Whether the buttons standing in the world show (the player's setting).
        internal static Func<bool> WorldButtons = () => true;

        // The space centre's own button of that name with its sprites; null without it, and the planner draws its own.
        internal static Func<string, SpaceCentreLook> SpaceCentreButton = name => null;

        // Keeps the clicks on a dialog and on a panel from reaching what lies behind (the map, the buildings); the dialog is returned.
        internal static Func<PopupDialog, PopupDialog> ShieldDialog = dialog => dialog;
        internal static Action<RectTransform, Func<bool>> ShieldFrame = (frame, shown) => { };
    }
}
