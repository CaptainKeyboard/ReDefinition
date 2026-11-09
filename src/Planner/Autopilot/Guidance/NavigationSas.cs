using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Planner.Autopilot.Guidance
{
    // The SAS's maneuver mode steers to where the navigation asks the nose to point in a climb or a descent, as it steers to a maneuver node. The game
    // offers the mode only with a node and reads its direction from the node. With the navigation's direction on the board and no node on the
    // vessel the mode is offered, and its direction is the board's, which follows the altitude.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class NavigationSas : MonoBehaviour
    {
        private const string HarmonyId = "ReDefinition.NavigationSas";
        private static bool hooked;
        // Whether the mode was chosen for the guidance under way, with the SAS off and with it on: the player may choose another afterwards, and the game
        // sets the stability assist where the SAS is switched on.
        private bool chosenOff, chosenOn;

        private void Start()
        {
            if (hooked) return;
            hooked = true;
            HarmonyHooks.Safely("navigation-sas", "The SAS cannot steer to the navigation's direction", () =>
                HarmonyHooks.Install(HarmonyId, typeof(NavigationSas), new List<HarmonyHook>
                {
                    new HarmonyHook(typeof(VesselAutopilot).GetMethod("ManeuverLockInvalid", BindingFlags.Instance | BindingFlags.NonPublic), null, nameof(Offered), null),
                    new HarmonyHook(typeof(VesselAutopilot).GetMethod("SetAutopilot", BindingFlags.Instance | BindingFlags.NonPublic), nameof(Steer), null, null),
                }));
        }

        // Whether the navigation has a direction for the vessel and the vessel has no node of its own: with a node the game steers to it.
        private static bool Steers(Vessel vessel)
        {
            if (vessel == null || !GuidanceBoard.Current(vessel) || !GuidanceBoard.HasDirection) return false;
            return GuideNode.First(vessel) == null;
        }

        // The game finds the mode locked without a node.
        private static void Offered(ref bool __result, Vessel ___vessel)
        {
            if (__result && Steers(___vessel)) __result = false;
        }

        // The mode points the SAS along the board's direction where the game would read a node's.
        private static bool Steer(VesselAutopilot __instance, ref bool __result, bool initialize, VesselAutopilot.AutopilotMode ___mode, Vessel ___vessel)
        {
            if (___mode != VesselAutopilot.AutopilotMode.Maneuver || !Steers(___vessel)) return true;
            __instance.SAS.SetTargetOrientation((Vector3)GuidanceBoard.Direction, initialize);
            __result = true;
            return false;
        }

        // With the guidance the mode is chosen once, so that the SAS follows it as soon as it is on.
        private void LateUpdate()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            // With the navigation's own node the game offers the mode itself; without a node the hooks do.
            if (!Steers(vessel) && !(GuidanceBoard.Current(vessel) && GuideNode.Holds(vessel)))
            {
                chosenOff = false;
                chosenOn = false;
                return;
            }
            VesselAutopilot autopilot = vessel.Autopilot;
            if (autopilot == null) return;
            if (autopilot.Enabled)
            {
                if (chosenOn) return;
                chosenOn = true;
            }
            else
            {
                chosenOn = false;
                if (chosenOff) return;
                chosenOff = true;
            }
            if (autopilot.Mode != VesselAutopilot.AutopilotMode.Maneuver) autopilot.SetMode(VesselAutopilot.AutopilotMode.Maneuver);
        }
    }
}
