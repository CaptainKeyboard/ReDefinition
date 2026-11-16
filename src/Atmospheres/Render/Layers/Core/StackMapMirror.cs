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
        private readonly WeatherMap[] typeAttached = new WeatherMap[CloudLayerDef.MaxLayers];
        private LayerStackSource owner;

        // Copies the coverage map of a layer into the stack and gives the stack the type map (shared by reference; the caller refills it in place).
        internal void Set(LayerStackSource stack, int layer, WeatherMap coverageSource, WeatherMap typeMap)
        {
            if (stack == null || coverageSource == null || layer < 0 || layer >= CloudLayerDef.MaxLayers || layer >= stack.LayerCount) return;
            ResetOwner(stack);
            CopyCover(layer, coverageSource);
            Attach(stack, layer, typeMap);
        }

        private void ResetOwner(LayerStackSource stack)
        {
            if (ReferenceEquals(owner, stack)) return;
            owner = stack;
            Array.Clear(attached, 0, attached.Length);
            Array.Clear(typeAttached, 0, typeAttached.Length);
        }

        // The copy of a layer's coverage alone (any thread while no other call runs on the same layer: the map round on its worker thread; the stack's readers see the values change).
        internal void CopyCover(int layer, WeatherMap coverageSource)
        {
            if (coverageSource == null || layer < 0 || layer >= CloudLayerDef.MaxLayers) return;
            WeatherMap copy = cover[layer];
            if (copy == null) cover[layer] = copy = new WeatherMap();
            copy.Resize(coverageSource.Width, coverageSource.Height);
            Array.Copy(coverageSource.Values, copy.Values, copy.Values.Length);
        }

        // The map round's worker: the layer's coverage and type map copied into back buffers the stack does not read (the worker owns them until AttachBack takes them).
        private readonly WeatherMap[] coverBack = new WeatherMap[CloudLayerDef.MaxLayers], typeBack = new WeatherMap[CloudLayerDef.MaxLayers], typeOwned = new WeatherMap[CloudLayerDef.MaxLayers];

        internal void CopyBack(int layer, WeatherMap coverageSource, WeatherMap typeSource)
        {
            if (coverageSource == null || typeSource == null || layer < 0 || layer >= CloudLayerDef.MaxLayers) return;
            WeatherMap c = coverBack[layer] ?? (coverBack[layer] = new WeatherMap());
            c.Resize(coverageSource.Width, coverageSource.Height);
            Array.Copy(coverageSource.Values, c.Values, c.Values.Length);
            WeatherMap t = typeBack[layer] ?? (typeBack[layer] = new WeatherMap());
            t.Resize(typeSource.Width, typeSource.Height);
            Array.Copy(typeSource.Values, t.Values, t.Values.Length);
        }

        // Main thread, before the first round on the worker: a layer whose type map is still the round's shared one (the worker refills it) gets a copy of its own as the stack's.
        internal void OwnTypeMaps(LayerStackSource stack)
        {
            if (stack == null || !ReferenceEquals(owner, stack)) return;
            for (int l = 0; l < CloudLayerDef.MaxLayers && l < stack.LayerCount; l++)
            {
                if (!attached[l] || typeOwned[l] != null || typeAttached[l] == null || cover[l] == null) continue;
                var t = new WeatherMap();
                t.Resize(typeAttached[l].Width, typeAttached[l].Height);
                Array.Copy(typeAttached[l].Values, t.Values, t.Values.Length);
                typeOwned[l] = t;
                typeAttached[l] = t;
                stack.SwapMaps(l, cover[l], t);
            }
        }

        // Main thread, after the worker's round: the back buffers become the stack's maps (a swap of references, no version change once the layer has maps) and the maps the stack
        // read until now become the next back buffers, so no reader ever sees a map that is being written.
        internal void AttachBack(LayerStackSource stack, int layer)
        {
            if (stack == null || layer < 0 || layer >= CloudLayerDef.MaxLayers || layer >= stack.LayerCount) return;
            WeatherMap c = coverBack[layer], t = typeBack[layer];
            if (c == null || t == null) return;
            ResetOwner(stack);
            coverBack[layer] = cover[layer];
            cover[layer] = c;
            typeBack[layer] = typeOwned[layer];
            typeOwned[layer] = t;
            if (attached[layer]) stack.SwapMaps(layer, c, t);
            else
            {
                stack.SetMaps(layer, c, t);
                attached[layer] = true;
            }
            typeAttached[layer] = t;
        }

        // Gives the stack the layer's copy and type map (main thread; CopyCover has run for the layer).
        internal void Attach(LayerStackSource stack, int layer, WeatherMap typeMap)
        {
            if (stack == null || layer < 0 || layer >= CloudLayerDef.MaxLayers || layer >= stack.LayerCount) return;
            WeatherMap copy = cover[layer];
            if (copy == null) return;
            ResetOwner(stack);
            // The type map is attached once; a layer whose type map changed to another instance (the look knobs cloud.typeFair / cloud.typeOverride, CloudLookTuning) is attached again.
            if (attached[layer] && ReferenceEquals(typeAttached[layer], typeMap)) return;
            stack.SetMaps(layer, copy, typeMap);
            attached[layer] = true;
            typeAttached[layer] = typeMap;
        }
    }
}
