using System;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Gives a render target back: Release frees its GPU memory, Destroy the native object behind it. A target that is only released stays
    // alive as an object, so a resolution that changes (dynamic render scale) would leave one native object per change. Every place that
    // drops a render target of the layer pass calls Dispose, and no place calls Release by itself (a test reads the folder for that).
    internal static class RenderTargets
    {
        // What a release and a destroy do; a test replaces them (the engine's calls cannot run outside the game).
        // (The engine's == also counts a destroyed object as null, so the defaults skip one that is gone already.)
        internal static Action<RenderTexture> Release = rt => { if (rt != null) rt.Release(); };
        internal static Action<UnityEngine.Object> Destroy = o => { if (o != null) UnityEngine.Object.Destroy(o); };

        // Releases and destroys a target; nothing for a reference that is null.
        internal static void Dispose(RenderTexture rt)
        {
            if (ReferenceEquals(rt, null)) return;
            Release(rt);
            Destroy(rt);
        }
    }
}
