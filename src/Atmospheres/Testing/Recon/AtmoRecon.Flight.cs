using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The way into the flight: the save, the space centre, a pod made as KSP makes a vessel, placed over the sea off the coast nearest
    // to the launch pad. The space centre comes first because the body's position and rotation are valid there; a vessel in the air gets its
    // place from an orbit made out of that position.
    internal sealed partial class AtmoRecon
    {
        // The height over the sea at which the pod of run1 hangs under its parachute at the start of the flight.
        private const double DropAltitude = 1000.0;

        // Where the pod is, as the coast search found it.
        private double podLatitude, podLongitude, coastBearing, coastDistance;

        private IEnumerable<ProbeWait> ToFlight()
        {
            foreach (ProbeWait w in WaitFor(() => HighLogic.LoadedScene == GameScenes.MAINMENU, 300)) yield return w;
            if (!waited)
            {
                Fail("the main menu did not come up");
                yield break;
            }
            yield return ProbeWait.Seconds(10);

            bool template = File.Exists(Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "saves"), SaveFolderName), "probe.sfs"));
            Game game = GamePersistence.LoadGame(template ? "probe" : "persistent", SaveFolderName, true, false);
            if (game == null)
            {
                Fail("no save " + SaveFolderName);
                yield break;
            }
            HighLogic.SaveFolder = SaveFolderName;
            HighLogic.CurrentGame = game;
            Say("to the space centre from the save " + SaveFolderName);
            HighLogic.LoadScene(GameScenes.SPACECENTER);
            foreach (ProbeWait w in WaitFor(() => HighLogic.LoadedScene == GameScenes.SPACECENTER, 240)) yield return w;
            if (!waited)
            {
                Fail("the space centre did not come up");
                yield break;
            }
            yield return ProbeWait.Seconds(8);

            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null || home.pqsController == null || !home.ocean)
            {
                Fail("the home body has no terrain or no ocean");
                yield break;
            }
            if (!FindCoast(home))
            {
                Fail("no coast found within 25 km of the launch pad");
                yield break;
            }
            Say("coast found " + Num(coastDistance) + " m from the pad at bearing " + Num(coastBearing) + ", latitude " + Num(podLatitude) + ", longitude " + Num(podLongitude));
            EmitText("Env.coast", Num(podLatitude) + "," + Num(podLongitude) + " bearing " + Num(coastBearing) + " at " + Num(coastDistance) + " m");

            // The sun must be high for the fog, shader and ambient tests: the clock goes on in steps until it is, here at the space centre
            // where the body's position is valid, and the flight starts at that time.
            foreach (ProbeWait w in NoonSearch(home)) yield return w;

            Game current = HighLogic.CurrentGame;
            current.flightState.universalTime = Planetarium.GetUniversalTime();
            current.flightState.protoVessels.RemoveAll(v => v.vesselName == VesselName);
            int index = AddVessel(current, home, full ? Vessel.Situations.FLYING : Vessel.Situations.SPLASHED, podLatitude, podLongitude,
                                  full ? DropAltitude : 0.0);
            Say("to the flight of " + VesselName + (full ? " in the air at " + Num(DropAltitude) + " m" : " afloat"));
            FlightDriver.StartAndFocusVessel(current, index);
            foreach (ProbeWait w in WaitFor(FlightReady, 240)) yield return w;
            if (!waited)
            {
                Fail("the flight did not come up");
                yield break;
            }
            Vessel pod = FlightGlobals.ActiveVessel;
            Say("the flight stands: " + pod.vesselName + " " + pod.situation + " at " + Num(pod.altitude) + " m, " + pod.parts.Count + " parts");
            Emit("Env.pod_parts", pod.parts.Count);
            Emit("Env.pod_has_parachute", pod.FindPartModuleImplementing<ModuleParachute>() != null);
            Vector3d toSun = (FlightGlobals.Bodies[0].position - pod.CoMD).normalized;
            Vector3d up = (pod.CoMD - pod.mainBody.position).normalized;
            sunElevation = Math.Asin(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(toSun, up)))) * 180.0 / Math.PI;
            Emit("Env.sun_elevation_deg", sunElevation);

            // The place the frame convention gave the pod against the place planned: a pod over land would lose P4, P10 and P11 at the end of
            // a start, so it is checked here and those steps are left out with a row that says why.
            CelestialBody podBody = pod.mainBody;
            Vector3d planned = podBody.GetWorldSurfacePosition(podLatitude, podLongitude, 0.0);
            Vector3d actual = podBody.GetWorldSurfacePosition(pod.latitude, pod.longitude, 0.0);
            double offsetMetres = (planned - actual).magnitude;
            double groundHeight = podBody.pqsController.GetSurfaceHeight(podBody.GetRelSurfaceNVector(pod.latitude, pod.longitude)) - podBody.Radius;
            spawnOk = offsetMetres < 500.0 && groundHeight < -12.0;
            Emit("Env.pod_offset_m", offsetMetres);
            Emit("Env.pod_ground_m", groundHeight);
            Emit("Env.pod_over_sea", groundHeight < 0.0);
            Emit("Env.spawn_ok", spawnOk);
        }

        // Whether the pod stands over deep water close to the place planned; the steps that need the sea are left out otherwise.
        private bool spawnOk;

        // The sun's elevation over the pod in degrees, set when the flight stands; the tests that need daylight read it.
        private double sunElevation;

        private bool Daylight
        {
            get { return sunElevation >= 25.0; }
        }

        // Moves the clock on in quarter-hour steps until the sun is at least 55 degrees high over the launch pad (at most a solar day).
        private IEnumerable<ProbeWait> NoonSearch(CelestialBody home)
        {
            CelestialBody sun = FlightGlobals.Bodies[0];
            double step = 900.0;
            for (int i = 0; i < 40; i++)
            {
                Vector3d up = (home.GetWorldSurfacePosition(PadLatitude, PadLongitude, 0.0) - home.position).normalized;
                Vector3d toSun = (sun.position - home.position).normalized;
                double elevation = Math.Asin(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(toSun, up)))) * 180.0 / Math.PI;
                if (elevation >= 55.0)
                {
                    Say("the sun is " + Num(elevation) + " degrees over the pad after " + i + " steps of " + Num(step) + " s");
                    yield break;
                }
                Planetarium.SetUniversalTime(Planetarium.GetUniversalTime() + step);
                yield return ProbeWait.Frames(3);
            }
            Say("no sun above 55 degrees was found within the search");
        }

        private bool FlightReady()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT || !FlightGlobals.ready) return false;
            Vessel v = FlightGlobals.ActiveVessel;
            return v != null && v.vesselName == VesselName && v.loaded && !v.packed && FlightCamera.fetch != null && FlightCamera.fetch.mainCamera != null;
        }

        // The first stretch of sea along a ray from the pad that is deep for three steps in a row, nearest bearing first; the pod goes at
        // the third step, 200 m out, so that land is behind it.
        private bool FindCoast(CelestialBody home)
        {
            double radius = home.Radius;
            Func<double, double, double> ground = (lat, lon) => home.pqsController.GetSurfaceHeight(home.GetRelSurfaceNVector(lat, lon)) - radius;
            double best = double.MaxValue;
            for (int degrees = 0; degrees < 360; degrees += 15)
            {
                double bearing = degrees * Math.PI / 180.0;
                int deepSteps = 0;
                for (double distance = 400.0; distance <= 25000.0 && distance < best; distance += 100.0)
                {
                    double lat = PadLatitude + distance * Math.Cos(bearing) / radius * 180.0 / Math.PI;
                    double lon = PadLongitude + distance * Math.Sin(bearing) / (radius * Math.Cos(lat * Math.PI / 180.0)) * 180.0 / Math.PI;
                    deepSteps = ground(lat, lon) < -12.0 ? deepSteps + 1 : 0;
                    if (deepSteps < 3) continue;
                    // The wind of the drop carries the canopy away from the coast along this bearing: the sea must go on for 2 km.
                    bool clear = true;
                    for (double extra = 100.0; extra <= 2000.0 && clear; extra += 100.0)
                    {
                        double lat2 = PadLatitude + (distance + extra) * Math.Cos(bearing) / radius * 180.0 / Math.PI;
                        double lon2 = PadLongitude + (distance + extra) * Math.Sin(bearing) / (radius * Math.Cos(lat2 * Math.PI / 180.0)) * 180.0 / Math.PI;
                        clear = ground(lat2, lon2) < -12.0;
                    }
                    if (!clear)
                    {
                        deepSteps = 0;
                        continue;
                    }
                    best = distance;
                    podLatitude = lat;
                    podLongitude = lon;
                    coastBearing = degrees;
                    coastDistance = distance;
                    break;
                }
            }
            return best < double.MaxValue;
        }

        // A Mk1 command pod made as KSP makes a vessel and added to the game's vessels, upright on the body. In the air it gets an orbit from
        // the position over the ground at rest with the ground (the state KSP gives a vessel that is carried along by the body);
        // splashed it floats at sea level and KSP does not set it on the sea floor.
        private static int AddVessel(Game game, CelestialBody body, Vessel.Situations situation, double latitude, double longitude, double altitude)
        {
            bool flying = situation == Vessel.Situations.FLYING;
            bool splashed = situation == Vessel.Situations.SPLASHED;
            double ut = Planetarium.GetUniversalTime();
            // The Mk1 pod has no parachute of its own: the Mk16 (parachuteSingle) sits on its top node. The saved form of a stack
            // attachment is the index of the other part: the pod (part 0) lists "attN = top, 1", the canopy "attN = bottom, 0", and the
            // canopy's place is the pod's top node (0.6423756 m, scale 1) plus the canopy's bottom node below its own origin: 0.120649 m
            // times the canopy's scale of 0.1, as KSP multiplies the node positions of a part by its scale (the Mk16-XL sits 0.0021 m
            // over its parent's node in the saved vessels).
            uint podId = ShipConstruction.GetUniqueFlightID(game.flightState);
            uint canopyId = ShipConstruction.GetUniqueFlightID(game.flightState);
            if (canopyId == podId) canopyId = podId + 1;
            ConfigNode pod = ProtoVessel.CreatePartNode("mk1pod.v2", podId);
            ConfigNode canopy = ProtoVessel.CreatePartNode("parachuteSingle", canopyId);
            pod.SetValue("parent", "0", true);
            pod.SetValue("position", "0,0,0", true);
            pod.RemoveValues("attN");
            pod.AddValue("attN", "top, 1");
            pod.AddValue("attN", "bottom, -1");
            canopy.SetValue("parent", "0", true);
            canopy.SetValue("position", "0,0.6544405,0", true);
            canopy.SetValue("rotation", "0,0,0,1", true);
            canopy.RemoveValues("attN");
            canopy.AddValue("attN", "bottom, 0");
            var orbit = new Orbit(0.0, 0.0, body.Radius + 100000.0, 0.0, 0.0, 0.0, ut, body);
            if (flying)
            {
                Vector3d surface = body.GetWorldSurfacePosition(latitude, longitude, altitude) - body.position;
                orbit.UpdateFromStateVectors(surface.xzy, Vector3d.Cross(body.angularVelocity, surface).xzy, body, ut);
            }
            ConfigNode node = ProtoVessel.CreateVesselNode(VesselName, VesselType.Ship, orbit, 0, new[] { pod, canopy });
            node.SetValue("sit", situation.ToString());
            node.SetValue("landed", "False");
            node.SetValue("splashed", splashed ? "True" : "False");
            node.SetValue("skipGroundPositioning", splashed || flying ? "True" : "False");
            node.SetValue("lat", latitude.ToString(CultureInfo.InvariantCulture));
            node.SetValue("lon", longitude.ToString(CultureInfo.InvariantCulture));
            node.SetValue("alt", altitude.ToString(CultureInfo.InvariantCulture));
            Quaternion upright = Quaternion.FromToRotation(Vector3.up, (Vector3)body.GetRelSurfaceNVector(latitude, longitude));
            node.SetValue("rot", KSPUtil.WriteQuaternion(upright));
            game.flightState.protoVessels.Add(new ProtoVessel(node, game));
            return game.flightState.protoVessels.Count - 1;
        }

        // The flight camera on the pod at a distance, a pitch above the horizon and a heading, in degrees, with the limits widened so
        // that the camera can be 10 km out.
        private static void PlaceCamera(float distance, float pitchDegrees, float headingDegrees)
        {
            FlightCamera camera = FlightCamera.fetch;
            if (camera == null) return;
            camera.maxDistance = Mathf.Max(camera.maxDistance, distance + 10f);
            camera.minDistance = Mathf.Min(camera.minDistance, distance);
            camera.SetDistanceImmediate(distance);
            camera.camPitch = pitchDegrees * Mathf.Deg2Rad;
            camera.camHdg = headingDegrees * Mathf.Deg2Rad;
        }
    }
}
