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

        // Whether the dashboard opens on the network, and the key a phone needs to connect (the player's settings). A host that has them
        // answers a null key until its settings are loaded; SetDashboardKey stores the key the dashboard made where the host's has none.
        internal static Func<bool> DashboardNetwork = () => true;
        internal static Func<string> DashboardKey = () => "";
        internal static Action<string> SetDashboardKey = key => { };

        // Whether no host is present, so that the planner and the dashboard offer their own settings; false with ReDefinition, whose settings window holds them.
        internal static Func<bool> StandsAlone = () => true;

        // The space centre's own button of that name with its sprites; null alone, and the caller draws its own.
        internal static Func<string, SpaceCentreLook> SpaceCentreButton = name => null;

        // The light the moons and planets give the clouds of a body seen from an eye (world space): towards them, weighted by brightness,
        // and the colour in units of the sunlight, linear. False alone and where none lights the clouds.
        internal delegate bool CloudMoonLightFn(CelestialBody main, Vector3d eye, out Vector3 towards, out Color colour);
        internal static CloudMoonLightFn CloudMoonLight = (CelestialBody main, Vector3d eye, out Vector3 towards, out Color colour) =>
        {
            towards = Vector3.up;
            colour = Color.black;
            return false;
        };

        // Puts a GPU profiler marker with that label into a command buffer; does nothing alone.
        internal static Action<UnityEngine.Rendering.CommandBuffer, string> GpuMark = (buffer, label) => { };
    }
}
