using System.Collections;
using System.Reflection;
using System;
using ReDefinition.Core;
using ReDefinition.Upscaler;

namespace ReDefinition.Settings.Behaviours
{
    // Scatter density and distance are baked into every scatter while its configs
    // load (PerformNormalisationConversions): range times the distance, population
    // times the density, the originals kept. Its window's change undoes that and
    // does it again with the new values (UpdateScatterSettings, through
    // ReverseNormalisationConversions and PerformNormalisationConversions, both
    // public), then rebuilds the planet while paused. The same two calls here, but
    // as the next scene is asked for: a scatter system already running keeps its
    // buffers sized for the density and range it was built with
    // (ScatterRenderer.Initialize), and rebaking under it would truncate what it
    // draws until the next body load. Without those calls the two settings are not
    // offered.
    internal sealed class ParallaxScatterBehaviour : ModBehaviour
    {
        private FieldInfo scatterBodies;
        private FieldInfo fastScatters;
        private MethodInfo reverse;
        private MethodInfo perform;
        private bool resolved;
        private bool canRenormalise;

        public override bool Reach(RegisteredMod mod, SettingRegistration setting, out Func<string> read,
                                   out Action<string> write, out Type type)
        {
            read = null;
            write = null;
            type = null;
            Resolve();
            // Without the calls: not offered, and not named among the drops.
            return !canRenormalise;
        }

        public override void Finish(RegisteredMod mod, BundledSetting setting, SettingRegistration registration)
        {
            Action<string> write = setting.Write;
            setting.Write = text =>
            {
                write(text);
                BundledSettingsAddon.AtNextSceneChange("parallax-renormalise", Renormalise);
            };
        }

        private void Resolve()
        {
            if (resolved) return;
            resolved = true;
            Type loader = TypeLookup.Find("Parallax.ConfigLoader");
            Type scatter = TypeLookup.Find("Parallax.Scatter");
            Type body = TypeLookup.Find("Parallax.ParallaxScatterBody");
            const BindingFlags Any = HostStack.Any;
            scatterBodies = loader != null ? loader.GetField("parallaxScatterBodies", Any) : null;
            fastScatters = body != null ? body.GetField("fastScatters", Any) : null;
            reverse = loader != null && scatter != null
                ? loader.GetMethod("ReverseNormalisationConversions", Any, null, new[] { scatter }, null)
                : null;
            perform = loader != null && scatter != null
                ? loader.GetMethod("PerformNormalisationConversions", Any, null, new[] { scatter }, null)
                : null;
            canRenormalise = scatterBodies != null && scatterBodies.IsStatic && fastScatters != null && reverse != null
                             && perform != null;
        }

        // Undo and redo the baking with the multipliers now set -- idempotent:
        // Perform keeps the originals it scales, Reverse returns to them.
        private void Renormalise()
        {
            IDictionary bodies = scatterBodies.GetValue(null) as IDictionary;
            if (bodies == null) return;

            // One argument array for all of them: this runs over every scatter of
            // every body, at a scene change.
            object[] one = new object[1];
            foreach (object body in bodies.Values)
            {
                Array scatters = body != null ? fastScatters.GetValue(body) as Array : null;
                if (scatters == null) continue;
                foreach (object scatter in scatters)
                {
                    if (scatter == null) continue;
                    one[0] = scatter;
                    reverse.Invoke(null, one);
                    perform.Invoke(null, one);
                }
            }
        }
    }
}
