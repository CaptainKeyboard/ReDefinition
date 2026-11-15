using System;
using System.Diagnostics;

namespace ReDefinition.Atmospheres
{
    // Keeps the ray-side pack of one body's layer stack current (package T18). Pure (no Unity): the module calls Tick every frame with the stack, the tests
    // call it with a test source. A new pack is baked a few texels per frame within a time budget (RtCloudTauBaker) whenever the stack's version moved, and
    // swapped in whole when it is complete; the pack that stands keeps serving until then. The live fields of the entries (the layers' shift by the wind,
    // the strength, the fades) are written every tick without a bake.
    internal sealed class RtCloudPackDriver
    {
        // What the series probe and the log read.
        internal sealed class Statistics
        {
            public int BakeStarts, BakesDone, Drops;
            // The main-thread time of the baking: the last finished bake (all its ticks), all bakes, and the worst single tick.
            public double LastBakeMs, TotalBakeMs;
            // The worst tick of the baking of a standing pack (the refresh) and of a bake that has no pack to show yet or a new layer count (the first bake, at its own budget).
            public double WorstTickMs, WorstFirstTickMs;
            // The real time from the start of the last finished bake to its pack, s: how old the pack's data is the moment it stands.
            public double LastBakeWallS;
            // The age of the standing pack's sky now and the largest it reached since the last reset (s): the bound on how old a cloud the rays see.
            public double AgeS, MaxAgeS;
            // Units of bake work (three stages over the texels), not texels.
            public long WorkUnitsBaked;
        }

        internal readonly Statistics Stats = new Statistics();

        // Real seconds; a test replaces it.
        internal Func<double> Clock = () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        private double lastStartS = double.NegativeInfinity, packStartS;
        // Whether the bake in progress has no pack to show (the first one, or one for another layer count): it runs at the larger budget.
        private bool bakeIsFirst;

        private RtCloudTauBaker baker;
        private double bakerMs;
        private int bakerWidth, bakerHeight, bakerLayers;
        // The stack the baker reads and the stack the pack standing was made from.
        private object bakeSource, packSource;
        private long counter;

        // The pack that stands (null: none). Read by the consumer; replaced as a whole.
        internal RtCloudPack Current { get; private set; }

        internal bool Baking { get { return baker != null; } }
        internal double BakeProgress { get { return baker == null ? 1.0 : baker.Progress; } }

        // One frame. `source` and `tables` are the stack (null: no stack, which drops the pack). `texelsPerTickCap` bounds the work of a tick besides the
        // time budget (a test passes a large number and a long budget to bake in one call).
        internal void Tick(ICloudLayerSource source, LayerTypeTable[] tables, RtCloudPackSettings settings, int texelsPerTickCap = int.MaxValue)
        {
            if (source == null || settings == null || !settings.Enabled || settings.LayersFor(source.LayerCount) == 0)
            {
                Drop();
                return;
            }
            long start = Stopwatch.GetTimestamp();
            int layers = settings.LayersFor(source.LayerCount);
            if (Current != null && Current.BodyIndex != source.BodyIndex) Drop();
            // A stack replaced while its bake ran: the bake is for a stack that is gone.
            if (baker != null && !ReferenceEquals(bakeSource, source)) baker = null;
            // A new stack, another size or layer count, or no pack yet: bake at once. Otherwise bake when the interval since the last start has passed.
            bool structural = Current == null || !ReferenceEquals(packSource, source) || Current.Width != settings.Width || Current.LayerCount != layers;
            // The stack's version does not follow the maps: they are refilled in place (StackMapMirror) and the version moves only when a layer gets its maps
            // the first time or the heights are applied. So a standing pack is made again on the interval, whatever the version.
            double now = Clock();
            bool due = !structural && now - lastStartS >= settings.RebakeIntervalS;
            if (baker == null && (structural || due))
            {
                baker = new RtCloudTauBaker(source, tables, layers, settings.Width, settings.Height, settings.Steps, settings.DepthStride);
                lastStartS = now;
                bakeIsFirst = structural;
                bakeSource = source;
                bakerWidth = settings.Width;
                bakerHeight = settings.Height;
                bakerLayers = layers;
                bakerMs = 0.0;
                Stats.BakeStarts++;
            }
            bool worked = baker != null, finished = false;
            if (worked)
            {
                long deadline = start + (long)((bakeIsFirst ? settings.FirstBudgetMs : settings.BudgetMs) * Stopwatch.Frequency / 1000.0);
                long before = (long)(baker.Progress * baker.WorkUnits + 0.5);
                bool done = baker.Step(texelsPerTickCap, () => Stopwatch.GetTimestamp() >= deadline);
                Stats.WorkUnitsBaked += (long)(baker.Progress * baker.WorkUnits + 0.5) - before;
                if (done)
                {
                    Publish(source);
                    finished = true;
                }
            }
            if (Current != null)
            {
                Refresh(source);
                // How old the sky the pack shows is: the time since its bake began (its texels were read between the start and the end of the bake).
                Stats.AgeS = Clock() - packStartS;
                Stats.MaxAgeS = Math.Max(Stats.MaxAgeS, Stats.AgeS);
            }
            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            if (worked)
            {
                bakerMs += ms;
                if (finished)
                {
                    Stats.LastBakeMs = bakerMs;
                    Stats.TotalBakeMs += bakerMs;
                    Stats.LastBakeWallS = Clock() - lastStartS;
                }
                if (bakeIsFirst) Stats.WorstFirstTickMs = Math.Max(Stats.WorstFirstTickMs, ms);
                else Stats.WorstTickMs = Math.Max(Stats.WorstTickMs, ms);
            }
        }

