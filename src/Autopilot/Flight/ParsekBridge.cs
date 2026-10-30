using System;
using System.Reflection;
using UnityEngine;
using ReDefinition.Core;

namespace ReDefinition.Autopilot.Flight
{
    // What the autopilot needs to know of Parsek, read through reflection behind the parsek-ghost assumption; nothing of Parsek is written.
    // A vessel Parsek replays from a recording, a ghost, takes no nodes and no orders: the plan leaves it alone.
    internal static class ParsekBridge
    {
        private static MethodInfo isGhost;
        private static bool looked;
        private static bool warned;

        // Why Parsek's ghosts cannot be told, for the window and the log; null where Parsek is not installed or is a build the code was checked
        // against.
        internal static string Unavailable
        {
            get
            {
                ForeignAssumption assumption = ForeignAssumptions.ParsekGhost;
                if (assumption.State == ForeignState.Absent || assumption.Holds) return null;
                return "Parsek " + assumption.Version + " is not a build the autopilot was checked against (" + assumption.Problem
                       + "): its ghosts count as real vessels.";
            }
        }

        // Tells the player once, on the screen and in the log, that Parsek is installed in a build the autopilot does not know.
        internal static void WarnOnce()
        {
            string why = Unavailable;
            if (why == null || warned) return;
            warned = true;
            Debug.LogWarning(Log.Tag + " " + why);
            ScreenMessages.PostScreenMessage("ReDefinition: " + why, 12f, ScreenMessageStyle.UPPER_CENTER);
        }

        // Whether the vessel is one of Parsek's ghosts. False without Parsek, or with a build the bridge was not checked against.
        internal static bool IsGhost(Vessel vessel)
        {
            if (vessel == null || !ForeignAssumptions.ParsekGhost.Holds) return false;
            if (!looked)
            {
                looked = true;
                Type type = TypeLookup.Find("Parsek.GhostMapPresence");
                isGhost = type != null ? type.GetMethod("IsGhostMapVessel", TypeLookup.Any, null, new[] { typeof(uint) }, null) : null;
            }
            if (isGhost == null) return false;
            try
            {
                return (bool)isGhost.Invoke(null, new object[] { vessel.persistentId });
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
