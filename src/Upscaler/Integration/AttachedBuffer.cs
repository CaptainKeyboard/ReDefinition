using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Upscaler
{
    // A command buffer of ReDefinition's that hangs on one camera at one event: made and added in AttachTo,
    // taken off and released in Detach. The masks, the clouds' motion, the input capture, the overlay and the stage
    // dump each held a buffer, its camera and this add-remove-release code of their own.
    internal sealed class AttachedBuffer
    {
        private readonly string name;
        private readonly CameraEvent when;

        // The buffer while it is attached, otherwise null; and the camera it hangs on.
        internal CommandBuffer Buffer { get; private set; }
        internal Camera Camera { get; private set; }

        // A buffer of that name for that event; nothing is made until AttachTo.
        internal AttachedBuffer(string name, CameraEvent when)
        {
            this.name = name;
            this.when = when;
        }

        // Whether the buffer hangs on that camera now.
        internal bool IsOn(Camera camera)
        {
            return Buffer != null && Camera == camera;
        }

        // A new, empty buffer on that camera, after the one held, if any, is taken off wherever it hung.
        internal CommandBuffer AttachTo(Camera camera)
        {
            Detach();
            Buffer = new CommandBuffer { name = name };
            camera.AddCommandBuffer(when, Buffer);
            Camera = camera;
            return Buffer;
        }

        // Takes the buffer off its camera, if that is still there, and releases it.
        internal void Detach()
        {
            if (Buffer == null) return;
            if (Camera != null) Camera.RemoveCommandBuffer(when, Buffer);
            Buffer.Release();
            Buffer = null;
            Camera = null;
        }
    }
}
