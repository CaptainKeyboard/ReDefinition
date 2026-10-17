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
        internal const string Argument = "-redefinitionGpuProfile";
        private const string Proxy = "dxgi.dll";
        private const string OwnBufferName = "ReDefinition GPU profiler";
        private const float Period = 10f;
        private const int Capacity = 2048;

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr KspProfGetMarkerFunc();

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern void KspProfSetEnabled(int enabled);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern void KspProfSetLabel(int id, string label);

        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspProfRead([Out] int[] ids, [Out] float[] ms, int capacity, out int frameNumber);

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

        private static IntPtr markerFunc = IntPtr.Zero;
        private static readonly Dictionary<string, int> ids = new Dictionary<string, int>();
        private static readonly List<string> labels = new List<string> { "frame start", "until Present" };
        private static readonly Dictionary<int, CommandBuffer> markerBuffers = new Dictionary<int, CommandBuffer>();
        // What was last laid out at a camera's or light's event: the foreign buffers in order.
        private static readonly Dictionary<string, CommandBuffer[]> layouts = new Dictionary<string, CommandBuffer[]>();
        private static readonly Dictionary<MethodBase, int[]> scriptMarkers = new Dictionary<MethodBase, int[]>();
        private static HarmonyLib.Harmony harmony;

        private sealed class Segment
        {
            public double Sum, Most;
            public int Frames;
        }

        private readonly Dictionary<int, Segment> segments = new Dictionary<int, Segment>();
        private readonly int[] readIds = new int[Capacity];
        private readonly float[] readMs = new float[Capacity];
        private int lastFrame, frames, spikesLogged;
        private double typical;
        private readonly List<KeyValuePair<double, int>> spikeParts = new List<KeyValuePair<double, int>>();
        private double frameSum, frameMost;
        private float nextScan, started;

        private void Awake()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), Argument) < 0)
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
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-redefinitionProbeNoProbeShadows") >= 0)
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

        private static ShadowQuality? heldShadows;

        private static void ProbeShadowsOff(Camera camera)
        {
            if (camera == null || camera.cameraType != CameraType.Reflection || heldShadows.HasValue) return;
            heldShadows = QualitySettings.shadows;
            QualitySettings.shadows = ShadowQuality.Disable;
        }

        private static void ProbeShadowsBack(Camera camera)
        {
            if (camera == null || camera.cameraType != CameraType.Reflection || !heldShadows.HasValue) return;
            QualitySettings.shadows = heldShadows.Value;
            heldShadows = null;
        }

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

        // A marker in a command buffer of ReDefinition's own, or here in the render stream,
        // that ends the segment named label; nothing without the profiler.
        internal static void Mark(CommandBuffer buffer, string label)
        {
            if (markerFunc != IntPtr.Zero) buffer.IssuePluginEvent(markerFunc, Id(label));
        }

        internal static void MarkNow(string label)
        {
            if (markerFunc != IntPtr.Zero) GL.IssuePluginEvent(markerFunc, Id(label));
        }

        // The last frame read, for ModCost: its GPU time from Present to Present, and how
        // many frames were read so far.
        internal static double LastFrameMs;
        internal static int FramesRead;

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

        private static CommandBuffer Marker(int id)
        {
            CommandBuffer buffer;
            if (markerBuffers.TryGetValue(id, out buffer)) return buffer;
            buffer = new CommandBuffer { name = OwnBufferName };
            buffer.IssuePluginEvent(markerFunc, id);
            markerBuffers[id] = buffer;
            return buffer;
        }

        private static string BufferName(CommandBuffer buffer)
        {
            return string.IsNullOrEmpty(buffer.name) ? "(unnamed command buffer)" : buffer.name;
        }

        // Each event of each camera, laid out again where its foreign buffers changed:
        // all taken off, then a marker for Unity's work up to the event, and each foreign
        // buffer followed by a marker of its own.
        private static void LayOutCamera(Camera camera)
        {
            string name = camera.name;
            foreach (CameraEvent at in CameraEvents)
            {
                CommandBuffer[] present = camera.GetCommandBuffers(at);
                CommandBuffer[] foreign = present.Where(b => b.name != OwnBufferName).ToArray();
                string key = camera.GetInstanceID() + "/" + at;
                CommandBuffer[] last;
                if (layouts.TryGetValue(key, out last) && last.SequenceEqual(foreign) && present.Length == 2 * foreign.Length + 1) continue;
                layouts[key] = foreign;
                camera.RemoveCommandBuffers(at);
                camera.AddCommandBuffer(at, Marker(Id(name + ": Unity until " + at)));
                foreach (CommandBuffer buffer in foreign)
                {
                    camera.AddCommandBuffer(at, buffer);
                    camera.AddCommandBuffer(at, Marker(Id(name + ": " + at + " " + BufferName(buffer))));
                }
            }
        }

        private static void LayOutLight(Light light)
        {
            string name = "light " + light.name;
            foreach (LightEvent at in LightEvents)
            {
                CommandBuffer[] present = light.GetCommandBuffers(at);
                CommandBuffer[] foreign = present.Where(b => b.name != OwnBufferName).ToArray();
                string key = light.GetInstanceID() + "/" + at;
                CommandBuffer[] last;
                if (layouts.TryGetValue(key, out last) && last.SequenceEqual(foreign) && present.Length == 2 * foreign.Length + 1) continue;
                layouts[key] = foreign;
                light.RemoveCommandBuffers(at);
                light.AddCommandBuffer(at, Marker(Id(name + ": Unity until " + at)));
                foreach (CommandBuffer buffer in foreign)
                {
                    light.AddCommandBuffer(at, buffer);
                    light.AddCommandBuffer(at, Marker(Id(name + ": " + at + " " + BufferName(buffer))));
                }
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

        private static void Before(MethodBase __originalMethod)
        {
            int[] marker;
            if (scriptMarkers.TryGetValue(__originalMethod, out marker)) GL.IssuePluginEvent(markerFunc, marker[0]);
        }

        private static void After(MethodBase __originalMethod)
        {
            int[] marker;
            if (scriptMarkers.TryGetValue(__originalMethod, out marker)) GL.IssuePluginEvent(markerFunc, marker[1]);
        }

        private void Scan()
        {
            foreach (Camera camera in Resources.FindObjectsOfTypeAll<Camera>())
                if (camera != null && camera.gameObject.scene.IsValid())
                    LayOutCamera(camera);
            foreach (Light light in Resources.FindObjectsOfTypeAll<Light>())
                if (light != null && light.gameObject.scene.IsValid() && (light.type == LightType.Directional || light.shadows != LightShadows.None))
                    LayOutLight(light);
            int handlers = PatchCameraHandlers();
            if (handlers > 0) Debug.Log(Log.Tag + " GPU profiler: " + handlers + " more camera event handlers marked.");
        }

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

        private static string LabelOf(int id)
        {
            if (id >= NativeBase && id < NativeBase + NativeLabels.Length) return NativeLabels[id - NativeBase];
            return id >= 0 && id < labels.Count ? labels[id] : "marker " + id;
        }

        private static string Ms(double value)
        {
            return value.ToString("0.000", CultureInfo.InvariantCulture);
        }

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
