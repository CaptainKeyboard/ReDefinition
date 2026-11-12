using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The log lines of the atmospheres: every one starts with the ReDefinition tag and "Atmospheres:".
    internal static class AtmospheresLog
    {
        internal const string Prefix = Log.Tag + " Atmospheres: ";

        // Where the lines of information go: the game's log. A test replaces it (the engine's log call cannot run outside the game).
        internal static Action<string> SayLine = line => Debug.Log(line);

        // A line of information.
        internal static void Say(string message)
        {
            SayLine(Prefix + message);
        }

        // A warning of a kind (CompatibilityLog counts them per kind and drops the repeats).
        internal static void Warn(string kind, string message)
        {
            CompatibilityLog.Warn(kind, "Atmospheres: " + message);
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
