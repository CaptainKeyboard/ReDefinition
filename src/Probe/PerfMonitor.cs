using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using ReDefinition.Core;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ReDefinition.Probe
{
    // With -redefinitionPerfMonitor: what ReDefinition costs the main thread, without
    // switching anything off. KSP's player is a release build, which Unity's profiler
    // does not attach to, so each method of ReDefinition that Unity calls every frame --
    // its MonoBehaviours' Update, LateUpdate, FixedUpdate, OnGUI, OnPreCull, OnPreRender,
    // OnPostRender, OnRenderImage, OnWillRenderObject, and what it hangs on
    // Camera.onPreCull, onPreRender and onPostRender -- is timed by a Harmony prefix and
    // postfix; every 10 s the most costly, per frame on average and at most, and their
    // sum against the frame time into the log.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    internal sealed class PerfMonitor : MonoBehaviour
    {
        // The switch that turns the monitor on, how often it writes to the log (s), and the Unity messages it times.
        internal const string Argument = "-redefinitionPerfMonitor";
        private const float Period = 10f;
        private static readonly string[] Messages =
        {
            "Update", "LateUpdate", "FixedUpdate", "OnGUI", "OnPreCull", "OnPreRender", "OnPostRender", "OnRenderImage",
            "OnWillRenderObject",
        };

        // One timed method's sums: over the period, in the current frame and in the period's most costly frame.
        private sealed class Cost
        {
            // Its name as logged, its ticks (period, this frame, most in a frame) and its calls in the period.
            public string Name;
            public long Ticks, FrameTicks, MostFrameTicks;
            public int Calls;
        }

        // Every timed method's sums, by the method.
        private static readonly Dictionary<MethodBase, Cost> costs = new Dictionary<MethodBase, Cost>();
        // Unity's samplers: each one's recorder and its sum and largest over the period, in ns.
        private static readonly Dictionary<string, UnityEngine.Profiling.Recorder> unitySamplers =
            new Dictionary<string, UnityEngine.Profiling.Recorder>();
        private static readonly Dictionary<string, long[]> unitySums = new Dictionary<string, long[]>();
        private static readonly HashSet<MethodBase> patched = new HashSet<MethodBase>();
        private static HarmonyLib.Harmony harmony;
        private static bool withAssembly;
        private int frames;
        private float frameSum, started;

        // KSP's own phases, by its TimingManager's first and last stages: from the earliest
        // FixedUpdate to the end of the physics step after it (WaitForFixedUpdate), summed
        // over the frame's steps; and from the earliest Update to the latest LateUpdate.
        private static bool phasesHooked;
        private static long fixedStart, physicsTicks, updateStart, updateTicks;
        private static int physicsSteps;
        private float nextCameraLook;

        // Off without the switch; with it, every per-frame method of ReDefinition (and, by the further switches, of
        // the other mods, of chosen classes and of Unity's samplers) patched to be timed.
        private void Awake()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), Argument) < 0)
            {
                Destroy(this);
                return;
            }
            DontDestroyOnLoad(this);
            harmony = new HarmonyLib.Harmony("ReDefinition.PerfMonitor");
            int count = 0;
            // -redefinitionPerfMonitorAll: every loaded mod's and KSP's MonoBehaviours too,
            // their names led by their assembly's, to see whose scripts the frame waits for.
            var assemblies = new List<Assembly> { typeof(PerfMonitor).Assembly };
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-redefinitionPerfMonitorAll") >= 0)
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string name = assembly.GetName().Name;
                    if (assembly == typeof(PerfMonitor).Assembly || name.StartsWith("UnityEngine", StringComparison.Ordinal)
                        || name.StartsWith("Unity.", StringComparison.Ordinal) || name.StartsWith("System", StringComparison.Ordinal)
                        || name == "mscorlib" || name == "0Harmony" || assembly.IsDynamic)
                        continue;
                    assemblies.Add(assembly);
                    withAssembly = true;
                }
            var types = new List<Type>();
            foreach (Assembly assembly in assemblies)
            {
                try
                {
                    types.AddRange(assembly.GetTypes());
                }
                catch (ReflectionTypeLoadException e)
                {
                    foreach (Type t in e.Types)
                        if (t != null) types.Add(t);
                }
            }
            foreach (Type type in types)
            {
                // A MonoBehaviour's coroutines, each step (MoveNext) of the class the compiler
                // makes for it: Unity times them all as one, CoroutinesDelayedCalls.
                if (withAssembly && type.DeclaringType != null && typeof(MonoBehaviour).IsAssignableFrom(type.DeclaringType)
                    && type.Name.StartsWith("<", StringComparison.Ordinal) && typeof(System.Collections.IEnumerator).IsAssignableFrom(type)
                    && !type.ContainsGenericParameters)
                {
                    MethodInfo step = type.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                                                                 | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                    if (step != null && Patch(step)) count++;
                    continue;
                }
                if (!typeof(MonoBehaviour).IsAssignableFrom(type) || type.ContainsGenericParameters || type == typeof(PerfMonitor)) continue;
                foreach (string message in Messages)
                {
                    MethodInfo method = type.GetMethod(message, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                                                               | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                    if (method != null && Patch(method)) count++;
                }
            }
            // -redefinitionPerfMonitorTypes A,B: every method of those classes of
            // ReDefinition too, to see inside the costly ones (times include the calls
            // they make).
            string[] args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, "-redefinitionPerfMonitorTypes");
            if (at >= 0 && at + 1 < args.Length)
                foreach (string entry in args[at + 1].Split(','))
                {
                    // "Type" for every method of it, "Type.Method" for one.
                    int dot = entry.IndexOf('.');
                    string name = dot < 0 ? entry : entry.Substring(0, dot);
                    string only = dot < 0 ? null : entry.Substring(dot + 1);
                    foreach (Type type in typeof(PerfMonitor).Assembly.GetTypes())
                        if (type.Name == name && !type.ContainsGenericParameters)
                            foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                                                                          | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                                if ((only == null || method.Name == only) && !method.IsSpecialName && method.GetMethodBody() != null
                                    && !typeof(System.Collections.IEnumerator).IsAssignableFrom(method.ReturnType) && Patch(method))
                                    count++;
                }
            started = Time.realtimeSinceStartup;
            if (withAssembly) PatchPortraits();
            Debug.Log(Log.Tag + " Perf monitor: " + count + " of ReDefinition's per-frame methods timed.");
            // -redefinitionPerfMonitorUnity: Unity's own profiler samplers as well --
            // culling, rendering, the canvases, physics, the player loop's phases --
            // where this player's build lets them be read (Recorder.isValid).
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-redefinitionPerfMonitorUnity") >= 0)
            {
                var names = new List<string>();
                UnityEngine.Profiling.Sampler.GetNames(names);
                foreach (string name in names)
                {
                    UnityEngine.Profiling.Recorder recorder = UnityEngine.Profiling.Recorder.Get(name);
                    if (recorder == null || !recorder.isValid) continue;
                    // The main thread's time alone (this is it); unfiltered a sampler sums
                    // the worker and render threads' as well.
                    recorder.FilterToCurrentThread();
                    recorder.enabled = true;
                    unitySamplers[name] = recorder;
                }
                Debug.Log(Log.Tag + " Perf monitor: " + unitySamplers.Count + " of Unity's " + names.Count + " samplers readable in this build.");
            }
            // -redefinitionUnityProfilerLog <file>: Unity's profiler records into that file
            // (.raw), for its Profiler window in the editor (Load). A development player
            // only (tools/unity_profile.ps1).
            string profilerLog = SceneProbe.ArgumentAfter("-redefinitionUnityProfilerLog");
            if (profilerLog != null && UnityEngine.Profiling.Profiler.supported)
            {
                UnityEngine.Profiling.Profiler.logFile = profilerLog;
                UnityEngine.Profiling.Profiler.enableBinaryLog = true;
                UnityEngine.Profiling.Profiler.maxUsedMemory = 256 * 1024 * 1024;
                UnityEngine.Profiling.Profiler.enabled = true;
                Debug.Log(Log.Tag + " Perf monitor: Unity's profiler records into " + profilerLog + ".");
            }
        }

        // Times one method with Before and After, once; false where it cannot be patched or already is.
        private static bool Patch(MethodBase method)
        {
            if (method == null || method.IsAbstract || method.ContainsGenericParameters || !patched.Add(method)) return false;
            try
            {
                harmony.Patch(method, new HarmonyLib.HarmonyMethod(typeof(PerfMonitor).GetMethod(nameof(Before), BindingFlags.Static | BindingFlags.NonPublic)),
                              new HarmonyLib.HarmonyMethod(typeof(PerfMonitor).GetMethod(nameof(After), BindingFlags.Static | BindingFlags.NonPublic)));
                return true;
            }
            catch (Exception e)
            {
                Debug.Log(Log.Tag + " Perf monitor: " + method.DeclaringType?.Name + "." + method.Name + " not timed (" + e.Message + ").");
                return false;
            }
        }

        // ReDefinition's handlers on the cameras' events, as they come: looked for again now
        // and then, as they are added with the scenes.
        private static int PatchCameraEvents()
        {
            int count = 0;
            Assembly own = typeof(PerfMonitor).Assembly;
            foreach (Camera.CameraCallback callback in new[] { Camera.onPreCull, Camera.onPreRender, Camera.onPostRender })
                if (callback != null)
                    foreach (Delegate handler in callback.GetInvocationList())
                        if (handler.Method.DeclaringType != null && (withAssembly || handler.Method.DeclaringType.Assembly == own) && handler.Method.DeclaringType != typeof(PerfMonitor) && Patch(handler.Method))
                            count++;
            return count;
        }

        // Harmony prefix of every timed method: the start time, handed to the postfix.
        private static void Before(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        // Harmony postfix: the call's ticks added to its method's sums, and to the portrait's while one renders.
        private static void After(long __state, MethodBase __originalMethod)
        {
            long ticks = Stopwatch.GetTimestamp() - __state;
            Cost cost;
            if (!costs.TryGetValue(__originalMethod, out cost))
                costs[__originalMethod] = cost = new Cost
                {
                    Name = (withAssembly && __originalMethod.DeclaringType != null ? __originalMethod.DeclaringType.Assembly.GetName().Name + ": " : "")
                           + (__originalMethod.DeclaringType?.DeclaringType != null ? __originalMethod.DeclaringType.DeclaringType.Name + "." : "")
                           + __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name,
                };
            cost.Ticks += ticks;
            cost.FrameTicks += ticks;
            cost.Calls++;
            // Inside a crew portrait's render as well (InPortrait).
            if (portraitDepth > 0 && !IsPortraitMethod(__originalMethod))
            {
                long sum;
                portraitCosts.TryGetValue(cost.Name, out sum);
                portraitCosts[cost.Name] = sum + ticks;
            }
        }

        // KSP's crew portraits (Kerbal.kerbalAvatarUpdate, kerbalSeatCamUpdate), timed with
        // what runs inside them: each renders a camera, and every camera's hooks of every
        // mod run with it. With -redefinitionPerfMonitorAll.
        private static int portraitDepth;
        private static long portraitTicks;
        private static int portraitRenders;
        private static readonly Dictionary<string, long> portraitCosts = new Dictionary<string, long>();

        // Whether a method is one of the two portrait updates themselves, kept out of their own breakdown.
        private static bool IsPortraitMethod(MethodBase method)
        {
            return method.DeclaringType == typeof(Kerbal) && (method.Name == "kerbalAvatarUpdate" || method.Name == "kerbalSeatCamUpdate");
        }

        // Whether the first portrait camera's settings are in the log already.
        private static bool portraitLogged;

        // Harmony prefix of the portrait updates: logs the first portrait camera's settings once, then starts the timing.
        private static void PortraitBefore(Kerbal __instance, MethodBase __originalMethod, out long __state)
        {
            if (!portraitLogged)
            {
                portraitLogged = true;
                Camera camera = __originalMethod.Name == "kerbalSeatCamUpdate"
                    ? __instance.protoCrewMember?.seat?.portraitCamera
                    : HarmonyLib.Traverse.Create(__instance).Field("kerbalCam").GetValue<Camera>();
                if (camera != null)
                    Debug.Log(Log.Tag + " Perf monitor: a crew portrait's camera " + camera.name + " (" + __originalMethod.Name + "): mask 0x"
                              + camera.cullingMask.ToString("X8") + ", path " + camera.actualRenderingPath + ", HDR " + camera.allowHDR
                              + ", MSAA " + camera.allowMSAA + ", depth texture " + camera.depthTextureMode + ", occlusion culling "
                              + camera.useOcclusionCulling + ", far " + camera.farClipPlane + ", target "
                              + (camera.targetTexture != null ? camera.targetTexture.width + "x" + camera.targetTexture.height + " " + camera.targetTexture.format
                                                                + " AA " + camera.targetTexture.antiAliasing : "none"));
            }
            portraitDepth++;
            __state = Stopwatch.GetTimestamp();
        }

        // Harmony postfix of the portrait updates: the render's ticks into the portraits' sum.
        private static void PortraitAfter(long __state)
        {
            portraitDepth--;
            portraitTicks += Stopwatch.GetTimestamp() - __state;
            portraitRenders++;
        }

        // Hangs PortraitBefore and PortraitAfter on both portrait updates of Kerbal.
        private static void PatchPortraits()
        {
            foreach (string name in new[] { "kerbalAvatarUpdate", "kerbalSeatCamUpdate" })
            {
                MethodInfo method = typeof(Kerbal).GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (method == null) continue;
                harmony.Patch(method, new HarmonyLib.HarmonyMethod(typeof(PerfMonitor).GetMethod(nameof(PortraitBefore), BindingFlags.Static | BindingFlags.NonPublic)),
                              new HarmonyLib.HarmonyMethod(typeof(PerfMonitor).GetMethod(nameof(PortraitAfter), BindingFlags.Static | BindingFlags.NonPublic)));
            }
        }

        // After each physics step (WaitForFixedUpdate): the step's time since the earliest FixedUpdate added to the frame's.
        private System.Collections.IEnumerator AfterPhysics()
        {
            var wait = new WaitForFixedUpdate();
            while (true)
            {
                yield return wait;
                if (fixedStart != 0)
                {
                    physicsTicks += Stopwatch.GetTimestamp() - fixedStart;
                    physicsSteps++;
                    fixedStart = 0;
                }
            }
        }

        // Hangs the phase timers on KSP's TimingManager, once it exists, and starts AfterPhysics.
        private void HookPhases()
        {
            if (phasesHooked) return;
            try
            {
                TimingManager.FixedUpdateAdd(TimingManager.TimingStage.ObscenelyEarly, () => fixedStart = Stopwatch.GetTimestamp());
                TimingManager.UpdateAdd(TimingManager.TimingStage.ObscenelyEarly, () => updateStart = Stopwatch.GetTimestamp());
                TimingManager.LateUpdateAdd(TimingManager.TimingStage.BetterLateThanNever, () =>
                {
                    if (updateStart != 0) updateTicks += Stopwatch.GetTimestamp() - updateStart;
                    updateStart = 0;
                });
                StartCoroutine(AfterPhysics());
                phasesHooked = true;
            }
            catch (Exception)
            {
                // No TimingManager yet: tried again next frame.
            }
        }

        // Last in the frame's scripts as far as it goes: the frame's sums closed, camera handlers looked for every 5 s,
        // and every period the most costly methods, KSP's phases, Unity's samplers and the portraits into the log.
        private void LateUpdate()
        {
            HookPhases();
            if (Time.realtimeSinceStartup >= nextCameraLook)
            {
                nextCameraLook = Time.realtimeSinceStartup + 5f;
                int found = PatchCameraEvents();
                if (found > 0) Debug.Log(Log.Tag + " Perf monitor: " + found + " camera event handlers timed.");
            }
            frames++;
            frameSum += Time.unscaledDeltaTime;
            // The frame before's time of each sampler, on the main thread.
            foreach (KeyValuePair<string, UnityEngine.Profiling.Recorder> pair in unitySamplers)
            {
                long ns = pair.Value.elapsedNanoseconds;
                if (ns <= 0) continue;
                long[] sum;
                if (!unitySums.TryGetValue(pair.Key, out sum)) unitySums[pair.Key] = sum = new long[3];
                sum[0] += ns;
                if (ns > sum[1]) sum[1] = ns;
                sum[2] += pair.Value.sampleBlockCount;
            }
            foreach (Cost cost in costs.Values)
            {
                if (cost.FrameTicks > cost.MostFrameTicks) cost.MostFrameTicks = cost.FrameTicks;
                cost.FrameTicks = 0;
            }
            if (Time.realtimeSinceStartup - started < Period || frames == 0) return;

            double toMs = 1000.0 / Stopwatch.Frequency;
            double total = costs.Values.Sum(c => (double)c.Ticks) * toMs / frames;
            var text = new StringBuilder(Log.Tag + " Perf monitor: " + HighLogic.LoadedScene + ", " + frames + " frames of "
                                         + (frameSum / frames * 1000f).ToString("0.00", CultureInfo.InvariantCulture) + " ms; ReDefinition's scripts "
                                         + total.ToString("0.000", CultureInfo.InvariantCulture) + " ms a frame:");
            foreach (Cost cost in costs.Values.Where(c => c.Calls > 0).OrderByDescending(c => c.Ticks).Take(withAssembly ? 40 : 25))
                text.Append("\n    ").Append(cost.Name).Append(' ')
                    .Append((cost.Ticks * toMs / frames).ToString("0.000", CultureInfo.InvariantCulture)).Append(" ms a frame, at most ")
                    .Append((cost.MostFrameTicks * toMs).ToString("0.00", CultureInfo.InvariantCulture)).Append(" ms, ")
                    .Append(cost.Calls).Append(" calls");
            text.Append("\n    KSP's phases: FixedUpdate and physics ").Append((physicsTicks * toMs / frames).ToString("0.00", CultureInfo.InvariantCulture))
                .Append(" ms a frame (").Append((physicsSteps / (float)frames).ToString("0.00", CultureInfo.InvariantCulture))
                .Append(" steps a frame), Update to LateUpdate ").Append((updateTicks * toMs / frames).ToString("0.00", CultureInfo.InvariantCulture))
                .Append(" ms a frame");
            if (unitySums.Count > 0)
            {
                text.Append("\n    Unity's samplers, per frame on average, at most, calls a frame:");
                foreach (KeyValuePair<string, long[]> pair in unitySums.OrderByDescending(p => p.Value[0]).Take(45))
                    text.Append("\n      ").Append((pair.Value[0] / 1e6 / frames).ToString("0.000", CultureInfo.InvariantCulture)).Append(" ms, at most ")
                        .Append((pair.Value[1] / 1e6).ToString("0.00", CultureInfo.InvariantCulture)).Append(", ")
                        .Append((pair.Value[2] / (float)frames).ToString("0.0", CultureInfo.InvariantCulture)).Append(": ").Append(pair.Key);
                unitySums.Clear();
            }
            if (portraitRenders > 0)
            {
                text.Append("\n    KSP's crew portraits: ").Append(portraitRenders).Append(" renders, ")
                    .Append((portraitTicks * toMs / portraitRenders).ToString("0.00", CultureInfo.InvariantCulture)).Append(" ms each; inside them:");
                foreach (KeyValuePair<string, long> pair in portraitCosts.OrderByDescending(p => p.Value).Take(15))
                    text.Append("\n      ").Append((pair.Value * toMs / portraitRenders).ToString("0.000", CultureInfo.InvariantCulture))
                        .Append(" ms a render: ").Append(pair.Key);
                portraitTicks = 0;
                portraitRenders = 0;
                portraitCosts.Clear();
            }
            physicsTicks = updateTicks = 0;
            physicsSteps = 0;
            Debug.Log(text.ToString());
            foreach (Cost cost in costs.Values)
            {
                cost.Ticks = cost.MostFrameTicks = 0;
                cost.Calls = 0;
            }
            frames = 0;
            frameSum = 0f;
            started = Time.realtimeSinceStartup;
        }
    }
}
