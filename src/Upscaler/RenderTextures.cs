using System;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // The render textures of the upscaler and the passes around it: made, checked
    // and released.
    internal static class RenderTextures
    {
        internal static void Release(ref RenderTexture texture)
        {
            if (texture == null) return;
            texture.Release();
            UnityEngine.Object.Destroy(texture);
            texture = null;
        }

        // `texture` at `size` and `format`, made anew where it is not; false where it
        // could not be made. `created` where it was made now.
        internal static bool Ensure(ref RenderTexture texture, Vector2Int size, RenderTextureFormat format, string name, out bool created)
        {
            created = false;
            if (texture != null && texture.width == size.x && texture.height == size.y && texture.format == format
                && texture.IsCreated()) return true;
            Release(ref texture);
            texture = new RenderTexture(size.x, size.y, 0, format, RenderTextureReadWrite.Linear)
            {
                name = name,
                filterMode = FilterMode.Point,
            };
            if (!texture.Create() || texture.GetNativeTexturePtr() == IntPtr.Zero)
            {
                Release(ref texture);
                return false;
            }
            created = true;
            return true;
        }

        // Unity's Create() answers true even where Direct3D could not make the
        // texture: it writes "D3D11: Failed to create RenderTexture ... 0x8007000e"
        // into the log and carries on. Measured in the game on 2026-09-20: the rig
        // was set up on textures that were never made, the cameras were redirected
        // into them, and the player got a black screen until the upscaler was
        // switched off and on. The native handle is what says a texture is there.
        internal static bool Made(RenderTexture texture)
        {
            if (texture == null) return false;
            if (!texture.Create() && !texture.IsCreated()) return false;
            return texture.GetNativeTexturePtr() != IntPtr.Zero;
        }

        // Made and not lost since (IsCreated).
        internal static bool Created(RenderTexture texture)
        {
            return texture != null && texture.IsCreated();
        }

        // With a native handle: the graphics API holds the texture.
        internal static bool Native(RenderTexture texture)
        {
            return texture != null && texture.GetNativeTexturePtr() != IntPtr.Zero;
        }
    }
}
