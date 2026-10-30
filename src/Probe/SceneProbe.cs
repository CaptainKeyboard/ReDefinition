using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using ReDefinition.Proxy;
using ReDefinition.Core;
using ReDefinition.Modules;
using ReDefinition.Settings;
using ReDefinition.Ui;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Probe
{
    // A measurement the game runs on its own, started with -redefinitionProbe on KSP's
    // command line. KSP loads the save ReDefinitionProbe and goes to the flight of the
    // vessel waiting on the launch pad. By default it turns the flight camera on a fixed path from
    // the vessel up into the sky. It writes what the scene camera holds at each stage
    // of its rendering, around the vessel, into ReDefinitionCaptures\<time>-probe.
    // Each run is made twice, with the vessel and with its renderers hidden, so that the
    // difference is what the vessel does to the sky around it, whatever the clouds.
    // With -redefinitionProbeVariants the same is made with each other mod's component and command
    // buffer on the flight's cameras switched off in turn. Then KSP quits. tools/analyze_probe.py
    // measures the halo and trail around the vessel in each.
    //
    // The same flight hosts the other probes, each started by its own switch. They are the light probe, the views,
    // the vendors' checks, the FLIP sweep, the soak, the scene round trips and the staging.
    // The scenes at sea, on grass and on another body (-redefinitionProbeSea, -redefinitionProbeGrass,
    // -redefinitionProbeLand) belong to them, as do the single shots of -redefinitionProbeStay.
    // The README of this folder lists them.
    //
    // Nothing of it runs without the argument.
    internal static class SceneProbe
    {
        // The switch that starts the probe; every other probe switch works only with it.
        internal const string Argument = "-redefinitionProbe";
        // -redefinitionProbeSave <folder>: another save than ReDefinitionProbe, a copy of a
        // player's (the probe saves the flight it leaves into it); -redefinitionProbeVessel
        // <name>: that vessel's flight, wherever it is.
        internal static readonly string SaveFolder = CommandLine.After("-redefinitionProbeSave") ?? "ReDefinitionProbe";
        internal static readonly string VesselName = CommandLine.After("-redefinitionProbeVessel");
        // -redefinitionProbeBody <name>: the sea capsule on that body's sea instead of the
        // home body's (-redefinitionProbeSea); -redefinitionProbeMoon <name>: the moon the
        // light probe's "mun-" shots look at instead of the Mun.
        internal static readonly string BodyName = CommandLine.After("-redefinitionProbeBody");
        internal static readonly string MoonName = CommandLine.After("-redefinitionProbeMoon") ?? "Mun";

        // Whether the main menu has started the probe's flight already, so that a return to it does not again.
        internal static bool Launched;
        // -redefinitionProbeSea: a capsule afloat out at sea, no land within kilometres.
        internal const string SeaArgument = "-redefinitionProbeSea";
        // -redefinitionProbeCoast, with it: the capsule off the island airfield's shore.
        internal const string CoastArgument = "-redefinitionProbeCoast";
        // -redefinitionProbeGrass: a capsule on a field of Parallax's grass, kilometres from the
        // space centre, instead of the launch pad (the benchmark's grass scene).
        internal const string GrassArgument = "-redefinitionProbeGrass";
        // The grass scene's vessel; one left in a save the probe loaded from "persistent" is taken out
        // before the next is made, so that two capsules never stand on the same spot.
        internal const string GrassVesselName = "ReDefinition grass probe";
        // -redefinitionProbeStay: the flight left to the player, the clouds hidden, no
        // measurement, KSP left running (with -redefinitionProbeSea: a capsule at sea to
        // look at the water's reflections oneself).
        internal const string StayArgument = "-redefinitionProbeStay";

        // -redefinitionProbeScenes <n>: n round trips from the flight to the space
        // centre and back, each flight settled, for what scene changes leave behind
        // in the logs (memory, devices, the upscaler's and raytracing's setup). -1
        // where not asked for.
        internal static int ScenesLeft = CommandLine.Int("-redefinitionProbeScenes", -1, true);
        // On each round trip the space centre first opens the VAB, then the flight.
        internal static bool EditorVisited;

        // The variants to run, -redefinitionProbeVariants <part of their name, or all>:
        // none without it. The baseline first says at which stage the halo enters.
        internal static string Variants
        {
            get { return CommandLine.After("-redefinitionProbeVariants", true); }
        }

        // Whether -redefinitionProbe is on the command line, in any case. Read once: the add-on asks in every frame,
        // and the command line is a new array on each read.
        internal static readonly bool Requested = CommandLine.Has(Argument, true);
    }

    // In the main menu: the save's flight, once.
    [KSPAddon(KSPAddon.Startup.MainMenu, false)]
    internal sealed class SceneProbeLauncher : MonoBehaviour
    {
        // 3 s after the menu stands: a profile applied and KSP quit (-redefinitionProbeProfile), or the probe's
        // save loaded and the flight of its vessel started.
        private IEnumerator Start()
        {
            if (SceneProbe.Launched || !SceneProbe.Requested) yield break;
            SceneProbe.Launched = true;
            yield return new WaitForSeconds(3f);
            // -redefinitionProbeProfile <name>: that graphics profile applied as the main
            // menu's question applies it, saved in the mods, and KSP quits, so that the next
            // start runs with it all; the caller keeps copies of the files it changes.
            string profileName = CommandLine.After("-redefinitionProbeProfile");
            if (profileName != null)
            {
                List<string> problems = new List<string>();
                List<GraphicsProfile> profiles = ProfileApplier.Ordered(problems);
                GraphicsProfile profile = ProfileApplier.Find(profiles, profileName);
                if (profile == null) Debug.LogWarning(Log.Tag + " Probe: no profile " + profileName + ".");
                else
                {
                    BundledSettings.SetEnabled(true);
                    ModuleProfiles.ApplyNow(profile, profiles, problems);
                    if (ReDefinitionAddon.Instance != null) ReDefinitionAddon.Instance.SaveSettingsForProbeProfile();
                    Debug.Log(Log.Tag + " Probe: profile " + profileName + " applied.");
                }
                foreach (string problem in problems) Debug.LogWarning(Log.Tag + " Probe: " + problem);
                yield return new WaitForSeconds(2f);
                Application.Quit();
                yield break;
            }
            // From the template the probe never writes, where it is: KSP saves the flight it
            // leaves into "persistent", time warps and moved vessels with it.
            bool template = System.IO.File.Exists(System.IO.Path.Combine(System.IO.Path.Combine(
                KSPUtil.ApplicationRootPath, "saves"), System.IO.Path.Combine(SceneProbe.SaveFolder, "probe.sfs")));
            Game game = GamePersistence.LoadGame(template ? "probe" : "persistent", SceneProbe.SaveFolder, true, false);
            if (game == null)
            {
                Debug.LogWarning(Log.Tag + " Probe: no save " + SceneProbe.SaveFolder + "; quitting.");
                Application.Quit();
                yield break;
            }
            // -redefinitionProbeScenario <name>: the flight of that save (Scenario), in the vessel it was left in.
            Game scenario = Scenario.Load();
            if (scenario != null)
            {
                HighLogic.SaveFolder = SceneProbe.SaveFolder;
                HighLogic.CurrentGame = scenario;
                Debug.Log(Log.Tag + " Probe: the flight of the scenario " + CommandLine.After(Scenario.LoadArgument) + ".");
                FlightDriver.StartAndFocusVessel(scenario, scenario.flightState.activeVesselIdx);
                yield break;
            }
            // -redefinitionProbeUniversalTime <seconds>: the game's time, to put the moons in a phase.
            string universalTime = CommandLine.After("-redefinitionProbeUniversalTime");
            if (universalTime != null) game.flightState.universalTime = double.Parse(universalTime, CultureInfo.InvariantCulture);
            // -redefinitionProbeDays <n>: n of the home body's solar days later, the sun where
            // it was and the clouds moved on.
            string days = CommandLine.After("-redefinitionProbeDays");
            if (days != null)
            {
                CelestialBody home = FlightGlobals.GetHomeBody();
                double day = home != null && home.solarRotationPeriod ? home.solarDayLength : 21600.0;
                game.flightState.universalTime += day * double.Parse(days, CultureInfo.InvariantCulture);
                Debug.Log(Log.Tag + " Probe: " + days + " solar days of " + day.ToString("0.###", CultureInfo.InvariantCulture) + " s on.");
            }
            int index = SceneProbe.VesselName != null
                ? game.flightState.protoVessels.FindIndex(v => v.vesselName == SceneProbe.VesselName)
                : game.flightState.protoVessels.FindIndex(v => v.situation == Vessel.Situations.PRELAUNCH);
            if (index < 0)
            {
                Debug.LogWarning(Log.Tag + " Probe: no vessel on the launch pad in " + SceneProbe.SaveFolder + "; quitting.");
                Application.Quit();
                yield break;
            }
            if (SceneProbe.VesselName == null && CommandLine.Has(SceneProbe.SeaArgument))
            {
                int capsule = SeaCapsule(game);
                if (capsule < 0)
                {
                    // Elsewhere than asked for, every measurement would be of the wrong place.
                    Debug.LogError(Log.Tag + " Probe: the capsule could not be put at sea; quitting.");
                    Application.Quit();
                    yield break;
                }
                index = capsule;
            }
            if (SceneProbe.VesselName == null && CommandLine.Has(SceneProbe.GrassArgument))
            {
                int capsule = GrassCapsule(game);
                if (capsule < 0)
                {
                    // On the pad, every measurement would be of the wrong scene. The benchmark reads this line
                    // (tools/benchmark_matrix.py, "ready" of the grass scene) and says the scene is unavailable.
                    Debug.LogError(Log.Tag + " Probe: grass scene unavailable: no capsule could be put on a field of grass; quitting.");
                    Application.Quit();
                    yield break;
                }
                index = capsule;
            }
            if (SceneProbe.VesselName == null && LandProbe.OrbitBodyName != null)
            {
                int capsule = LandProbe.OrbitCapsule(game);
                if (capsule < 0)
                {
                    Debug.LogError(Log.Tag + " Probe: the capsule could not be put in orbit round " + LandProbe.OrbitBodyName + "; quitting.");
                    Application.Quit();
                    yield break;
                }
                index = capsule;
            }
            if (SceneProbe.VesselName == null && LandProbe.BodyName != null)
            {
                int capsule = LandProbe.Capsule(game);
                if (capsule < 0)
                {
                    // On the pad, every shot would be of the wrong body.
                    Debug.LogError(Log.Tag + " Probe: the capsule could not be put on " + LandProbe.BodyName + "; quitting.");
                    Application.Quit();
                    yield break;
                }
                index = capsule;
            }
            HighLogic.SaveFolder = SceneProbe.SaveFolder;
            HighLogic.CurrentGame = game;
            // -redefinitionProbeLaunch <craft>: a new launch of that craft instead (StagingProbe), from the space
            // centre, whose scene holds the launch pad KSP puts a new vessel on.
            if (StagingProbe.CraftPath() != null)
            {
                // The pad cleared of the save's vessel, as KSP clears it for a launch.
                game.flightState.protoVessels.RemoveAt(index);
                StagingProbe.Pending = true;
                Debug.Log(Log.Tag + " Probe: to the space centre for a new launch.");
                HighLogic.LoadScene(GameScenes.SPACECENTER);
                yield break;
            }
            Debug.Log(Log.Tag + " Probe: flight of " + game.flightState.protoVessels[index].vesselName + ".");
            FlightDriver.StartAndFocusVessel(game, index);
        }

        // A Mk1 command pod made as KSP makes a vessel (ProtoVessel.CreateVesselNode) and added to the game's vessels, upright
        // on the body at the latitude and longitude, at the altitude given. Landed it is set on the ground by KSP; splashed it
        // floats at sea level and is not set down on the ground, which at sea is the sea floor. It returns the vessel's index.
        private static int AddProbeVessel(Game game, CelestialBody body, string name, Vessel.Situations situation, double latitude,
                                          double longitude, double altitude)
        {
            bool splashed = situation == Vessel.Situations.SPLASHED;
            ConfigNode part = ProtoVessel.CreatePartNode("mk1pod.v2", ShipConstruction.GetUniqueFlightID(game.flightState));
            var orbit = new Orbit(0.0, 0.0, body.Radius + 100000.0, 0.0, 0.0, 0.0, Planetarium.GetUniversalTime(), body);
            ConfigNode node = ProtoVessel.CreateVesselNode(name, VesselType.Ship, orbit, 0, new[] { part });
            node.SetValue("sit", situation.ToString());
            node.SetValue("landed", splashed ? "False" : "True");
            node.SetValue("splashed", splashed ? "True" : "False");
            node.SetValue("skipGroundPositioning", splashed ? "True" : "False");
            node.SetValue("lat", latitude.ToString(CultureInfo.InvariantCulture));
            node.SetValue("lon", longitude.ToString(CultureInfo.InvariantCulture));
            node.SetValue("alt", altitude.ToString(CultureInfo.InvariantCulture));
            Quaternion upright = Quaternion.FromToRotation(Vector3.up, (Vector3)body.GetRelSurfaceNVector(latitude, longitude));
            node.SetValue("rot", KSPUtil.WriteQuaternion(upright));
            game.flightState.protoVessels.Add(new ProtoVessel(node, game));
            return game.flightState.protoVessels.Count - 1;
        }

        // A Mk1 command pod on the ground where Parallax's Kerbin grass grows thick. It stands at the first
        // place, by distance from the launch pad, in the Grasslands biome (Kerbin.cfg's grass
        // scatters grow from 25 m of altitude and are blacklisted from water, deserts and
        // ice). The place is on level ground that is grassland and above 80 m all round, 3 km from the pad
        // at least. The pod is made as KSP makes a vessel and set on the ground by KSP.
        private static int GrassCapsule(Game game)
        {
            // Without Parallax the biome is bare: a field the shot would call grass.
            if (!ReDefinition.Scenery.KscGrass.ParallaxInstalled)
            {
                Debug.LogError(Log.Tag + " Probe: grass scene unavailable: Parallax is not installed.");
                return -1;
            }
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null || home.pqsController == null || home.BiomeMap == null)
            {
                Debug.LogError(Log.Tag + " Probe: grass scene unavailable: the home body has no terrain or no biome map.");
                return -1;
            }
            const double padLatitude = -0.0972, padLongitude = -74.5577;
            double radius = home.Radius;
            Func<double, double, double> ground = (lat, lon) =>
                home.pqsController.GetSurfaceHeight(home.GetRelSurfaceNVector(lat, lon)) - radius;
            Func<double, double, string> biome = (lat, lon) =>
                home.BiomeMap.GetAtt(lat * Math.PI / 180.0, lon * Math.PI / 180.0).name;
            for (double distance = 3000.0; distance <= 30000.0; distance += 500.0)
                for (int degrees = 0; degrees < 360; degrees += 10)
                {
                    double bearing = degrees * Math.PI / 180.0;
                    double lat = padLatitude + distance * Math.Cos(bearing) / radius * 180.0 / Math.PI;
                    double lon = padLongitude + distance * Math.Sin(bearing) / (radius * Math.Cos(lat * Math.PI / 180.0)) * 180.0 / Math.PI;
                    double here = ground(lat, lon);
                    if (here < 80.0 || here > 600.0) continue;
                    // 7 x 7 samples 40 m apart: grassland, above 80 m, and a slope of less than 8 degrees.
                    bool field = true;
                    double step = 40.0 / radius * 180.0 / Math.PI;
                    for (int i = -3; i <= 3 && field; i++)
                        for (int j = -3; j <= 3 && field; j++)
                        {
                            double sampleLat = lat + i * step, sampleLon = lon + j * step / Math.Cos(lat * Math.PI / 180.0);
                            if (biome(sampleLat, sampleLon) != "Grasslands")
                            {
                                field = false;
                                continue;
                            }
                            double sampleHeight = ground(sampleLat, sampleLon);
                            field = sampleHeight > 80.0 && Math.Abs(sampleHeight - here) < 40.0 * 3.0 * Math.Tan(8.0 * Math.PI / 180.0);
                        }
                    if (!field) continue;
                    game.flightState.protoVessels.RemoveAll(v => v.vesselName == SceneProbe.GrassVesselName);
                    int index = AddProbeVessel(game, home, SceneProbe.GrassVesselName, Vessel.Situations.LANDED, lat, lon, here + 1.0);
                    Debug.Log(Log.Tag + " Probe: a capsule on grassland " + distance.ToString("0") + " m from the pad (bearing " + degrees
                              + "), latitude " + lat.ToString(CultureInfo.InvariantCulture) + ", longitude "
                              + lon.ToString(CultureInfo.InvariantCulture) + ", the ground " + here.ToString("0", CultureInfo.InvariantCulture)
                              + " m above the sea.");
                    return index;
                }
            Debug.LogError(Log.Tag + " Probe: grass scene unavailable: no field of grassland found around the pad.");
            return -1;
        }

        // A Mk1 command pod afloat out at sea east of the space centre, no land within
        // kilometres: the first of a few places where the sea floor lies deep there and
        // round it. Made as KSP makes a vessel (ProtoVessel.CreateVesselNode), splashed at
        // sea level and not set down on the ground, which there is the sea floor.
        private static int SeaCapsule(Game game)
        {
            CelestialBody home = SceneProbe.BodyName != null
                ? FlightGlobals.Bodies.Find(b => b.bodyName == SceneProbe.BodyName)
                : FlightGlobals.GetHomeBody();
            if (home == null || home.pqsController == null || !home.ocean) return -1;
            Func<double, double, double> ground = (lat, lon) =>
                home.pqsController.GetSurfaceHeight(home.GetRelSurfaceNVector(lat, lon)) - home.Radius;
            // -redefinitionProbeCoast: 2.6 km off the island airfield's east shore, the
            // island and its sea floor in view; otherwise far out at sea.
            bool coast = CommandLine.Has(SceneProbe.CoastArgument);
            var places = new List<double[]>();
            if (coast)
                // Eastwards from the shore until the sea is deep enough: the shore lies a
                // little elsewhere with other terrain.
                for (double lon = -71.70; lon <= -71.30; lon += 0.02)
                    places.Add(new[] { -1.54, lon });
            else if (SceneProbe.BodyName == null)
                places.AddRange(new[] { new[] { -1.0, -66.0 }, new[] { 0.0, -62.0 }, new[] { -4.0, -64.0 }, new[] { 3.0, -60.0 } });
            else
                // Another body: the first deep sea near its equator.
                for (double lat = 0.0; lat <= 20.0; lat += 5.0)
                    for (double lon = -180.0; lon < 180.0; lon += 10.0)
                    {
                        places.Add(new[] { lat, lon });
                        if (lat > 0.0) places.Add(new[] { -lat, lon });
                    }
            foreach (double[] place in places)
            {
                bool deep = true;
                for (int i = -2; i <= 2 && deep; i++)
                    for (int j = -2; j <= 2 && deep; j++)
                        deep = ground(place[0] + i * (coast ? 0.01 : 0.2), place[1] + j * (coast ? 0.01 : 0.2)) < (coast ? -10.0 : -100.0);
                if (!deep) continue;
                int index = AddProbeVessel(game, home, "ReDefinition sea probe", Vessel.Situations.SPLASHED, place[0], place[1], 0.0);
                Debug.Log(Log.Tag + " Probe: a capsule afloat on " + home.bodyName + " at latitude " + place[0].ToString(CultureInfo.InvariantCulture)
                          + ", longitude " + place[1].ToString(CultureInfo.InvariantCulture) + ", the sea floor "
                          + ground(place[0], place[1]).ToString("0", CultureInfo.InvariantCulture) + " m.");
                return index;
            }
            Debug.LogWarning(Log.Tag + " Probe: no open sea found at any of the places tried.");
            return -1;
        }
    }

    // At the space centre: the staging probe's new launch, or during the scene round trips back to the vessel's flight.
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    internal sealed class SceneProbeReturn : MonoBehaviour
    {
        // A pending new launch of the staging probe first; else, 10 s in, every other round trip into the VAB,
        // the others back to the flight of the vessel on the pad.
        private IEnumerator Start()
        {
            // The new launch the probe asked for (StagingProbe), once the space centre stands.
            if (SceneProbe.Requested && StagingProbe.Pending)
            {
                StagingProbe.Pending = false;
                yield return new WaitForSeconds(5f);
                if (!StagingProbe.Launch(HighLogic.CurrentGame)) Application.Quit();
                yield break;
            }
            if (!SceneProbe.Requested || SceneProbe.ScenesLeft < 0 || HighLogic.SaveFolder != SceneProbe.SaveFolder)
                yield break;
            yield return new WaitForSeconds(10f);
            if (!SceneProbe.EditorVisited)
            {
                SceneProbe.EditorVisited = true;
                Debug.Log(Log.Tag + " Probe: to the VAB.");
                EditorDriver.StartEditor(EditorFacility.VAB);
                yield break;
            }
            SceneProbe.EditorVisited = false;
            Game game = HighLogic.CurrentGame;
            int index = game.flightState.protoVessels.FindIndex(v => v.situation == Vessel.Situations.PRELAUNCH);
            if (index < 0)
            {
                Debug.LogWarning(Log.Tag + " Probe: no vessel on the launch pad after the round trip; quitting.");
                Application.Quit();
                yield break;
            }
            Debug.Log(Log.Tag + " Probe: back to the flight of " + game.flightState.protoVessels[index].vesselName + ".");
            FlightDriver.StartAndFocusVessel(game, index);
        }
    }

    // In the VAB during the scene round trips: shots of its surfaces, then back to the space centre.
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    internal sealed class SceneProbeEditor : MonoBehaviour
    {
        // 35 s in: the VAB's shots, then the space centre.
        private IEnumerator Start()
        {
            if (!SceneProbe.Requested || SceneProbe.ScenesLeft < 0 || HighLogic.SaveFolder != SceneProbe.SaveFolder)
                yield break;
            yield return new WaitForSeconds(35f);
            // The VAB's inside as the surfaces' materials mean their shine, as Deferred gives
            // it, and the surfaces' overlay (RtCamera, SurfaceIntent).
            string folder = Path.Combine(CaptureFolder.Root,
                                         DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-vab");
            Directory.CreateDirectory(folder);
            foreach (string shot in new[] { "on", "deferred", "overlay" })
            {
                Raytracing.RtCamera.SurfacesAsDeferred = shot == "deferred";
                Raytracing.RtCamera.SurfaceView = shot == "overlay";
                yield return ProbeSteps.Frames(30);
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, shot + ".png"));
                SceneProbeRunner.EnvironmentFaces(folder, shot);
                yield return null;
                yield return null;
            }
            Raytracing.RtCamera.SurfacesAsDeferred = false;
            Raytracing.RtCamera.SurfaceView = false;
            Debug.Log(Log.Tag + " Probe: the VAB's surfaces into " + folder + ".");
            yield return new WaitForSeconds(2f);
            Debug.Log(Log.Tag + " Probe: from the VAB back to the space centre.");
            HighLogic.LoadScene(GameScenes.SPACECENTER);
        }
    }

    // In the probe's flight: whichever measurement the switches ask for, by default the camera path's stages
    // around the vessel with and without it, for each variant; then KSP quits; -redefinitionProbeStay leaves the flight to the player.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal sealed partial class SceneProbeRunner : MonoBehaviour
    {
        // The proxy's CPU sampler (src/DxgiProxy/Latency/CpuSampler.h): the main thread sampled for seconds into path; 1
        // started, 0 not.
        [System.Runtime.InteropServices.DllImport(ReDefinition.Proxy.ProxyDll.Name, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl,
                                                   CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int KspCpuSample(int seconds, string path);

        // The crop around the vessel, in pixels, and the frames of a run: steady, then
        // the camera pitched up by a degree a frame, then steady again. Per frame, not
        // per second, so the path is the same at any frame rate.
        private const int Crop = 768;
        private const int SteadyFrames = 40;
        // Below the vessel's middle, looking up past it into the sky.
        private const float StartPitchDegrees = -15f;
        private const float SettleSeconds = 20f;

        // The flight's cameras whose foreign components and command buffers the variants switch off in turn.
        private static readonly string[] CameraNames = { "Camera ScaledSpace", "Camera 01", "Camera 00" };

        // A stage of the scene camera's rendering a capture crops.
        private struct Station
        {
            // Its name in the files (<capture>-<name>.bin); the camera event at whose end it is copied.
            internal string Name;
            internal CameraEvent Event;
        }

        // A crop waiting to be read back: the file it goes to and the texture it is in.
        private sealed class Read
        {
            // The file the crop's bytes are written to; the texture, released once read.
            internal string Path;
            internal RenderTexture Texture;
        }

        // The scene camera (Camera 00), the run's folder and meta.txt's lines. Then come the stations, the probe's
        // command buffer on each event, and the crops waiting to be read and 1 while one is read. Then come the
        // vessel's mask material and the full-size mask, and the variant running. Then come the capture's number
        // and file prefix ("h" without the vessel), the path's segment being captured, and the heading both passes
        // start from. The last field says whether a sweep captures in RenderDoc.
        private Camera scene;
        private string folder;
        private readonly StringBuilder meta = new StringBuilder();
        private readonly List<Station> stations = new List<Station>();
        private readonly Dictionary<CameraEvent, CommandBuffer> buffers = new Dictionary<CameraEvent, CommandBuffer>();
        private readonly Queue<Read> reads = new Queue<Read>();
        private int busy;
        private Material maskMaterial;
        private RenderTexture fullMask;
        private string variant;
        private int captureIndex;
        private string capturePrefix = "";
        private string phase = "";
        private float startHeading = float.NaN;
        private bool captureDuringSweep;
        // The sweep's step RenderDoc captures: where its turn reverses, of the
        // frames the halo shows in (-redefinitionProbeCaptureStep, default 21).
        private static int CaptureStep
        {
            get { return CommandLine.Int("-redefinitionProbeCaptureStep", 21); }
        }
        // The sweep in this many frames a step of the path (FlipProbe).
        private int sweepSubdivide = 1;

        // Runs only in the probe's flight, once it stands. It does the switches' single runs (each quits KSP), the
        // scene round trips, else the default measurement. That measurement is the baseline, each variant asked for,
        // and the baseline again. Then the reads are finished, meta.txt is written, and KSP quits.
        private IEnumerator Start()
        {
            // A new launch (StagingProbe) is the probe's flight whatever save folder KSP names then.
            bool probeFlight = HighLogic.SaveFolder == SceneProbe.SaveFolder || StagingProbe.Launched;
            if (SceneProbe.Requested && !probeFlight)
                Debug.Log(Log.Tag + " Probe: flight of the save " + HighLogic.SaveFolder + ", not the probe's; left alone.");
            if (!SceneProbe.Requested || !probeFlight) yield break;
            // The vessel Parsek put back is flown by the route probe, which is the measurement.
            if (ParsekLeave.Resumed) yield break;
            yield return WaitForFlight();
            Debug.Log(Log.Tag + " Probe: in flight with " + FlightGlobals.ActiveVessel.vesselName + ".");
            if (LandProbe.CraftToOrbit()) yield break;
            if (CommandLine.Has(SceneProbe.StayArgument))
            {
                yield return StayMode();
                yield break;
            }
            yield return new WaitForSeconds(SettleSeconds);

            if (SceneProbe.ScenesLeft >= 0)
            {
                yield return RoundTripMode();
                yield break;
            }

            if (!PrepareMeasurement())
            {
                Application.Quit();
                yield break;
            }

            if (RenderDocCapture.Available)
            {
                yield return RenderDocMode();
                yield break;
            }

            if (VendorProbe.Requested)
            {
                yield return VendorMode();
                yield break;
            }

            // -redefinitionProbeSoak <seconds>: the camera circles the vessel for that
            // long, for the proxy's and the game's own reports over time (memory,
            // frame times, errors), then KSP quits.
            int soakSeconds = CommandLine.Int("-redefinitionProbeSoak", 0);
            if (soakSeconds > 0)
            {
                yield return SoakMode(soakSeconds);
                yield break;
            }

            if (FlipProbe.Requested)
            {
                yield return FlipMode();
                yield break;
            }

            if (LightProbe.Requested)
            {
                Detach();
                yield return ProbeSteps.Safe(LightProbe.Run(folder), "light probe");
                yield return ProbeSteps.QuitAfter(1f);
                yield break;
            }

            yield return VariantsMode();
        }

        // Waits until the flight stands: ready, with a vessel and its camera, saying every 10 s what it still waits for.
        private static IEnumerator WaitForFlight()
        {
            float waitSaid = Time.realtimeSinceStartup;
            while (!FlightGlobals.ready || FlightGlobals.ActiveVessel == null || FlightCamera.fetch == null)
            {
                if (Time.realtimeSinceStartup - waitSaid > 10f)
                {
                    waitSaid = Time.realtimeSinceStartup;
                    Debug.Log(Log.Tag + " Probe: waiting for the flight: ready " + FlightGlobals.ready + ", vessel "
                              + (FlightGlobals.ActiveVessel != null) + ", camera " + (FlightCamera.fetch != null) + ".");
                }
                yield return null;
            }
        }

        // -redefinitionProbeStay: the flight left to the player, the clouds hidden. The switches for a single run
        // (the staging, the diagnostics window, RenderDoc, the CPU sample, a shot) then run it and quit KSP; with none
        // KSP is left running.
        private IEnumerator StayMode()
        {
            yield return new WaitForSeconds(5f);
            LightProbe.HideClouds();
            Debug.Log(Log.Tag + " Probe: the flight is the player's.");
            // -redefinitionProbeNoGloss: Parallax's materials without their shine for the whole flight.
            if (ViewProbe.NoGloss && ReDefinitionAddon.Instance != null)
                ReDefinitionAddon.Instance.StartCoroutine(ViewProbe.KeepGlossOff());
            if (CommandLine.Has(SceneProbe.GrassArgument))
            {
                // Where the grass scene ended up: the situation, the height above the ground
                // and the biome, to read against the grass the shot shows.
                Vessel vessel = FlightGlobals.ActiveVessel;
                Debug.Log(Log.Tag + " Probe: grass scene, " + vessel.vesselName + " " + vessel.situation + ", "
                          + vessel.heightFromTerrain.ToString("0.0", CultureInfo.InvariantCulture) + " m over the ground, "
                          + vessel.altitude.ToString("0.0", CultureInfo.InvariantCulture) + " m above the sea, biome "
                          + ScienceUtil.GetExperimentBiome(vessel.mainBody, vessel.latitude, vessel.longitude) + ".");
            }
            yield return ShotVariant.Apply();
            // -redefinitionProbeReentry: the pod held in the air of a re-entry, each variant measured (ReentryProbe), then KSP
            // quits. It comes before the other shots, since the pod is already falling.
            if (ReentryProbe.Requested)
            {
                yield return ReentryProbe.Run(CaptureFolder.Run, CommandLine.After("-redefinitionProbeShot") ?? "reentry");
                yield return ProbeSteps.QuitAfter(2f);
                yield break;
            }
            // -redefinitionProbeStaging <seconds>: the launch flown and a stage dropped, recorded (StagingProbe).
            if (StagingProbe.Requested)
            {
                string stagingShot = CommandLine.After("-redefinitionProbeShot") ?? "staging";
                yield return StagingProbe.Run(CaptureFolder.Run, stagingShot);
                Application.Quit();
                yield break;
            }
            // -redefinitionProbeDiagnostics "<name>@<commands>|<name>@<commands>": the diagnostics
            // window opened and set by each step's commands (ReDefinitionAddon.ProbeDiagnostics),
            // a shot of the whole frame after each as diagnostics-<name>.png, then KSP quits.
            string diagnosticsSteps = CommandLine.After("-redefinitionProbeDiagnostics");
            if (diagnosticsSteps != null)
            {
                yield return DiagnosticsShots(diagnosticsSteps);
                Application.Quit();
                yield break;
            }
            // -redefinitionProbeRenderDoc, under RenderDoc (tools/renderdoc_probe.ps1
            // -Stay): one frame captured 25 s on, then KSP quits.
            if (CommandLine.Has("-redefinitionProbeRenderDoc"))
            {
                yield return new WaitForSeconds(25f);
                RenderDocCapture.Capture("the flight left to run");
                yield return new WaitForSeconds(5f);
                Application.Quit();
                yield break;
            }
            if (CommandLine.Has("-redefinitionProbeCpuSample"))
            {
                yield return CpuSampleMode();
                yield break;
            }
            // -redefinitionProbeShot <name>: the frame as KSP draws it, 30 s on, into
            // the session's capture folder (CaptureFolder.Run) as <name>.png, then KSP quits.
            string shot = CommandLine.After("-redefinitionProbeShot");
            if (shot == null) yield break;
            yield return ShotMode(shot);
        }

        // -redefinitionProbeCpuSample: the main thread sampled for 30 s from 20 s on
        // (src/DxgiProxy/Latency/CpuSampler.h) into the session's capture folder, cpu-samples.txt,
        // then KSP quits; tools/cpu_samples.py reads it.
        private static IEnumerator CpuSampleMode()
        {
            yield return new WaitForSeconds(20f);
            string samplesFolder = CaptureFolder.Run;
            int started = 0;
            try
            {
                started = KspCpuSample(30, Path.GetFullPath(Path.Combine(samplesFolder, "cpu-samples.txt")));
            }
            catch (Exception e)
            {
                Debug.Log(Log.Tag + " Probe: no CPU sampler (" + e.GetType().Name + ").");
            }
            Debug.Log(Log.Tag + " Probe: CPU sampler " + (started != 0 ? "started" : "not started") + ".");
            yield return ProbeSteps.QuitAfter(33f);
        }

        // The shot of the stay mode, named shot, and what the switches add to it. That is the surfaces, the renderers'
        // materials, the stages at a renderer, the light buffer, the ini settings, the settings window, the views
        // and a burst of frames. KSP quits after them.
        private IEnumerator ShotMode(string shot)
        {
            yield return new WaitForSeconds(30f);
            yield return LightValues();
            string folder = CaptureFolder.Run;
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, shot + ".png"));
            EnvironmentFaces(folder, shot);
            // -redefinitionProbeSurfaces: the raytraced camera's surfaces of the frame
            // after, as <shot>-<name>.bin (RtCamera.WriteSurfaceImages).
            if (CommandLine.Has("-redefinitionProbeSurfaces"))
            {
                yield return new WaitForEndOfFrame();
                Raytracing.RtCamera traced = Raytracing.RtCamera.Current;
                Debug.Log(Log.Tag + " Probe: surfaces " + (traced != null ? traced.WriteSurfaceImages(folder, shot) : "none, no raytraced camera")
                          + ".");
            }
            Debug.Log(Log.Tag + " Probe: shot " + shot + ".");
            LogMaterials(CommandLine.After("-redefinitionProbeMaterials"));
            // -redefinitionProbeStagesAt <renderer name>[@<parent's name>]: the scene camera's image after each
            // of its stages at the middle of the nearest renderer so named, linear, a 5 by 5
            // pixel mean each: in which stage it takes its brightness.
            string stagesAt = CommandLine.After("-redefinitionProbeStagesAt");
            if (stagesAt != null)
                yield return Stages(stagesAt);
            // -redefinitionProbeLightBuffer: the scene camera's light buffer, whole, after the
            // G-buffer and before the lights, as <shot>-afterGBuffer.bin and
            // <shot>-beforeLighting.bin (the surfaces' format): what each surface brings
            // into the lighting of its own.
            if (CommandLine.Has("-redefinitionProbeLightBuffer"))
                yield return LightBuffer(folder, shot);
            // -redefinitionProbeShotIni "key=value,key=value;key=value": then each set of the
            // proxy's live settings in turn written into ReDefinitionProxy.ini, which the proxy
            // re-reads within a second, and the same view 10 s later as <shot>-ini<n>.png;
            // the values as they were written back at the end.
            string iniSets = CommandLine.After("-redefinitionProbeShotIni");
            if (iniSets != null)
                yield return IniShots(folder, shot, iniSets);
            // -redefinitionProbeWindow "step;...": the settings window used as the player does (WindowProbe).
            string windowSteps = CommandLine.After("-redefinitionProbeWindow");
            if (windowSteps != null)
                yield return WindowProbe.Run(folder, shot, windowSteps);
            // -redefinitionProbeViews "pitch,distance,heading;...": shots from several poses, in each
            // raytracing mode asked for (ViewProbe).
            string views = CommandLine.After(ViewProbe.ViewsArgument);
            if (views != null)
                yield return ViewProbe.Run(folder, shot, views);
            // -redefinitionProbeMoonSky <moon>: the camera at that moon, shot with the brightening off and on (MoonSkyProbe).
            if (CommandLine.After(MoonSkyProbe.Argument) != null)
                yield return MoonSkyProbe.Run(folder, shot);
            // -redefinitionProbeMoonGround <moon>: that moon's phases in the night sky from the pod's ground (MoonGroundProbe).
            if (CommandLine.After(MoonGroundProbe.Argument) != null)
                yield return MoonGroundProbe.Run(folder, shot);

            // -redefinitionProbeBurst <n>: n frames more, one after the other, as
            // <shot>-burst<i>.png, for what changes from frame to frame (flicker,
            // ghosting).
            string burst = CommandLine.After("-redefinitionProbeBurst");
            int burstFrames;
            if (burst != null && int.TryParse(burst, out burstFrames))
                for (int i = 0; i < burstFrames; i++)
                {
                    yield return null;
                    ScreenCapture.CaptureScreenshot(Path.Combine(folder, shot + "-burst" + i.ToString("00") + ".png"));
                }
            yield return ProbeSteps.QuitAfter(2f);
        }

        // -redefinitionProbeMaterials <part of a name>: the renderers so named, with
        // their materials' shaders, queues and keywords.
        private static void LogMaterials(string materialsOf)
        {
            if (materialsOf == null) return;
            foreach (Renderer renderer in FindObjectsOfType<Renderer>())
            {
                Transform parent = renderer.transform.parent;
                if (renderer.name.IndexOf(materialsOf, StringComparison.OrdinalIgnoreCase) < 0
                    && (parent == null || parent.name.IndexOf(materialsOf, StringComparison.OrdinalIgnoreCase) < 0))
                    continue;
                StringBuilder line = new StringBuilder(Log.Tag + " Probe: renderer " + renderer.name + " under "
                                                       + (renderer.transform.parent != null ? renderer.transform.parent.name : "nothing")
                                                       + ", layer " + renderer.gameObject.layer + ", enabled " + renderer.enabled + ", "
                                                       + renderer.GetType().Name + ", mesh filter "
                                                       + (renderer.GetComponent<MeshFilter>() != null) + ":");
                foreach (Material material in renderer.sharedMaterials)
                    if (material != null)
                        line.Append(' ').Append(material.name).Append(" (").Append(material.shader.name).Append(", queue ")
                            .Append(material.renderQueue).Append(", keywords ").Append(string.Join(" ", material.shaderKeywords))
                            .Append(material.HasProperty("_Color") ? ", _Color " + material.GetColor("_Color") : "")
                            .Append(material.HasProperty("_EmissiveColor") ? ", _EmissiveColor " + material.GetColor("_EmissiveColor") : "")
                            .Append(material.HasProperty("_EmissionColor") ? ", _EmissionColor " + material.GetColor("_EmissionColor") : "")
                            .Append(')');
                Debug.Log(line.ToString());
            }
        }

        // Round trip to the space centre and back (-redefinitionProbeScenes): the map view, then the space centre; the
        // round trips are counted down, and at none left KSP quits.
        private IEnumerator RoundTripMode()
        {
            if (SceneProbe.ScenesLeft == 0)
            {
                Debug.Log(Log.Tag + " Probe: scene round trips done; quitting.");
                yield return ProbeSteps.QuitAfter(1f);
                yield break;
            }
            SceneProbe.ScenesLeft--;
            Debug.Log(Log.Tag + " Probe: map view.");
            MapView.EnterMapView();
            yield return new WaitForSeconds(8f);
            MapView.ExitMapView();
            yield return new WaitForSeconds(3f);
            Debug.Log(Log.Tag + " Probe: to the space centre, " + SceneProbe.ScenesLeft + " round trip(s) after this.");
            HighLogic.LoadScene(GameScenes.SPACECENTER);
        }

        // The measurement's set-up: the scene camera, the run's folder and meta.txt, the stations and the vessel's mask
        // material, the flight camera's sharpness, the interface hidden and the frame rate capped where asked. False,
        // after a warning, where there is no scene camera.
        private bool PrepareMeasurement()
        {
            scene = ProbeSteps.SceneCamera();
            if (scene == null)
            {
                Debug.LogWarning(Log.Tag + " Probe: no Camera 00; quitting.");
                return false;
            }
            folder = Path.Combine(CaptureFolder.Root,
                                  DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-probe");
            Directory.CreateDirectory(folder);
            scene.depthTextureMode |= DepthTextureMode.Depth;
            bool deferred = scene.actualRenderingPath == RenderingPath.DeferredShading;
            // AfterEverything is not the scene camera's image (measured: the vessel is
            // missing there), and before its G-buffer the target is still empty.
            stations.Add(new Station { Name = "opaque", Event = deferred ? CameraEvent.AfterLighting : CameraEvent.AfterForwardOpaque });
            stations.Add(new Station { Name = "alpha", Event = CameraEvent.AfterForwardAlpha });
            stations.Add(new Station { Name = "effects", Event = CameraEvent.BeforeImageEffects });
            stations.Add(new Station { Name = "after", Event = CameraEvent.AfterImageEffects });
            Shader colored = Shader.Find("Hidden/Internal-Colored");
            maskMaterial = new Material(colored);
            maskMaterial.SetColor("_Color", Color.white);
            maskMaterial.SetInt("_ZTest", (int)CompareFunction.Always);
            maskMaterial.SetInt("_Cull", (int)CullMode.Off);
            maskMaterial.SetInt("_ZWrite", 0);

            meta.Append("# ").Append(DateTime.Now.ToString("u", CultureInfo.InvariantCulture)).Append(", ")
                .Append(scene.pixelWidth).Append('x').Append(scene.pixelHeight).Append(", ")
                .Append(scene.actualRenderingPath).Append(", crop ").Append(Crop).Append('\n');
            meta.Append("# variant frame capture cropX cropY phase; files <variant>/<capture>-<station>.bin (ARGBHalf; ")
                .Append("screen ARGB32, the finished frame), -mask.bin (R8), -depth.bin (RFloat)\n");
            Debug.Log(Log.Tag + " Probe: measuring into " + folder + ".");
            // The flight camera eases towards camHdg and camPitch; without the easing
            // a pose depends on nothing but the step, whatever came before it.
            FlightCamera.fetch.sharpness = 1e4f;
            FlightCamera.fetch.orientationSharpness = 1e4f;
            // -redefinitionProbeNoGloss: Parallax's materials without their shine for the whole flight.
            if (ViewProbe.NoGloss && ReDefinitionAddon.Instance != null)
                ReDefinitionAddon.Instance.StartCoroutine(ViewProbe.KeepGlossOff());
            LogMotionModes(FlightGlobals.ActiveVessel);
            // The finished frame without KSP's interface over it; -redefinitionProbeShowHud keeps
            // the interface, for what frame generation does to it (tools/hud_probe.py).
            if (!CommandLine.Has("-redefinitionProbeShowHud"))
                GameEvents.onHideUI.Fire();
            // -redefinitionProbeFpsCap <n>: n rendered frames a second at most, so that frame
            // generation's frames all fit the display's refresh rate and the desktop recording sees them.
            int fpsCap = CommandLine.Int("-redefinitionProbeFpsCap", 0);
            if (fpsCap > 0)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = fpsCap;
                Debug.Log(Log.Tag + " Probe: rendered frames capped at " + fpsCap + " a second.");
            }
            return true;
        }

        // Under RenderDoc: a frame of the camera still and a sweep of it with the vessel, then without it; KSP quits.
        private IEnumerator RenderDocMode()
        {
            startHeading = FlightCamera.fetch.camHdg;
            FlightCamera.fetch.camPitch = StartPitchDegrees * Mathf.Deg2Rad;
            yield return ProbeSteps.Frames(60);
            RenderDocCapture.Capture("camera still");
            yield return ProbeSteps.Frames(30);
            captureDuringSweep = true;
            yield return Sweep("renderdoc");
            // The same pose without the vessel: what the vessel changes around it.
            List<Renderer> hidden = HideVessel();
            FlightCamera.fetch.camHdg = startHeading;
            FlightCamera.fetch.camPitch = StartPitchDegrees * Mathf.Deg2Rad;
            yield return ProbeSteps.Frames(60);
            RenderDocCapture.Capture("camera still, without the vessel");
            yield return ProbeSteps.Frames(30);
            yield return Sweep("renderdoc without the vessel");
            captureDuringSweep = false;
            foreach (Renderer r in hidden)
                if (r != null) r.enabled = true;
            yield return ProbeSteps.Frames(60);
            Detach();
            Debug.Log(Log.Tag + " Probe: RenderDoc captures done; quitting.");
            yield return ProbeSteps.QuitAfter(3f);
        }

        // The vendors' checks (VendorProbe), each recorded during a sweep; KSP quits.
        private IEnumerator VendorMode()
        {
            startHeading = FlightCamera.fetch.camHdg;
            yield return VendorProbe.Run(Sweep);
            Detach();
            File.WriteAllText(Path.Combine(folder, "meta.txt"), meta.ToString());
            Debug.Log(Log.Tag + " Probe: vendor checks done; quitting.");
            yield return ProbeSteps.QuitAfter(1f);
        }

        // The camera circles the vessel for the seconds given, for the proxy's and the game's own reports over time.
        private IEnumerator SoakMode(int soakSeconds)
        {
            Detach();
            float until = Time.realtimeSinceStartup + soakSeconds;
            float hdg = FlightCamera.fetch.camHdg;
            int frame = 0;
            Debug.Log(Log.Tag + " Probe: soak for " + soakSeconds + " s.");
            while (Time.realtimeSinceStartup < until)
            {
                hdg += 1f * Mathf.Deg2Rad;
                FlightCamera.fetch.camHdg = hdg;
                FlightCamera.fetch.camPitch = (StartPitchDegrees + 10f * Mathf.Sin(frame++ * 0.01f)) * Mathf.Deg2Rad;
                yield return null;
            }
            Debug.Log(Log.Tag + " Probe: soak done after " + frame + " frames; quitting.");
            yield return ProbeSteps.QuitAfter(1f);
        }

        // The sweep recorded for FLIP (FlipProbe); KSP quits.
        private IEnumerator FlipMode()
        {
            startHeading = FlightCamera.fetch.camHdg;
            sweepSubdivide = FlipProbe.Subdivide;
            FlightCamera.fetch.camPitch = StartPitchDegrees * Mathf.Deg2Rad;
            yield return ProbeSteps.Frames(60);
            bool started = ScreenRecordingBridge.Start(FlipProbe.Frames, FlipProbe.Dumps);
            Debug.Log(Log.Tag + " Probe: flip sweep, " + (started ? "recording" : "not recorded: "
                      + ScreenRecordingBridge.State()) + "; step 1/" + sweepSubdivide
                      + ", first step at game frame " + (Time.frameCount + 1) + ".");
            yield return Sweep("flip");
            yield return ProbeSteps.RecordingSaved(30f);
            Debug.Log(Log.Tag + " Probe: flip sweep: " + ScreenRecordingBridge.State());
            Detach();
            yield return ProbeSteps.QuitAfter(1f);
        }

        // The default measurement: the baseline, each variant asked for (-redefinitionProbeVariants), the baseline again,
        // then the reads are finished and meta.txt is written; KSP quits.
        private IEnumerator VariantsMode()
        {
            yield return Run("baseline");
            string wanted = SceneProbe.Variants;
            foreach (string cameraName in wanted == null ? new string[0] : CameraNames)
            {
                Camera camera = Find(cameraName);
                if (camera == null) continue;
                foreach (Behaviour behaviour in camera.GetComponents<Behaviour>())
                {
                    if (behaviour == null || !behaviour.enabled || !Foreign(behaviour.GetType())) continue;
                    string name = "off-" + Safe(cameraName) + "-" + Safe(behaviour.GetType().Name);
                    if (!Wanted(wanted, name)) continue;
                    behaviour.enabled = false;
                    yield return Run(name);
                    behaviour.enabled = true;
                }
                foreach (CameraEvent e in Enum.GetValues(typeof(CameraEvent)))
                {
                    foreach (CommandBuffer cb in camera.GetCommandBuffers(e))
                    {
                        if (cb.name != null && cb.name.StartsWith("ReDefinition", StringComparison.Ordinal)) continue;
                        if (camera == scene && buffers.ContainsValue(cb)) continue;
                        string name = "off-" + Safe(cameraName) + "-" + e + "-" + Safe(cb.name);
                        if (!Wanted(wanted, name)) continue;
                        camera.RemoveCommandBuffer(e, cb);
                        yield return Run(name);
                        camera.AddCommandBuffer(e, cb);
                    }
                }
            }
            yield return Run("baseline-again");

            Detach();
            while (reads.Count > 0 || Volatile.Read(ref busy) != 0)
            {
                ReadNext();
                yield return null;
            }
            File.WriteAllText(Path.Combine(folder, "meta.txt"), meta.ToString());
            Debug.Log(Log.Tag + " Probe: done; quitting.");
            yield return ProbeSteps.QuitAfter(1f);
        }

        // Whether the variant is asked for: "all", or its name holds the text asked for, ignoring case.
        private static bool Wanted(string wanted, string name)
        {
            return string.Equals(wanted, "all", StringComparison.OrdinalIgnoreCase)
                   || name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // The enabled camera so named; null where there is none.
        private static Camera Find(string name)
        {
            foreach (Camera c in Camera.allCameras)
                if (c.name == name) return c;
            return null;
        }

        // Whether a component is another mod's: not Unity's, KSP's or ReDefinition's own.
        private static bool Foreign(Type type)
        {
            string assembly = type.Assembly.GetName().Name;
            return !assembly.StartsWith("UnityEngine", StringComparison.Ordinal) && assembly != "Assembly-CSharp"
                   && assembly != "Assembly-CSharp-firstpass" && assembly != "ReDefinition";
        }

        // A name for a folder: every character but letters and digits an underscore; "unnamed" for none.
        private static string Safe(string text)
        {
            if (string.IsNullOrEmpty(text)) return "unnamed";
            StringBuilder safe = new StringBuilder(text.Length);
            foreach (char c in text) safe.Append(char.IsLetterOrDigit(c) ? c : '_');
            return safe.ToString();
        }

        // Unity draws a ForceNoMotion renderer into the motion vectors with zero
        // motion; the vessel's renderers by mode, to read next to a capture.
        private static void LogMotionModes(Vessel vessel)
        {
            int objectMode = 0, cameraMode = 0, noMotion = 0;
            var names = new StringBuilder();
            // In flight the parts are not children of the vessel's transform.
            foreach (Part part in vessel.parts)
            foreach (Renderer renderer in part.GetComponentsInChildren<Renderer>(true))
            {
                switch (renderer.motionVectorGenerationMode)
                {
                    case MotionVectorGenerationMode.Object: objectMode++; break;
                    case MotionVectorGenerationMode.Camera: cameraMode++; break;
                    default:
                        if (noMotion++ < 5) names.Append(noMotion > 1 ? ", '" : " ('").Append(renderer.name).Append('\'');
                        break;
                }
            }
            if (noMotion > 0) names.Append(noMotion > 5 ? ", ...)" : ")");
            Debug.Log(Log.Tag + " Probe: vessel renderers by motion vector mode: Object " + objectMode
                      + " / Camera " + cameraMode + " / ForceNoMotion " + noMotion + names + ".");
            Shader custom = GraphicsSettings.GetCustomShader(BuiltinShaderType.MotionVectors);
            Debug.Log(Log.Tag + " Probe: motion vector shader mode "
                      + GraphicsSettings.GetShaderMode(BuiltinShaderType.MotionVectors)
                      + ", custom shader " + (custom != null ? custom.name : "none") + ".");
        }
    }
}
