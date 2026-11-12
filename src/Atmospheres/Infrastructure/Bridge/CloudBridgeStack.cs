using System;
using System.Collections.Generic;
using System.Text;

namespace ReDefinition.Atmospheres
{
    // Turns a body's layer specs (the LAYER nodes of its CLOUDS node, package T10) into the layer stack the own cloud pass draws (package
    // T09). Pure (no engine). The stack is rebuilt when the chosen layers change (a group member the weather picked, the layer cap), never per
    // frame: its Key says whether two builds are the same stack, and the height and map updates of the pass write into the stack that stands.
    internal static class CloudBridgeStack
    {
        // The handle that says "this layer has maps" (LayerRenderer.Upload reads only whether it is negative).
        internal const int MapHandle = 0;

        // The stack for the specs: one member of each group (pick answers the member's index by group name, null: the first), at most cap
        // layers, the lowest first. The key names the chosen layers; it is the same for two builds that draw the same layers.
        internal static LayerStackSource Build(IReadOnlyList<CloudLayerSpec> specs, int cap, Func<string, int> pick, int bodyIndex,
                                               double bodyRadiusM, bool hasSurface, out string key)
        {
            List<CloudLayerSpec> chosen = CloudLayerSet.Select(specs, cap, pick);
            var source = new LayerStackSource(bodyIndex, bodyRadiusM, hasSurface);
            var text = new StringBuilder();
            for (int i = 0; i < chosen.Count; i++)
            {
                CloudLayerDef def = chosen[i].Build(bodyIndex);
                def.CoverageMapHandle = MapHandle;
                def.TypeMapHandle = MapHandle;
                source.Add(def, null, null);
                text.Append(chosen[i].Name).Append('|');
            }
            key = text.ToString();
            return source;
        }

        // The index of the group member to draw by the surface temperature: the second member (the snow deck) below freezing, the first above;
        // a one-member group is index 0 whatever the temperature. The hysteresis keeps the choice where it was inside the band, so a temperature that
        // hovers at the freezing point does not rebuild the stack every frame.
        internal static int PickByTemperature(double surfaceK, bool wasCold, double bandK, out bool cold)
        {
            const double Freezing = 273.15;
            cold = wasCold ? surfaceK < Freezing + bandK : surfaceK < Freezing - bandK;
            return cold ? 1 : 0;
        }

        // The height of the middle of a layer's tight interval above sea level: the height at which the field is asked for the layer's coverage.
        internal static double MidHeightM(CloudLayerDef layer)
        {
            float inner, outer;
            layer.TightAltitudes(out inner, out outer);
            return 0.5 * (inner + outer);
        }
    }
}
