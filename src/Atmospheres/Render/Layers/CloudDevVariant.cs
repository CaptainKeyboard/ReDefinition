using System;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The cloud shaders' test and measuring switches as a variant (owner 2026-11-22: the product holds no test code; CloudDev.cginc). Every switch that only probes, the weather player or
    // the viewer change is written DEV(uniform.component, product value) in the shaders: without the keyword CLOUD_DEV the product value is a constant and fxc removes the test branch
    // (the march pass: 16 % fewer instructions, 12 % fewer registers, PRUEFPROTOKOLL 81). The tables below are the product values on the C# side, the same numbers as the shaders'
    // literals (CloudDevVariantTests); Apply turns the variant on where the material holds any other value, so a probe that sets an aid picture or a cost cut gets its test code back.
    internal static class CloudDevVariant
    {
        internal const string Keyword = "CLOUD_DEV";
        // layers.devVariant 1: the variant on whatever the values (a cost comparison between a test value and the product's measures both on the same shader).
        internal const string TuneForce = "layers.devVariant";

        internal struct Entry
        {
            internal readonly string Uniform;
            internal readonly int Component;
            internal readonly float Product;

            internal Entry(string uniform, int component, float product)
            {
                Uniform = uniform;
                Component = component;
                Product = product;
            }
        }

        // The layer pass (ReDefinitionAtmospheresLayers.shader and its includes).
        internal static readonly Entry[] Layer =
        {
            new Entry("_LimitP", 0, 0f),    // the ray length limit / the profile ramp (the weather player's aux jobs)
            new Entry("_LimitP", 1, 0f),    // the aid pictures (optical depth heat map, layer colours)
            new Entry("_LimitP", 2, 0f),    // the layer solo mask (cloud.soloLayer, LayerFrame.LayerMask)
            new Entry("_LimitP", 3, 1f),    // the scene's depth limits the ray (off only for a pin probe)
            new Entry("_OutP", 3, 1f),      // the display stage (off: cloud.outputGamma 1)
            new Entry("_SpacingP", 2, 1f),  // the leap's cap in noise scales (layers.leapScale)
            new Entry("_ReuseP", 1, 0f),    // the cost cut (layers.costCutM)
            new Entry("_CostP", 0, 1f),     // the coverage bound (layers.coverageBound)
            new Entry("_CostP", 1, 0f),     // the cost debug bits (layers.costDebug)
            new Entry("_DitherP", 2, 3f),   // the step offset's pattern (layers.ditherSequence)
            new Entry("_EdgeP", 0, 0f),     // the edge-adaptive draw (layers.edgeAdaptive)
            new Entry("_CellA", 3, 0f),     // the parity test cell (CloudLightSettings.CellRadiusM)
            new Entry("_BlendP", 0, 1f),    // the noise blend (cloud.noise.blend)
        };

        // The cloud shadow (it reads the noise include).
        internal static readonly Entry[] Shadow =
        {
            new Entry("_BlendP", 0, 1f),
        };

        // Whether a material with these values needs the variant (read: the material's vector of a uniform).
        internal static bool Needed(Func<string, Vector4> read, Entry[] table)
        {
            if (Tuning.Flag(TuneForce, false)) return true;
            foreach (Entry e in table)
                if (read(e.Uniform)[e.Component] != e.Product) return true;
            return false;
        }

        // Sets the keyword on the material from the values it holds now: call after the last upload of the frame that touches a switch of the table.
        internal static void Apply(Material m, Entry[] table)
        {
            if (m == null) return;
            bool on = Needed(name => m.GetVector(name), table);
            if (on == m.IsKeywordEnabled(Keyword)) return;
            if (on) m.EnableKeyword(Keyword);
            else m.DisableKeyword(Keyword);
        }
    }
}
