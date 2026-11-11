using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // A draw that runs after the upscaler. upscaled is the upscaled colour target, depth the render-size depth where the host gives
    // one (null otherwise).
    internal delegate void PostUpscaleDraw(CommandBuffer commands, RenderTexture upscaled, RenderTexture depth, Camera camera);

    // The way effects that must be drawn after the upscaler reach the host. Implemented by the bridge to ReDefinition.Api (which finds the
    // members by name at run time); where the host is absent every call answers false and the caller draws its Scene-mode fallback.
    internal interface IPostUpscaleCompositor
    {
        // Whether ReDefinition.Api answers.
        bool HostAvailable { get; }
        // Whether the host gives the render-size depth with the draw.
        bool DepthAvailable { get; }
        // Registers a draw; false where the host cannot take it. The owner is a name for the log.
        bool Register(string owner, PostUpscaleDraw draw, bool wantsDepth);
        // Removes a draw registered before.
        bool Unregister(PostUpscaleDraw draw);
        // Tells the upscaler to drop its history because the picture jumped (a weather cut).
        void RequestHistoryReset(string reason);
    }

    // The composer for a game without the host: nothing is available.
    internal sealed class NullPostUpscaleCompositor : IPostUpscaleCompositor
    {
        public bool HostAvailable { get { return false; } }
        public bool DepthAvailable { get { return false; } }

        public bool Register(string owner, PostUpscaleDraw draw, bool wantsDepth)
        {
            return false;
        }

        public bool Unregister(PostUpscaleDraw draw)
        {
            return false;
        }

        public void RequestHistoryReset(string reason)
        {
        }
    }

    // Switches EVE's own particle fields (rain, snow, dust) off while ours draw, and back. Implemented by the painter of the cloud maps,
    // called by the precipitation renderer.
    internal interface IEveFieldSuppressor
    {
        // Whether EVE's particle fields are suppressed now.
        bool Suppressed { get; }
        // Suppresses or releases them; the reason is for the log. Releasing restores what was changed.
        void SetSuppressed(bool suppress, string reason);
    }

    // The suppressor for a game without EVE: there is nothing to suppress.
    internal sealed class NullEveFieldSuppressor : IEveFieldSuppressor
    {
        public bool Suppressed { get { return false; } }

        public void SetSuppressed(bool suppress, string reason)
        {
        }
    }

    // A layer drawn on the planet in the map and tracking-station view (the cloud shell, the ice, the ground cover). The host that draws
    // the orbit view calls the registered layers in order of Order.
    internal interface IOrbitOverlayLayer
    {
        // A name for the log.
        string Name { get; }
        // Lower numbers are drawn first.
        int Order { get; }
        // Whether the layer has anything to draw for the body.
        bool Wants(CelestialBody body);
        // Brings the layer's picture of the body up to date. focused is true for the body in focus; the others are drawn at low priority.
        void Draw(CelestialBody body, bool focused, double ut);
    }

    // The orbit overlay layers that implementations register. Copy-on-write, so a draw loop iterates without allocating.
    internal static class OrbitOverlayLayers
    {
        private static readonly object Gate = new object();
        private static IOrbitOverlayLayer[] layers = new IOrbitOverlayLayer[0];

        // The layers in draw order.
        internal static IOrbitOverlayLayer[] All
        {
            get { return layers; }
        }

        // Adds a layer, in order; a layer already there is not added twice.
        internal static void Register(IOrbitOverlayLayer layer)
        {
            if (layer == null) throw new ArgumentNullException("layer");
            lock (Gate)
            {
                if (Array.IndexOf(layers, layer) >= 0) return;
                var next = new List<IOrbitOverlayLayer>(layers) { layer };
                next.Sort(CompareOrder);
                layers = next.ToArray();
            }
        }

        internal static bool Unregister(IOrbitOverlayLayer layer)
        {
            lock (Gate)
            {
                int index = Array.IndexOf(layers, layer);
                if (index < 0) return false;
                var next = new List<IOrbitOverlayLayer>(layers);
                next.RemoveAt(index);
                layers = next.ToArray();
                return true;
            }
        }

        private static int CompareOrder(IOrbitOverlayLayer a, IOrbitOverlayLayer b)
        {
            return a.Order.CompareTo(b.Order);
        }
    }

    // The slots where implementations that other packages call are found. Each starts with a working null object, so a caller never
    // checks for null; the implementing package assigns its own at its Init.
    internal static class Services
    {
        private static IPostUpscaleCompositor postUpscale = new NullPostUpscaleCompositor();
        private static IEveFieldSuppressor eveFieldSuppressor = new NullEveFieldSuppressor();

        // The route to the host's after-upscaling hooks.
        internal static IPostUpscaleCompositor PostUpscale
        {
            get { return postUpscale; }
            set { postUpscale = value ?? new NullPostUpscaleCompositor(); }
        }

        // The switch for EVE's particle fields.
        internal static IEveFieldSuppressor EveFieldSuppressor
        {
            get { return eveFieldSuppressor; }
            set { eveFieldSuppressor = value ?? new NullEveFieldSuppressor(); }
        }
    }
}
