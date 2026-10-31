using System;
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
        // The parts the vessel had when the route was handed over: a landing that breaks some is a failure.
        internal static int PartCount;
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

    // The flight after the vessel that was set down is flown (-redefinitionProbeResumeLanded): how it stands once the game has set it on the ground, or on the water.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal sealed class ResumedLandedProbe : MonoBehaviour
    {
        // What the game sees as it unpacks the vessel: the speeds it takes from the orbit, and the orbit itself.
        private static void Unpacked(Vessel vessel)
        {
            Orbit orbit = vessel.orbitDriver.orbit;
            CelestialBody body = vessel.mainBody;
            Debug.Log(Log.Tag + " Probe: unpacked: UT " + Planetarium.GetUniversalTime().ToString("0.00") + ", orbit epoch " + orbit.epoch.ToString("0.00") + ", " + orbit.PeA.ToString("0") + " x " + orbit.ApA.ToString("0")
                      + " m, orbit speed " + orbit.GetVel().magnitude.ToString("0.00") + ", rotating frame speed " + body.getRFrmVel(vessel.vesselTransform.position).magnitude.ToString("0.00")
                      + ", surface speed " + vessel.srfSpeed.ToString("0.00") + ", vertical " + vessel.verticalSpeed.ToString("0.00") + ", mode " + vessel.orbitDriver.updateMode
                      + ", throttle " + vessel.ctrlState.mainThrottle.ToString("0.00") + ", thrust " + vessel.parts.Sum(p => p.FindModulesImplementing<ModuleEngines>().Sum(e => (double)e.finalThrust)).ToString("0.0")
                      + " kN, " + vessel.parts.Count(p => p.FindModulesImplementing<ModuleEngines>().Any(e => e.EngineIgnited)) + " engine part(s) lit, Krakensbane " + Krakensbane.GetFrameVelocity().magnitude.ToString("0.00") + ", situation " + vessel.situation + ", landed " + vessel.Landed + ", rotating " + FlightGlobals.RefFrameIsRotating + ".");
        }

        private IEnumerator Start()
        {
            if (!ParsekLeave.Resumed || !CommandLine.Has("-redefinitionProbeResumeLanded")) yield break;
            while (FlightGlobals.ActiveVessel == null || FlightGlobals.ActiveVessel.packed) yield return null;
            Unpacked(FlightGlobals.ActiveVessel);
            // Every second for the first 80 (the vessel is let down for the first 60): what the game does with the vessel after it has set it on the ground.
            for (int second = 0; second < 80; second++)
            {
                yield return new WaitForSeconds(1f);
                Vessel flown = FlightGlobals.ActiveVessel;
                Debug.Log(Log.Tag + " Probe: resumed: state: " + flown.situation + ", round " + flown.mainBody.bodyName + ", " + flown.latitude.ToString("0.000") + ", " + flown.longitude.ToString("0.000")
                          + ", " + flown.altitude.ToString("0.0") + " m up, " + flown.heightFromTerrain.ToString("0.0") + " m over the ground, speed " + flown.srfSpeed.ToString("0.00")
                          + " m/s (" + flown.verticalSpeed.ToString("0.0") + " up), tilt " + Vector3.Angle(flown.vesselTransform.up, (Vector3)flown.upAxis).ToString("0") + " degrees, " + flown.parts.Count + " parts, packed " + flown.packed + ", crew " + flown.GetCrewCount() + ".");
                // A vessel set down by the background route stands still on the ground, a few metres over it at most, or floats at the water level.
                bool stands = flown.situation == Vessel.Situations.LANDED ? flown.heightFromTerrain <= 20f : flown.situation == Vessel.Situations.SPLASHED && Math.Abs(flown.altitude) <= 15.0;
                // A rocket stands on its engines or its legs on the ground, and lies on the water.
                float tilt = Vector3.Angle(flown.vesselTransform.up, (Vector3)flown.upAxis);
                if (second == 79 && (!stands || flown.srfSpeed > 2.0 || (flown.situation == Vessel.Situations.LANDED && tilt > 30f) || flown.parts.Count < 1))
                {
                    ParsekLeaveWatch.Fail("the vessel set down does not stand: " + flown.situation + ", " + flown.srfSpeed.ToString("0.0") + " m/s, " + flown.heightFromTerrain.ToString("0.0")
                                          + " m over the ground, tilted " + tilt.ToString("0") + " degrees, " + flown.parts.Count + " parts");
                    yield break;
                }
            }
            Debug.Log(Log.Tag + " Probe: resumed: ended.");
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
            // The flight was left by the flight probe at a burn: the recording was made by the route driver, and its end is Parsek's.
            if (ParsekLeave.EndUT <= 0.0) ParsekLeave.EndUT = ReDefinition.Autopilot.Flight.ParsekRecording.LatestEnd();
            Tell("on arrival");
            TimeWarp.fetch.WarpTo(ParsekLeave.EndUT + 120.0);
            float started = Time.realtimeSinceStartup;
            float next = 0f;
            while (Planetarium.GetUniversalTime() < ParsekLeave.EndUT + 60.0 && Time.realtimeSinceStartup - started < 900f)
            {
                if (Time.realtimeSinceStartup > next)
                {
                    next = Time.realtimeSinceStartup + 15f;
                    Tell("on the way");
                }
                yield return new WaitForSeconds(1f);
            }
            yield return new WaitForSeconds(15f);
            int found = Tell("after the end");
            // Parts that were let go are vessels of their own: all of them together are the parts the vessel had.
            int total = 0;
            foreach (Vessel each in FlightGlobals.Vessels)
                if (each.vesselName.StartsWith(ParsekLeave.Name)) total += each.protoVessel.protoPartSnapshots.Count;
            Debug.Log(Log.Tag + " Probe: leave: the vessels named " + ParsekLeave.Name + " hold " + total + " of " + ParsekLeave.PartCount + " parts.");
            // -redefinitionProbeExpectOrbit <body>: a vessel of the name is in an orbit round that body when the route is over.
            string expected = CommandLine.After("-redefinitionProbeExpectOrbit");
            if (expected != null && !FlightGlobals.Vessels.Any(v => v.vesselName.StartsWith(ParsekLeave.Name) && v.situation == Vessel.Situations.ORBITING && v.mainBody.bodyName == expected))
            {
                Fail("no vessel named " + ParsekLeave.Name + " is in an orbit round " + expected + " after the route");
                yield break;
            }
            if (ParsekLeave.PartCount > 0 && total != ParsekLeave.PartCount)
            {
                Fail("the parts of the vessels named " + ParsekLeave.Name + " add up to " + total + ", the vessel had " + ParsekLeave.PartCount);
                yield break;
            }
            if (CommandLine.Has("-redefinitionProbeResumeLanded") && !FlightGlobals.Vessels.Any(v => v.vesselName.StartsWith(ParsekLeave.Name) && (v.situation == Vessel.Situations.LANDED || v.situation == Vessel.Situations.SPLASHED)))
            {
                Fail("after the end of the route no vessel named " + ParsekLeave.Name + " stands on the ground or the water (" + found + " vessel(s) found)");
                yield break;
            }
            if (CommandLine.Has("-redefinitionProbeParsekResume"))
            {
                // Flies the vessel Parsek put back round the destination: the autopilot on it goes on with the route.
                Game game = HighLogic.CurrentGame.Updated();
                // With -redefinitionProbeResumeLanded the vessel that was set down is flown, wherever it stands.
                bool landed = CommandLine.Has("-redefinitionProbeResumeLanded");
                int index = landed
                    ? game.flightState.protoVessels.FindIndex(p => p.vesselName.StartsWith(ParsekLeave.Name) && (p.situation == Vessel.Situations.LANDED || p.situation == Vessel.Situations.SPLASHED))
                    : game.flightState.protoVessels.FindIndex(p => p.orbitSnapShot != null && p.orbitSnapShot.ReferenceBodyIndex == FlightGlobals.Bodies.FindIndex(b => b.bodyName == Destination));
                Debug.Log(Log.Tag + " Probe: leave: the vessel round " + Destination + " is flown (" + index + ").");
                if (index < 0)
                {
                    Fail("the vessel to fly was not found in the game's vessels");
                    yield break;
                }
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

        // The liquid fuel in the vessel's saved parts.
        private static double Propellant(Vessel vessel)
        {
            double sum = 0.0;
            foreach (ProtoPartSnapshot part in vessel.protoVessel.protoPartSnapshots)
                foreach (ProtoPartResourceSnapshot resource in part.resources)
                    if (resource.resourceName == "LiquidFuel") sum += resource.amount;
            return sum;
        }

        // The body the route ends at: where Parsek puts the vessel back.
        private static string Destination
        {
            get { return CommandLine.After("-redefinitionProbeMapPlannerTo") ?? "Duna"; }
        }

        // The probe ends the game at once and says why: a run that cannot reach its goal is not waited for.
        internal static void Fail(string reason)
        {
            Debug.Log(Log.Tag + " Probe: FAILED: " + reason + ".");
            Application.Quit();
        }

        private static int Tell(string when)
        {
            var found = FlightGlobals.Vessels.Where(v => v.vesselName.StartsWith(ParsekLeave.Name) || v.mainBody.bodyName == Destination).ToList();
            Debug.Log(Log.Tag + " Probe: leave: " + when + ", UT " + Planetarium.GetUniversalTime().ToString("0") + " (the recording ends at " + ParsekLeave.EndUT.ToString("0")
                      + "): " + found.Count + " vessel(s) named " + ParsekLeave.Name + " or round " + Destination + ".");
            foreach (Vessel v in found)
                Debug.Log(Log.Tag + " Probe: leave:   " + v.vesselName + " (pid " + v.persistentId + "), " + v.situation + ", round " + v.mainBody.bodyName + ", " + v.orbit.PeA.ToString("0") + " x " + v.orbit.ApA.ToString("0")
                          + " m, " + Propellant(v).ToString("0") + " units of fuel, " + v.protoVessel.protoPartSnapshots.Count + " parts, the autopilot " + AutopilotOn(v) + ", crew " + string.Join("/", v.protoVessel.GetVesselCrew().Select(c => c.name).ToArray())
                          + (v.persistentId == ParsekLeave.RemovedPid ? ", THE REMOVED ONE" : "") + ".");
            return found.Count;
        }
    }
}
