using System;

namespace ReDefinition.Atmospheres
{
    // The switches of the cloud shadow (package T04). The settings that carry them (cloudShadows, cloudShadowQuality, cloudShadowFloor) and
    // their rows in tools/benchmark_matrix.json are requested from the settings package (T10/D36); until they exist the values below are the
    // compiled defaults. cloudShadows follows ownClouds (LayerSwitches): no own clouds, no own shadow; under cloudSource Volumetric the shadow
    // is TVC's own and ours stays off.
    internal static class CloudShadowSwitches
    {
        // The default of cloudShadows: on whenever the own clouds are.
        internal const bool ShadowsDefault = true;
        // The default share of the direct light that always remains under a cloud (0: the pure Beer-Lambert shadow; the diffuse light under
        // a cloud is the ambient's and the light-in-medium package's business).
        internal const float FloorDefault = 0f;

        // The quality levels: midpoint steps in each span of a layer and directions across the sun's disc.
        internal static void Quality(int level, out int stepsPerSpan, out int discTaps)
        {
            switch (level)
            {
                case 0: stepsPerSpan = 4; discTaps = 1; break;
                case 1: stepsPerSpan = 8; discTaps = 4; break;
                default: stepsPerSpan = 12; discTaps = 6; break;
            }
        }

        // Whether the shadow is drawn for a body: the shadow switch is on, the own clouds draw (LayerSwitches.Active) and there is a layer.
        internal static bool Active(bool shadows, bool ownCloudsActive, int layerCount)
        {
            return shadows && ownCloudsActive && layerCount > 0;
        }
    }
}
