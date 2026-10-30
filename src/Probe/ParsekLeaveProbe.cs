using System.Collections;
using System.Linq;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Probe
{
    // What the flight probe hands to the tracking station: the vessel was taken out of the world and recorded for Parsek, and its recording ends
    // at EndUT.
    internal static class ParsekLeave
    {
        internal static bool Active;
        internal static string Name;
        internal static double EndUT;
        // The persistent id of the vessel taken out of the world, to tell it among the vessels in the other scene.
        internal static uint RemovedPid;
        // Where the flight probe goes: the tracking station, or, with -redefinitionProbeParsekLeaveTo KSC, the space centre.
        internal static GameScenes Scene = GameScenes.TRACKSTATION;
        // Set once the time was warped in the space centre: the tracking station is then only looked at, with -redefinitionProbeParsekThenStation.
        internal static bool Second;
        // Set once the vessel Parsek put back is flown: the scene probe leaves that flight alone.
        internal static bool Resumed;
    }

    // -redefinitionProbeParsekLeave: the flight probe left with a recording, and in the tracking station the time is warped past its end.
    // The vessels of that name are told as they are there: Parsek's ghost on the way, and the vessel it puts back at the end.
    [KSPAddon(KSPAddon.Startup.TrackingStation, false)]
    internal sealed class ParsekLeaveProbe : MonoBehaviour
    {
        private IEnumerator Start()
        {
            return ParsekLeaveWatch.Run();
        }
    }

    // The same in the space centre.
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    internal sealed class ParsekLeaveCentreProbe : MonoBehaviour
    {
        private IEnumerator Start()
        {
            return ParsekLeaveWatch.Run();
        }
    }

    internal static class ParsekLeaveWatch
    {
        internal static IEnumerator Run()
        {
            if (ParsekLeave.Second)
            {
                ParsekLeave.Second = false;
                yield return new WaitForSeconds(15f);
                Tell("in the tracking station after the space centre");
                Debug.Log(Log.Tag + " Probe: leave done.");
                yield break;
            }
            if (!ParsekLeave.Active) yield break;
            ParsekLeave.Active = false;
            yield return new WaitForSeconds(8f);
            Tell("on arrival");
            TimeWarp.fetch.WarpTo(ParsekLeave.EndUT + 120.0);
            float started = Time.realtimeSinceStartup;
            float next = 0f;
            while (Planetarium.GetUniversalTime() < ParsekLeave.EndUT + 60.0 && Time.realtimeSinceStartup - started < 900f)
            {
                if (Time.realtimeSinceStartup > next)
                {
                    next = Time.realtimeSinceStartup + 30f;
                    Tell("on the way");
                }
                yield return new WaitForSeconds(1f);
            }
            yield return new WaitForSeconds(15f);
            Tell("after the end");
            if (CommandLine.Has("-redefinitionProbeParsekResume"))
            {
                // Flies the vessel Parsek put back round Duna: the autopilot on it goes on with the route.
                Game game = HighLogic.CurrentGame.Updated();
                int index = game.flightState.protoVessels.FindIndex(p => p.orbitSnapShot != null && p.orbitSnapShot.ReferenceBodyIndex == FlightGlobals.Bodies.FindIndex(b => b.bodyName == "Duna"));
                Debug.Log(Log.Tag + " Probe: leave: the vessel round Duna is flown (" + index + ").");
                ParsekLeave.Resumed = true;
                if (index >= 0) FlightDriver.StartAndFocusVessel(game, index);
                yield break;
            }
            if (CommandLine.Has("-redefinitionProbeParsekThenStation") && HighLogic.LoadedScene == GameScenes.SPACECENTER)
            {
                ParsekLeave.Second = true;
                // The tracking station building saves the game before the scene is loaded, which keeps the time.
                GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
                HighLogic.LoadScene(GameScenes.TRACKSTATION);
                yield break;
            }
            Debug.Log(Log.Tag + " Probe: leave done.");
        }

        // What the vessel's saved route state says: on, off, or not there.
        private static string AutopilotOn(Vessel vessel)
        {
            var node = new ConfigNode();
            vessel.protoVessel.Save(node);
            ConfigNode modules = node.GetNode("VESSELMODULES");
            ConfigNode state = modules != null ? modules.GetNode("RouteState") : null;
            return state == null ? "has no route state" : state.GetValue("autopilot") == "True" ? "is on" : "is off";
        }

        private static void Tell(string when)
        {
            var found = FlightGlobals.Vessels.Where(v => v.vesselName.StartsWith(ParsekLeave.Name) || v.mainBody.bodyName == "Duna").ToList();
            Debug.Log(Log.Tag + " Probe: leave: " + when + ", UT " + Planetarium.GetUniversalTime().ToString("0") + " (the recording ends at " + ParsekLeave.EndUT.ToString("0")
                      + "): " + found.Count + " vessel(s) named " + ParsekLeave.Name + " or round Duna.");
            foreach (Vessel v in found)
                Debug.Log(Log.Tag + " Probe: leave:   " + v.vesselName + " (pid " + v.persistentId + "), " + v.situation + ", round " + v.mainBody.bodyName + ", " + v.orbit.PeA.ToString("0") + " x " + v.orbit.ApA.ToString("0")
                          + " m, " + v.protoVessel.protoPartSnapshots.Count + " parts, the autopilot " + AutopilotOn(v) + ", crew " + string.Join("/", v.protoVessel.GetVesselCrew().Select(c => c.name).ToArray())
                          + (v.persistentId == ParsekLeave.RemovedPid ? ", THE REMOVED ONE" : "") + ".");
        }
    }
}
