using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Seasons
{
    // The log lines of the seasons: every one starts with the ReDefinition tag and "Seasons:".
    internal static class SeasonsLog
    {
        internal const string Prefix = Log.Tag + " Seasons: ";

        // A line of information.
        internal static void Say(string message)
        {
            Debug.Log(Prefix + message);
        }

        // A warning of a kind (CompatibilityLog counts them per kind and drops the repeats).
        internal static void Warn(string kind, string message)
        {
            CompatibilityLog.Warn(kind, "Seasons: " + message);
        }

        // A warning that is always written, for a failure that happens once.
        internal static void WarnOnce(string message)
        {
            Debug.LogWarning(Prefix + message);
        }

        // A warning for a failed call: the message and what went wrong inside it.
        internal static void Failed(string kind, string what, Exception e)
        {
            Warn(kind, what + " (" + CompatibilityLog.Reason(e) + ").");
        }
    }
}
