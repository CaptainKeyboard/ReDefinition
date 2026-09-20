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

        private static int motionVectorsSaid = -1;
        private static int afterUpscalingSaid = -1;
        private static int overlaySaid = -1;

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

        // Once per scene: a line every frame would say nothing more and fill the
        // log.
        private static bool Once(ref int said)
        {
            int scene = (int)HighLogic.LoadedScene;
            if (said == scene) return false;
            said = scene;
            return true;
        }

        private static void MotionVectors(CommandBuffer buffer, RenderTexture motionVectors, RenderTexture depth,
                                          Camera scene)
        {
            if (!Once(ref motionVectorsSaid)) return;
            StringBuilder sb = new StringBuilder();
            sb.Append(Log.Tag).Append(" Hook probe, motion vectors: called on ")
              .Append(scene == null ? "no camera" : scene.name)
              .Append(", buffer ").Append(buffer == null ? "none" : buffer.name)
              .Append(", motion vectors ").Append(Of(motionVectors))
              .Append(", depth ").Append(Of(depth))
              .Append(" -- ").Append(Frames());
            Debug.Log(sb.ToString());
        }

        private static void AfterUpscaling(CommandBuffer buffer, RenderTexture image, Camera scene)
        {
            if (!Once(ref afterUpscalingSaid)) return;
            Debug.Log(Log.Tag + " Hook probe, after upscaling: called on "
                      + (scene == null ? "no camera" : scene.name)
                      + ", image " + Of(image) + " -- " + Frames());
        }

        private static void Overlay(CommandBuffer buffer, Camera scene)
        {
            if (!Once(ref overlaySaid)) return;
            Debug.Log(Log.Tag + " Hook probe, overlay: called on "
                      + (scene == null ? "no camera" : scene.name) + " -- " + Frames());
        }
    }
}
