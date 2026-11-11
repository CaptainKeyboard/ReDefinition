using System;
using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // The reflection bridge from the atmospheres to ReDefinition.Api (the main mod's interface for other mods). The atmospheres never
    // reference ReDefinition.dll: every member is found by name when the bridge is made, and a member that is absent or has another
    // signature simply counts as not there, so the caller draws its Scene-mode fallback. Implements IPostUpscaleCompositor; the
    // slot Services.PostUpscale receives it at the start of the module that uses it.
    //
    // Members looked for (ReDefinition.Api): Hooks.RegisterAfterUpscaling / UnregisterAfterUpscaling (Action<CommandBuffer, RenderTexture,
    // Camera>), Hooks.RegisterAfterUpscalingWithDepth / UnregisterAfterUpscalingWithDepth (Action<CommandBuffer, RenderTexture,
    // RenderTexture, Camera>; added by package C24 [VERIFY the signature once C24 has landed]), Hooks.RegisterMotionVectors /
    // UnregisterMotionVectors (Action<CommandBuffer, RenderTexture, RenderTexture, Camera>), Hooks.RegisterOverlay /
    // UnregisterOverlay (Action<CommandBuffer, Camera>) and Frame.RequestHistoryReset(string).
    // Every call runs on the main thread. A handler the host removed because it threw is not registered again here.
    internal sealed class ApiBridge : IPostUpscaleCompositor
    {
        internal const string HooksTypeName = "ReDefinition.Api.Hooks";
        internal const string FrameTypeName = "ReDefinition.Api.Frame";

        // A pair of host methods, the way in and the way out of one kind of hook.
        private sealed class HookPair
        {
            internal MethodInfo Register;
            internal MethodInfo Unregister;

            internal bool Present
            {
                get { return Register != null && Unregister != null; }
            }
        }

        private sealed class Registered
        {
            internal Delegate Handler;
            internal HookPair Pair;
        }

        private readonly HookPair afterUpscaling;
        private readonly HookPair afterUpscalingDepth;
        private readonly HookPair motionVectors;
        private readonly HookPair overlay;
        private readonly MethodInfo historyReset;
        private readonly Dictionary<object, Registered> registered = new Dictionary<object, Registered>();
        private readonly Action<string> log;

        // The bridge to the host as the game has it: the types are found among the loaded assemblies.
        internal ApiBridge() : this(TypeLookup.Find(HooksTypeName), TypeLookup.Find(FrameTypeName), null)
        {
        }

        // The bridge to given types (a test passes types of its own; null stands for an absent host). log receives a line for every
        // registration that was refused.
        internal ApiBridge(Type hooks, Type frame, Action<string> log)
        {
            this.log = log ?? AtmospheresLog.WarnOnce;
            afterUpscaling = Pair(hooks, "RegisterAfterUpscaling", "UnregisterAfterUpscaling", typeof(Action<CommandBuffer, RenderTexture, Camera>));
            afterUpscalingDepth = Pair(hooks, "RegisterAfterUpscalingWithDepth", "UnregisterAfterUpscalingWithDepth",
                typeof(Action<CommandBuffer, RenderTexture, RenderTexture, Camera>));
            motionVectors = Pair(hooks, "RegisterMotionVectors", "UnregisterMotionVectors", typeof(Action<CommandBuffer, RenderTexture, RenderTexture, Camera>));
            overlay = Pair(hooks, "RegisterOverlay", "UnregisterOverlay", typeof(Action<CommandBuffer, Camera>));
            historyReset = Find(frame, "RequestHistoryReset", typeof(string));
        }

        // The static method with that name and exactly that parameter list, or null.
        private static MethodInfo Find(Type type, string name, params Type[] parameters)
        {
            if (type == null) return null;
            try
            {
                return type.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, parameters, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static HookPair Pair(Type hooks, string register, string unregister, Type handler)
        {
            return new HookPair { Register = Find(hooks, register, handler), Unregister = Find(hooks, unregister, handler) };
        }

        // Whether ReDefinition.Api answers (its after-upscaling hook is there).
        public bool HostAvailable
        {
            get { return afterUpscaling.Present; }
        }

        // Whether the host gives the render-size depth with the draw.
        public bool DepthAvailable
        {
            get { return afterUpscalingDepth.Present; }
        }

        // Whether the host takes motion-vector handlers.
        internal bool MotionVectorsAvailable
        {
            get { return motionVectors.Present; }
        }

        // Whether the host takes overlay handlers.
        internal bool OverlayAvailable
        {
            get { return overlay.Present; }
        }

        // Whether the host takes a history reset.
        internal bool HistoryResetAvailable
        {
            get { return historyReset != null; }
        }

        // The number of registrations this bridge holds.
        internal int RegistrationCount
        {
            get { return registered.Count; }
        }

        // Registers a draw after the upscaler. A draw that wants the depth goes to the depth hook; where the host has none, the host
        // cannot take it and the answer is false (the caller draws its Scene-mode fallback). A draw that does not want it goes to the
        // plain hook and is handed null for the depth.
        public bool Register(string owner, PostUpscaleDraw draw, bool wantsDepth)
        {
            if (draw == null) throw new ArgumentNullException("draw");
            if (registered.ContainsKey(draw)) return true;
            Delegate handler;
            HookPair pair;
            if (wantsDepth)
            {
                if (!afterUpscalingDepth.Present) return false;
                pair = afterUpscalingDepth;
                handler = new Action<CommandBuffer, RenderTexture, RenderTexture, Camera>((commands, image, depth, camera) => draw(commands, image, depth, camera));
            }
            else
            {
                if (!afterUpscaling.Present) return false;
                pair = afterUpscaling;
                handler = new Action<CommandBuffer, RenderTexture, Camera>((commands, image, camera) => draw(commands, image, null, camera));
            }
            return Invoke(owner, pair, handler, draw);
        }

        public bool Unregister(PostUpscaleDraw draw)
        {
            return Remove(draw);
        }

        // Registers a handler for the motion vectors the mod draws itself; false where the host has no such hook. The owner is a name
        // for the log; the key identifies the registration for Unregister.
        internal bool RegisterMotionVectors(string owner, Action<CommandBuffer, RenderTexture, RenderTexture, Camera> handler)
        {
            if (handler == null) throw new ArgumentNullException("handler");
            if (registered.ContainsKey(handler)) return true;
            return motionVectors.Present && Invoke(owner, motionVectors, handler, handler);
        }

        internal bool UnregisterMotionVectors(Action<CommandBuffer, RenderTexture, RenderTexture, Camera> handler)
        {
            return Remove(handler);
        }

        // Registers a handler for lines and markers drawn after the scene; false where the host has no such hook.
        internal bool RegisterOverlay(string owner, Action<CommandBuffer, Camera> handler)
        {
            if (handler == null) throw new ArgumentNullException("handler");
            if (registered.ContainsKey(handler)) return true;
            return overlay.Present && Invoke(owner, overlay, handler, handler);
        }

        internal bool UnregisterOverlay(Action<CommandBuffer, Camera> handler)
        {
            return Remove(handler);
        }

        // Tells the upscaler to drop its history; nothing happens where the host has no such call.
        public void RequestHistoryReset(string reason)
        {
            if (historyReset == null) return;
            try
            {
                historyReset.Invoke(null, new object[] { reason ?? "atmospheres" });
            }
            catch (Exception e)
            {
                log("history reset failed (" + CompatibilityLog.Reason(e) + ")");
            }
        }

        // Removes every registration (the module that owns the bridge calls it at its shutdown).
        internal void UnregisterAll()
        {
            foreach (object key in new List<object>(registered.Keys)) Remove(key);
        }

        private bool Invoke(string owner, HookPair pair, Delegate handler, object key)
        {
            try
            {
                pair.Register.Invoke(null, new object[] { handler });
            }
            catch (Exception e)
            {
                log("the host refused the hook of " + owner + " (" + CompatibilityLog.Reason(e) + ")");
                return false;
            }
            registered[key] = new Registered { Handler = handler, Pair = pair };
            return true;
        }

        private bool Remove(object key)
        {
            Registered entry;
            if (key == null || !registered.TryGetValue(key, out entry)) return false;
            registered.Remove(key);
            try
            {
                entry.Pair.Unregister.Invoke(null, new object[] { entry.Handler });
            }
            catch (Exception e)
            {
                log("removing a hook from the host failed (" + CompatibilityLog.Reason(e) + ")");
                return false;
            }
            return true;
        }
    }
}
