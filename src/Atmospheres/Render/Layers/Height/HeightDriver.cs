using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // Gives the layers of a stack their height maps from the weather field (package T13). The game builds them off the main thread (HeightJob,
    // driven by OwnCloudModule); Update is the same build in one call, for the tests and the weather player, which read the result in the same
    // frame. Both go through HeightPlan, so they give the same bytes.
    internal static class HeightDriver
    {
        // Builds the maps of every layer of the stack from the field at the time ut. body is the tuning of the body (switches already
        // applied, OwnCloudOptions.Apply); the seed makes the noise of one body's layers differ from another's.
        internal static void Update(LayerStackSource source, IWeatherField field, HeightTuning body, double ut, uint bodySeed, int width, int height)
        {
            HeightPlan plan = HeightPlan.Make(source, body, ut, bodySeed, width, height);
            HeightGather gather = plan.Gather(field);
            gather.Step(0.0);
            plan.Build(gather, HeightNoiseCache.Shared, null).ApplyTo(source);
        }
    }
}
