using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using ReDefinition.Bridges;
using ReDefinition.Core;
using ReDefinition.Settings;
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
        // -redefinitionProbeSave <folder>: another save than ReDefinitionProbe, a copy of a
        // player's (the probe saves the flight it leaves into it); -redefinitionProbeVessel
        // <name>: that vessel's flight, wherever it is.
        internal static readonly string SaveFolder = ArgumentAfter("-redefinitionProbeSave") ?? "ReDefinitionProbe";
        internal static readonly string VesselName = ArgumentAfter("-redefinitionProbeVessel");
        // -redefinitionProbeBody <name>: the sea capsule on that body's sea instead of the
        // home body's (-redefinitionProbeSea); -redefinitionProbeMoon <name>: the moon the
        // light probe's "mun-" shots look at instead of the Mun.
        internal static readonly string BodyName = ArgumentAfter("-redefinitionProbeBody");
        internal static readonly string MoonName = ArgumentAfter("-redefinitionProbeMoon") ?? "Mun";

        internal static string ArgumentAfter(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, name);
            return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        }

        internal static bool Launched;
        // -redefinitionProbeSea: a capsule afloat out at sea, no land within kilometres.
        internal const string SeaArgument = "-redefinitionProbeSea";
        // -redefinitionProbeCoast, with it: the capsule off the island airfield's shore.
        internal const string CoastArgument = "-redefinitionProbeCoast";
        // -redefinitionProbeStay: the flight left to the player, the clouds hidden, no
        // measurement, KSP left running (with -redefinitionProbeSea: a capsule at sea to
        // look at the water's reflections oneself).
        internal const string StayArgument = "-redefinitionProbeStay";

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
            // -redefinitionProbeProfile <name>: that graphics profile applied as the main
            // menu's question applies it, saved in the mods, and KSP quits, so that the next
            // start runs with it all; the caller keeps copies of the files it changes.
            string profileName = SceneProbe.ArgumentAfter("-redefinitionProbeProfile");
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
            // -redefinitionProbeDays <n>: n of the home body's solar days later, the sun where
            // it was and the clouds moved on.
            string days = SceneProbe.ArgumentAfter("-redefinitionProbeDays");
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
            if (SceneProbe.VesselName == null && Array.IndexOf(Environment.GetCommandLineArgs(), SceneProbe.SeaArgument) >= 0)
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
            CelestialBody home = SceneProbe.BodyName != null
                ? FlightGlobals.Bodies.Find(b => b.bodyName == SceneProbe.BodyName)
                : FlightGlobals.GetHomeBody();
            if (home == null || home.pqsController == null || !home.ocean) return -1;
            Func<double, double, double> ground = (lat, lon) =>
                home.pqsController.GetSurfaceHeight(home.GetRelSurfaceNVector(lat, lon)) - home.Radius;
            // -redefinitionProbeCoast: 2.6 km off the island airfield's east shore, the
            // island and its sea floor in view; otherwise far out at sea.
            bool coast = Array.IndexOf(Environment.GetCommandLineArgs(), SceneProbe.CoastArgument) >= 0;
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
                Debug.Log(Log.Tag + " Probe: a capsule afloat on " + home.bodyName + " at latitude " + place[0].ToString(CultureInfo.InvariantCulture)
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
                for (int i = 0; i < 30; i++) yield return null;
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

    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal sealed class SceneProbeRunner : MonoBehaviour
    {
        [System.Runtime.InteropServices.DllImport("dxgi.dll", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl,
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

        // The reflection probe the rays see the sky in (RtKsp, Environment), its six faces
        // into `folder` as <shot>-env<face>.ppm, clamped to 0..1.
        internal static void EnvironmentFaces(string folder, string shot)
        {
            ReflectionProbe found = null;
            foreach (ReflectionProbe p in UnityEngine.Object.FindObjectsOfType<ReflectionProbe>())
                if (p.isActiveAndEnabled && p.texture != null)
                {
                    found = p;
                    break;
                }
            var cube = found != null ? found.texture as RenderTexture : null;
            if (cube == null)
            {
                Debug.Log(Log.Tag + " Probe: no reflection probe render texture to dump.");
                return;
            }
            var face = new RenderTexture(cube.width, cube.height, 0, cube.format);
            face.Create();
            var read = new Texture2D(cube.width, cube.height, TextureFormat.RGBA32, false);
            RenderTexture active = RenderTexture.active;
            for (int i = 0; i < 6; i++)
            {
                Graphics.CopyTexture(cube, i, 0, face, 0, 0);
                RenderTexture.active = face;
                read.ReadPixels(new Rect(0, 0, cube.width, cube.height), 0, 0);
                Color32[] pixels = read.GetPixels32();
                var bytes = new List<byte>(Encoding.ASCII.GetBytes("P6\n" + cube.width + " " + cube.height + "\n255\n"));
                for (int y = cube.height - 1; y >= 0; y--)
                    for (int x = 0; x < cube.width; x++)
                    {
                        Color32 c = pixels[y * cube.width + x];
                        bytes.Add(c.r);
                        bytes.Add(c.g);
                        bytes.Add(c.b);
                    }
                File.WriteAllBytes(Path.Combine(folder, shot + "-env" + i + ".ppm"), bytes.ToArray());
            }
            RenderTexture.active = active;
            UnityEngine.Object.Destroy(read);
            face.Release();
            UnityEngine.Object.Destroy(face);
            Debug.Log(Log.Tag + " Probe: reflection probe " + found.name + " (" + cube.width + ", " + cube.format + ") dumped.");
        }

        // The raytraced camera's light at a few places of the pad's view, into the log: the
        // traced diffuse light (linear), the sun's visibility, the forward ratio (k, w),
        // Unity's ambient probe up / side / down and the sun's colour.
        private static readonly CameraEvent[] StageEvents =
        {
            CameraEvent.AfterGBuffer, CameraEvent.BeforeLighting, CameraEvent.AfterLighting, CameraEvent.AfterForwardOpaque,
            CameraEvent.AfterSkybox, CameraEvent.AfterForwardAlpha, CameraEvent.BeforeImageEffects, CameraEvent.AfterImageEffects,
            CameraEvent.AfterEverything,
        };

        private static IEnumerator Stages(string rendererName)
        {
            Camera scene = null;
            foreach (Camera c in Camera.allCameras)
                if (c.name == "Camera 00") scene = c;
            // <name>@<parent's name>: only under a parent so named.
            string[] parts = rendererName.Split('@');
            string name = parts[0], parentName = parts.Length > 1 ? parts[1] : null;
            Renderer target = null;
            float nearest = float.MaxValue;
            if (scene != null)
                foreach (Renderer r in FindObjectsOfType<Renderer>())
                {
                    if (r.name != name || (parentName != null && (r.transform.parent == null || r.transform.parent.name != parentName))
                        || !r.isVisible)
                        continue;
                    float d = Vector3.Distance(scene.transform.position, r.bounds.center);
                    if (d < nearest) { nearest = d; target = r; }
                }
            if (target == null)
            {
                Debug.Log(Log.Tag + " Probe: stages, no visible renderer " + rendererName + " or no Camera 00.");
                yield break;
            }
            // In the camera's pixels: its screen points are the screen's. A window around the
            // middle of its bounds, which may lie off the renderer's own pixels.
            const int Window = 32;
            Vector3 at = scene.WorldToViewportPoint(target.bounds.center);
            int px = Mathf.Clamp(Mathf.RoundToInt(at.x * scene.pixelWidth), Window / 2, scene.pixelWidth - Window / 2);
            int py = Mathf.Clamp(Mathf.RoundToInt(at.y * scene.pixelHeight), Window / 2, scene.pixelHeight - Window / 2);
            Vector2 scale = new Vector2((float)Window / scene.pixelWidth, (float)Window / scene.pixelHeight);
            Vector2 offset = new Vector2((float)(px - Window / 2) / scene.pixelWidth, (float)(py - Window / 2) / scene.pixelHeight);
            var buffers = new List<KeyValuePair<CameraEvent, CommandBuffer>>();
            var crops = new List<KeyValuePair<string, RenderTexture>>();
            foreach (CameraEvent e in StageEvents)
            {
                var cb = new CommandBuffer { name = "ReDefinition probe stages " + e };
                RenderTexture crop = new RenderTexture(Window, Window, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                crop.Create();
                cb.Blit(BuiltinRenderTextureType.CameraTarget, crop, scale, offset);
                crops.Add(new KeyValuePair<string, RenderTexture>(e.ToString(), crop));
                scene.AddCommandBuffer(e, cb);
                buffers.Add(new KeyValuePair<CameraEvent, CommandBuffer>(e, cb));
            }
            yield return new WaitForEndOfFrame();
            foreach (KeyValuePair<CameraEvent, CommandBuffer> b in buffers)
                scene.RemoveCommandBuffer(b.Key, b.Value);
            var line = new StringBuilder(Log.Tag + " Probe: stages at " + rendererName + " under "
                                         + (target.transform.parent != null ? target.transform.parent.name : "nothing") + ", "
                                         + nearest.ToString("0.0", CultureInfo.InvariantCulture) + " m, pixel " + px + "," + py
                                         + " of " + scene.pixelWidth + "x" + scene.pixelHeight + ", the window's brightest pixel and mean:");
            Texture2D read = new Texture2D(Window, Window, TextureFormat.RGBAFloat, false, true);
            RenderTexture active = RenderTexture.active;
            foreach (KeyValuePair<string, RenderTexture> crop in crops)
            {
                RenderTexture.active = crop.Value;
                read.ReadPixels(new Rect(0, 0, Window, Window), 0, 0);
                Color sum = Color.clear, most = Color.clear;
                foreach (Color c in read.GetPixels())
                {
                    sum += c;
                    if (c.r + c.g + c.b > most.r + most.g + most.b) most = c;
                }
                sum /= Window * Window;
                line.Append(System.Environment.NewLine).Append("  ").Append(crop.Key).Append(' ').Append(Rgb(most)).Append(", mean ").Append(Rgb(sum));
                crop.Value.Release();
            }
            RenderTexture.active = active;
            Destroy(read);
            Debug.Log(line.ToString());
        }

        private static IEnumerator LightBuffer(string folder, string shot)
        {
            Camera scene = null;
            foreach (Camera c in Camera.allCameras)
                if (c.name == "Camera 00") scene = c;
            if (scene == null) yield break;
            var events = new[] { CameraEvent.AfterGBuffer, CameraEvent.BeforeLighting };
            var buffers = new CommandBuffer[events.Length];
            var targets = new RenderTexture[events.Length];
            for (int i = 0; i < events.Length; i++)
            {
                targets[i] = new RenderTexture(scene.pixelWidth, scene.pixelHeight, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                targets[i].Create();
                buffers[i] = new CommandBuffer { name = "ReDefinition probe light buffer " + events[i] };
                buffers[i].Blit(BuiltinRenderTextureType.CameraTarget, targets[i]);
                scene.AddCommandBuffer(events[i], buffers[i]);
            }
            yield return new WaitForEndOfFrame();
            RenderTexture active = RenderTexture.active;
            for (int i = 0; i < events.Length; i++)
            {
                scene.RemoveCommandBuffer(events[i], buffers[i]);
                Texture2D read = new Texture2D(targets[i].width, targets[i].height, TextureFormat.RGBAFloat, false, true);
                RenderTexture.active = targets[i];
                read.ReadPixels(new Rect(0, 0, targets[i].width, targets[i].height), 0, 0, false);
                string name = events[i] == CameraEvent.AfterGBuffer ? "afterGBuffer" : "beforeLighting";
                using (var file = new BinaryWriter(File.Create(Path.Combine(folder, shot + "-" + name + ".bin"))))
                {
                    file.Write(read.width);
                    file.Write(read.height);
                    foreach (Color c in read.GetPixels())
                    {
                        file.Write(c.r);
                        file.Write(c.g);
                        file.Write(c.b);
                        file.Write(c.a);
                    }
                }
                Destroy(read);
                targets[i].Release();
            }
            RenderTexture.active = active;
            Debug.Log(Log.Tag + " Probe: light buffer of Camera 00 written.");
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool WritePrivateProfileString(string section, string key, string value, string file);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern uint GetPrivateProfileString(string section, string key, string fallback, StringBuilder value, uint size,
                                                           string file);

        private static IEnumerator IniShots(string folder, string shot, string sets)
        {
            string ini = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Application.dataPath)) ?? "", "ReDefinitionProxy.ini");
            var before = new Dictionary<string, string>();
            int n = 0;
            foreach (string set in sets.Split(';'))
            {
                foreach (string pair in set.Split(','))
                {
                    int equals = pair.IndexOf('=');
                    if (equals <= 0) continue;
                    string key = pair.Substring(0, equals).Trim();
                    if (!before.ContainsKey(key))
                    {
                        var value = new StringBuilder(260);
                        GetPrivateProfileString("Proxy", key, "", value, (uint)value.Capacity, ini);
                        before[key] = value.ToString();
                    }
                    WritePrivateProfileString("Proxy", key, pair.Substring(equals + 1).Trim(), ini);
                }
                Debug.Log(Log.Tag + " Probe: ReDefinitionProxy.ini set to " + set + ".");
                yield return new WaitForSecondsRealtime(12f);
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, shot + "-ini" + n + ".png"));
                Debug.Log(Log.Tag + " Probe: shot " + shot + "-ini" + n + " with " + set + ".");
                n++;
                yield return null;
            }
            foreach (KeyValuePair<string, string> pair in before)
                WritePrivateProfileString("Proxy", pair.Key, pair.Value, ini);
            Debug.Log(Log.Tag + " Probe: ReDefinitionProxy.ini's values set back.");
        }

        private static string Rgb(Color c)
        {
            return c.r.ToString("0.0000", CultureInfo.InvariantCulture) + " " + c.g.ToString("0.0000", CultureInfo.InvariantCulture) + " "
                   + c.b.ToString("0.0000", CultureInfo.InvariantCulture);
        }

        private static IEnumerator LightValues()
        {
            Raytracing.RtCamera rt = Raytracing.RtCamera.Current;
            if (rt == null) yield break;
            // The screen-space shadow mask Unity collected for the sun this frame, copied after
            // it is made, into the session's capture folder, shadowmask.pgm.
            Light masked = rt.SunLightNow;
            if (masked != null)
            {
                var copy = new RenderTexture(rt.Camera.pixelWidth, rt.Camera.pixelHeight, 0, RenderTextureFormat.ARGB32);
                var grab = new CommandBuffer { name = "ReDefinition probe shadow mask" };
                grab.Blit(BuiltinRenderTextureType.CurrentActive, copy);
                masked.AddCommandBuffer(LightEvent.AfterScreenspaceMask, grab);
                yield return new WaitForEndOfFrame();
                yield return null;
                masked.RemoveCommandBuffer(LightEvent.AfterScreenspaceMask, grab);
                AsyncGPUReadbackRequest maskRead = AsyncGPUReadback.Request(copy, 0, TextureFormat.RGBA32);
                while (!maskRead.done) yield return null;
                if (!maskRead.hasError)
                {
                    NativeArray<Color32> m = maskRead.GetData<Color32>();
                    int mw = copy.width, mh = copy.height;
                    byte[] head = Encoding.ASCII.GetBytes("P5\n" + mw + " " + mh + "\n255\n");
                    var mb = new byte[head.Length + mw * mh];
                    Array.Copy(head, mb, head.Length);
                    for (int y = 0; y < mh; y++)
                        for (int x = 0; x < mw; x++)
                            mb[head.Length + y * mw + x] = m[(mh - 1 - y) * mw + x].r;
                    File.WriteAllBytes(Path.Combine(CaptureFolder.Run, "shadowmask.pgm"), mb);
                }
                grab.Release();
                copy.Release();
            }
            var places = new[] { new KeyValuePair<string, Rect>("lawn", new Rect(0.02f, 0.57f, 0.18f, 0.12f)),
                                 new KeyValuePair<string, Rect>("pad", new Rect(0.16f, 0.80f, 0.15f, 0.07f)),
                                 new KeyValuePair<string, Rect>("rocket", new Rect(0.485f, 0.35f, 0.022f, 0.14f)),
                                 new KeyValuePair<string, Rect>("far", new Rect(0.06f + 0.88f * 21f / 22f, 0.08f + 0.64f * 5f / 12f,
                                                                                0.88f / 22f, 0.64f / 12f)) };
            foreach (var texture in new[] { new KeyValuePair<string, RenderTexture>("diffuse", rt.DiffuseTexture),
                                            new KeyValuePair<string, RenderTexture>("sun", rt.SunTexture),
                                            new KeyValuePair<string, RenderTexture>("ratio", rt.ForwardRatioTexture),
                                            new KeyValuePair<string, RenderTexture>("forward", rt.ForwardTexture),
                                            new KeyValuePair<string, RenderTexture>("specular", rt.SpecularTexture),
                                            new KeyValuePair<string, RenderTexture>("ocean", rt.OceanTracedTexture) })
            {
                if (texture.Value == null) continue;
                AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(texture.Value, 0, TextureFormat.RGBAFloat);
                while (!request.done) yield return null;
                if (request.hasError) continue;
                NativeArray<Color> data = request.GetData<Color>();
                int w = texture.Value.width, h = texture.Value.height;
                // The forward ratio's w and the sun's visibility as pictures (PGM, top row
                // first): black where the rays held no surface, or saw no sun.
                if (texture.Key == "ratio" || texture.Key == "sun" || texture.Key == "diffuse" || texture.Key == "ocean")
                {
                    byte[] header = Encoding.ASCII.GetBytes("P5\n" + w + " " + h + "\n255\n");
                    var bytes = new byte[header.Length + w * h];
                    Array.Copy(header, bytes, header.Length);
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            Color texel = data[(h - 1 - y) * w + x];
                            bytes[header.Length + y * w + x] = (byte)Mathf.Clamp((texture.Key == "sun" ? texel.r : texture.Key == "diffuse" ? texel.g * 4f : texture.Key == "ocean" ? (texel.a > 0f ? 0.5f + texel.g : 0f) : texel.g * 0.5f + 0.5f) * 255f, 0f, 255f);
                        }
                    string maskFolder = CaptureFolder.Run;
                    File.WriteAllBytes(Path.Combine(maskFolder, texture.Key + ".pgm"), bytes);
                }
                var line = new StringBuilder(Log.Tag + " Probe: light values, " + texture.Key + ":");
                foreach (var place in places)
                {
                    Color sum = Color.clear;
                    int n = 0;
                    // Rows from the bottom in the texture, the places from the top.
                    for (int y = (int)((1f - place.Value.yMax) * h); y < (int)((1f - place.Value.yMin) * h); y++)
                        for (int x = (int)(place.Value.xMin * w); x < (int)(place.Value.xMax * w); x++)
                        {
                            sum += data[y * w + x];
                            n++;
                        }
                    if (n > 0) sum /= n;
                    line.Append(' ').Append(place.Key).Append(' ').Append(sum.r.ToString("0.000", CultureInfo.InvariantCulture))
                        .Append(", ").Append(sum.g.ToString("0.000", CultureInfo.InvariantCulture)).Append(", ")
                        .Append(sum.b.ToString("0.000", CultureInfo.InvariantCulture)).Append(", ")
                        .Append(sum.a.ToString("0.000", CultureInfo.InvariantCulture)).Append(';');
                }
                Debug.Log(line.ToString());
            }
            // What lies at a few cells of tools' grid comparison (22 by 12 over x 6-94 %, y 8-72 %).
            // And at pixels of the 2560 by 1440 shot: the lawn before the runway, the runway, the lawn beyond.
            foreach (Vector2Int cell in new[] { new Vector2Int(21, 5), new Vector2Int(18, 5), new Vector2Int(2, 6),
                                                new Vector2Int(10, 7), new Vector2Int(14, 9), new Vector2Int(-500, 545),
                                                new Vector2Int(-500, 528), new Vector2Int(-500, 570), new Vector2Int(-500, 600) })
            {
                float vx = cell.x < 0 ? -cell.x / 2560f : 0.06f + 0.88f * (cell.x + 0.5f) / 22f;
                float vy = cell.x < 0 ? 1f - cell.y / 1440f : 1f - (0.08f + 0.64f * (cell.y + 0.5f) / 12f);
                Ray ray = rt.Camera.ViewportPointToRay(new Vector3(vx, vy, 0f));
                RaycastHit hit;
                string what = "nothing";
                if (Physics.Raycast(ray, out hit, 1e6f, ~0, QueryTriggerInteraction.Ignore))
                {
                    Renderer renderer = hit.collider.GetComponent<Renderer>() ?? hit.collider.GetComponentInParent<Renderer>();
                    Material material = renderer != null ? renderer.sharedMaterial : null;
                    what = hit.collider.name + " at " + hit.distance.ToString("0") + " m, "
                           + (material != null ? material.name + " (" + material.shader.name + ")" : "no renderer");
                }
                Debug.Log(Log.Tag + " Probe: light values, cell " + cell.x + "," + cell.y + ": " + what + ".");
            }
            foreach (Light light in UnityEngine.Object.FindObjectsOfType<Light>())
                if (light.isActiveAndEnabled && light.intensity > 0f)
                    Debug.Log(Log.Tag + " Probe: light values, light '" + light.name + "' " + light.type + " intensity "
                              + light.intensity.ToString("0.00", CultureInfo.InvariantCulture) + " colour " + light.color
                              + " shadows " + light.shadows + " strength " + light.shadowStrength.ToString("0.00", CultureInfo.InvariantCulture)
                              + " mask " + light.cullingMask + " at " + light.transform.position + " forward " + light.transform.forward
                              + " parent " + (light.transform.parent != null ? light.transform.parent.name : "none")
                              + (light == rt.SunLightNow ? " (the raytracing's sun)" : "") + ".");
            // The cloud layer's check (-redefinitionProbeCloudShadow): at a grid of pixels, the
            // ground's point by a physics ray, EVE's own prediction of the shadow there, beside
            // what the rays wrote (the sun's visibility is the clouds' transmittance then).
            Raytracing.RtEveClouds eve = Raytracing.RtEveClouds.Last;
            if (eve != null && Array.IndexOf(Environment.GetCommandLineArgs(), "-redefinitionProbeCloudShadow") >= 0 && rt.SunTexture != null)
            {
                AsyncGPUReadbackRequest sunRead = AsyncGPUReadback.Request(rt.SunTexture, 0, TextureFormat.RGBAFloat);
                while (!sunRead.done) yield return null;
                if (!sunRead.hasError)
                {
                    NativeArray<Color> sunData = sunRead.GetData<Color>();
                    int sw = rt.SunTexture.width, sh = rt.SunTexture.height;
                    Light sunLight = rt.SunLightNow;
                    Vector3 towards = sunLight != null ? -sunLight.transform.forward : Vector3.up;
                    var lines = new StringBuilder();
                    var csv = new StringBuilder("vx,vy,hit,predicted,rays\n");
                    int pairs = 0;
                    double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0, absDiff = 0;
                    for (int gy = 0; gy < 24; gy++)
                        for (int gx = 0; gx < 32; gx++)
                        {
                            float vx = (gx + 0.5f) / 32f, vy = (gy + 0.5f) / 24f;
                            RaycastHit hit;
                            if (!Physics.Raycast(rt.Camera.ViewportPointToRay(new Vector3(vx, vy, 0f)), out hit, 1e5f, ~0, QueryTriggerInteraction.Ignore)) continue;
                            float predicted = eve.EvePrediction(hit.point, towards, new Color(2f, 2f, 2f, 1f), 1f);
                            if (predicted < 0f) continue;
                            // Only comparable where the traced camera's pixel is the surface the physics
                            // ray met: run once more with -redefinitionProbeCloudValue 4, which writes the
                            // pixel's distance, and join the two cloudcheck.csv by row (at sea 34 of 390
                            // points: in flight KSP's cameras split the view, and the near water's pixels
                            // hold no depth in the traced one).
                            float ours = sunData[Mathf.Clamp((int)(vy * sh), 0, sh - 1) * sw + Mathf.Clamp((int)(vx * sw), 0, sw - 1)].r;
                            if (pairs < 6 || pairs % 60 == 0 || Array.IndexOf(Environment.GetCommandLineArgs(), "-redefinitionProbeCloudValue") >= 0 && pairs < 40)
                                Debug.Log(Log.Tag + " Probe: cloud check at " + hit.point + " (" + hit.collider.name + ", " + hit.distance.ToString("0.0") + " m, viewport " + vx.ToString("0.00")
                                          + "," + vy.ToString("0.00") + "): EVE " + predicted.ToString("0.00") + ", rays " + ours.ToString("0.000")
                                          + ", " + eve.EveRaw(hit.point, towards) + ".");
                            csv.Append(vx.ToString(CultureInfo.InvariantCulture)).Append(',').Append(vy.ToString(CultureInfo.InvariantCulture)).Append(',')
                               .Append(hit.distance.ToString(CultureInfo.InvariantCulture)).Append(',').Append(predicted.ToString(CultureInfo.InvariantCulture))
                               .Append(',').Append(ours.ToString(CultureInfo.InvariantCulture)).Append('\n');
                            pairs++;
                            sx += predicted; sy += ours; sxx += predicted * predicted; syy += ours * ours; sxy += predicted * ours;
                            absDiff += Math.Abs(predicted - ours);
                            if (pairs <= 12) lines.Append(' ').Append(predicted.ToString("0.00", CultureInfo.InvariantCulture)).Append('/')
                                                  .Append(ours.ToString("0.00", CultureInfo.InvariantCulture));
                        }
                    double n = Math.Max(pairs, 1);
                    double correlation = (sxy / n - sx / n * sy / n)
                                         / Math.Sqrt(Math.Max((sxx / n - sx / n * sx / n) * (syy / n - sy / n * sy / n), 1e-12));
                    File.WriteAllText(Path.Combine(CaptureFolder.Run, "cloudcheck.csv"), csv.ToString());
                    Debug.Log(Log.Tag + " Probe: cloud " + eve.MapCheck() + ".");
                    Debug.Log(Log.Tag + " Probe: cloud check: " + pairs + " ground points, EVE's predicted shadow against the rays' (first: "
                              + lines + "), means " + (sx / n).ToString("0.000", CultureInfo.InvariantCulture) + " / "
                              + (sy / n).ToString("0.000", CultureInfo.InvariantCulture) + ", correlation "
                              + correlation.ToString("0.000", CultureInfo.InvariantCulture) + ", mean difference "
                              + (absDiff / n).ToString("0.000", CultureInfo.InvariantCulture) + ".");
                }
            }
            var directions = new[] { Vector3.up, Vector3.right, Vector3.down };
            var ambient = new Color[3];
            UnityEngine.Rendering.SphericalHarmonicsL2 probe;
            LightProbes.GetInterpolatedProbe(rt.transform.position, null, out probe);
            probe.Evaluate(directions, ambient);
            Debug.Log(Log.Tag + " Probe: light values, ambient up " + ambient[0] + " side " + ambient[1] + " down " + ambient[2]
                      + ", RenderSettings.ambientLight " + RenderSettings.ambientLight + ", mode " + RenderSettings.ambientMode
                      + ", sun " + rt.TracedSunColour + ", path " + rt.Camera.actualRenderingPath + ".");
        }

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
                yield return ShotVariant.Apply();
                // -redefinitionProbeRenderDoc, under RenderDoc (tools/renderdoc_probe.ps1
                // -Stay): one frame captured 25 s on, then KSP quits.
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "-redefinitionProbeRenderDoc") >= 0)
                {
                    yield return new WaitForSeconds(25f);
                    RenderDocCapture.Capture("the flight left to run");
                    yield return new WaitForSeconds(5f);
                    Application.Quit();
                    yield break;
                }
                // -redefinitionProbeCpuSample: the main thread sampled for 30 s from 20 s on
                // (src/DxgiProxy/CpuSampler.h) into the session's capture folder, cpu-samples.txt,
                // then KSP quits; tools/cpu_samples.py reads it.
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "-redefinitionProbeCpuSample") >= 0)
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
                    yield return new WaitForSeconds(33f);
                    Application.Quit();
                    yield break;
                }
                // -redefinitionProbeShot <name>: the frame as KSP draws it, 30 s on, into
                // the session's capture folder (CaptureFolder.Run) as <name>.png, then KSP quits.
                string shot = SceneProbe.ArgumentAfter("-redefinitionProbeShot");
                if (shot == null) yield break;
                yield return new WaitForSeconds(30f);
                yield return LightValues();
                string folder = CaptureFolder.Run;
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, shot + ".png"));
                EnvironmentFaces(folder, shot);
                // -redefinitionProbeSurfaces: the raytraced camera's surfaces of the frame
                // after, as <shot>-<name>.bin (RtCamera.WriteSurfaceImages).
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "-redefinitionProbeSurfaces") >= 0)
                {
                    yield return new WaitForEndOfFrame();
                    Raytracing.RtCamera traced = Raytracing.RtCamera.Current;
                    Debug.Log(Log.Tag + " Probe: surfaces " + (traced != null ? traced.WriteSurfaceImages(folder, shot) : "none, no raytraced camera")
                              + ".");
                }
                Debug.Log(Log.Tag + " Probe: shot " + shot + ".");
                // -redefinitionProbeMaterials <part of a name>: the renderers so named, with
                // their materials' shaders, queues and keywords.
                string materialsOf = SceneProbe.ArgumentAfter("-redefinitionProbeMaterials");
                if (materialsOf != null)
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
                // -redefinitionProbeStagesAt <renderer name>[@<parent's name>]: the scene camera's image after each
                // of its stages at the middle of the nearest renderer so named, linear, a 5 by 5
                // pixel mean each: in which stage it takes its brightness.
                string stagesAt = SceneProbe.ArgumentAfter("-redefinitionProbeStagesAt");
                if (stagesAt != null)
                    yield return Stages(stagesAt);
                // -redefinitionProbeLightBuffer: the scene camera's light buffer, whole, after the
                // G-buffer and before the lights, as <shot>-afterGBuffer.bin and
                // <shot>-beforeLighting.bin (the surfaces' format): what each surface brings
                // into the lighting of its own.
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "-redefinitionProbeLightBuffer") >= 0)
                    yield return LightBuffer(folder, shot);
                // -redefinitionProbeShotIni "key=value,key=value;key=value": then each set of the
                // proxy's live settings in turn written into ReDefinitionProxy.ini, which the proxy
                // re-reads within a second, and the same view 10 s later as <shot>-ini<n>.png;
                // the values as they were written back at the end.
                string iniSets = SceneProbe.ArgumentAfter("-redefinitionProbeShotIni");
                if (iniSets != null)
                    yield return IniShots(folder, shot, iniSets);
                // -redefinitionProbeShotRaytracing <mode,mode,...>: then the raytracing
                // switched to each mode in turn, as in the settings window, and the same view
                // 10 s later as <shot>-<mode>.png: several modes compared in one run.
                string otherModes = SceneProbe.ArgumentAfter("-redefinitionProbeShotRaytracing");
                if (otherModes != null && ReDefinitionAddon.Instance != null)
                    foreach (string otherMode in otherModes.Split(','))
                    {
                        Raytracing.RaytracingMode mode;
                        if (!Enum.TryParse(otherMode, true, out mode)) continue;
                        OwnSettings before = ReDefinitionAddon.Instance.Current();
                        OwnSettings after = before.Clone();
                        after.Raytracing = mode;
                        ReDefinitionAddon.Instance.Apply(before, after);
                        Debug.Log(Log.Tag + " Probe: the raytracing switched from " + before.Raytracing + " to " + mode + ".");
                        yield return new WaitForSeconds(10f);
                        ScreenCapture.CaptureScreenshot(Path.Combine(folder, shot + "-" + mode + ".png"));
                        Debug.Log(Log.Tag + " Probe: shot " + shot + "-" + mode + ".");
                        if (stagesAt != null)
                            yield return Stages(stagesAt);
                    }
                // -redefinitionProbeBurst <n>: n frames more, one after the other, as
                // <shot>-burst<i>.png, for what changes from frame to frame (flicker,
                // ghosting).
                string burst = SceneProbe.ArgumentAfter("-redefinitionProbeBurst");
                int burstFrames;
                if (burst != null && int.TryParse(burst, out burstFrames))
                    for (int i = 0; i < burstFrames; i++)
                    {
                        yield return null;
                        ScreenCapture.CaptureScreenshot(Path.Combine(folder, shot + "-burst" + i.ToString("00") + ".png"));
                    }
                yield return new WaitForSeconds(2f);
                Application.Quit();
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
            // In the camera's pixels: its screen points are the screen's (ScreenCoordinates).
            int width = scene.pixelWidth, height = scene.pixelHeight;
            Vector3 viewport = scene.WorldToViewportPoint(vessel.CoMD);
            Vector3 screen = new Vector3(viewport.x * width, viewport.y * height, viewport.z);
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
