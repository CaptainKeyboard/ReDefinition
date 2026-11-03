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
    // direction. A climb has a second node behind the first, the burn that makes the orbit circular at the apoapsis, so that the map shows the way into the orbit. The game's whole maneuver display then works with it: the SAS's maneuver mode, the navball's burn marker, the burn gauge, and the X
    // button that takes the maneuver away. The node is there only while it is the only one. Where the player or the planner has a node of its own, the
    // guide goes, and it is not made again while that node stands.
    internal static class GuideNode
    {
        // How far ahead of now the node lies, in s of the game's time, at least. A node at the moment itself is not found on the vessel's orbit, and one
        // that is behind is taken off by the game. LeastDeltaV is the least burn that the game takes for a node.
        private const double Lead = 1.0;
        internal const double LeastDeltaV = 1.5;
        // How often the node is put right, in s of real time.
        private const float Period = 0.1f;
        private const string HarmonyId = "ReDefinition.GuideNode";

        private static ManeuverNode node;
        private static ManeuverNode second;
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
            return other != null && (other == node || other == second);
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
            return node != null && solver != null && solver.maneuverNodes.Count > 0 && solver.maneuverNodes.TrueForAll(Is);
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
                Release();
                return;
            }
            if (owner != vessel) Release();
            if (node != null && !solver.maneuverNodes.Contains(node)) node = null;
            if (second != null && !solver.maneuverNodes.Contains(second)) second = null;
            Vector3d world = GuidanceBoard.Direction * Math.Max(LeastDeltaV, GuidanceBoard.Remaining);
            burn = world;
            if (node != null && Time.realtimeSinceStartup - updated < Period) return;
            double now = Planetarium.GetUniversalTime();
            double ut = now + Math.Max(Lead, 0.3 * TimeWarp.CurrentRate);
            State at = KspOrbits.StateAt(vessel.orbit, ut);
            V3 inNode = Maneuvers.ToNodeFrame(at, KspOrbits.ToV3(world), true);
            // A climb shows the burn that brings the apoapsis to the height aimed for, in the direction the navigation asks for, and then the burn that
            // circularizes there: the orbit that is aimed at, and not the vessel thrown out of its orbit by all the burn that is left. The direction and the
            // size that the game's SAS and navball read are the board's.
            double utSecond = 0.0;
            V3 inSecond = new V3(0.0, 0.0, 0.0);
            V3 inFirst = inNode;
            bool both = GuidanceBoard.HasClimb && Climb(vessel, at, ut, out inFirst, out utSecond, out inSecond);
            if (both) inNode = inFirst;
            if (node == null)
            {
                node = solver.AddManeuverNode(ut);
                owner = vessel;
            }
            node.UT = ut;
            node.DeltaV = new Vector3d(inNode.X, inNode.Y, inNode.Z);
            if (both)
            {
                // The second burn lies on the orbit that the first makes, which the flight plan has only once it is worked out.
                solver.UpdateFlightPlan();
                if (second == null) second = solver.AddManeuverNode(utSecond);
                if (second != null)
                {
                    second.UT = utSecond;
                    second.DeltaV = new Vector3d(inSecond.X, inSecond.Y, inSecond.Z);
                }
            }
            else if (second != null)
            {
                ManeuverNode gone = second;
                second = null;
                removedByUs = true;
                try
                {
                    gone.RemoveSelf();
                }
                finally
                {
                    removedByUs = false;
                }
            }
            solver.UpdateFlightPlan();
            updated = Time.realtimeSinceStartup;
        }

        // The two burns of a climb as the node's own changes of velocity: the one along the board's direction that puts the apoapsis at the height aimed for,
        // and the one that circularizes there, with when it is. False where the orbit after the first burn is open.
        private static bool Climb(Vessel vessel, State at, double ut, out V3 inNode, out double utSecond, out V3 inSecond)
        {
            inNode = new V3(0.0, 0.0, 0.0);
            inSecond = new V3(0.0, 0.0, 0.0);
            utSecond = ut;
            double mu = vessel.mainBody.gravParameter;
            V3 direction = KspOrbits.ToV3(GuidanceBoard.Direction).Unit;
            double size = Math.Max(LeastDeltaV, ClimbGuide.RaiseBurn(vessel.mainBody, at, direction, GuidanceBoard.ClimbAltitude));
            var after = new State(at.R, at.V + direction * size);
            var conic = new Conic(after, mu);
            if (!conic.Closed) return false;
            double wait = conic.TimeToApoapsis;
            if (double.IsNaN(wait) || double.IsInfinity(wait)) return false;
            // At the apoapsis itself the time comes out as a whole revolution as often as as nothing; a second node lies after the first, never with it.
            if (wait > conic.Period - Lead) wait = 0.0;
            wait = Math.Max(Lead, wait);
            State top = Kepler.Propagate(after, wait, mu);
            double circular = Math.Sqrt(mu / top.R.Length);
            inNode = Maneuvers.ToNodeFrame(at, direction * size, true);
            inSecond = Maneuvers.ToNodeFrame(top, top.V.Unit * Math.Max(LeastDeltaV, circular - top.V.Length), true);
            utSecond = ut + wait;
            return true;
        }


        // The guide taken away, from the vessel it was made for.
        internal static void Release()
        {
            if (node == null) return;
            ManeuverNode guide = node;
            ManeuverNode behind = second;
            node = null;
            second = null;
            owner = null;
            updated = -1000f;
            if (guide == null || (guide.solver == null && (behind == null || behind.solver == null))) return;
            removedByUs = true;
            try
            {
                if (behind != null && behind.solver != null) behind.RemoveSelf();
                if (guide.solver != null) guide.RemoveSelf();
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

        // The node that a handover left to the game, taken away if the climb is guided again.
        private static ManeuverNode kept;

        internal static void DropKept()
        {
            ManeuverNode gone = kept;
            kept = null;
            if (gone == null || gone.solver == null) return;
            removedByUs = true;
            try
            {
                gone.RemoveSelf();
            }
            finally
            {
                removedByUs = false;
            }
        }

        // The climb is done: the first node, which follows the climb, is taken away, and the second, the burn that circularizes, stays as a node of the game's
        // own. It is not the guide's any more, so the game's burn display shows it, until the plan makes the node of its own.
        internal static void Handover()
        {
            if (node == null) return;
            ManeuverNode first = node;
            kept = second;
            node = null;
            second = null;
            owner = null;
            updated = -1000f;
            if (first.solver == null) return;
            removedByUs = true;
            try
            {
                first.RemoveSelf();
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

        // A guide node that stood when the player took away the other; taken away in the next frame.
        private static ManeuverNode leftover;

        internal static void Sweep()
        {
            if (leftover == null) return;
            ManeuverNode gone = leftover;
            leftover = null;
            if (gone.solver == null) return;
            removedByUs = true;
            try
            {
                gone.RemoveSelf();
            }
            finally
            {
                removedByUs = false;
            }
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
                Release();
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
            if (removedByUs || node == null || vessel != owner || solver == null) return;
            if (solver.maneuverNodes.Contains(node))
            {
                // Only the second burn is gone: the guide makes it again.
                if (second != null && !solver.maneuverNodes.Contains(second)) second = null;
                return;
            }
            // The one that is left is not the player's: it goes after the event, when the solver is done with the removal.
            if (second != null && solver.maneuverNodes.Contains(second)) leftover = second;
            node = null;
            second = null;
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
                    new HarmonyHook(typeof(PatchedConicSolver).GetMethod("Save", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(ConfigNode) }, null),
                        nameof(SaveWithout), null, nameof(SaveWith)),
                    new HarmonyHook(typeof(ManeuverNode).GetMethod("GetBurnVector", BindingFlags.Instance | BindingFlags.Public), nameof(BurnOf), null, null),
                }));
        }

        // The burn of the guide is the one the board gave it, as it stands now.
        private static bool BurnOf(ManeuverNode __instance, ref Vector3d __result)
        {
            if (node == null || __instance != node) return true;
            __result = burn;
            return false;
        }

        // Where the guide's nodes stood in the list while the save is made without them.
        private static int savedNode = -1, savedSecond = -1;

        private static void SaveWithout(PatchedConicSolver __instance)
        {
            savedNode = savedSecond = -1;
            if (second != null)
            {
                savedSecond = __instance.maneuverNodes.IndexOf(second);
                if (savedSecond >= 0) __instance.maneuverNodes.RemoveAt(savedSecond);
            }
            if (node != null)
            {
                savedNode = __instance.maneuverNodes.IndexOf(node);
                if (savedNode >= 0) __instance.maneuverNodes.RemoveAt(savedNode);
            }
        }

        // A finalizer, so that the nodes come back into the list where the save throws too.
        private static void SaveWith(PatchedConicSolver __instance)
        {
            if (savedNode >= 0 && node != null) __instance.maneuverNodes.Insert(Math.Min(savedNode, __instance.maneuverNodes.Count), node);
            if (savedSecond >= 0 && second != null) __instance.maneuverNodes.Insert(Math.Min(savedSecond, __instance.maneuverNodes.Count), second);
            savedNode = savedSecond = -1;
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

        private void Update()
        {
            GuideNode.Sweep();
        }
    }
}
