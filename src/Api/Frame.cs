using System;
using ReDefinition.Shared;
using UnityEngine;

namespace ReDefinition.Api
{
    /// <summary>
    /// The frame's state, decided once per frame before its first scene camera culls; the same as the
    /// shader globals <c>_ReDefinition_Frame</c>, <c>_ReDefinition_RenderSize</c>,
    /// <c>_ReDefinition_DisplaySize</c>, <c>_ReDefinition_Jitter</c> and
    /// <c>_ReDefinition_OriginShift</c> (ReDefinition.cginc). Read it from rendering code -- camera
    /// callbacks, command buffers, <c>OnRenderImage</c>; read in <c>Update</c>, it is the frame before's.
    /// </summary>
    public static class Frame
    {
        /// <summary>
        /// Whether ReDefinition's upscaler reconstructs this frame, in any mode.
        /// </summary>
        public static bool UpscalerActive
        {
            get { return SharedFrame.UpscalerActive; }
        }

        /// <summary>
        /// Whether frame generation receives this frame's inputs.
        /// </summary>
        public static bool FrameGenerationActive
        {
            get { return SharedFrame.FrameGenerationActive; }
        }

        /// <summary>
        /// <c>"FSR 3"</c>, <c>"DLSS"</c> or <c>"AMD FSR (DLL)"</c> while the upscaler is active; null
        /// otherwise.
        /// </summary>
        public static string Technique
        {
            get { return SharedFrame.Technique; }
        }

        /// <summary>
        /// The size the 3D cameras render at; the screen's without the upscaler.
        /// </summary>
        public static Vector2Int RenderSize
        {
            get { return SharedFrame.RenderSize; }
        }

        /// <summary>
        /// The size of the image shown; the screen's.
        /// </summary>
        public static Vector2Int DisplaySize
        {
            get { return SharedFrame.DisplaySize; }
        }

        /// <summary>
        /// The jitter the 3D cameras render with this frame, in pixels at render size, as the upscaler is
        /// told; zero without it.
        /// </summary>
        public static Vector2 Jitter
        {
            get { return SharedFrame.Jitter; }
        }

        /// <summary>
        /// The same jitter in normalized device coordinates, as ReDefinition adds it to each camera's
        /// projection; the projection without it is <c>Camera.nonJitteredProjectionMatrix</c>.
        /// </summary>
        public static Vector2 JitterNdc
        {
            get { return SharedFrame.JitterNdc; }
        }

        /// <summary>
        /// Whether temporal history is to be dropped this frame: a cut of KSP's flight camera, or a mod's
        /// <see cref="RequestHistoryReset"/>.
        /// </summary>
        public static bool HistoryReset
        {
            get { return SharedFrame.ResetReason != null; }
        }

        /// <summary>
        /// Why <see cref="HistoryReset"/> is true; null when it is not.
        /// </summary>
        public static string HistoryResetReason
        {
            get { return SharedFrame.ResetReason; }
        }

        /// <summary>
        /// The floating origin's shifts since the frame before, summed: KSP moved the active vessel, the
        /// camera and nearby objects by minus this.
        /// </summary>
        public static Vector3d OriginShift
        {
            get { return SharedFrame.OriginShift; }
        }

        /// <summary>
        /// The same for bodies and landed or packed vessels, which KSP moves by the Krakensbane step too.
        /// </summary>
        public static Vector3d BodyShift
        {
            get { return SharedFrame.BodyShift; }
        }

        /// <summary>
        /// Whether the floating origin shifted since the frame before.
        /// </summary>
        public static bool OriginShifted
        {
            get { return SharedFrame.Shifted; }
        }

        /// <summary>
        /// Reports a cut the mod made -- a camera switch, a jump. Before the frame's first scene camera
        /// culls, it reaches every mod in that frame; later, the upscaler and frame generation in that
        /// frame and the mods in the next.
        /// </summary>
        /// <param name="reason">For the log and <see cref="HistoryResetReason"/>.</param>
        public static void RequestHistoryReset(string reason)
        {
            SharedFrame.RequestHistoryReset(reason);
        }

        /// <summary>
        /// Calls <paramref name="handler"/> with the reason whenever <see cref="HistoryReset"/> turns true
        /// for a frame.
        /// </summary>
        /// <param name="handler">Called as the frame begins; removed if it throws.</param>
        public static void RegisterHistoryReset(Action<string> handler)
        {
            SharedFrame.HistoryResetHandlers.Add(handler);
        }

        /// <summary>
        /// Removes a handler registered with <see cref="RegisterHistoryReset"/>.
        /// </summary>
        /// <param name="handler">The handler registered.</param>
        public static void UnregisterHistoryReset(Action<string> handler)
        {
            SharedFrame.HistoryResetHandlers.Remove(handler);
        }
    }
}
