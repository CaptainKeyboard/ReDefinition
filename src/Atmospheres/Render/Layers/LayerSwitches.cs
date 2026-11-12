using System;

namespace ReDefinition.Atmospheres
{
    // The switches of the own cloud system's layer core (package T01): whether it draws at all, how many layers at most, and the march
    // quality. The settings that carry them (ownClouds, layerCap, layerStepQuality; AtmospheresSettings.Clouds.cs) reach the structs through
    // OwnCloudOptions, and their rows are in tools/benchmark_matrix.json (group weather-5). ownClouds is off until the integration package
    // (T09) accepts the system, then on for cloudSource Ours; under Volumetric the system is always off.
    internal static class LayerSwitches
    {
        // The default of ownClouds.
        internal const bool OwnCloudsDefault = false;
        // The layers one pass draws at most (1 to CloudLayerDef.MaxLayers).
        internal const int LayerCapDefault = CloudLayerDef.MaxLayers;

        // The march quality levels: most steps per span, steps towards the sun, and the shortest step in m.
        internal static void StepQuality(int level, out int maxSteps, out int sunSteps, out float minStepM)
        {
            switch (level)
            {
                case 0: maxSteps = 32; sunSteps = 2; minStepM = 40f; break;
                case 1: maxSteps = 48; sunSteps = 3; minStepM = 25f; break;
                default: maxSteps = 64; sunSteps = 4; minStepM = 15f; break;
            }
        }

        // Whether the system draws for a body: the switch is on and the cloud source is Ours (a source that is Volumetric, Both or None
        // leaves the drawing to the renderer it names; Both draws ours as the base and TVC's layers on top is T09's decision).
        internal static bool Active(bool ownClouds, CloudSourceKind source)
        {
            return ownClouds && source == CloudSourceKind.Ours;
        }

        // The layers a pass draws: the cap clamped to 1 to MaxLayers, and no more than the stack holds.
        internal static int LayersToDraw(int stackCount, int cap)
        {
            return Math.Max(0, Math.Min(stackCount, Math.Max(1, Math.Min(cap, CloudLayerDef.MaxLayers))));
        }
    }
}
