using System;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The axis of a layer's fibre stretch (cfg key fibreStretch; E40 L5 shape): the direction the layer's noise features are long in. Cirrus fibres lie along the wind of their height, so the
    // axis is the horizontal direction of the layer's wind at the camera (LayerNoiseMotion.DriftMs is built from east and north), the body's pole where the air is (nearly) still, and
    // it turns to a new wind over AxisTurnSeconds, because a camera that moves over the planet turns the wind's direction in world space and a pattern that turned at once would swim.
    internal static class FibreAxis
    {
        // The wind below which the fibres keep to the pole (m/s), and the time the axis takes to turn to a new wind (s).
        internal const float MinWindMs = 0.5f;
        internal const double TurnSeconds = 20.0;

        // The axis a wind asks for: its own direction, the pole where the wind is slower than MinWindMs.
        internal static Vector3 Target(Vector3 windMs, Vector3 pole)
        {
            return windMs.sqrMagnitude < MinWindMs * MinWindMs ? pole : windMs.normalized;
        }

        // The axis after dt seconds of turning towards a target. A fibre has no direction, only an axis: the target or its opposite, whichever is nearer the old axis, is followed, so a wind
        // that turns half a circle does not pass through zero.
        internal static Vector3 Turn(Vector3 axis, Vector3 target, double dt)
        {
            if (dt <= 0.0) return axis;
            float k = 1f - (float)Math.Exp(-dt / TurnSeconds);
            Vector3 goal = Vector3.Dot(axis, target) < 0f ? -target : target;
            Vector3 turned = axis + (goal - axis) * k;
            return turned.sqrMagnitude > 1e-8f ? turned.normalized : goal;
        }
    }
}