        // The whole pack in one call, not published and not counted: the stack as it stands in this very call. The series probe measures the standing pack against
        // it (a pack made a few seconds ago differs from the stack by what moved since) and the tests bake with it.
        internal static RtCloudPack BakeNow(ICloudLayerSource source, LayerTypeTable[] tables, RtCloudPackSettings settings)
        {
            int layers = settings.LayersFor(source.LayerCount);
            var baker = new RtCloudTauBaker(source, tables, layers, settings.Width, settings.Height, settings.Steps, settings.DepthStride);
            baker.Step(int.MaxValue);
            var entries = new RtCloudLayerEntry[layers];
            for (int i = 0; i < layers; i++) entries[i] = EntryFor(source, i);
            return new RtCloudPack(source.BodyIndex, source.BodyRadiusM, source.HasSurface, settings.Width, settings.Height, entries, baker.Data, baker.SourceVersion);
        }

        // Swaps the finished bake in.
        private void Publish(ICloudLayerSource source)
        {
            var entries = new RtCloudLayerEntry[bakerLayers];
            for (int i = 0; i < bakerLayers; i++) entries[i] = EntryFor(source, i);
            var pack = new RtCloudPack(source.BodyIndex, source.BodyRadiusM, source.HasSurface, bakerWidth, bakerHeight, entries, baker.Data, baker.SourceVersion);
            pack.Version = ++counter;
            pack.LiveVersion = pack.Version;
            Current = pack;
            packStartS = lastStartS;
            packSource = bakeSource;
            baker = null;
            Stats.BakesDone++;
        }

        // Drops the pack and any bake in progress (the switch went off, the stack is gone, the body changed).
        internal void Drop()
        {
            if (Current != null || baker != null) Stats.Drops++;
            Current = null;
            baker = null;
            bakeSource = null;
            packSource = null;
        }

        // The entry of layer i: the shell as radii from the body's centre, from the tight interval (the type table's heights widened by the height
        // variation's reach), and the live fields.
        internal static RtCloudLayerEntry EntryFor(ICloudLayerSource source, int i)
        {
            CloudLayerDef layer = source.LayerAt(i);
            float inner, outer;
            layer.TightAltitudes(out inner, out outer);
            var e = new RtCloudLayerEntry
            {
                StackIndex = i,
                Name = layer.Name,
                InnerRadiusM = source.BodyRadiusM + inner,
                OuterRadiusM = source.BodyRadiusM + outer,
                MiddleRadiusM = source.BodyRadiusM + 0.5 * (inner + outer),
                HeightDownM = layer.HeightDownM,
                HeightUpM = layer.HeightUpM
            };
            Live(e, layer);
            return e;
        }

        private static void Live(RtCloudLayerEntry e, CloudLayerDef layer)
        {
            e.ShadowStrength = layer.ShadowStrength;
            e.Coverage = layer.EffectiveCoverage;
            e.EdgeSoftness = layer.EffectiveEdgeSoftness;
            e.ClearSkyK = layer.ClearSkyK;
            e.WaterPower = layer.EffectiveWaterPower;
            e.MapShiftDeg = layer.MapShiftDeg;
            e.FadeNearM = layer.EffectiveFadeNearM;
            e.FadeFarM = layer.EffectiveFadeFarM;
        }

        // Writes the live fields of the standing pack from the stack; the pack's LiveVersion moves when one changed.
        private void Refresh(ICloudLayerSource source)
        {
            RtCloudPack pack = Current;
            if (!ReferenceEquals(packSource, source)) return;
            bool changed = false;
            for (int i = 0; i < pack.LayerCount && i < source.LayerCount; i++)
            {
                RtCloudLayerEntry e = pack.Entries[i];
                float strength = e.ShadowStrength, shift = e.MapShiftDeg, near = e.FadeNearM, far = e.FadeFarM, coverage = e.Coverage, soft = e.EdgeSoftness, clearK = e.ClearSkyK, power = e.WaterPower;
                Live(e, source.LayerAt(e.StackIndex));
                if (strength != e.ShadowStrength || shift != e.MapShiftDeg || near != e.FadeNearM || far != e.FadeFarM || coverage != e.Coverage
                    || soft != e.EdgeSoftness || clearK != e.ClearSkyK || power != e.WaterPower) changed = true;
            }
            if (changed) pack.LiveVersion = ++counter;
        }
    }
}
