using System;
using UnityEngine;

namespace ReDefinition.Core
{
    // The own cloud layers of ReDefinition Atmospheres for the host's traced rays (Atmospheres package T18, RtOwnClouds -> the main mod's
    // raytracing, RtKsp). One writer: the atmospheres, every frame while their pack stands; one reader: the host's raytracing, which asks every
    // frame it traces (Ask) and reads the view only while it is fresh. The texture's layout is RayCloudLayout; the shaders' mirror of the law is
    // RtCommon.hlsli (OwnCloud*), and the contract's defines RT_OWN_CLOUDS_* hold the layout's numbers (RtContractTests).
    internal sealed class RayCloudView
    {
        // The body the layers belong to (CelestialBody.flightGlobalsIndex); -1 for none.
        public int BodyIndex = -1;
        // The native texture (R32G32B32A32 float, the numbers in r and g, b and a 0: the raytracing cannot share R32G32): LayerCount slices of Width x SliceHeight texels, then RayCloudLayout.HeaderRows rows of numbers.
        public IntPtr Texture;
        public int Width, SliceHeight, LayerCount;
        // The lowest base and the highest top of the layers, m from the body's centre.
        public float InnerRadiusM, OuterRadiusM;
        // The rotation from world space into the layers' map frame (x towards longitude 0, y towards longitude 90 east, z north).
        public Matrix4x4 WorldToMap = Matrix4x4.identity;
        // The most taps a ray takes in one span of a layer.
        public int Taps = RayCloudLayout.DefaultTaps;
        // Time.frameCount of the last write.
        public int Frame = -100;
    }

    internal static class RayCloudSeam
    {
        // The frames a view or an ask stays fresh.
        internal const int FreshFrames = 2;
        // The frames without an ask after which the atmospheres stop keeping the pack: the raytracing is off.
        internal const int AskTimeoutFrames = 60;

        internal static readonly RayCloudView View = new RayCloudView();

        // Time.frameCount of the host's last ask (it traces and would take the layers).
        private static int askedFrame = -1000;

        // Time.frameCount the host last put the layers into a traced frame; for the probe (T18Probe).
        internal static int TakenFrame = -1000;

        // The host's raytracing status line (the proxy's own words, its problem among them); for the probe. Null alone.
        internal static Func<string> HostStatus = () => null;

        // The host: it traces this frame.
        internal static void Ask(int frame)
        {
            askedFrame = frame;
        }

        // The atmospheres: whether the host traces now, so that the pack is worth its bake.
        internal static bool Asked(int frame)
        {
            return frame - askedFrame <= AskTimeoutFrames && frame >= askedFrame;
        }

        // The host: whether the view holds layers written in the last frames for this body.
        internal static bool Fresh(int frame, int bodyIndex)
        {
            RayCloudView v = View;
            return v.Texture != IntPtr.Zero && v.LayerCount > 0 && v.BodyIndex == bodyIndex && frame - v.Frame <= FreshFrames && frame >= v.Frame;
        }

        // The atmospheres: no layers any more.
        internal static void Clear()
        {
            View.Texture = IntPtr.Zero;
            View.LayerCount = 0;
            View.BodyIndex = -1;
        }
    }

    // The layout of the texture of RayCloudView, binding for the writer (RtOwnClouds) and the shaders (RtContract.h RT_OWN_CLOUDS_*).
    //   Slice i (rows i x SliceHeight to (i + 1) x SliceHeight - 1): layer i, two floats a texel, equirectangular in the layer's map frame; r the coverage
    //   map's value, g the column depth at full cloud (tau_full). Row j at latitude 90 - (j + 0.5) x 180 / SliceHeight, column c at longitude
    //   -180 + (c + 0.5) x 360 / Width before the layer's map shift.
    //   Header row (row LayerCount x SliceHeight): texel 0 holds (the body's radius m, 1 with a surface else 0); layer i's numbers start at texel
    //   GlobalTexels + i x LayerTexels: (inner radius m, outer radius m), (shadow strength, coverage), (edge softness, clear-sky K), (water power, map
    //   shift degrees), (fade near m, fade far m; far 0 for none).
    internal static class RayCloudLayout
    {
        internal const int HeaderRows = 1;
        internal const int GlobalTexels = 1;
        internal const int LayerTexels = 5;
        internal const int DefaultTaps = 8;
        // The mode in the packet's cloud block (cloudRadii.w): EVE's one layer, or these layers.
        internal const int ModeEve = 1;
        internal const int ModeOwn = 2;
    }
}
