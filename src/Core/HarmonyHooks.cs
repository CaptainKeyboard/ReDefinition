using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System;

namespace ReDefinition.Core
{
    // One Harmony patch of a bundled mod: the method patched, and the names of
    // the static hooks on the class that installs it -- null where it has none of
    // that kind; and where their order among other patches of the same method
    // matters, the priority of its prefix and of its postfix (Harmony's Priority:
    // First 800, Normal 400, Last 0; -1 for Harmony's own).
    internal struct HarmonyHook
    {
        // The method patched, then the names of the prefix, postfix, finalizer and transpiler on the installing class,
        // and the two priorities (-1: Harmony's own).
        public readonly MethodBase Target;
        public readonly string Prefix;
        public readonly string Postfix;
        public readonly string Finalizer;
        public readonly int PrefixPriority;
        public readonly int PostfixPriority;
        public readonly string Transpiler;

        // A hook whose order among other patches does not matter: both priorities Harmony's own, no transpiler.
        public HarmonyHook(MethodBase target, string prefix, string postfix, string finalizer)
            : this(target, prefix, postfix, finalizer, -1, -1, null)
        {
        }

        // A hook with its priorities given, and optionally a transpiler.
        public HarmonyHook(MethodBase target, string prefix, string postfix, string finalizer, int prefixPriority,
                           int postfixPriority, string transpiler = null)
        {
            Target = target;
            Prefix = prefix;
            Postfix = postfix;
            Finalizer = finalizer;
            PrefixPriority = prefixPriority;
            PostfixPriority = postfixPriority;
            Transpiler = transpiler;
        }
    }

    // The Harmony plumbing every hook into KSP or another mod goes through.
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
                    harmony.Patch(hook.Target, prefix: Method(owner, hook.Prefix, hook.PrefixPriority),
                        postfix: Method(owner, hook.Postfix, hook.PostfixPriority), transpiler: Method(owner, hook.Transpiler, -1),
                        finalizer: Method(owner, hook.Finalizer, -1));
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

        // The installing class's private static method of that name as a Harmony patch, with the priority set where one
        // is given; null where the hook has none of that kind, and an exception where the method is missing.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static HarmonyLib.HarmonyMethod Method(Type owner, string name, int priority)
        {
            if (name == null) return null;
            MethodInfo method = owner.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null) throw new MissingMethodException(owner.Name, name);
            HarmonyLib.HarmonyMethod harmonyMethod = new HarmonyLib.HarmonyMethod(method);
            if (priority >= 0) harmonyMethod.priority = priority;
            return harmonyMethod;
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
