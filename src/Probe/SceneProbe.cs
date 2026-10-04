using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using ReDefinition.Bridges;
using ReDefinition.Core;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Probe
{
    // A measurement the game runs on its own, started with -redefinitionProbe on KSP's
    // command line: KSP loads the save ReDefinitionProbe, goes to the flight of the
    // vessel waiting on the launch pad, turns the flight camera on a fixed path from
    // the vessel up into the sky, and writes what the scene camera holds at each stage
    // of its rendering, around the vessel, into ReDefinitionCaptures\<time>-probe --
    // each run twice, with the vessel and with its renderers hidden, so that the
    // difference is what the vessel does to the sky around it, whatever the clouds;
    // then the same with each other mod's component and command buffer on the flight's
    // cameras switched off in turn, and quits. tools/analyze_probe.py measures the
    // halo and trail around the vessel in each.
    //
    // Nothing of it runs without the argument.
    internal static class SceneProbe
    {
        internal const string Argument = "-redefinitionProbe";
        internal const string SaveFolder = "ReDefinitionProbe";
        internal static bool Launched;
        // -redefinitionProbeSea: a capsule afloat out at sea, no land within kilometres.
        internal const string CoastArgument = "-redefinitionProbeCoast";
        // -redefinitionProbeStay: the flight left to the player, the clouds hidden, no
        // measurement, KSP left running (with -redefinitionProbeSea: a capsule at sea to
        // look at the water's reflections oneself).
        internal const string StayArgument = "-redefinitionProbeStay";
        internal const string SeaArgument = "-redefinitionProbeSea";

        // -redefinitionProbeScenes <n>: n round trips from the flight to the space
        // centre and back, each flight settled, for what scene changes leave behind
        // in the logs (memory, devices, the upscaler's and raytracing's setup). -1
        // where not asked for.
        internal static int ScenesLeft = SceneCount();
        // On each round trip the space centre first opens the VAB, then the flight.
        internal static bool EditorVisited;

        private static int SceneCount()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], "-redefinitionProbeScenes", StringComparison.OrdinalIgnoreCase))
                    return int.Parse(args[i + 1], CultureInfo.InvariantCulture);
            return -1;
        }

        // The variants to run, -redefinitionProbeVariants <part of their name, or all>:
        // none without it. The baseline first says at which stage the halo enters.
        internal static string Variants
        {
            get
            {
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                    if (string.Equals(args[i], "-redefinitionProbeVariants", StringComparison.OrdinalIgnoreCase))
                        return args[i + 1];
                return null;
            }
        }

        internal static bool Requested
        {
            get
            {
                foreach (string arg in Environment.GetCommandLineArgs())
                    if (string.Equals(arg, Argument, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
        }
    }

    // In the main menu: the save's flight, once.
    [KSPAddon(KSPAddon.Startup.MainMenu, false)]
    internal sealed class SceneProbeLauncher : MonoBehaviour
    {
        private IEnumerator Start()
        {
            if (SceneProbe.Launched || !SceneProbe.Requested) yield break;
            SceneProbe.Launched = true;
            yield return new WaitForSeconds(3f);
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
            int index = game.flightState.protoVessels.FindIndex(v => v.situation == Vessel.Situations.PRELAUNCH);
            if (index < 0)
            {
                Debug.LogWarning(Log.Tag + " Probe: no vessel on the launch pad in " + SceneProbe.SaveFolder + "; quitting.");
                Application.Quit();
                yield break;
            }
            if (Array.IndexOf(Environment.GetCommandLineArgs(), SceneProbe.SeaArgument) >= 0)
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
            HighLogic.SaveFolder = SceneProbe.SaveFolder;
            HighLogic.CurrentGame = game;
            Debug.Log(Log.Tag + " Probe: flight of " + game.flightState.protoVessels[index].vesselName + ".");
            FlightDriver.StartAndFocusVessel(game, index);
        }

        // A Mk1 command pod afloat out at sea east of the space centre, no land within
        // kilometres: the first of a few places where the sea floor lies deep there and
        // round it. Made as KSP makes a vessel (ProtoVessel.CreateVesselNode), splashed at
        // sea level and not set down on the ground, which there is the sea floor.
        private static int SeaCapsule(Game game)
        {
            CelestialBody home = FlightGlobals.GetHomeBody();
            if (home == null || home.pqsController == null) return -1;
            Func<double, double, double> ground = (lat, lon) =>
                home.pqsController.GetSurfaceHeight(home.GetRelSurfaceNVector(lat, lon)) - home.Radius;
            // -redefinitionProbeCoast: 2.6 km off the island airfield's east shore, the
            // island and its sea floor in view; otherwise far out at sea.
            bool coast = Array.IndexOf(Environment.GetCommandLineArgs(), SceneProbe.CoastArgument) >= 0;
            double[][] places = coast
                ? new[] { new[] { -1.54, -71.66 }, new[] { -1.54, -71.62 } }
                : new[] { new[] { -1.0, -66.0 }, new[] { 0.0, -62.0 }, new[] { -4.0, -64.0 }, new[] { 3.0, -60.0 } };
            foreach (double[] place in places)
            {
                bool deep = true;
                for (int i = -2; i <= 2 && deep; i++)
                    for (int j = -2; j <= 2 && deep; j++)
                        deep = ground(place[0] + i * (coast ? 0.01 : 0.2), place[1] + j * (coast ? 0.01 : 0.2)) < (coast ? -10.0 : -100.0);
                if (!deep) continue;
                ConfigNode part = ProtoVessel.CreatePartNode("mk1pod.v2", ShipConstruction.GetUniqueFlightID(game.flightState));
                var orbit = new Orbit(0.0, 0.0, home.Radius + 100000.0, 0.0, 0.0, 0.0, Planetarium.GetUniversalTime(), home);
                ConfigNode node = ProtoVessel.CreateVesselNode("ReDefinition sea probe", VesselType.Ship, orbit, 0, new[] { part });
                node.SetValue("sit", Vessel.Situations.SPLASHED.ToString());
                node.SetValue("landed", "False");
                node.SetValue("splashed", "True");
                node.SetValue("skipGroundPositioning", "True");
                node.SetValue("lat", place[0].ToString(CultureInfo.InvariantCulture));
                node.SetValue("lon", place[1].ToString(CultureInfo.InvariantCulture));
                node.SetValue("alt", "0");
                Quaternion upright = Quaternion.FromToRotation(Vector3.up, (Vector3)home.GetRelSurfaceNVector(place[0], place[1]));
                node.SetValue("rot", KSPUtil.WriteQuaternion(upright));
                game.flightState.protoVessels.Add(new ProtoVessel(node, game));
                Debug.Log(Log.Tag + " Probe: a capsule afloat at latitude " + place[0].ToString(CultureInfo.InvariantCulture)
                          + ", longitude " + place[1].ToString(CultureInfo.InvariantCulture) + ", the sea floor "
                          + ground(place[0], place[1]).ToString("0", CultureInfo.InvariantCulture) + " m.");
                return game.flightState.protoVessels.Count - 1;
            }
            Debug.LogWarning(Log.Tag + " Probe: no open sea found at any of the places tried.");
            return -1;
        }
    }

    // At the space centre during the scene round trips: back to the vessel's flight.
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    internal sealed class SceneProbeReturn : MonoBehaviour
    {
        private IEnumerator Start()
        {
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

    // In the VAB during the scene round trips: back to the space centre.
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    internal sealed class SceneProbeEditor : MonoBehaviour
    {
        private IEnumerator Start()
        {
            if (!SceneProbe.Requested || SceneProbe.ScenesLeft < 0 || HighLogic.SaveFolder != SceneProbe.SaveFolder)
                yield break;
            yield return new WaitForSeconds(15f);
            Debug.Log(Log.Tag + " Probe: from the VAB back to the space centre.");
            HighLogic.LoadScene(GameScenes.SPACECENTER);
        }
    }

    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal sealed class SceneProbeRunner : MonoBehaviour
    {
        // The crop around the vessel, in pixels, and the frames of a run: steady, then
        // the camera pitched up by a degree a frame, then steady again. Per frame, not
        // per second, so the path is the same at any frame rate.
        private const int Crop = 768;
        private const int SteadyFrames = 40;
        // Below the vessel's middle, looking up past it into the sky.
        private const float StartPitchDegrees = -15f;
        private const float SettleSeconds = 20f;

        private static readonly string[] CameraNames = { "Camera ScaledSpace", "Camera 01", "Camera 00" };

        private struct Station
        {
            internal string Name;
            internal CameraEvent Event;
        }

        private sealed class Read
        {
            internal string Path;
            internal RenderTexture Texture;
        }

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
            get
            {
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i + 1 < args.Length; i++)
                    if (args[i] == "-redefinitionProbeCaptureStep") return int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                return 21;
            }
        }
        // The sweep in this many frames a step of the path (FlipProbe).
        private int sweepSubdivide = 1;

        private IEnumerator Start()
        {
            if (!SceneProbe.Requested || HighLogic.SaveFolder != SceneProbe.SaveFolder) yield break;
            while (!FlightGlobals.ready || FlightGlobals.ActiveVessel == null || FlightCamera.fetch == null)
                yield return null;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), SceneProbe.StayArgument) >= 0)
            {
                yield return new WaitForSeconds(5f);
                LightProbe.HideClouds();
                Debug.Log(Log.Tag + " Probe: the flight is the player's.");
                yield break;
            }
            yield return new WaitForSeconds(SettleSeconds);

            if (SceneProbe.ScenesLeft >= 0)
            {
                if (SceneProbe.ScenesLeft == 0)
                {
                    Debug.Log(Log.Tag + " Probe: scene round trips done; quitting.");
                    yield return new WaitForSeconds(1f);
                    Application.Quit();
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
                yield break;
            }

            foreach (Camera c in Camera.allCameras)
                if (c.name == "Camera 00") scene = c;
            if (scene == null)
            {
                Debug.LogWarning(Log.Tag + " Probe: no Camera 00; quitting.");
                Application.Quit();
                yield break;
            }
            folder = Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "ReDefinitionCaptures"),
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
            LogMotionModes(FlightGlobals.ActiveVessel);
            // The finished frame without KSP's interface over it.
            GameEvents.onHideUI.Fire();

            if (RenderDocCapture.Available)
            {
                startHeading = FlightCamera.fetch.camHdg;
                FlightCamera.fetch.camPitch = StartPitchDegrees * Mathf.Deg2Rad;
                for (int i = 0; i < 60; i++) yield return null;
                RenderDocCapture.Capture("camera still");
                for (int i = 0; i < 30; i++) yield return null;
                captureDuringSweep = true;
                yield return Sweep("renderdoc");
                // The same pose without the vessel: what the vessel changes around it.
                List<Renderer> hidden = HideVessel();
                FlightCamera.fetch.camHdg = startHeading;
                FlightCamera.fetch.camPitch = StartPitchDegrees * Mathf.Deg2Rad;
                for (int i = 0; i < 60; i++) yield return null;
                RenderDocCapture.Capture("camera still, without the vessel");
                for (int i = 0; i < 30; i++) yield return null;
                yield return Sweep("renderdoc without the vessel");
                captureDuringSweep = false;
                foreach (Renderer r in hidden)
                    if (r != null) r.enabled = true;
                for (int i = 0; i < 60; i++) yield return null;
                Detach();
                Debug.Log(Log.Tag + " Probe: RenderDoc captures done; quitting.");
                yield return new WaitForSeconds(3f);
                Application.Quit();
                yield break;
            }

            if (VendorProbe.Requested)
            {
                startHeading = FlightCamera.fetch.camHdg;
                yield return VendorProbe.Run(Sweep);
                Detach();
                File.WriteAllText(Path.Combine(folder, "meta.txt"), meta.ToString());
                Debug.Log(Log.Tag + " Probe: vendor checks done; quitting.");
                yield return new WaitForSeconds(1f);
                Application.Quit();
                yield break;
            }

            // -redefinitionProbeMapToggles <n>: into the map view and out again n
            // times, four seconds each (-redefinitionProbeMapTenths), then KSP quits -- each switch turns the
            // upscaler, Ray Reconstruction and frame generation off and on.
            int toggles = IntArgument("-redefinitionProbeMapToggles");
            // -redefinitionProbeMapTenths <n>: each phase n tenths of a second, 40 if not given.
            int tenths = IntArgument("-redefinitionProbeMapTenths");
            float mapSeconds = (tenths > 0 ? tenths : 40) / 10f;
            if (toggles > 0)
            {
                Detach();
                for (int i = 0; i < toggles; i++)
                {
                    Debug.Log(Log.Tag + " Probe: map view " + (i + 1) + " of " + toggles + ".");
                    MapView.EnterMapView();
                    yield return new WaitForSeconds(mapSeconds);
                    MapView.ExitMapView();
                    yield return new WaitForSeconds(mapSeconds);
                }
                Debug.Log(Log.Tag + " Probe: map view toggles done; quitting.");
                yield return new WaitForSeconds(1f);
                Application.Quit();
                yield break;
            }

            // -redefinitionProbeSoak <seconds>: the camera circles the vessel for that
            // long, for the proxy's and the game's own reports over time (memory,
            // frame times, errors), then KSP quits.
            int soakSeconds = SoakSeconds;
            if (soakSeconds > 0)
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
                yield return new WaitForSeconds(1f);
                Application.Quit();
                yield break;
            }

            if (FlipProbe.Requested)
            {
                startHeading = FlightCamera.fetch.camHdg;
                sweepSubdivide = FlipProbe.Subdivide;
                FlightCamera.fetch.camPitch = StartPitchDegrees * Mathf.Deg2Rad;
                for (int i = 0; i < 60; i++) yield return null;
                bool started = ScreenRecordingBridge.Start(FlipProbe.Frames, FlipProbe.Dumps);
                Debug.Log(Log.Tag + " Probe: flip sweep, " + (started ? "recording" : "not recorded: "
                          + ScreenRecordingBridge.State()) + "; step 1/" + sweepSubdivide
                          + ", first step at game frame " + (Time.frameCount + 1) + ".");
                yield return Sweep("flip");
                float until = Time.realtimeSinceStartup + 30f;
                while (Time.realtimeSinceStartup < until)
                {
                    string state = ScreenRecordingBridge.State();
                    if (!state.StartsWith("recording", StringComparison.Ordinal)
                        && !state.StartsWith("saving", StringComparison.Ordinal)) break;
                    yield return null;
                }
                Debug.Log(Log.Tag + " Probe: flip sweep: " + ScreenRecordingBridge.State());
                Detach();
                yield return new WaitForSeconds(1f);
                Application.Quit();
                yield break;
            }

            if (LightProbe.Requested)
            {
                Detach();
                yield return LightProbe.Run(folder);
                yield return new WaitForSeconds(1f);
                Application.Quit();
                yield break;
            }

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
            yield return new WaitForSeconds(1f);
            Application.Quit();
        }

        private static bool Wanted(string wanted, string name)
        {
            return string.Equals(wanted, "all", StringComparison.OrdinalIgnoreCase)
                   || name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Camera Find(string name)
        {
            foreach (Camera c in Camera.allCameras)
                if (c.name == name) return c;
            return null;
        }

        private static bool Foreign(Type type)
        {
            string assembly = type.Assembly.GetName().Name;
            return !assembly.StartsWith("UnityEngine", StringComparison.Ordinal) && assembly != "Assembly-CSharp"
                   && assembly != "Assembly-CSharp-firstpass" && assembly != "ReDefinition";
        }

        private static string Safe(string text)
        {
            if (string.IsNullOrEmpty(text)) return "unnamed";
            StringBuilder safe = new StringBuilder(text.Length);
            foreach (char c in text) safe.Append(char.IsLetterOrDigit(c) ? c : '_');
            return safe.ToString();
        }

        // One variant: the camera path with the vessel, then without.
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

        private IEnumerator Run(string name)
        {
            variant = name;
            Directory.CreateDirectory(Path.Combine(folder, name));
            capturePrefix = "";
            // Both passes from the same heading: the sky behind the vessel the same.
            if (float.IsNaN(startHeading)) startHeading = FlightCamera.fetch.camHdg;
            // -redefinitionProbeFreeze: game time stopped for both passes, so that
            // clouds and water stand still between them and only the vessel differs.
            float timeScale = Time.timeScale;
            if (Freeze) Time.timeScale = 0f;
            yield return Turn(name);
            List<Renderer> hidden = HideVessel();
            capturePrefix = "h";
            yield return Turn(name + " without the vessel");
            foreach (Renderer r in hidden)
                if (r != null) r.enabled = true;
            Time.timeScale = timeScale;
        }

        private static int IntArgument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == name) return int.Parse(args[i + 1], CultureInfo.InvariantCulture);
            return 0;
        }

        private static int SoakSeconds
        {
            get
            {
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i + 1 < args.Length; i++)
                    if (args[i] == "-redefinitionProbeSoak") return int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                return 0;
            }
        }

        private static bool Freeze
        {
            get { return Array.IndexOf(Environment.GetCommandLineArgs(), "-redefinitionProbeFreeze") >= 0; }
        }

        private static List<Renderer> HideVessel()
        {
            List<Renderer> hidden = new List<Renderer>();
            foreach (Part part in FlightGlobals.ActiveVessel.parts)
                foreach (Renderer r in part.GetComponentsInChildren<Renderer>())
                    if (r.enabled)
                    {
                        r.enabled = false;
                        hidden.Add(r);
                    }
            return hidden;
        }

        // For the vendors' checks: from below the vessel, the sky moving past it left
        // and right, without captures of its own.
        private IEnumerator Sweep(string name)
        {
            FlightCamera camera = FlightCamera.fetch;
            float hdg = startHeading;
            camera.camPitch = StartPitchDegrees * Mathf.Deg2Rad;
            int n = sweepSubdivide;
            // -redefinitionProbeSweepRight: the same path the other way round.
            float sign = Array.IndexOf(Environment.GetCommandLineArgs(), "-redefinitionProbeSweepRight") >= 0 ? -1f : 1f;
            for (int i = 0; i < 60 * n; i++)
            {
                hdg += sign * (i < 20 * n || i >= 40 * n ? 1.5f : -1.5f) / n * Mathf.Deg2Rad;
                camera.camHdg = hdg;
                camera.camPitch = StartPitchDegrees * Mathf.Deg2Rad;
                // The capture without the vessel spans the frames either side of the
                // one with it, of which one shows the same step.
                if (captureDuringSweep && name == "renderdoc" && i == CaptureStep)
                    RenderDocCapture.Capture("camera at sweep step " + CaptureStep);
                if (captureDuringSweep && name != "renderdoc" && i == CaptureStep - 1)
                    RenderDocCapture.CaptureFrames(4, "camera at sweep step " + CaptureStep + ", " + name);
                yield return null;
            }
        }

        // A stretch of the path: per frame the change of heading and pitch, in degrees.
        private struct Segment
        {
            internal string Name;
            internal float Heading;
            internal float Pitch;
            internal int Frames;
        }

        // From below the vessel, the sky behind it, the camera moves the sky past it to
        // each side: left, right, down, up, each followed by a steady stretch.
        private static readonly Segment[] Segments =
        {
            new Segment { Name = "steady", Frames = SteadyFrames },
            new Segment { Name = "left", Heading = 1.5f, Frames = 20 },
            new Segment { Name = "after-left", Frames = 20 },
            new Segment { Name = "right", Heading = -1.5f, Frames = 40 },
            new Segment { Name = "after-right", Frames = 20 },
            new Segment { Name = "down", Pitch = 1f, Frames = 20 },
            new Segment { Name = "after-down", Frames = 20 },
            new Segment { Name = "up", Pitch = -1f, Frames = 20 },
            new Segment { Name = "after-up", Frames = 20 },
        };

        // The camera path, with captures in the middle of each move and at the end of
        // each steady stretch.
        private IEnumerator Turn(string name)
        {
            captureIndex = 0;
            FlightCamera camera = FlightCamera.fetch;
            float hdg = startHeading;
            float pitch = StartPitchDegrees;
            foreach (Segment segment in Segments)
            {
                bool moving = segment.Heading != 0f || segment.Pitch != 0f;
                for (int i = 1; i <= segment.Frames; i++)
                {
                    hdg += segment.Heading * Mathf.Deg2Rad;
                    pitch += segment.Pitch;
                    camera.camPitch = pitch * Mathf.Deg2Rad;
                    camera.camHdg = hdg;
                    bool capture = moving ? i == segment.Frames / 2 || i == segment.Frames / 2 + 1 : i == segment.Frames;
                    if (capture)
                    {
                        phase = segment.Name;
                        yield return Capture();
                    }
                    else
                    {
                        ReadNext();
                        yield return null;
                    }
                }
            }
            Debug.Log(Log.Tag + " Probe: " + name + " measured.");
        }

        // The stages of this frame's scene camera, around the vessel.
        private IEnumerator Capture()
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            Vector3 screen = scene.WorldToScreenPoint(vessel.CoMD);
            int width = scene.pixelWidth, height = scene.pixelHeight;
            int x = Mathf.Clamp(Mathf.RoundToInt(screen.x) - Crop / 2, 0, Mathf.Max(0, width - Crop));
            int y = Mathf.Clamp(Mathf.RoundToInt(screen.y) - Crop / 2, 0, Mathf.Max(0, height - Crop));
            Vector2 scale = new Vector2((float)Crop / width, (float)Crop / height);
            Vector2 offset = new Vector2((float)x / width, (float)y / height);
            int n = captureIndex++;
            string capture = capturePrefix + n.ToString("00", CultureInfo.InvariantCulture);
            string prefix = Path.Combine(Path.Combine(folder, variant), capture);
            meta.Append(variant).Append(' ').Append(Time.frameCount).Append(' ').Append(capture).Append(' ')
                .Append(x).Append(' ').Append(y).Append(' ').Append(phase).Append('\n');

            foreach (Station station in stations)
            {
                RenderTexture crop = Target(RenderTextureFormat.ARGBHalf);
                CommandBuffer cb = Buffer(station.Event);
                cb.Blit(BuiltinRenderTextureType.CameraTarget, crop, scale, offset);
                reads.Enqueue(new Read { Path = prefix + "-" + station.Name + ".bin", Texture = crop });
            }
            CommandBuffer last = Buffer(CameraEvent.AfterEverything);
            RenderTexture depth = Target(RenderTextureFormat.RFloat);
            last.Blit(new RenderTargetIdentifier("_CameraDepthTexture"), depth, scale, offset);
            reads.Enqueue(new Read { Path = prefix + "-depth.bin", Texture = depth });
            if (fullMask == null || fullMask.width != width || fullMask.height != height)
            {
                if (fullMask != null) fullMask.Release();
                fullMask = new RenderTexture(width, height, 24, RenderTextureFormat.R8);
                fullMask.Create();
            }
            last.SetRenderTarget(fullMask);
            last.ClearRenderTarget(true, true, Color.clear);
            last.SetViewProjectionMatrices(scene.worldToCameraMatrix, scene.projectionMatrix);
            foreach (Part part in vessel.parts)
                foreach (Renderer r in part.GetComponentsInChildren<Renderer>())
                    if (r.enabled && r.gameObject.activeInHierarchy)
                        for (int s = 0; s < r.sharedMaterials.Length; s++)
                            last.DrawRenderer(r, maskMaterial, s, 0);
            RenderTexture mask = Target(RenderTextureFormat.R8);
            last.Blit(fullMask, mask, scale, offset);
            reads.Enqueue(new Read { Path = prefix + "-mask.bin", Texture = mask });

            // The finished frame, as it goes to the screen: the scene camera's image
            // reaches it only after its own last event (measured: AfterEverything
            // holds the frame without the vessel).
            yield return new WaitForEndOfFrame();
            // Around the same point of the screen, Crop screen pixels wide whatever the
            // render size, so that runs at different render sizes compare. The
            // screenshot's rows run the other way from the camera's buffers: counted
            // from the other edge.
            int screenX = Mathf.Clamp(Mathf.RoundToInt(screen.x * Screen.width / width) - Crop / 2, 0,
                                      Mathf.Max(0, Screen.width - Crop));
            int screenY = Mathf.Clamp(Screen.height - Mathf.RoundToInt(screen.y * Screen.height / height) - Crop / 2, 0,
                                      Mathf.Max(0, Screen.height - Crop));
            RenderTexture full = RenderTexture.GetTemporary(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32);
            ScreenCapture.CaptureScreenshotIntoRenderTexture(full);
            RenderTexture screenCrop = Target(RenderTextureFormat.ARGB32);
            Graphics.Blit(full, screenCrop, new Vector2((float)Crop / Screen.width, (float)Crop / Screen.height),
                          new Vector2((float)screenX / Screen.width, (float)screenY / Screen.height));
            RenderTexture.ReleaseTemporary(full);
            reads.Enqueue(new Read { Path = prefix + "-screen.bin", Texture = screenCrop });
            // The buffers ran in that frame: emptied for the frames after.
            foreach (CommandBuffer cb in buffers.Values) cb.Clear();
        }

        private static RenderTexture Target(RenderTextureFormat format)
        {
            RenderTexture texture = new RenderTexture(Crop, Crop, 0, format, RenderTextureReadWrite.Linear);
            texture.Create();
            return texture;
        }

        private CommandBuffer Buffer(CameraEvent e)
        {
            CommandBuffer cb;
            if (!buffers.TryGetValue(e, out cb))
            {
                cb = new CommandBuffer { name = "ReDefinition probe " + e };
                scene.AddCommandBuffer(e, cb);
                buffers[e] = cb;
            }
            return cb;
        }

        private void Detach()
        {
            foreach (KeyValuePair<CameraEvent, CommandBuffer> pair in buffers)
                if (scene != null) scene.RemoveCommandBuffer(pair.Key, pair.Value);
            buffers.Clear();
        }

        // One read-back at a time, written by a thread.
        private void ReadNext()
        {
            if (Volatile.Read(ref busy) != 0 || reads.Count == 0) return;
            Read next = reads.Dequeue();
            Volatile.Write(ref busy, 1);
            AsyncGPUReadback.Request(next.Texture, 0, request =>
            {
                next.Texture.Release();
                Destroy(next.Texture);
                if (request.hasError)
                {
                    Volatile.Write(ref busy, 0);
                    return;
                }
                NativeArray<byte> data = request.GetData<byte>();
                byte[] bytes = data.ToArray();
                string path = next.Path;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        File.WriteAllBytes(path, bytes);
                    }
                    catch (Exception)
                    {
                        // A diagnostic: a file not written is a file missing.
                    }
                    Volatile.Write(ref busy, 0);
                });
            });
        }
    }
}
