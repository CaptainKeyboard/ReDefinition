using System;
using System.Collections.Generic;
using System.Threading;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // GpuSlew: the slew limit of the layer maps on the GPU instead of a step of every texel on the CPU every frame.
    //
    // The old path keeps one shown copy of each map and, every frame while any texel is short of its goal, steps all of it (MapSlew.StepMap), converts it and uploads it whole (16 MB a
    // frame for four layers). Here a map is a pair of byte maps in one RG16 array slice, r = from (the value the map showed when its newest goal arrived) and g = to (that goal),
    // and the shader shows from + clamp(to - from, -reach, +reach) with reach = rate * (seconds since the goal arrived), a share the store hands over as a uniform each frame
    // (ReachVector): MapSlewField is the same rule on the CPU. The CPU never steps a texel. A new goal costs one upload of its slice, about once a second (OwnCloudPlan.MapIntervalS),
    // and the pair for it is made by the store's own worker thread: "from" of the new pair is the value the old pair shows at the moment the new pair goes live, which the worker
    // computes ahead for that moment (epoch = the store's clock + SlewLeadS); the main thread uploads the pair at the first frame at or after the epoch, so the shown value is
    // continuous through the hand-over (the shader's age is measured from the epoch, not from the upload, so a late upload shifts the slew by at most the lateness, a few levels at the
    // slowest). Per frame the main thread does the clock, at most SlewApplyPerFrame uploads of one slice (staging texture, then a GPU copy into the array's slice) and nothing else.
    internal sealed partial class LayerMapStore
    {
        private SlewEngine engine;

        // True: the maps slew on the GPU (Ensure's third argument).
        internal bool GpuSlew { get; private set; }

        // Seconds ahead of the worker's finish at which a new pair takes over (the worker needs milliseconds; the rest is the frames until the upload).
        internal static float SlewLeadS = 0.25f;

        // The most slices uploaded in one frame.
        internal static int SlewApplyPerFrame = 2;

        // The shared slew's reach per layer (shares of full scale, 0 to 1) of the coverage (kind 0) or type (kind 1) arrays: what the shader's slew may have moved each layer's texels.
        // Zero without GpuSlew (the arrays then hold the shown value in both channels).
        internal Vector4 ReachVector(int kind)
        {
            if (ReachPinned != null) return ReachPinned[kind == 0 ? 0 : 1];
            return engine != null ? engine.Reach(kind) : Vector4.zero;
        }

        // The weather player's replay of a pinned capture: the game's reach of the coverage (0) and type (1) arrays, handed to the shader as the game's frame had them. The
        // player's maps hold the shown value in both channels, so the shown value does not depend on it; null (the game, every other scene): the store's own.
        internal Vector4[] ReachPinned;

        // A store of the GpuSlew mode without textures (the tests): the uploads go to the hook, which gets the slot (kind * MaxLayers + layer) and the RG16 pairs and must copy them.
        internal static LayerMapStore ForTest(int width, int height, Action<int, byte[]> uploadHook)
        {
            var s = new LayerMapStore { Width = width, Height = height, GpuSlew = true };
            s.engine = new SlewEngine(s, uploadHook);
            return s;
        }

        // The goals still being made or waiting for their upload (GpuSlew); 0 without it.
        internal int SlewPending
        {
            get { return engine != null ? engine.Pending() : 0; }
        }

        // Blocks until every goal given so far is live (the tests and the player; the game does not call it).
        internal void SlewFlush()
        {
            if (engine != null) engine.Flush();
        }

        private sealed class SlewState
        {
            internal byte[] From, To;
            internal double Epoch;
        }

        private sealed class SlewSlot
        {
            internal SlewState Live, Spare, Ready;
            internal byte[] ReadyPairs;
            internal long Version;
        }

        private sealed class SlewJob
        {
            internal int Slot;
            internal byte[] Goal;
        }

        private sealed class SlewEngine
        {
            private const int MaxReady = 3;
            private readonly LayerMapStore store;
            private readonly int count;
            private readonly object gate = new object();
            private readonly SlewSlot[] slots = new SlewSlot[2 * CloudLayerDef.MaxLayers];
            private readonly Queue<SlewJob> jobs = new Queue<SlewJob>();
            private readonly Queue<int> readyOrder = new Queue<int>();
            private readonly Stack<byte[]> bytePool = new Stack<byte[]>();
            private readonly Stack<byte[]> pairPool = new Stack<byte[]>();
            private readonly Texture2D[] staging = new Texture2D[2];
            private readonly bool copyOk;
            private int stagingNext;
            private double clock;
            private float rate = MapSlew.DefaultRate;
            private bool stopping, busy;
            private int readyCount;
            private Thread thread;
            internal readonly byte[] ScratchGoal;

            private readonly Action<int, byte[]> hook;

            // hook: the tests' stand-in for the GPU (no Unity objects are made or touched): it gets the slot and the pairs of each upload, and must copy them.
            internal SlewEngine(LayerMapStore owner, Action<int, byte[]> uploadHook = null)
            {
                store = owner;
                hook = uploadHook;
                count = owner.Width * owner.Height;
                ScratchGoal = new byte[count];
                for (int i = 0; i < slots.Length; i++) slots[i] = new SlewSlot();
                if (hook != null) return;
                copyOk = MakeStaging(owner.Width, owner.Height);
            }

            // The Unity side of the constructor, apart so that a test engine (no hook-less use) never loads it.
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            private bool MakeStaging(int width, int height)
            {
                CopyTextureSupport support = SystemInfo.copyTextureSupport;
                bool ok = (support & CopyTextureSupport.Basic) != 0 && (support & CopyTextureSupport.DifferentTypes) != 0;
                if (ok)
                    for (int i = 0; i < staging.Length; i++)
                        staging[i] = new Texture2D(width, height, TextureFormat.RG16, false, true) { name = "layer map staging " + i, filterMode = FilterMode.Point };
                return ok;
            }

            internal void Stop()
            {
                lock (gate)
                {
                    stopping = true;
                    Monitor.PulseAll(gate);
                }
                if (hook == null) DestroyStaging();
            }

            // The Unity calls are in methods of their own (a method that holds one cannot be compiled in a test, where no engine runs).
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            private void DestroyStaging()
            {
                for (int i = 0; i < staging.Length; i++)
                    if (staging[i] != null) UnityEngine.Object.Destroy(staging[i]);
            }

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            private void ApplyArrays()
            {
                store.Coverage.Apply(false);
                store.Types.Apply(false);
            }

            private byte[] Rent(Stack<byte[]> pool, int length)
            {
                lock (gate)
                {
                    if (pool.Count > 0) return pool.Pop();
                }
                return new byte[length];
            }

            private void Return(Stack<byte[]> pool, byte[] array)
            {
                if (array == null) return;
                lock (gate) pool.Push(array);
            }

            // Hands a goal over: copies it (the caller's array is free at once) and queues it for the worker. Any thread.
            internal void Stage(int kind, int layer, byte[] bytes)
            {
                if (bytes == null || bytes.Length != count) throw new ArgumentException("The map has " + (bytes == null ? 0 : bytes.Length) + " texels, the store " + count + ".");
                byte[] goal = Rent(bytePool, count);
                Buffer.BlockCopy(bytes, 0, goal, 0, count);
                lock (gate)
                {
                    if (stopping) return;
                    jobs.Enqueue(new SlewJob { Slot = kind * CloudLayerDef.MaxLayers + layer, Goal = goal });
                    if (thread == null)
                    {
                        thread = new Thread(Run) { IsBackground = true, Name = "ReDefinition layer map slew" };
                        thread.Start();
                    }
                    Monitor.PulseAll(gate);
                }
            }

            private void Run()
            {
                while (true)
                {
                    SlewJob job;
                    lock (gate)
                    {
                        while (jobs.Count == 0 && !stopping) Monitor.Wait(gate);
                        if (stopping) return;
                        job = jobs.Dequeue();
                        busy = true;
                    }
                    try
                    {
                        Process(job);
                    }
                    catch (Exception e)
                    {
                        AtmospheresLog.Say("layer map slew: a goal was dropped: " + e.Message);
                    }
                    lock (gate)
                    {
                        busy = false;
                        Monitor.PulseAll(gate);
                    }
                }
            }

            private void Process(SlewJob job)
            {
                SlewSlot s = slots[job.Slot];
                SlewState live, spare;
                double now;
                float r;
                lock (gate)
                {
                    // One goal of a slice is in flight at a time, and a few slices at most wait for their upload.
                    while (!stopping && (s.Ready != null || readyCount >= MaxReady)) Monitor.Wait(gate);
                    if (stopping) return;
                    live = s.Live;
                    spare = s.Spare;
                    s.Spare = null;
                    now = clock;
                    r = rate;
                }
                var st = spare ?? new SlewState();
                if (st.To != null) Return(bytePool, st.To);
                if (st.From == null) st.From = Rent(bytePool, count);
                st.To = job.Goal;
                if (live == null)
                {
                    // The first goal of a slice jumps.
                    Buffer.BlockCopy(job.Goal, 0, st.From, 0, count);
                    st.Epoch = now;
                }
                else
                {
                    double epoch = Math.Max(live.Epoch, now + SlewLeadS);
                    MapSlewField.Handover(live.From, live.To, MapSlewField.Reach01(r, epoch - live.Epoch), st.From);
                    st.Epoch = epoch;
                }
                byte[] pairs = Rent(pairPool, 2 * count);
                MapSlewField.Interleave(st.From, st.To, pairs);
                lock (gate)
                {
                    s.Ready = st;
                    s.ReadyPairs = pairs;
                    readyOrder.Enqueue(job.Slot);
                    readyCount++;
                    Monitor.PulseAll(gate);
                }
            }

            // Main thread: advances the clock and puts the pairs that are due on the GPU. snap: every goal given so far takes over at once and every map shows its goal.
            internal int Step(float dt, float newRate, bool snap)
            {
                lock (gate)
                {
                    if (newRate > 0f) rate = newRate;
                    if (dt > 0f) clock += dt;
                }
                if (snap)
                {
                    Flush();
                    SnapAll();
                }
                else
                {
                    ApplyReady(false);
                }
                return Pending();
            }

            internal int Pending()
            {
                lock (gate) return jobs.Count + readyCount + (busy ? 1 : 0);
            }

            internal void Flush()
            {
                while (true)
                {
                    lock (gate)
                    {
                        if (jobs.Count == 0 && !busy) break;
                    }
                    ApplyReady(true);
                    lock (gate)
                    {
                        if (jobs.Count == 0 && !busy) break;
                        Monitor.Wait(gate, 2);
                    }
                }
                ApplyReady(true);
            }

            private void ApplyReady(bool all)
            {
                int applied = 0;
                while (true)
                {
                    int slot;
                    SlewSlot s;
                    SlewState st;
                    byte[] pairs;
                    lock (gate)
                    {
                        if (readyOrder.Count == 0) break;
                        slot = readyOrder.Peek();
                        s = slots[slot];
                        st = s.Ready;
                        if (!all && ((st.Epoch > clock && s.Live != null) || applied >= SlewApplyPerFrame)) break;
                        readyOrder.Dequeue();
                        pairs = s.ReadyPairs;
                    }
                    Upload(slot, pairs);
                    lock (gate)
                    {
                        SlewState old = s.Live;
                        if (s.Spare != null) { Release(s.Spare); }
                        s.Live = st;
                        s.Spare = old;
                        s.Ready = null;
                        s.ReadyPairs = null;
                        s.Version++;
                        readyCount--;
                        pairPool.Push(pairs);
                        Monitor.PulseAll(gate);
                    }
                    applied++;
                }
                if (!copyOk && hook == null && applied > 0) ApplyArrays();
            }

            // Both arrays of a state back to the pool (under the gate).
            private void Release(SlewState state)
            {
                if (state.From != null) bytePool.Push(state.From);
                if (state.To != null) bytePool.Push(state.To);
                state.From = state.To = null;
            }

            // Every slice shows its goal: a cut. A new state is made (the old arrays may be read by the worker).
            private void SnapAll()
            {
                for (int slot = 0; slot < slots.Length; slot++)
                {
                    SlewSlot s = slots[slot];
                    SlewState live = s.Live;
                    if (live == null) continue;
                    var st = new SlewState { From = Rent(bytePool, count), To = Rent(bytePool, count), Epoch = clock };
                    Buffer.BlockCopy(live.To, 0, st.From, 0, count);
                    Buffer.BlockCopy(live.To, 0, st.To, 0, count);
                    byte[] pairs = Rent(pairPool, 2 * count);
                    MapSlewField.InterleaveSame(st.To, pairs);
                    Upload(slot, pairs);
                    lock (gate)
                    {
                        if (s.Spare != null) Release(s.Spare);
                        s.Live = st;
                        s.Spare = live;
                        s.Version++;
                        pairPool.Push(pairs);
                    }
                }
                if (!copyOk && hook == null) ApplyArrays();
            }

            private void Upload(int slot, byte[] pairs)
            {
                if (hook != null)
                {
                    hook(slot, pairs);
                    return;
                }
                UploadGpu(slot, pairs);
            }

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
            private void UploadGpu(int slot, byte[] pairs)
            {
                int kind = slot / CloudLayerDef.MaxLayers, layer = slot % CloudLayerDef.MaxLayers;
                Texture2DArray array = kind == 0 ? store.Coverage : store.Types;
                if (copyOk)
                {
                    Texture2D target = staging[stagingNext];
                    stagingNext ^= 1;
                    target.SetPixelData(pairs, 0, 0);
                    target.Apply(false, false);
                    Graphics.CopyTexture(target, 0, 0, array, layer, 0);
                }
                else
                {
                    array.SetPixelData(pairs, 0, layer, 0);
                }
            }

            internal Vector4 Reach(int kind)
            {
                var v = new Vector4();
                for (int l = 0; l < CloudLayerDef.MaxLayers; l++)
                {
                    SlewState live = slots[kind * CloudLayerDef.MaxLayers + l].Live;
                    v[l] = live == null ? 0f : MapSlewField.Reach01(rate, clock - live.Epoch);
                }
                return v;
            }

            private SlewState LiveOf(int kind, int layer)
            {
                return slots[kind * CloudLayerDef.MaxLayers + layer].Live;
            }

            internal long Version(int kind, int layer)
            {
                return slots[kind * CloudLayerDef.MaxLayers + layer].Version;
            }

            internal byte[] GoalBytes(int kind, int layer)
            {
                SlewState live = LiveOf(kind, layer);
                return live == null ? null : live.To;
            }

            internal bool Pair(int kind, int layer, out byte[] from, out byte[] to)
            {
                SlewState live = LiveOf(kind, layer);
                from = live == null ? null : live.From;
                to = live == null ? null : live.To;
                return live != null;
            }

            // The shown map as bytes, made now.
            internal byte[] NowBytes(int kind, int layer)
            {
                var now = new byte[count];
                SlewState live = LiveOf(kind, layer);
                if (live != null) MapSlewField.Handover(live.From, live.To, MapSlewField.Reach01(rate, clock - live.Epoch), now);
                return now;
            }

            internal bool Sample(int layer, double latDeg, double lonDeg, bool goal, out float coverage, out float type)
            {
                coverage = SampleKind(0, layer, latDeg, lonDeg, goal);
                type = SampleKind(1, layer, latDeg, lonDeg, goal);
                return true;
            }

            // Bilinear like the shader's read of the array, then the slew on the two reads: the shader's order.
            private float SampleKind(int kind, int layer, double latDeg, double lonDeg, bool goal)
            {
                SlewState live = LiveOf(kind, layer);
                if (live == null) return 0f;
                float to = store.Bilinear(live.To, latDeg, lonDeg);
                if (goal) return to;
                float from = store.Bilinear(live.From, latDeg, lonDeg);
                float reach = MapSlewField.Reach01(rate, clock - live.Epoch);
                return from + Math.Max(-reach, Math.Min(reach, to - from));
            }
        }
    }
}
