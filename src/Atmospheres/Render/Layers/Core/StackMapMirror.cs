using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // The CPU copy of the maps the GPU reads. The layer stack is built without maps (CloudBridgeStack.Build), and a layer without maps reads as fully covered with the
    // first type (LayerStackSource.SampleMaps); every CPU reader of the stack (the sun dimming and the weather driver through WeatherHooks.CloudOpticalDepth, the cloud
    // shadow field, the probe's layer rows) therefore saw map value 1.0 and type 0 in every layer whatever the weather (K-T2d start 48: layer<l>_map_cover 1.0 at both covers).
    // This hands the stack, per layer, a copy of the coverage map the fetch took (the layer's own map where the generator has one, else the shared one) and the type map.
    // The copy is the target the GPU moves toward (the GPU slews to it, the CPU reads it directly). The stack's version moves only when a layer gets its maps the first time.
    internal sealed class StackMapMirror
    {
        private readonly WeatherMap[] cover = new WeatherMap[CloudLayerDef.MaxLayers];
        private readonly bool[] attached = new bool[CloudLayerDef.MaxLayers];
        private LayerStackSource owner;

        // Copies the coverage map of a layer into the stack and gives the stack the type map (shared by reference; the caller refills it in place).
        internal void Set(LayerStackSource stack, int layer, WeatherMap coverageSource, WeatherMap typeMap)
        {
            if (stack == null || coverageSource == null || layer < 0 || layer >= CloudLayerDef.MaxLayers || layer >= stack.LayerCount) return;
            if (!ReferenceEquals(owner, stack))
            {
                owner = stack;
                Array.Clear(attached, 0, attached.Length);
            }
            WeatherMap copy = cover[layer];
            if (copy == null) cover[layer] = copy = new WeatherMap();
            copy.Resize(coverageSource.Width, coverageSource.Height);
            Array.Copy(coverageSource.Values, copy.Values, copy.Values.Length);
            if (attached[layer]) return;
            stack.SetMaps(layer, copy, typeMap);
            attached[layer] = true;
        }
    }
}
