using System;
using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Core;
using ReDefinition.Orbits;
using UnityEngine;

namespace ReDefinition.Autopilot.Guidance
{
    // The climb to an orbit and the descent to the ground are a maneuver of the vessel's own, as a burn is: a real maneuver node that follows the navigation's
    // direction. The game's whole maneuver display then works with it: the SAS's maneuver mode, the navball's burn marker, the burn gauge, and the X
    // button that takes the maneuver away. The node is there only while it is the only one. Where the player or the planner has a node of its own, the
    // guide goes, and it is not made again while that node stands.
    internal static class GuideNode
    {
        // How far ahead of now the node lies, in s of the game's time, at least. A node at the moment itself is not found on the vessel's orbit, and one
        // that is behind is taken off by the game. LeastDeltaV is the least burn that the game takes for a node.
        private const double Lead = 1.0;
        private const double LeastDeltaV = 1.5;
        // How often the node is put right, in s of real time.
        private const float Period = 0.1f;
        private const string HarmonyId = "ReDefinition.GuideNode";

        private static ManeuverNode node;
        private static Vessel owner;
        // The burn the node stands for, in the world's axes: what the game's SAS and navball read. The burn the game works out from its patches
        // shrinks between two updates as the vessel burns, and turns round where little is left.
        private static Vector3d burn;
        private static float updated = -1000f;
        private static bool removedByUs;
        private static bool subscribed;

        // Whether the player took the node away (the X button or the map): no new one is made until the guidance starts anew.
        internal static bool Dismissed { get; private set; }
        // Told once when the player takes the node away.
        internal static Action OnDismissed;

        internal static bool Is(ManeuverNode other)
        {
            return node != null && other == node;
        }

        // The first node of the vessel that is not the guide; null for none.
        internal static ManeuverNode First(Vessel vessel)
        {
            PatchedConicSolver solver = vessel != null ? vessel.patchedConicSolver : null;
            if (solver == null || solver.maneuverNodes == null) return null;
            foreach (ManeuverNode each in solver.maneuverNodes)
                if (!Is(each)) return each;
            return null;
        }

        // Whether the vessel holds the guide and nothing else, so that the game's maneuver display is the guide's.
        internal static bool Holds(Vessel vessel)
        {
            PatchedConicSolver solver = vessel != null ? vessel.patchedConicSolver : null;
            return node != null && solver != null && solver.maneuverNodes.Count == 1 && solver.maneuverNodes[0] == node;
        }

        // The guide put right from the board: where the navigation asks the nose to point, as the burn that is left of it. Made where there is none,
        // and taken away where there is nothing to guide or a node of another kind stands.
        internal static void Ensure(Vessel vessel)
        {
            Subscribe();
            PatchedConicSolver solver = vessel != null ? vessel.patchedConicSolver : null;
            bool wanted = solver != null && !Dismissed && GuidanceBoard.Current(vessel) && GuidanceBoard.HasDirection && GuidanceBoard.HasBar && vessel.orbitDriver != null
                          && vessel.orbitDriver.updateMode != OrbitDriver.UpdateMode.IDLE && First(vessel) == null;
            if (!wanted)
            {
                Release(vessel);
                return;
            }
            if (owner != vessel) Release(owner);
            if (node != null && !solver.maneuverNodes.Contains(node)) node = null;
            Vector3d world = GuidanceBoard.Direction * Math.Max(LeastDeltaV, GuidanceBoard.Remaining);
            burn = world;
            if (node != null && Time.realtimeSinceStartup - updated < Period) return;
            double now = Planetarium.GetUniversalTime();
            double ut = now + Math.Max(Lead, 0.3 * TimeWarp.CurrentRate);
            V3 inNode = Maneuvers.ToNodeFrame(KspOrbits.StateAt(vessel.orbit, ut), KspOrbits.ToV3(world), true);
            if (node == null)
            {
                node = solver.AddManeuverNode(ut);
                owner = vessel;
            }
            node.UT = ut;
            node.DeltaV = new Vector3d(inNode.X, inNode.Y, inNode.Z);
            solver.UpdateFlightPlan();
            updated = Time.realtimeSinceStartup;
        }

        // The guide taken away where the vessel has it; the vessel, or null for the one it was made for.
        internal static void Release(Vessel vessel)
        {
            if (node == null) return;
            ManeuverNode guide = node;
            node = null;
            owner = null;
            updated = -1000f;
            if (guide == null || guide.solver == null) return;
            removedByUs = true;
            try
            {
                guide.RemoveSelf();
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Navigation: the guide node could not be taken off: " + e.Message);
            }
            finally
            {
                removedByUs = false;
            }
        }

        // The guidance starts anew: a node taken away before is not remembered.
        internal static void Reset()
        {
            Dismissed = false;
        }

        // The game's events call an object's method, not a static one.
        private sealed class Listener
        {
            public void Removed(Vessel vessel, PatchedConicSolver solver)
            {
                GuideNode.Taken(vessel, solver);
            }

            public void Changed(Vessel vessel)
            {
                Release(owner);
            }
        }

        private static readonly Listener Hearing = new Listener();

        private static void Subscribe()
        {
            if (subscribed) return;
            subscribed = true;
            GameEvents.onManeuverRemoved.Add(Hearing.Removed);
            GameEvents.onVesselChange.Add(Hearing.Changed);
        }

        // A node taken off the vessel that was not by us, with the guide among those that are gone: the player took the maneuver away.
        private static void Taken(Vessel vessel, PatchedConicSolver solver)
        {
            if (removedByUs || node == null || vessel != owner || solver == null || solver.maneuverNodes.Contains(node)) return;
            node = null;
            owner = null;
            updated = -1000f;
            Dismissed = true;
            Debug.Log(Log.Tag + " Navigation: the maneuver of " + (vessel != null ? vessel.vesselName : "the vessel") + " was taken away; the guidance ends.");
            if (OnDismissed != null) OnDismissed();
        }

        // The save does not hold the guide: it is the display of a climb, which is not there when the vessel is loaded again.
        internal static void Install()
        {
            HarmonyHooks.Safely("guide-node-save", "The guide node is saved with the vessel", () =>
                HarmonyHooks.Install(HarmonyId, typeof(GuideNode), new List<HarmonyHook>
                {
                    new HarmonyHook(typeof(PatchedConicSolver).GetMethod("Save", BindingFlags.Instance | BindingFlags.Public), nameof(SaveWithout), nameof(SaveWith), null),
                    new HarmonyHook(typeof(ManeuverNode).GetMethod("GetBurnVector", BindingFlags.Instance | BindingFlags.Public), nameof(BurnOf), null, null),
                }));
        }

        // The burn of the guide is the one the board gave it, as it stands now.
        private static bool BurnOf(ManeuverNode __instance, ref Vector3d __result)
        {
            if (!Is(__instance)) return true;
            __result = burn;
            return false;
        }

        private static void SaveWithout(PatchedConicSolver __instance, out int __state)
        {
            __state = -1;
            if (node == null) return;
            __state = __instance.maneuverNodes.IndexOf(node);
            if (__state >= 0) __instance.maneuverNodes.RemoveAt(__state);
        }

        private static void SaveWith(PatchedConicSolver __instance, int __state)
        {
            if (__state >= 0 && node != null) __instance.maneuverNodes.Insert(Math.Min(__state, __instance.maneuverNodes.Count), node);
        }
    }

    // Installs the hooks of the guide node once, in the flight.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class GuideNodeHooks : MonoBehaviour
    {
        private static bool installed;

        private void Start()
        {
            if (installed) return;
            installed = true;
            GuideNode.Install();
        }
    }
}
