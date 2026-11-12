using System;
using System.Diagnostics;
using System.Threading;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // What one build of the height maps needs, copied from the stack on the caller's thread: per layer its tuning (a copy), seed and the altitude the
    // updraft is read at. Nothing in it refers to the stack, so a worker can build from it while the stack changes.
    internal sealed class HeightPlan
    {
        internal int Count, Width, Height, BodyIndex;
        internal double Ut, BodyRadiusM;
        // Per layer: the tuning with its seed set, or null where the layer has no height variation.
        internal HeightTuning[] Tunings;
        internal uint[] Seeds;
        internal double[] AltitudesM;

        // The plan of a stack at the time ut. body is the tuning of the body (switches already applied, OwnCloudOptions.Apply); the seed makes the
        // noise of one body's layers differ from another's.
        internal static HeightPlan Make(LayerStackSource source, HeightTuning body, double ut, uint bodySeed, int width, int height)
        {
            int n = source.LayerCount;
            var plan = new HeightPlan
            {
                Count = n, Width = width, Height = height, BodyIndex = source.BodyIndex, Ut = ut, BodyRadiusM = source.BodyRadiusM,
                Tunings = new HeightTuning[n], Seeds = new uint[n], AltitudesM = new double[n]
            };
            for (int l = 0; l < n; l++)
            {
                CloudLayerDef layer = source.LayerAt(l);
                HeightTuning t = body.ForLayer(layer.Kind);
                if (!t.Any) continue;
                // 24 bits: the seed travels to the shader as a float, which holds no more exactly.
                uint seed = (bodySeed * 0x9E3779B1u + (uint)(l + 1) * 0x85EBCA6Bu) & 0xFFFFFFu;
                t.Seed = seed;
                plan.Tunings[l] = t;
                plan.Seeds[l] = seed;
                plan.AltitudesM[l] = layer.InnerAltitudeM + t.UpdraftSampleShare * layer.ThicknessM;
            }
            return plan;
        }

        internal bool[] Wanted()
        {
            var w = new bool[Count];
            for (int l = 0; l < Count; l++) w[l] = Tunings[l] != null;
            return w;
        }

        internal HeightGather Gather(IWeatherField field)
        {
            return new HeightGather(field, BodyIndex, Ut, Width, Height, Wanted(), AltitudesM);
        }

        // The maps of every layer from a finished reading. cancelled is polled between layers (null: never); the bytes for the GPU are made too.
        internal HeightResult Build(HeightGather gather, HeightNoiseCache cache, Func<bool> cancelled)
        {
            var result = new HeightResult { Count = Count, Tunings = Tunings, Seeds = Seeds, Maps = new HeightMaps[Count] };
            for (int l = 0; l < Count; l++)
            {
                if (Tunings[l] == null) continue;
                if (cancelled != null && cancelled()) return null;
                HeightMaps maps = HeightMapBuilder.Build(gather.InputsOf(l), Tunings[l], BodyRadiusM, Seeds[l], cache);
                maps.MakeRgba();
                result.Maps[l] = maps;
            }
            return result;
        }
    }

    // The finished maps of a build, to be applied to the stack that was planned.
    internal sealed class HeightResult
    {
        internal int Count;
        internal HeightTuning[] Tunings;
        internal HeightMaps[] Maps;
        internal uint[] Seeds;
        // The stack the plan was made from; the caller applies the result only to that one.
        internal LayerStackSource Source;
        // Milliseconds from Begin to the finished maps (worker time and the waiting of the main-thread reading together).
        internal double ElapsedMs;

        // Gives every layer its maps (null maps clear the layer, as the one-shot build does). Cheap: a version bump per layer.
        internal void ApplyTo(LayerStackSource source)
        {
            for (int l = 0; l < Count && l < source.LayerCount; l++) source.SetHeights(l, Tunings[l], Maps[l], Seeds[l]);
        }
    }

    internal enum HeightTake
    {
        // Nothing finished.
        None,
        // The result is handed out.
        Ready,
        // The build failed on the worker (logged once); the next one is tried at the next interval.
        Failed
    }

    // One height build at a time, off the main thread. The caller makes the plan (copies of everything the build reads), Begin hands it over, the
    // main thread calls Pump every frame (it reads a field that is not thread safe in slices of a budget) and TryTake until the result comes. Cancel
    // drops the build in the air: its worker finishes the layer it is on and its result is never handed out. The only state shared with a worker is
    // the Run of that build; a new Begin after Cancel starts a Run of its own.
    internal sealed class HeightJob
    {
        private const int Reading = 0, Working = 1, Ready = 2, Failed = 3;

        private sealed class Run
        {
            internal volatile bool Cancelled;
            internal int State;
            internal HeightPlan Plan;
            internal HeightGather Gather;
            internal HeightResult Result;
            internal Stopwatch Watch;
            internal LayerStackSource Source;
            internal string Error;
        }

        private Run current;

        // Where the work of a build is run: the thread pool by default; the tests replace it (a worker that waits, one that runs at once).
        internal Action<Action> Schedule = work => ThreadPool.QueueUserWorkItem(delegate { work(); });
        internal HeightNoiseCache Cache = HeightNoiseCache.Shared;

        // Builds started and finished since the job was made (the tests read them).
        internal int Started, Finished, Dropped;
        internal string LastError;

        internal bool Busy
        {
            get { return current != null; }
        }

        // Starts a build. False where one is in the air. A thread safe field is read on the worker too; any other is read by Pump.
        internal bool Begin(HeightPlan plan, IWeatherField field, LayerStackSource source)
        {
            if (current != null) return false;
            var run = new Run { Plan = plan, Source = source, Watch = Stopwatch.StartNew(), Gather = plan.Gather(field) };
            current = run;
            Started++;
            bool onWorker = field == null || field.ThreadSafe;
            if (onWorker) Hand(run, true);
            return true;
        }

        // The main thread's share of a build whose field is not thread safe: reads it for at most the budget in ms, and hands the build to the
        // worker when the reading is complete. Free for any other build.
        internal void Pump(double budgetMs)
        {
            Run run = current;
            if (run == null || Volatile.Read(ref run.State) != Reading) return;
            if (run.Gather.Step(budgetMs)) Hand(run, false);
        }

        private void Hand(Run run, bool readOnWorker)
        {
            Volatile.Write(ref run.State, Working);
            Schedule(delegate
            {
                try
                {
                    if (run.Cancelled) return;
                    if (readOnWorker) run.Gather.Step(0.0);
                    HeightResult result = run.Plan.Build(run.Gather, Cache, () => run.Cancelled);
                    if (result == null) return;
                    result.Source = run.Source;
                    run.Watch.Stop();
                    result.ElapsedMs = run.Watch.Elapsed.TotalMilliseconds;
                    run.Result = result;
                    Volatile.Write(ref run.State, Ready);
                }
                catch (Exception e)
                {
                    run.Error = e.GetType().Name + ": " + e.Message;
                    Volatile.Write(ref run.State, Failed);
                }
            });
        }

        // The finished result, or the failure, of the build in the air; frees the job for the next one.
        internal HeightTake TryTake(out HeightResult result)
        {
            result = null;
            Run run = current;
            if (run == null) return HeightTake.None;
            int state = Volatile.Read(ref run.State);
            if (state == Ready)
            {
                result = run.Result;
                current = null;
                Finished++;
                return HeightTake.Ready;
            }
            if (state == Failed)
            {
                LastError = run.Error;
                current = null;
                Dropped++;
                return HeightTake.Failed;
            }
            return HeightTake.None;
        }

        // Drops the build in the air (the stack changed, a switch changed, the module stops). The job is free at once.
        internal void Cancel()
        {
            Run run = current;
            if (run == null) return;
            run.Cancelled = true;
            current = null;
            Dropped++;
        }
    }
}
