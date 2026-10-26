using System;
using System.Runtime.InteropServices;
using UnityEngine.Rendering;

namespace ReDefinition.Proxy
{
    // A packet a ring sends: copied into the one boxed copy the ring keeps of its type
    // (PacketRing.Issue), as this = packet. Marshal.StructureToPtr takes an object, and
    // boxing every packet anew would allocate with each of them, in every frame.
    internal interface IRingPacket<T> where T : struct
    {
        // This packet becomes a copy of the one given.
        void CopyFrom(ref T packet);
    }

    // Packets for a render event of the dxgi.dll proxy, each in a slot of its own:
    // the render thread reads a packet some frames after the main thread wrote it.
    // Eight slots exceed how far the main thread runs ahead for one packet a frame;
    // more for more. Never freed: the render thread may still read a slot after the
    // rig is gone. For frame generation's packets (FrameGenerationBridge), the
    // upscalers' in the proxy (NativeUpscalerLink) and Direct3D 12 for mods
    // (D3d12Bridge).
    internal sealed class PacketRing
    {
        // The slots' unmanaged memory, how many slots there are, and the slot the next packet goes into.
        private readonly IntPtr memory;
        private readonly int slots;
        private int next;

        // Slots of packetSize bytes each, allocated at once.
        internal PacketRing(int packetSize, int slots = 8)
        {
            PacketSize = packetSize;
            this.slots = slots;
            memory = Marshal.AllocHGlobal(packetSize * slots);
        }

        // A slot's size in bytes: the largest packet the ring sends.
        internal int PacketSize { get; }

        // The packet into the next slot, and the event with the slot into the
        // buffer. One larger than the slots is not sent.
        internal bool Issue<T>(CommandBuffer buffer, IntPtr renderEvent, int eventId, ref T packet)
            where T : struct, IRingPacket<T>
        {
            if (Marshal.SizeOf<T>() > PacketSize) return false;
            IntPtr slot = new IntPtr(memory.ToInt64() + (long)next * PacketSize);
            next = (next + 1) % slots;
            object boxed = Boxed<T>.Value;
            if (boxed == null) Boxed<T>.Value = boxed = packet;
            else ((IRingPacket<T>)boxed).CopyFrom(ref packet);
            Marshal.StructureToPtr(boxed, slot, false);
            buffer.IssuePluginEventAndData(renderEvent, eventId, slot);
            return true;
        }

        // Per packet type, for every ring: the main thread's only, as Issue is.
        private static class Boxed<T> where T : struct
        {
            // The one boxed copy of the type, made at its first packet.
            internal static object Value;
        }
    }
}
