using System;
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Core
{
    // Warnings from the classes that adjust other mods at run time. A few of
    // each kind at most are logged, so a failure repeated at every mode change cannot flood
    // the log. They are counted per kind, so one kind failing cannot silence
    // another.
    internal static class CompatibilityLog
    {
        // How many warnings each kind writes, and how many each kind has written so far.
        private const int PerKind = 3;
        private static readonly Dictionary<string, int> Counts = new Dictionary<string, int>();

        // Writes the warning unless its kind has used up its three; the last one says that further ones are dropped.
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
