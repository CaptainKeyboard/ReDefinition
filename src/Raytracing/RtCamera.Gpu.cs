using System;
using System.Collections.Generic;
using ReDefinition.Core;
using ReDefinition.Proxy;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Raytracing
{
    // The part of RtCamera that gathers the GPU's instances for the proxy and counts the places each buffer took.
    internal sealed partial class RtCamera
    {
        // The GPU's instances gathered for the proxy (ReDefinitionRtScatter.compute), and
        // the append buffers' counts.
        private ComputeShader gather;
        private int gatherKernel = -1;
        private bool gatherLooked;
        // A read-back of the counts: the buffers it counted, in their order. Kept and
        // used again, with its callback, so that a frame's request allocates nothing.
        private sealed class CountRead
        {
            // The buffers counted in their order, how many of them, and the callback that reads the counts (CountsRead).
            public ComputeBuffer[] Buffers = new ComputeBuffer[64];
            public int Count;
            public Action<AsyncGPUReadbackRequest> Done;
        }

        // The read-backs in flight, and those done.
        private readonly List<CountRead> countReads = new List<CountRead>();
        private readonly Stack<CountRead> idleCountReads = new Stack<CountRead>();
        private RenderTexture gpuInstances;
        private IntPtr gpuInstancesHandle;
        private ComputeBuffer gpuCounts;

        // The GPU's instances of this frame into the texture the proxy reads them from,
        // relative to the camera; false where there are none.
        private bool GatherGpuInstances()
        {
            int total = Scene.GpuTotal;
            if (total == 0) return false;
            if (!gatherLooked)
            {
                // Once: a kernel that did not compile for this device throws each time.
                gatherLooked = true;
                gather = ComputeSource("ReDefinitionRtScatter");
                try
                {
                    gatherKernel = gather != null ? gather.FindKernel("Gather") : -1;
                }
                catch (Exception)
                {
                    gatherKernel = -1;
                }
            }
            if (gatherKernel < 0) return false;
            int rows = (total + RtNative.InstancesPerRow - 1) / RtNative.InstancesPerRow;
            if (gpuInstances == null || gpuInstances.height < rows)
            {
                if (gpuInstances != null)
                {
                    gpuInstances.Release();
                    Destroy(gpuInstances);
                }
                gpuInstances = new RenderTexture(RtNative.InstancesPerRow * RtNative.InstanceTexels, Math.Max(rows, 16), 0, RenderTextureFormat.ARGBFloat,
                                                 RenderTextureReadWrite.Linear)
                {
                    name = "ReDefinition raytracing GPU instances",
                    enableRandomWrite = true,
                    filterMode = FilterMode.Point,
                };
                gpuInstances.Create();
                // Once: GetNativeTexturePtr waits for the render thread.
                gpuInstancesHandle = gpuInstances.GetNativeTexturePtr();
            }
            int items = Scene.GpuItems.Count;
            if (gpuCounts == null || gpuCounts.count < items)
            {
                gpuCounts?.Release();
                gpuCounts = new ComputeBuffer(Math.Max(items, 64), 4, ComputeBufferType.Raw);
            }
            Vector3 eye = transform.position;
            command.SetComputeVectorParam(gather, GatherEye, new Vector4(eye.x, eye.y, eye.z, 0f));
            command.SetComputeTextureParam(gather, gatherKernel, GatherOut, gpuInstances);
            command.SetComputeBufferParam(gather, gatherKernel, GatherCounts, gpuCounts);
            // How many places each buffer took, for the next frames' reservations.
            CountRead counted = null;
            if (SystemInfo.supportsAsyncGPUReadback && countReads.Count < 4)
            {
                counted = idleCountReads.Count > 0 ? idleCountReads.Pop() : NewCountRead();
                if (counted.Buffers.Length < items) counted.Buffers = new ComputeBuffer[items];
                counted.Count = items;
            }
            for (int i = 0; i < items; i++)
            {
                RtGpuItem item = Scene.GpuItems[i];
                if (counted != null) counted.Buffers[i] = item.Transforms;
                int places = Scene.GpuPlaces[i];
                command.CopyCounterValue(item.Transforms, gpuCounts, (uint)(i * 4));
                command.SetComputeBufferParam(gather, gatherKernel, GatherSource, item.Transforms);
                command.SetComputeIntParam(gather, GatherCountIndex, i);
                command.SetComputeIntParam(gather, GatherCapacity, places);
                command.SetComputeIntParam(gather, GatherFirst, Scene.GpuFirst[i]);
                command.SetComputeVectorParam(gather, GatherReach, item.Reach);
                command.DispatchCompute(gather, gatherKernel, (places + 63) / 64, 1, 1);
            }
            if (counted != null)
            {
                countReads.Add(counted);
                command.RequestAsyncReadback(gpuCounts, items * 4, 0, counted.Done);
            }
            return true;
        }

        // A read-back with its own callback, made once and used again.
        private CountRead NewCountRead()
        {
            var read = new CountRead();
            read.Done = request => CountsRead(read, request);
            return read;
        }

        // How many places each GPU buffer took, handed to the scene for the next reservations; the read-back then idle.
        private void CountsRead(CountRead read, AsyncGPUReadbackRequest request)
        {
            countReads.Remove(read);
            if (!request.hasError)
            {
                Unity.Collections.NativeArray<uint> taken = request.GetData<uint>();
                for (int i = 0; i < read.Count && i < taken.Length; i++)
                    Scene.NoteGpuTaken(read.Buffers[i], (int)taken[i]);
            }
            Array.Clear(read.Buffers, 0, read.Count);
            read.Count = 0;
            idleCountReads.Push(read);
        }

        // Releases the GPU instances' texture and the counts' buffer.
        private void ReleaseGpuInstances()
        {
            if (gpuInstances != null)
            {
                gpuInstances.Release();
                Destroy(gpuInstances);
            }
            gpuInstances = null;
            gpuInstancesHandle = IntPtr.Zero;
            gpuCounts?.Release();
            gpuCounts = null;
        }
    }
}
