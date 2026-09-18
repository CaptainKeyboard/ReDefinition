using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System;
using ReDefinition.Core;

namespace ReDefinition.Settings
{
    // One Harmony patch of a bundled mod: the method patched, and the names of
    // the static hooks on the class that installs it -- null where it has none of
    // that kind.
    internal struct HarmonyHook
    {
        public readonly MethodBase Target;
        public readonly string Prefix;
        public readonly string Postfix;
        public readonly string Finalizer;

        public HarmonyHook(MethodBase target, string prefix, string postfix, string finalizer)
        {
            Target = target;
            Prefix = prefix;
            Postfix = postfix;
            Finalizer = finalizer;
        }
    }

    // The Harmony plumbing the behaviours and ModWindowClose share.
    internal static class HarmonyHooks
    {
        // All or none: one that cannot be installed takes the ones before it
        // back out. Harmony is named only here; NoInlining keeps it out of the
        // callers' compilation, which also runs outside the game.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Install(string harmonyId, Type owner, IEnumerable<HarmonyHook> hooks)
        {
            HarmonyLib.Harmony harmony = new HarmonyLib.Harmony(harmonyId);
            try
            {
                foreach (HarmonyHook hook in hooks)
                {
                    if (hook.Target == null) throw new MissingMethodException(owner.Name + ": a method to hook is missing.");
                    harmony.Patch(hook.Target, prefix: Method(owner, hook.Prefix), postfix: Method(owner, hook.Postfix),
                        finalizer: Method(owner, hook.Finalizer));
                }
            }
            catch
            {
                // ReDefinition's own id only: UnpatchAll without one removes every mod's
                // patches.
                harmony.UnpatchAll(harmonyId);
                throw;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static HarmonyLib.HarmonyMethod Method(Type owner, string name)
        {
            if (name == null) return null;
            MethodInfo method = owner.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null) throw new MissingMethodException(owner.Name, name);
            return new HarmonyLib.HarmonyMethod(method);
        }

        // A hook's body: its failure is a warning, never an exception thrown
        // into the other mod's own code.
        public static void Safely(string key, string what, Action body)
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn(key, what + " (" + CompatibilityLog.Reason(e) + ").");
            }
        }
    }
}
