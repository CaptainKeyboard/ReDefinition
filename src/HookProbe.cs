using System.Text;
using ReDefinition.Api;
using ReDefinition.Core;
using UnityEngine.Rendering;
using UnityEngine;

namespace ReDefinition
{
    // Registers a handler on each of the three hooks and writes down, once per
    // scene and hook, that it was called and with what. It draws nothing.
    //
    // The hooks are the way another mod adds motion vectors, draws on the
    // upscaled image or puts an overlay on top (docs/modders/shared-foundation.md).
    // Nothing in ReDefinition uses them -- its own work reaches the rig directly --
    // so without this the path is only exercised by the example mod, outside a
    // normal game. Switched on in the diagnostics window under Debug, it says in
    // KSP.log whether each hook fires, at which size and on which camera, which is
    // what a mod author's handler would see.
    //
    // Never saved: it costs a registration and a line per scene, and it is for
    // finding out, not for playing with. The overlay hook makes a camera of its
    // own while a handler is registered, so switching this on is a test of that
    // as well.
    internal static class HookProbe
    {
        private static bool on;

        // Twice per scene: the first call, and one about two seconds later. The
        // first is the frame the rig was built in, where the jitter is zero by
        // design; a mod's handler sees the settled frame, which the second line
        // is.
        private const int SettledAfter = 120;

        private static int motionVectorsSaid = -1;
        private static int afterUpscalingSaid = -1;
        private static int overlaySaid = -1;
        private static int motionVectorsFirst;
        private static int afterUpscalingFirst;
        private static int overlayFirst;

        internal static bool On
        {
            get { return on; }
        }

        internal static void Set(bool wanted)
        {
            if (wanted == on) return;
            on = wanted;
            if (wanted)
            {
                motionVectorsSaid = -1;
                afterUpscalingSaid = -1;
                overlaySaid = -1;
                motionVectorsFirst = 0;
                afterUpscalingFirst = 0;
                overlayFirst = 0;
                Hooks.RegisterMotionVectors(MotionVectors);
                Hooks.RegisterAfterUpscaling(AfterUpscaling);
                Hooks.RegisterOverlay(Overlay);
                Debug.Log(Log.Tag + " Hook probe on: a handler is registered on all three hooks, and says in this log"
                                  + " when each is called. It draws nothing.");
                return;
            }

            Hooks.UnregisterMotionVectors(MotionVectors);
            Hooks.UnregisterAfterUpscaling(AfterUpscaling);
            Hooks.UnregisterOverlay(Overlay);
            Debug.Log(Log.Tag + " Hook probe off.");
        }

        // The frame's state as a mod's handler reads it, which is what tells a
        // handler what it is looking at.
        private static string Frames()
        {
            return "upscaler " + (Frame.UpscalerActive ? Frame.Technique : "off")
                   + ", frame generation " + (Frame.FrameGenerationActive ? "on" : "off")
                   + ", render " + Size(Frame.RenderSize) + ", display " + Size(Frame.DisplaySize)
                   + ", jitter " + Frame.Jitter.x.ToString("0.00") + "/" + Frame.Jitter.y.ToString("0.00")
                   + (Frame.HistoryReset ? ", history reset (" + Frame.HistoryResetReason + ")" : "");
        }

        private static string Size(Vector2Int size)
        {
            return size.x + "x" + size.y;
        }

        private static string Of(RenderTexture texture)
        {
            return texture == null ? "none" : texture.width + "x" + texture.height + " " + texture.format;
        }

        // The first call in a scene, and one once the frame has settled: a line
        // every frame would fill the log, and one line alone would say nothing
        // about the frames that follow it.
        private static bool Say(ref int said, ref int first)
        {
            int scene = (int)HighLogic.LoadedScene;
            int frame = Time.frameCount;
            if (said != scene)
            {
                said = scene;
                first = frame;
                return true;
            }

            if (first == 0 || frame - first < SettledAfter) return false;
            first = 0;
            return true;
        }

        private static string At()
        {
            return "frame " + Time.frameCount + ": ";
        }

        private static void MotionVectors(CommandBuffer buffer, RenderTexture motionVectors, RenderTexture depth,
                                          Camera scene)
        {
            if (!Say(ref motionVectorsSaid, ref motionVectorsFirst)) return;
            StringBuilder sb = new StringBuilder();
            sb.Append(Log.Tag).Append(" Hook probe, motion vectors, ").Append(At()).Append("called on ")
              .Append(scene == null ? "no camera" : scene.name)
              .Append(", buffer ").Append(buffer == null ? "none" : buffer.name)
              .Append(", motion vectors ").Append(Of(motionVectors))
              .Append(", depth ").Append(Of(depth))
              .Append(" -- ").Append(Frames());
            Debug.Log(sb.ToString());
        }

        private static void AfterUpscaling(CommandBuffer buffer, RenderTexture image, Camera scene)
        {
            if (!Say(ref afterUpscalingSaid, ref afterUpscalingFirst)) return;
            Debug.Log(Log.Tag + " Hook probe, after upscaling, " + At() + "called on "
                      + (scene == null ? "no camera" : scene.name)
                      + ", image " + Of(image) + " -- " + Frames());
        }

        private static void Overlay(CommandBuffer buffer, Camera scene)
        {
            if (!Say(ref overlaySaid, ref overlayFirst)) return;
            Debug.Log(Log.Tag + " Hook probe, overlay, " + At() + "called on "
                      + (scene == null ? "no camera" : scene.name) + " -- " + Frames());
        }
    }
}
