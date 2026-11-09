using System;
using UnityEngine;

namespace ReDefinition.Core
{
    // A facility button of the space centre: its sprites for the four states.
    internal sealed class SpaceCentreLook
    {
        internal Sprite Normal, Highlighted, Pressed, Disabled;
    }

    // What the host fills in when ReDefinition is installed, and what the shared code reads. Each has a default that works alone.
    // The game keeps one copy of ReDefinition.Common, so this is state shared by every mod that uses it: whoever assigns last wins.
    internal static class HostSeams
    {
        // The factor the interface is scaled by; 1 alone.
        internal static Func<float> WorldScale = () => 1f;

        // Whether a route is flown by the vessel itself in the background (the player's setting).
        internal static Func<bool> BackgroundRoute = () => true;

        // Whether the buttons standing in the world show (the player's setting).
        internal static Func<bool> WorldButtons = () => true;

        // Whether no host is present, so that the planner offers its own settings; false with ReDefinition, whose settings window holds them.
        internal static Func<bool> StandsAlone = () => true;

        // The space centre's own button of that name with its sprites; null alone, and the caller draws its own.
        internal static Func<string, SpaceCentreLook> SpaceCentreButton = name => null;
    }
}
