using System.Text;
using ReDefinition.Api;
using ReDefinition.Core;
using UnityEngine.Rendering;
using UnityEngine;

namespace ReDefinition.Diagnostics
{
    // Registers a handler on each of the three hooks and writes down, once per
    // scene and hook, that it was called and with what. It draws nothing.
    //
    // The hooks are the way another mod adds motion vectors, draws on the
    // upscaled image or puts an overlay on top (docs/modders/shared-foundation.md).
    // Nothing in ReDefinition uses them -- its own work reaches the rig directly --
    // so without this the path is only exercised by the example mod, outside a
    // normal game. Switched on in the diagnostics window under Capture & log, it says in
    // KSP.log whether each hook fires, at which size and on which camera, which is
    // what a mod author's handler would see.
    //
    // Never saved: it costs a registration and a line per scene, and it is for
    // finding out, not for playing with. The overlay hook makes a camera of its
    // own while a handler is registered, so switching this on is a test of that
    // as well.
    internal static class HookProbe
    {
        // Whether the handlers are registered.
        private static bool on;

        // Twice per scene: the first call, and one about two seconds later. The
        // first is the frame the rig was built in, where the jitter is zero by
        // design; a mod's handler sees the settled frame, which the second line
        // is.
        private const int SettledAfter = 120;

        // Per hook: the scene it last wrote its first line in (-1 for none yet), and the frame of that first line, 0
        // once the settled line is written too.
        private static int motionVectorsSaid = -1;
        private static int afterUpscalingSaid = -1;
        private static int overlaySaid = -1;
        private static int motionVectorsFirst;
        private static int afterUpscalingFirst;
        private static int overlayFirst;

        // Whether the probe is on, for the diagnostics window's switch.
        internal static bool On
        {
            get { return on; }
        }

        // Registers the three handlers, with every hook's lines due again, or takes them back out.
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

        // A size as width x height.
        private static string Size(Vector2Int size)
        {
            return size.x + "x" + size.y;
        }

        // A texture's size and format; "none" where there is none.
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

        // The frame number every probe line begins with.
        private static string At()
        {
            return "frame " + Time.frameCount + ": ";
        }

        // The motion-vector hook's handler: logs the camera, the command buffer, the two textures and the frame's state.
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

        // The after-upscaling hook's handler: logs the camera, the upscaled image and the frame's state.
        private static void AfterUpscaling(CommandBuffer buffer, RenderTexture image, Camera scene)
        {
            if (!Say(ref afterUpscalingSaid, ref afterUpscalingFirst)) return;
            Debug.Log(Log.Tag + " Hook probe, after upscaling, " + At() + "called on "
                      + (scene == null ? "no camera" : scene.name)
                      + ", image " + Of(image) + " -- " + Frames());
        }

        // The overlay hook's handler: logs the camera and the frame's state.
        private static void Overlay(CommandBuffer buffer, Camera scene)
        {
            if (!Say(ref overlaySaid, ref overlayFirst)) return;
            Debug.Log(Log.Tag + " Hook probe, overlay, " + At() + "called on "
                      + (scene == null ? "no camera" : scene.name) + " -- " + Frames());
        }
    }
}
