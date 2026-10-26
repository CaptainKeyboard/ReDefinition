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
    // Unity's own passes, such as its terrain, scatters, ocean and shaders in the G-buffer and
    // the shadows. No marker can split that work from the rest, but the difference with the
    // mod off can. One after the other, each mod's behaviours, the renderers drawing
    // with its shaders and its command buffers on the cameras and lights are switched
    // off and on in turn, on, off, on, off, on. The frame is measured over the last 5 s of
    // each and the off windows are set against the on windows around them.
    // The measures are the GPU's frame from Present to Present (GpuProfiler), the frame's time, and the
    // main thread's and the GPU's load (the proxy's LoadMonitor). Then KSP quits. A
    // probe: switching a mod off and on mid-flight is nothing a game does, and what a
    // mod leaves broken afterwards shows in the "back on" rows.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    internal sealed class ModCost : MonoBehaviour
    {
        // The switch; seconds a state settles before it is measured; seconds each state is measured.
        internal const string Argument = "-redefinitionProbeModCost";
        private const float Settle = 3f;
        private const float Measure = 5f;

        // The proxy's load monitor: the main thread's, the render thread's and the GPU's load in per cent, overall and this process's.
        [DllImport(ReDefinition.Proxy.ProxyDll.Name, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspPerfLoad(out float mainThread, out float renderThread, out float gpu, out float gpuThisProcess);

        // One thing switched off and on: a mod, or a setting of KSP's.
        private sealed class Target
        {
            // Its name in the log; which behaviours are its own; the shader name prefixes of the renderers that draw with
            // its shaders; words in the names of its command buffers.
            public string Name;
            public Func<MonoBehaviour, bool> Behaviour;
            public string[] ShaderPrefixes = new string[0];
            public string[] BufferKeywords = new string[0];
            // Something else to switch: off returns how many it switched.
            public Func<int> CustomOff;
            public Action CustomOn;
        }

        // KSP's crew portraits: each IVA kerbal's kerbalAvatarUpdateCycle coroutine renders
        // its portrait camera now and then. Off: all the kerbals' coroutines stopped;
        // on: that one started again.
        private static readonly List<Kerbal> stoppedKerbals = new List<Kerbal>();

        // The sun's shadows: their distance halved, or two cascades in place of four, for
        // the off windows; set back for the on ones.
        private static float heldDistance;
        private static int heldCascades;

        // Off window of "shadow distance halved": the distance kept and halved.
        private static int ShadowDistanceHalved()
        {
            heldDistance = QualitySettings.shadowDistance;
            QualitySettings.shadowDistance = heldDistance * 0.5f;
            return 1;
        }

        // On again: the kept shadow distance back.
        private static void ShadowDistanceBack()
        {
            QualitySettings.shadowDistance = heldDistance;
        }

        // Off window of "two shadow cascades": the count kept and set to two; nothing switched where it is two or less.
        private static int TwoCascades()
        {
            heldCascades = QualitySettings.shadowCascades;
            if (heldCascades <= 2) return 0;
            QualitySettings.shadowCascades = 2;
            return 1;
        }

        // On again: the kept cascade count back.
        private static void CascadesBack()
        {
            QualitySettings.shadowCascades = heldCascades;
        }

        // Off window of the crew portraits: every kerbal's coroutines stopped; how many kerbals.
        private static int PortraitsOff()
        {
            stoppedKerbals.Clear();
            foreach (Kerbal kerbal in FindObjectsOfType<Kerbal>())
            {
                kerbal.StopAllCoroutines();
                stoppedKerbals.Add(kerbal);
            }
            return stoppedKerbals.Count;
        }

        // On again: each stopped kerbal's kerbalAvatarUpdateCycle started anew, found by reflection.
        private static void PortraitsOn()
        {
            System.Reflection.MethodInfo cycle = typeof(Kerbal).GetMethod("kerbalAvatarUpdateCycle",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            foreach (Kerbal kerbal in stoppedKerbals)
                if (kerbal != null && cycle != null)
                    kerbal.StartCoroutine((IEnumerator)cycle.Invoke(kerbal, cycle.GetParameters().Length == 0 ? null : new object[cycle.GetParameters().Length]));
            stoppedKerbals.Clear();
        }

        // Whether the behaviour's assembly name holds one of the names, ignoring case.
        private static bool InAssembly(MonoBehaviour b, params string[] names)
        {
            string assembly = b.GetType().Assembly.GetName().Name;
            return names.Any(n => assembly.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // What is measured, in this order.
        private static readonly Target[] Targets =
        {
            new Target { Name = "KSP's crew portraits", Behaviour = b => false, CustomOff = PortraitsOff, CustomOn = PortraitsOn },
            new Target { Name = "shadow distance halved", Behaviour = b => false, CustomOff = ShadowDistanceHalved, CustomOn = ShadowDistanceBack },
            new Target { Name = "two shadow cascades", Behaviour = b => false, CustomOff = TwoCascades, CustomOn = CascadesBack },
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

        // What one off window switched off, to switch it on again.
        private sealed class Off
        {
            // The behaviours disabled; the target's own switch back on and how many it switched; the renderers disabled;
            // the command buffers taken off the cameras and the lights, with their events.
            public readonly List<Behaviour> Behaviours = new List<Behaviour>();
            public Action Custom;
            public int CustomCount;
            public readonly List<Renderer> Renderers = new List<Renderer>();
            public readonly List<KeyValuePair<Camera, KeyValuePair<CameraEvent, CommandBuffer>>> CameraBuffers =
                new List<KeyValuePair<Camera, KeyValuePair<CameraEvent, CommandBuffer>>>();
            public readonly List<KeyValuePair<Light, KeyValuePair<LightEvent, CommandBuffer>>> LightBuffers =
                new List<KeyValuePair<Light, KeyValuePair<LightEvent, CommandBuffer>>>();
        }

        // Only with the switch: the exception counter on and the run started.
        private void Start()
        {
            if (!CommandLine.Has(Argument)) return;
            Application.logMessageReceived += CountExceptions;
            StartCoroutine(Run());
        }

        // The target switched off: its custom switch, its behaviours, its renderers and its command buffers.
        private static Off SwitchOff(Target target)
        {
            var off = new Off();
            if (target.CustomOff != null)
            {
                off.Custom = target.CustomOn;
                off.CustomCount = target.CustomOff();
            }
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

        // All that SwitchOff took away given back, those destroyed meanwhile left out.
        private static void SwitchOn(Off off)
        {
            if (off.Custom != null) off.Custom();
            foreach (KeyValuePair<Camera, KeyValuePair<CameraEvent, CommandBuffer>> entry in off.CameraBuffers)
                if (entry.Key != null) entry.Key.AddCommandBuffer(entry.Value.Key, entry.Value.Value);
            foreach (KeyValuePair<Light, KeyValuePair<LightEvent, CommandBuffer>> entry in off.LightBuffers)
                if (entry.Key != null) entry.Key.AddCommandBuffer(entry.Value.Key, entry.Value.Value);
            foreach (Renderer r in off.Renderers)
                if (r != null) r.enabled = true;
            foreach (Behaviour b in off.Behaviours)
                if (b != null) b.enabled = true;
        }

        // What one window of Measure seconds came to.
        private sealed class Window
        {
            // The GPU's frame and the frame's time in ms on average; the main thread's and the GPU's load in per cent at
            // its end (-1 without the proxy); the frames in it.
            public double Gpu, FrameMs;
            public float MainThread, GpuLoad;
            public int Frames;
        }

        // A window measured: Settle seconds waited, then Measure seconds of frames, and the proxy's load at the end.
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

        // The windows averaged; their frames summed.
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

        // Log listener that adds one to ExceptionCount for each message Unity's log reports as an exception.
        private static void CountExceptions(string message, string stack, LogType type)
        {
            if (type == LogType.Exception) ExceptionCount++;
        }

        // The log listener off.
        private void OnDestroy()
        {
            Application.logMessageReceived -= CountExceptions;
        }

        // One log row of a window; with a baseline also its GPU and frame differences to it.
        private static string Row(string name, Window w, Window baseline)
        {
            string delta = baseline == null ? "" : string.Format(CultureInfo.InvariantCulture, "  (GPU {0:+0.00;-0.00} ms, frame {1:+0.00;-0.00} ms)",
                                                                 w.Gpu - baseline.Gpu, w.FrameMs - baseline.FrameMs);
            return string.Format(CultureInfo.InvariantCulture, "\n    {0,-48} GPU {1,6:0.00} ms  frame {2,6:0.00} ms  main thread {3,3:0} %  GPU load {4,3:0} %{5}",
                                 name, w.Gpu, w.FrameMs, w.MainThread, w.GpuLoad, delta);
        }

        // The whole probe: everything on measured, then each target on, off, on, off, on, the result logged, KSP quit.
        private IEnumerator Run()
        {
            while (!FlightGlobals.ready || FlightGlobals.ActiveVessel == null) yield return null;
            // The flight settled first: the scene's loading and the first builds past.
            yield return new WaitForSecondsRealtime(20f);
            var text = new StringBuilder(Log.Tag + " Mod cost (" + Measure + " s each, after " + Settle + " s to settle):");
            var first = new Window();
            yield return MeasureInto(first);
            text.Append(Row("everything on", first, null));
            text.Append(string.Format(CultureInfo.InvariantCulture, "\n    (shadow distance {0:0} m, {1} cascades, resolution {2})",
                                      QualitySettings.shadowDistance, QualitySettings.shadowCascades, QualitySettings.shadowResolution));
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
                    switched = off.Behaviours.Count + off.Renderers.Count + off.CameraBuffers.Count + off.LightBuffers.Count + off.CustomCount;
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
