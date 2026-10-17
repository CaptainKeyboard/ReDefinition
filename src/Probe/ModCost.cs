using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Probe
{
    // With -redefinitionProbeModCost, in a probe flight left to run
    // (-redefinitionProbeStay, with -redefinitionGpuProfile): what each graphics mod
    // costs the frame, by switching it off for a while. Much of a mod's work lies inside
    // Unity's own passes -- its terrain, scatters, ocean and shaders in the G-buffer and
    // the shadows -- where no marker can split it from the rest; the difference with the
    // mod off can. One after the other, each mod's behaviours, the renderers drawing
    // with its shaders and its command buffers on the cameras and lights are switched
    // off and on in turn, on, off, on, off, on, the frame measured over the last 5 s of
    // each and the off windows set against the on windows around them:
    // the GPU's frame from Present to Present (GpuProfiler), the frame's time, and the
    // main thread's and the GPU's load (the proxy's LoadMonitor). Then KSP quits. A
    // probe: switching a mod off and on mid-flight is nothing a game does, and what a
    // mod leaves broken afterwards shows in the "back on" rows.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal sealed class ModCost : MonoBehaviour
    {
        internal const string Argument = "-redefinitionProbeModCost";
        private const float Settle = 3f;
        private const float Measure = 5f;

        [DllImport("dxgi.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspPerfLoad(out float mainThread, out float renderThread, out float gpu, out float gpuThisProcess);

        private sealed class Target
        {
            public string Name;
            public Func<MonoBehaviour, bool> Behaviour;
            public string[] ShaderPrefixes = new string[0];
            public string[] BufferKeywords = new string[0];
        }

        private static bool InAssembly(MonoBehaviour b, params string[] names)
        {
            string assembly = b.GetType().Assembly.GetName().Name;
            return names.Any(n => assembly.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static readonly Target[] Targets =
        {
            // Only the scatters: Parallax's terrain subdivision (JobifiedSubdivision) throws
            // once switched off and on.
            new Target
            {
                Name = "Parallax scatters", Behaviour = b => InAssembly(b, "Parallax") && b.GetType().Name.IndexOf("Scatter", StringComparison.Ordinal) >= 0,
            },
            new Target
            {
                Name = "EVE (clouds and their shadows)", Behaviour = b => b.GetType().Namespace == "Atmosphere" || InAssembly(b, "EVEManager"),
                ShaderPrefixes = new[] { "EVE/" }, BufferKeywords = new[] { "EVE" },
            },
            new Target
            {
                Name = "Scatterer (atmosphere, ocean, godrays)", Behaviour = b => InAssembly(b, "scatterer"),
                ShaderPrefixes = new[] { "Scatterer/", "Proland/" }, BufferKeywords = new[] { "Scatterer", "scatterer" },
            },
            new Target { Name = "Deferred (its passes; its shaders stay)", Behaviour = b => InAssembly(b, "Deferred"), BufferKeywords = new[] { "Deferred" } },
            new Target
            {
                Name = "TUFX (post-processing)", Behaviour = b => InAssembly(b, "TUFX") || b.GetType().Name == "PostProcessLayer",
                BufferKeywords = new[] { "Post-processing" },
            },
            new Target { Name = "Waterfall", Behaviour = b => InAssembly(b, "Waterfall"), ShaderPrefixes = new[] { "Waterfall/" } },
            new Target { Name = "Firefly", Behaviour = b => InAssembly(b, "Firefly") },
            new Target { Name = "DistantObject", Behaviour = b => InAssembly(b, "DistantObject") },
        };

        private sealed class Off
        {
            public readonly List<Behaviour> Behaviours = new List<Behaviour>();
            public readonly List<Renderer> Renderers = new List<Renderer>();
            public readonly List<KeyValuePair<Camera, KeyValuePair<CameraEvent, CommandBuffer>>> CameraBuffers =
                new List<KeyValuePair<Camera, KeyValuePair<CameraEvent, CommandBuffer>>>();
            public readonly List<KeyValuePair<Light, KeyValuePair<LightEvent, CommandBuffer>>> LightBuffers =
                new List<KeyValuePair<Light, KeyValuePair<LightEvent, CommandBuffer>>>();
        }

        private void Start()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), Argument) < 0) return;
            Application.logMessageReceived += CountExceptions;
            StartCoroutine(Run());
        }

        private static Off SwitchOff(Target target)
        {
            var off = new Off();
            foreach (MonoBehaviour b in FindObjectsOfType<MonoBehaviour>())
                if (b != null && b.enabled && !(b is ModCost) && !(b is GpuProfiler) && target.Behaviour(b))
                {
                    b.enabled = false;
                    off.Behaviours.Add(b);
                }
            if (target.ShaderPrefixes.Length > 0)
                foreach (Renderer r in FindObjectsOfType<Renderer>())
                    if (r != null && r.enabled && r.sharedMaterials.Any(m => m != null && m.shader != null
                                                                          && target.ShaderPrefixes.Any(p => m.shader.name.StartsWith(p, StringComparison.Ordinal))))
                    {
                        r.enabled = false;
                        off.Renderers.Add(r);
                    }
            if (target.BufferKeywords.Length > 0)
            {
                foreach (Camera camera in Resources.FindObjectsOfTypeAll<Camera>())
                {
                    if (camera == null || !camera.gameObject.scene.IsValid()) continue;
                    foreach (CameraEvent at in (CameraEvent[])Enum.GetValues(typeof(CameraEvent)))
                        foreach (CommandBuffer buffer in camera.GetCommandBuffers(at))
                            if (buffer.name != null && target.BufferKeywords.Any(k => buffer.name.Contains(k)))
                            {
                                camera.RemoveCommandBuffer(at, buffer);
                                off.CameraBuffers.Add(new KeyValuePair<Camera, KeyValuePair<CameraEvent, CommandBuffer>>(
                                    camera, new KeyValuePair<CameraEvent, CommandBuffer>(at, buffer)));
                            }
                }
                foreach (Light light in Resources.FindObjectsOfTypeAll<Light>())
                {
                    if (light == null || !light.gameObject.scene.IsValid()) continue;
                    foreach (LightEvent at in (LightEvent[])Enum.GetValues(typeof(LightEvent)))
                        foreach (CommandBuffer buffer in light.GetCommandBuffers(at))
                            if (buffer.name != null && target.BufferKeywords.Any(k => buffer.name.Contains(k)))
                            {
                                light.RemoveCommandBuffer(at, buffer);
                                off.LightBuffers.Add(new KeyValuePair<Light, KeyValuePair<LightEvent, CommandBuffer>>(
                                    light, new KeyValuePair<LightEvent, CommandBuffer>(at, buffer)));
                            }
                }
            }
            return off;
        }

        private static void SwitchOn(Off off)
        {
            foreach (KeyValuePair<Camera, KeyValuePair<CameraEvent, CommandBuffer>> entry in off.CameraBuffers)
                if (entry.Key != null) entry.Key.AddCommandBuffer(entry.Value.Key, entry.Value.Value);
            foreach (KeyValuePair<Light, KeyValuePair<LightEvent, CommandBuffer>> entry in off.LightBuffers)
                if (entry.Key != null) entry.Key.AddCommandBuffer(entry.Value.Key, entry.Value.Value);
            foreach (Renderer r in off.Renderers)
                if (r != null) r.enabled = true;
            foreach (Behaviour b in off.Behaviours)
                if (b != null) b.enabled = true;
        }

        private sealed class Window
        {
            public double Gpu, FrameMs;
            public float MainThread, GpuLoad;
            public int Frames;
        }

        private static IEnumerator MeasureInto(Window window)
        {
            yield return new WaitForSecondsRealtime(Settle);
            float until = Time.realtimeSinceStartup + Measure;
            int lastRead = GpuProfiler.FramesRead;
            double gpu = 0.0, frameMs = 0.0;
            int gpuFrames = 0, frames = 0;
            while (Time.realtimeSinceStartup < until)
            {
                yield return null;
                frames++;
                frameMs += Time.unscaledDeltaTime * 1000.0;
                if (GpuProfiler.FramesRead != lastRead)
                {
                    lastRead = GpuProfiler.FramesRead;
                    gpu += GpuProfiler.LastFrameMs;
                    gpuFrames++;
                }
            }
            window.Frames = frames;
            window.FrameMs = frames > 0 ? frameMs / frames : 0.0;
            window.Gpu = gpuFrames > 0 ? gpu / gpuFrames : 0.0;
            try
            {
                float render, process;
                KspPerfLoad(out window.MainThread, out render, out window.GpuLoad, out process);
            }
            catch (Exception)
            {
                window.MainThread = window.GpuLoad = -1f;
            }
        }

        private static Window Mean(List<Window> windows)
        {
            var mean = new Window();
            foreach (Window w in windows)
            {
                mean.Gpu += w.Gpu / windows.Count;
                mean.FrameMs += w.FrameMs / windows.Count;
                mean.MainThread += w.MainThread / windows.Count;
                mean.GpuLoad += w.GpuLoad / windows.Count;
                mean.Frames += w.Frames;
            }
            return mean;
        }

        // Exceptions logged meanwhile, of anyone's, as what a switch may break.
        private static int ExceptionCount;

        private static void CountExceptions(string message, string stack, LogType type)
        {
            if (type == LogType.Exception) ExceptionCount++;
        }

        private void OnDestroy()
        {
            Application.logMessageReceived -= CountExceptions;
        }

        private static string Row(string name, Window w, Window baseline)
        {
            string delta = baseline == null ? "" : string.Format(CultureInfo.InvariantCulture, "  (GPU {0:+0.00;-0.00} ms, frame {1:+0.00;-0.00} ms)",
                                                                 w.Gpu - baseline.Gpu, w.FrameMs - baseline.FrameMs);
            return string.Format(CultureInfo.InvariantCulture, "\n    {0,-48} GPU {1,6:0.00} ms  frame {2,6:0.00} ms  main thread {3,3:0} %  GPU load {4,3:0} %{5}",
                                 name, w.Gpu, w.FrameMs, w.MainThread, w.GpuLoad, delta);
        }

        private IEnumerator Run()
        {
            while (!FlightGlobals.ready || FlightGlobals.ActiveVessel == null) yield return null;
            // The flight settled first: the scene's loading and the first builds past.
            yield return new WaitForSecondsRealtime(20f);
            var text = new StringBuilder(Log.Tag + " Mod cost (" + Measure + " s each, after " + Settle + " s to settle):");
            var first = new Window();
            yield return MeasureInto(first);
            text.Append(Row("everything on", first, null));
            foreach (Target target in Targets)
            {
                var on = new List<Window>();
                var offs = new List<Window>();
                int switched = -1;
                string counts = "";
                int exceptions = ExceptionCount;
                for (int round = 0; round < 5; round++)
                {
                    var window = new Window();
                    if (round % 2 == 0)
                    {
                        yield return MeasureInto(window);
                        on.Add(window);
                        continue;
                    }
                    Off off = SwitchOff(target);
                    switched = off.Behaviours.Count + off.Renderers.Count + off.CameraBuffers.Count + off.LightBuffers.Count;
                    counts = off.Behaviours.Count + " behaviours, " + off.Renderers.Count + " renderers, "
                             + (off.CameraBuffers.Count + off.LightBuffers.Count) + " command buffers";
                    if (switched == 0) break;
                    yield return MeasureInto(window);
                    offs.Add(window);
                    SwitchOn(off);
                }
                if (switched == 0)
                {
                    text.Append("\n    ").Append(target.Name).Append(": nothing of it found");
                    continue;
                }
                Window onMean = Mean(on), offMean = Mean(offs);
                text.Append(Row(target.Name + " on", onMean, null)).Append("  (on windows ")
                    .Append(string.Join(", ", on.Select(w => w.Gpu.ToString("0.0", CultureInfo.InvariantCulture)).ToArray())).Append(")");
                text.Append(Row(target.Name + " off", offMean, onMean)).Append("  (off windows ")
                    .Append(string.Join(", ", offs.Select(w => w.Gpu.ToString("0.0", CultureInfo.InvariantCulture)).ToArray())).Append(")");
                text.Append("\n      (").Append(counts).Append(" switched; ").Append(ExceptionCount - exceptions).Append(" exceptions meanwhile)");
            }
            Debug.Log(text.ToString());
            yield return new WaitForSecondsRealtime(1f);
            Application.Quit();
        }
    }
}
