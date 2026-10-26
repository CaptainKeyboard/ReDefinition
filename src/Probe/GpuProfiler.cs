using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Probe
{
    // With -redefinitionGpuProfile: where the GPU's time goes inside Unity's frame, named
    // by camera, pass and the mod whose work it is. KSP's player is a release build: no
    // GPU markers of Unity's own reach a profiler, so a capture shows thousands of
    // unnamed draws. Here markers go into Unity's render stream -- the proxy ends a D3D11
    // timestamp query at each (src/DxgiProxy/GpuProfiler.h) -- at:
    //   every camera's events (CameraEvent), the command buffers other mods and KSP hang
    //   there each between two markers of its own;
    //   the lights' shadow and screen-space mask events, the same way;
    //   the drawing scripts of every assembly -- OnPreRender, OnPostRender, OnRenderImage,
    //   OnRenderObject, and the handlers on Camera.onPreRender and onPostRender -- before
    //   and after, through GL.IssuePluginEvent where they run.
    // A segment is the GPU time from one marker to the next, named for the work that
    // lies between them; "Unity until X" is Unity's own rendering of that camera before
    // event X. Every 10 s the frame's GPU time, each camera's share and the costliest
    // segments go into the log. The same names are annotation markers, which RenderDoc
    // shows in a capture.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    internal sealed class GpuProfiler : MonoBehaviour
    {
        // The switch; the proxy the markers go to; the name of the profiler's marker buffers; seconds between two
        // reports; the most markers read back of one frame.
        internal const string Argument = "-redefinitionGpuProfile";
        private const string Proxy = ReDefinition.Proxy.ProxyDll.Name;
        private const string OwnBufferName = "ReDefinition GPU profiler";
        private const float Period = 10f;
        private const int Capacity = 2048;

        // The proxy's render-thread callback that ends a timestamp query at the marker id it is issued with.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr KspProfGetMarkerFunc();

        // Turns the proxy's timestamp queries on (1) or off (0).
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern void KspProfSetEnabled(int enabled);

        // Names a marker id for the proxy's annotation markers, which RenderDoc shows.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern void KspProfSetLabel(int id, string label);

        // The last complete frame: its marker ids in the GPU's order, each with the milliseconds from the Present
        // before to it, and the frame's number; returns how many markers it holds.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspProfRead([Out] int[] ids, [Out] float[] ms, int capacity, out int frameNumber);

        // The camera events, the light events and the drawing messages that get markers.
        private static readonly CameraEvent[] CameraEvents = (CameraEvent[])Enum.GetValues(typeof(CameraEvent));
        private static readonly LightEvent[] LightEvents =
        {
            LightEvent.BeforeShadowMap, LightEvent.AfterShadowMap, LightEvent.BeforeScreenspaceMask, LightEvent.AfterScreenspaceMask,
        };
        private static readonly string[] DrawingMessages = { "OnPreRender", "OnPostRender", "OnRenderImage", "OnRenderObject" };
        // The graphics mods' frame scripts too, which dispatch compute shaders and blit
        // outside any camera: by assembly name.
        // Not their physics steps, nor ReDefinition's own (its GPU work goes through
        // command buffers marked where they are built): each marker costs the main thread
        // a little, and those run many times a frame.
        private static readonly string[] UpdateMessages = { "Update", "LateUpdate" };
        private static readonly string[] GraphicsAssemblies =
        {
            "parallax", "scatterer", "atmosphere", "eve", "deferred", "tufx", "waterfall", "firefly",
        };

        // The proxy's marker callback (zero while the profiler is off), the ids of the labels and the labels by id,
        // the one-marker command buffer for each id, the before and after ids of each patched script method, and
        // the Harmony instance that patches them.
        private static IntPtr markerFunc = IntPtr.Zero;
        private static readonly Dictionary<string, int> ids = new Dictionary<string, int>();
        private static readonly List<string> labels = new List<string> { "frame start", "until Present" };
        private static readonly Dictionary<int, CommandBuffer> markerBuffers = new Dictionary<int, CommandBuffer>();
        private static readonly Dictionary<MethodBase, int[]> scriptMarkers = new Dictionary<MethodBase, int[]>();
        private static HarmonyLib.Harmony harmony;

        // One segment's GPU time over a report period.
        private sealed class Segment
        {
            // Its milliseconds summed over the period and in its costliest frame; the frames it appeared in.
            public double Sum, Most;
            public int Frames;
        }

        // The period's segments by marker id; the buffers a frame is read into; the last frame read, the frames
        // of the period and the spikes logged in it; the frame's usual time; the segments of a spike frame; the
        // period's frame time summed and at most; when the next scan for cameras is due and the period started.
        private readonly Dictionary<int, Segment> segments = new Dictionary<int, Segment>();
        private readonly int[] readIds = new int[Capacity];
        private readonly float[] readMs = new float[Capacity];
        private int lastFrame, frames, spikesLogged;
        private double typical;
        private readonly List<KeyValuePair<double, int>> spikeParts = new List<KeyValuePair<double, int>>();
        private double frameSum, frameMost;
        private float nextScan, started;

        // Only with the switch and the proxy: the markers on, the culling hook, the scripts patched.
        private void Awake()
        {
            if (!CommandLine.Has(Argument))
            {
                Destroy(this);
                return;
            }
            try
            {
                markerFunc = KspProfGetMarkerFunc();
                KspProfSetEnabled(1);
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                Debug.Log(Log.Tag + " GPU profiler: the dxgi.dll proxy of this release is needed (" + e.GetType().Name + ").");
                Destroy(this);
                return;
            }
            DontDestroyOnLoad(this);
            harmony = new HarmonyLib.Harmony("ReDefinition.GpuProfiler");
            Camera.onPreCull += MarkCulling;
            // -redefinitionProbeNoProbeShadows: KSP's reflection probe drawn without shadows,
            // to measure what its shadow maps cost.
            if (CommandLine.Has("-redefinitionProbeNoProbeShadows"))
            {
                Camera.onPreCull += ProbeShadowsOff;
                Camera.onPostRender += ProbeShadowsBack;
                Debug.Log(Log.Tag + " GPU profiler: the reflection probe drawn without shadows.");
            }
            int scripts = PatchScripts();
            started = Time.realtimeSinceStartup;
            Debug.Log(Log.Tag + " GPU profiler: on, " + scripts + " drawing script methods marked.");
        }

        // Each camera's start, in the stream where it begins: what came before it apart.
        private static void MarkCulling(Camera camera)
        {
            if (camera != null) GL.IssuePluginEvent(markerFunc, Id("until " + camera.name + " culls"));
        }

        // The shadow quality while a reflection probe camera draws without shadows; null between.
        private static ShadowQuality? heldShadows;

        // Before a reflection probe camera culls: shadows off, the quality kept.
        private static void ProbeShadowsOff(Camera camera)
        {
            if (camera == null || camera.cameraType != CameraType.Reflection || heldShadows.HasValue) return;
            heldShadows = QualitySettings.shadows;
            QualitySettings.shadows = ShadowQuality.Disable;
        }

        // After the reflection probe camera drew: the kept shadow quality back.
        private static void ProbeShadowsBack(Camera camera)
        {
            if (camera == null || camera.cameraType != CameraType.Reflection || !heldShadows.HasValue) return;
            QualitySettings.shadows = heldShadows.Value;
            heldShadows = null;
        }

        // The culling hook off and the proxy's queries stopped.
        private void OnDestroy()
        {
            Camera.onPreCull -= MarkCulling;
            if (markerFunc == IntPtr.Zero) return;
            try
            {
                KspProfSetEnabled(0);
            }
            catch (Exception)
            {
                // Quitting.
            }
        }

        // Whether the profiler runs: a label put together for Mark is asked for only then,
        // not built every frame for nothing.
        internal static bool On
        {
            get { return markerFunc != IntPtr.Zero; }
        }

        // A marker in a command buffer of ReDefinition's own, or here in the render stream,
        // that ends the segment named label; nothing without the profiler.
        internal static void Mark(CommandBuffer buffer, string label)
        {
            if (markerFunc != IntPtr.Zero) buffer.IssuePluginEvent(markerFunc, Id(label));
        }

        // The same marker issued straight into the render stream, from code that draws without a command buffer.
        internal static void MarkNow(string label)
        {
            if (markerFunc != IntPtr.Zero) GL.IssuePluginEvent(markerFunc, Id(label));
        }

        // The last frame read, for ModCost: its GPU time from Present to Present, and how
        // many frames were read so far.
        internal static double LastFrameMs;
        internal static int FramesRead;

        // The marker id of a label: a new one, handed to the proxy with its label, the first time it is asked for.
        internal static int Id(string label)
        {
            int id;
            if (ids.TryGetValue(label, out id)) return id;
            id = labels.Count;
            labels.Add(label);
            ids[label] = id;
            KspProfSetLabel(id, label);
            return id;
        }

        // A command buffer that only issues the marker id, built once per id.
        private static CommandBuffer Marker(int id)
        {
            CommandBuffer buffer;
            if (markerBuffers.TryGetValue(id, out buffer)) return buffer;
            buffer = new CommandBuffer { name = OwnBufferName + " #" + id.ToString(CultureInfo.InvariantCulture) };
            buffer.IssuePluginEvent(markerFunc, id);
            markerBuffers[id] = buffer;
            return buffer;
        }

        // Whether the marker is on the event already. GetCommandBuffers hands out new wrapper
        // objects every call, so the buffers are told apart by their names (one a marker),
        // not by reference: a reference check never found it, and every scan, once a second,
        // added another marker to every event of every camera until the frame held thousands.
        private static bool Has(CommandBuffer[] buffers, CommandBuffer marker)
        {
            foreach (CommandBuffer buffer in buffers)
                if (buffer.name == marker.name) return true;
            return false;
        }

        // Each event of each camera: a marker at its end, appended once, which ends the
        // segment of Unity's work up to the event and the command buffers hung there. The
        // other buffers are not touched: taking them off and adding them back between
        // markers of their own darkened the whole frame (the raytraced sun lost, at the
        // launch pad, whichever half of the events was laid out), for a reason not found.
        // What each mod's work costs, the mod cost probe measures (ModCost).
        private static void LayOutCamera(Camera camera)
        {
            string name = camera.name;
            foreach (CameraEvent at in CameraEvents)
            {
                CommandBuffer marker = Marker(Id(name + ": until the end of " + at));
                if (Has(camera.GetCommandBuffers(at), marker)) continue;
                camera.AddCommandBuffer(at, marker);
            }
        }

        // The same for the lights' shadow and mask events.
        private static void LayOutLight(Light light)
        {
            string name = "light " + light.name;
            foreach (LightEvent at in LightEvents)
            {
                CommandBuffer marker = Marker(Id(name + ": until the end of " + at));
                if (Has(light.GetCommandBuffers(at), marker)) continue;
                light.AddCommandBuffer(at, marker);
            }
        }

        // Every assembly's drawing scripts, and the camera event handlers found now.
        private static int PatchScripts()
        {
            int count = 0;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.GetName().Name;
                if (name.StartsWith("UnityEngine", StringComparison.Ordinal) || name.StartsWith("Unity.", StringComparison.Ordinal)
                    || name.StartsWith("System", StringComparison.Ordinal) || name == "mscorlib" || name == "0Harmony" || assembly.IsDynamic)
                    continue;
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).ToArray();
                }
                foreach (Type type in types)
                {
                    if (!typeof(MonoBehaviour).IsAssignableFrom(type) || type.ContainsGenericParameters || type == typeof(GpuProfiler)) continue;
                    bool graphics = GraphicsAssemblies.Any(g => name.IndexOf(g, StringComparison.OrdinalIgnoreCase) >= 0);
                    foreach (string message in graphics ? DrawingMessages.Concat(UpdateMessages) : DrawingMessages)
                    {
                        MethodInfo method = type.GetMethod(message, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                                                           null, Type.EmptyTypes, null)
                                            ?? (message == "OnRenderImage"
                                                ? type.GetMethod(message, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                                                : null);
                        if (method != null && Patch(method)) count++;
                    }
                }
            }
            return count + PatchCameraHandlers();
        }

        // The handlers on Camera.onPreRender and onPostRender not marked yet; how many were patched.
        private static int PatchCameraHandlers()
        {
            int count = 0;
            foreach (Camera.CameraCallback callback in new[] { Camera.onPreRender, Camera.onPostRender })
                if (callback != null)
                    foreach (Delegate handler in callback.GetInvocationList())
                        if (handler.Method.DeclaringType != typeof(GpuProfiler) && Patch(handler.Method))
                            count++;
            return count;
        }

        // A method patched with a marker before it ("until" its name) and after it (its name); false where it is
        // abstract, generic, marked already or Harmony refuses it.
        private static bool Patch(MethodBase method)
        {
            if (method == null || method.IsAbstract || method.ContainsGenericParameters || scriptMarkers.ContainsKey(method)) return false;
            string name = (method.DeclaringType != null ? method.DeclaringType.Assembly.GetName().Name + ": " + method.DeclaringType.Name : "?") + "." + method.Name;
            scriptMarkers[method] = new[] { Id("until " + name), Id(name) };
            try
            {
                harmony.Patch(method, new HarmonyLib.HarmonyMethod(typeof(GpuProfiler).GetMethod(nameof(Before), BindingFlags.Static | BindingFlags.NonPublic)),
                              new HarmonyLib.HarmonyMethod(typeof(GpuProfiler).GetMethod(nameof(After), BindingFlags.Static | BindingFlags.NonPublic)));
                return true;
            }
            catch (Exception e)
            {
                Debug.Log(Log.Tag + " GPU profiler: " + name + " not marked (" + e.Message + ").");
                return false;
            }
        }

        // Harmony prefix of a patched method: its "until" marker.
        private static void Before(MethodBase __originalMethod)
        {
            int[] marker;
            if (scriptMarkers.TryGetValue(__originalMethod, out marker)) GL.IssuePluginEvent(markerFunc, marker[0]);
        }

        // Harmony postfix of a patched method: the marker that ends its own segment.
        private static void After(MethodBase __originalMethod)
        {
            int[] marker;
            if (scriptMarkers.TryGetValue(__originalMethod, out marker)) GL.IssuePluginEvent(markerFunc, marker[1]);
        }

        // Not on Unity's camera of a reflection probe: a command buffer of the profiler's on
        // it left the probe out (the raytracing's self-check: "missing the sky's probe"),
        // and the raytraced scene dark with it. -redefinitionGpuProfileNoCameras: no
        // markers on the cameras and lights at all.
        private static readonly bool CameraMarkers = !CommandLine.Has("-redefinitionGpuProfileNoCameras");

        // Once a second: markers on the cameras and lights that came since (unless switched off), and the camera
        // event handlers added since patched.
        private void Scan()
        {
            if (!CameraMarkers)
            {
                int more = PatchCameraHandlers();
                if (more > 0) Debug.Log(Log.Tag + " GPU profiler: " + more + " more camera event handlers marked.");
                return;
            }
            foreach (Camera camera in Resources.FindObjectsOfTypeAll<Camera>())
                if (camera != null && camera.gameObject.scene.IsValid())
                    if (camera.cameraType != CameraType.Reflection)
                        LayOutCamera(camera);
            foreach (Light light in Resources.FindObjectsOfTypeAll<Light>())
                if (light != null && light.gameObject.scene.IsValid() && (light.type == LightType.Directional || light.shadows != LightShadows.None))
                    LayOutLight(light);
            int handlers = PatchCameraHandlers();
            if (handlers > 0) Debug.Log(Log.Tag + " GPU profiler: " + handlers + " more camera event handlers marked.");
        }

        // Each frame: the scan when due, the proxy's last complete frame read into the segments (a spike logged
        // with its costliest segments), and the report every Period seconds.
        private void LateUpdate()
        {
            if (Time.realtimeSinceStartup >= nextScan)
            {
                nextScan = Time.realtimeSinceStartup + 1f;
                try
                {
                    Scan();
                }
                catch (Exception e)
                {
                    Debug.Log(Log.Tag + " GPU profiler: scan failed: " + e);
                }
            }
            int frame;
            int count = KspProfRead(readIds, readMs, Capacity, out frame);
            if (frame != lastFrame && count >= 2)
            {
                lastFrame = frame;
                frames++;
                double total = readMs[count - 1];
                LastFrameMs = total;
                FramesRead++;
                // A spike: twice the frame's usual time and above 33 ms. Its own costliest
                // segments into the log, a few each report, to say what made it.
                bool spike = typical > 0.0 && total > 2.0 * typical && total > 33.0 && spikesLogged < 8;
                if (typical <= 0.0) typical = total;
                else if (total < 3.0 * typical) typical += (total - typical) * 0.05;
                frameSum += total;
                if (total > frameMost) frameMost = total;
                if (spike) spikeParts.Clear();
                for (int i = 1; i < count; i++)
                {
                    double ms = readMs[i] - readMs[i - 1];
                    if (ms < 0.0) continue;
                    if (spike) spikeParts.Add(new KeyValuePair<double, int>(ms, i == 1 ? -1 : readIds[i]));
                    // The first segment runs from the Present before: the GPU's idle time while
                    // the CPU prepares the frame is in it.
                    int id = readIds[i] == 1 ? Id("until Present, after " + LabelOf(readIds[i - 1]))
                             : i == 1 ? Id("from the Present before to the frame's first marker (GPU idle included), " + LabelOf(readIds[i]))
                             : readIds[i];
                    Segment segment;
                    if (!segments.TryGetValue(id, out segment)) segments[id] = segment = new Segment();
                    segment.Sum += ms;
                    segment.Frames++;
                    if (ms > segment.Most) segment.Most = ms;
                }
                if (spike)
                {
                    spikesLogged++;
                    var text = new StringBuilder(Log.Tag + " GPU profiler: a spike, " + Ms(total) + " ms against " + Ms(typical) + " usually:");
                    foreach (KeyValuePair<double, int> part in spikeParts.OrderByDescending(p => p.Key).Take(5))
                        text.Append("\n    ").Append(Ms(part.Key)).Append(" ms: ")
                            .Append(part.Value < 0 ? "from the Present before to the frame's first marker (GPU idle included)" : LabelOf(part.Value));
                    Debug.Log(text.ToString());
                }
            }
            if (Time.realtimeSinceStartup - started < Period) return;
            started = Time.realtimeSinceStartup;
            if (frames > 0) Report();
            spikesLogged = 0;
            segments.Clear();
            frames = 0;
            frameSum = frameMost = 0.0;
        }

        // The proxy's markers (src/DxgiProxy/GpuProfiler.h, kProfNativeBase).
        private const int NativeBase = 1000000;
        private static readonly string[] NativeLabels =
        {
            "proxy raytracing: waiting for D3D12 to be done with the shared textures",
            "proxy raytracing: inputs copied to the shared textures",
            "proxy raytracing: D3D12 work (scene build, rays, denoiser), waited for",
            "proxy raytracing: results copied back",
            "proxy Ray Reconstruction: waiting for D3D12 to be done with the shared textures",
            "proxy Ray Reconstruction: inputs copied to the shared textures",
            "proxy Ray Reconstruction: D3D12 work (guides, DLSS-RR), waited for",
            "proxy Ray Reconstruction: result copied back",
            "proxy raytracing, ocean: waiting for D3D12 to be done with the shared textures",
            "proxy raytracing, ocean: inputs copied to the shared textures",
            "proxy raytracing, ocean: D3D12 work (scene build, rays), waited for",
            "proxy raytracing, ocean: results copied back",
        };

        // The label of a marker id, the proxy's own included; "marker n" for an id not known.
        private static string LabelOf(int id)
        {
            if (id >= NativeBase && id < NativeBase + NativeLabels.Length) return NativeLabels[id - NativeBase];
            return id >= 0 && id < labels.Count ? labels[id] : "marker " + id;
        }

        // Milliseconds for the log, three decimals.
        private static string Ms(double value)
        {
            return value.ToString("0.000", CultureInfo.InvariantCulture);
        }

        // The period into the log: the mean frame time, each camera's or light's share and the costliest segments.
        private void Report()
        {
            double frame = frameSum / frames;
            var text = new StringBuilder(Log.Tag + " GPU profiler: " + HighLogic.LoadedScene + ", " + frames + " frames measured, GPU "
                                         + Ms(frame) + " ms a frame from Present to Present (at most " + Ms(frameMost) + ").");
            // Per camera or light: the part of the label before its colon.
            var groups = new Dictionary<string, double>();
            foreach (KeyValuePair<int, Segment> pair in segments)
            {
                string label = LabelOf(pair.Key);
                int colon = label.IndexOf(": ", StringComparison.Ordinal);
                string group = label.StartsWith("from the Present before", StringComparison.Ordinal) ? "frame start, GPU idle included"
                               : label.StartsWith("until ", StringComparison.Ordinal) ? "between scripts and cameras"
                               : colon < 0 ? "other" : label.Substring(0, colon);
                double value;
                groups.TryGetValue(group, out value);
                groups[group] = value + pair.Value.Sum / frames;
            }
            text.Append("\n  By camera or light:");
            foreach (KeyValuePair<string, double> pair in groups.OrderByDescending(p => p.Value))
                text.Append("\n    ").Append(pair.Key).Append(' ').Append(Ms(pair.Value)).Append(" ms (")
                    .Append((pair.Value / frame * 100.0).ToString("0.0", CultureInfo.InvariantCulture)).Append(" %)");
            text.Append("\n  Costliest segments, per frame on average, at most, in how many frames:");
            foreach (KeyValuePair<int, Segment> pair in segments.OrderByDescending(p => p.Value.Sum).Take(45))
                text.Append("\n    ").Append(Ms(pair.Value.Sum / frames)).Append(" ms, at most ").Append(Ms(pair.Value.Most))
                    .Append(", ").Append(pair.Value.Frames).Append(": ").Append(LabelOf(pair.Key));
            Debug.Log(text.ToString());
        }
    }
}
