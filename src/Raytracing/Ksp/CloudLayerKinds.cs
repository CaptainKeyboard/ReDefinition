using System;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // Which of EVE's cloud layers are weather and which shine by themselves. A body can have many layers: Volumetric Clouds
    // brings a base layer, a cumulonimbus layer and weather layers, and SPVE (SpacePotato's Volumetric Enhancements) adds
    // cirrus, hurricanes, rain, fog, dust, ambience and the auroras. The weather layers are lit by the sun and the moon. An
    // aurora is not: its config gives it a negative skylightTintMultiplier (-10000 on Kerbin) so that it glows at night, and
    // its colour would change with the light handed to it, so it is left alone. It takes no part in the clouds' moonlight,
    // in the rays' cloud map or in the layers' height.
    internal static class CloudLayerKinds
    {
        private static readonly int SkylightTintId = Shader.PropertyToID("skylightTintMultiplier");

        // Whether the layer named `name` with the raymarched material `raymarched` shines by itself. Either may be null.
        internal static bool IsEmissive(string name, Material raymarched)
        {
            if (name != null && name.IndexOf("aurora", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return raymarched != null && raymarched.GetFloat(SkylightTintId) < 0f;
        }
    }
}
