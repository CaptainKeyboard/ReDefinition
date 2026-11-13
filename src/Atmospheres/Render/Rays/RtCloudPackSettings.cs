using System;

namespace ReDefinition.Atmospheres
{
    // The settings of the ray-side pack (package T18). The switch and the layer cap are player settings (rtOwnClouds, rtOwnCloudsLayerCap); the numbers
    // are tuning keys with the compiled values below.
    internal sealed class RtCloudPackSettings
    {
        internal const string TunePackWidth = "rt.packWidth", TuneBakeSteps = "rt.bakeSteps", TuneBakeBudgetMs = "rt.bakeBudgetMs",
            TuneDepthStride = "rt.depthStride", TuneRebakeIntervalS = "rt.rebakeIntervalS";

        // On or off: off builds nothing and publishes nothing.
        internal bool Enabled;
        // The most layers the rays see; 0 is all of the stack. The lowest layers are kept, as the stack's own cap keeps them.
        internal int LayerCap;
        // The width of a slice in texels (the height is half of it). 512 x 256 is the lattice of the weather maps the layers are painted from, so a texel
        // centre of the pack is a texel centre of the map and the filtered coverage is the map's own value there.
        internal int Width = 512;
        // The midpoint steps of a column of the full-cloud depth (the cloud shadow's rule, CloudShadowField.StepsPerSpan).
        internal int Steps = 8;
        // What one frame may spend on baking, ms of the main thread.
        internal float BudgetMs = 0.25f;
        // The full-cloud depth is made at every Nth texel in each direction and interpolated between (1: every texel). A layer without height variation
        // reads a table and gains little from it. Two: a third of the bake with height variation (RtBakeCostTests: 0.6 s against 1.8 s for four layers on
        // the test machine) at a median shadow difference of 3.9 % against 3.5 % for every texel.
        internal int DepthStride = 2;
        // The least real time between the start of one bake and the next when only the stack's version moved: the stack's version rises with every
        // refresh of its maps (OwnCloudPlan.MapIntervalS, one second), which is more often than a bake can finish. A new stack, another size or layer count
        // starts at once.
        internal float RebakeIntervalS = 10f;

        internal int Height { get { return Math.Max(1, Width / 2); } }

        // The layers of a stack of `count` the rays take.
        internal int LayersFor(int count)
        {
            int cap = LayerCap <= 0 ? RtCloudPackLayout.MaxLayers : Math.Min(LayerCap, RtCloudPackLayout.MaxLayers);
            return Math.Max(0, Math.Min(count, cap));
        }

        // The numbers from the tuning table within their ranges; the switches from the settings.
        internal static RtCloudPackSettings From(bool enabled, int layerCap, Func<string, float, float> tuning)
        {
            var s = new RtCloudPackSettings { Enabled = enabled, LayerCap = Math.Max(0, Math.Min(RtCloudPackLayout.MaxLayers, layerCap)) };
            var compiled = new RtCloudPackSettings();
            s.Width = (int)Math.Max(64f, Math.Min(1024f, tuning(TunePackWidth, compiled.Width)));
            s.Width = (s.Width + 1) & ~1;
            s.Steps = (int)Math.Max(2f, Math.Min(32f, tuning(TuneBakeSteps, compiled.Steps)));
            s.BudgetMs = Math.Max(0.02f, Math.Min(5f, tuning(TuneBakeBudgetMs, compiled.BudgetMs)));
            s.DepthStride = (int)Math.Max(1f, Math.Min(4f, tuning(TuneDepthStride, compiled.DepthStride)));
            s.RebakeIntervalS = Math.Max(0f, Math.Min(600f, tuning(TuneRebakeIntervalS, compiled.RebakeIntervalS)));
            return s;
        }
    }

    // When the rays see the own layers. The own pack stands in for the cloud layers of the renderer that casts the decks' shadow (CloudShadowSwitches.Caster),
    // so that the rays never see a deck twice: under cloudSource Ours the pack is the own one, under Volumetric or Both the TVC layer of the main mod's
    // RtEveClouds stays the rays' only cloud and the own pack is not built.
    internal static class RtOwnCloudsPlan
    {
        internal static bool Wanted(bool hostOpen, bool switchOn, CloudShadowSwitches.Caster caster, bool ownCloudsActive, int layers)
        {
            return hostOpen && switchOn && ownCloudsActive && caster == CloudShadowSwitches.Caster.Ours && layers > 0;
        }
    }
}
