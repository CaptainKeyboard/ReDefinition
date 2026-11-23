using System;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The axis of a layer's filament stretch (cfg key filamentStretch; E40 L5 shape): the direction the layer's noise features are long in. Cirrus filaments lie along the wind of their height, so the
    // axis is the horizontal direction of the layer's wind at the camera (LayerNoiseMotion.DriftMs is built from east and north), the body's pole where the air is (nearly) still, and
    // it turns to a new wind over the layer's turn time, because a camera that moves over the planet turns the wind's direction in world space and a pattern that turned at once would swim.
    // The two numbers are the layer's (cfg keys filamentMinWindMs and filamentTurnSeconds, E40 L6 shape); the defaults are what a layer without the keys gets.
    internal static class FilamentAxis
    {
        internal const float DefaultMinWindMs = 0.5f;
        internal const float DefaultTurnSeconds = 20f;

        // The axis a wind asks for: its own direction, the pole where the wind is slower than minWindMs.
        internal static Vector3 Target(Vector3 windMs, Vector3 pole, float minWindMs)
        {
            return windMs.sqrMagnitude < minWindMs * minWindMs ? pole : windMs.normalized;
        }

        // The axis after dt seconds of turning towards a target over turnSeconds. A filament has no direction, only an axis: the target or its opposite, whichever is nearer the old axis, is
        // followed, so a wind that turns half a circle does not pass through zero.
        internal static Vector3 Turn(Vector3 axis, Vector3 target, double dt, double turnSeconds)
        {
            if (dt <= 0.0) return axis;
            float k = turnSeconds > 0.0 ? 1f - (float)Math.Exp(-dt / turnSeconds) : 1f;
            Vector3 goal = Vector3.Dot(axis, target) < 0f ? -target : target;
            Vector3 turned = axis + (goal - axis) * k;
            return turned.sqrMagnitude > 1e-8f ? turned.normalized : goal;
        }
    }
}
