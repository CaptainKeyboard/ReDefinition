using System;
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Core
{
    // Warnings from the classes that adjust other mods at run time: a few of
    // each kind at most, so a failure repeated at every mode change cannot flood
    // the log -- and counted per kind, so one kind failing cannot silence
    // another.
    internal static class CompatibilityLog
    {
        private const int PerKind = 3;
        private static readonly Dictionary<string, int> Counts = new Dictionary<string, int>();

        public static void Warn(string kind, string message)
        {
            int count;
            Counts.TryGetValue(kind, out count);
            if (count >= PerKind) return;
            Counts[kind] = count + 1;
            Debug.LogWarning(Log.Tag + " " + message
                             + (count + 1 == PerKind ? " (further warnings of this kind are not logged)" : ""));
        }

        // What went wrong inside a reflected call, not the wrapper around it.
        public static string Reason(Exception e)
        {
            return (e.InnerException ?? e).Message;
        }
    }
}
