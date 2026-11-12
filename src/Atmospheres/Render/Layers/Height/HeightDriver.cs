using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // Gives the layers of a stack their height maps from the weather field (package T13). The integration package (T09) calls Update when
    // the field's maps have changed (a slow rhythm: the offsets move over hours of game time) and after the switches changed; the call
    // is cheap enough for that (a 256 x 128 map per layer: a few ms), never per frame.
    internal static class HeightDriver
    {
        // Builds the maps of every layer of the stack from the field at the time ut. body is the tuning of the body (switches already
        // applied, OwnCloudOptions.Apply); the seed makes the noise of one body's layers differ from another's.
        internal static void Update(LayerStackSource source, IWeatherField field, HeightTuning body, double ut, uint bodySeed, int width, int height)
        {
            for (int l = 0; l < source.LayerCount; l++)
            {
                CloudLayerDef layer = source.LayerAt(l);
                HeightTuning t = body.ForLayer(layer.Kind);
                if (!t.Any)
                {
                    source.SetHeights(l, null, null, 0u);
                    continue;
                }
                // 24 bits: the seed travels to the shader as a float, which holds no more exactly.
                uint seed = (bodySeed * 0x9E3779B1u + (uint)(l + 1) * 0x85EBCA6Bu) & 0xFFFFFFu;
                t.Seed = seed;
                double altitude = layer.InnerAltitudeM + t.UpdraftSampleShare * layer.ThicknessM;
                HeightInputs input = field == null ? new HeightInputs { Width = width, Height = height }
                                                   : HeightFieldInputs.Read(field, source.BodyIndex, ut, width, height, altitude);
                source.SetHeights(l, t, HeightMapBuilder.Build(input, t, source.BodyRadiusM, seed), seed);
            }
        }
    }
}
