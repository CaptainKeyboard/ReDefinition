using System;
using System.Reflection;
using ReDefinition.Core;

namespace ReDefinition.Autopilot
{
    // What the autopilot needs to know of Parsek, read through reflection behind the parsek-bridge assumption; nothing of Parsek is written.
    // A vessel Parsek replays from a recording, a ghost, takes no nodes and no orders: the plan leaves it alone.
    internal static class ParsekBridge
    {
        private static MethodInfo isGhost;
        private static bool looked;

        // Whether the vessel is one of Parsek's ghosts. False without Parsek, or with a build the bridge was not checked against.
        internal static bool IsGhost(Vessel vessel)
        {
            if (vessel == null || !ForeignAssumptions.ParsekBridge.Holds) return false;
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
