using System;
using System.Runtime.InteropServices;
using UnityEngine.Rendering;

namespace ReDefinition
{
    // Packets for a render event of the dxgi.dll proxy, each in a slot of its own:
    // the render thread reads a packet some frames after the main thread wrote it.
    // Eight slots exceed how far the main thread runs ahead for one packet a frame;
    // more for more. Never freed: the render thread may still read a slot after the
    // rig is gone. For frame generation's packets (FrameGenerationBridge), the
    // upscalers' in the proxy (NativeUpscalerLink) and Direct3D 12 for mods
    // (D3d12Bridge).
    internal sealed class PacketRing
    {
        private readonly IntPtr memory;
        private readonly int slots;
        private int next;

        internal PacketRing(int packetSize, int slots = 8)
        {
            PacketSize = packetSize;
            this.slots = slots;
            memory = Marshal.AllocHGlobal(packetSize * slots);
        }

        internal int PacketSize { get; }

        // The packet into the next slot, and the event with the slot into the
        // buffer. One larger than the slots is not sent.
        internal bool Issue<T>(CommandBuffer buffer, IntPtr renderEvent, int eventId, ref T packet) where T : struct
        {
            if (Marshal.SizeOf<T>() > PacketSize) return false;
            IntPtr slot = new IntPtr(memory.ToInt64() + (long)next * PacketSize);
            next = (next + 1) % slots;
            Marshal.StructureToPtr(packet, slot, false);
            buffer.IssuePluginEventAndData(renderEvent, eventId, slot);
            return true;
        }
    }
}
