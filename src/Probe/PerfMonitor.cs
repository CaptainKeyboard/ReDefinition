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
        internal const string Argument = "-redefinitionPerfMonitor";
        private const float Period = 10f;
        private static readonly string[] Messages =
        {
            "Update", "LateUpdate", "FixedUpdate", "OnGUI", "OnPreCull", "OnPreRender", "OnPostRender", "OnRenderImage",
            "OnWillRenderObject",
        };

        private sealed class Cost
        {
            public string Name;
            public long Ticks, FrameTicks, MostFrameTicks;
            public int Calls;
        }

        private static readonly Dictionary<MethodBase, Cost> costs = new Dictionary<MethodBase, Cost>();
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
            Debug.Log(Log.Tag + " Perf monitor: " + count + " of ReDefinition's per-frame methods timed.");
        }

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
                        if (handler.Method.DeclaringType != null && handler.Method.DeclaringType.Assembly == own && Patch(handler.Method))
                            count++;
            return count;
        }

        private static void Before(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        private static void After(long __state, MethodBase __originalMethod)
        {
            long ticks = Stopwatch.GetTimestamp() - __state;
            Cost cost;
            if (!costs.TryGetValue(__originalMethod, out cost))
                costs[__originalMethod] = cost = new Cost
                {
                    Name = (withAssembly && __originalMethod.DeclaringType != null ? __originalMethod.DeclaringType.Assembly.GetName().Name + ": " : "")
                           + __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name,
                };
            cost.Ticks += ticks;
            cost.FrameTicks += ticks;
            cost.Calls++;
        }

        // Last in the frame's scripts as far as it goes: the frame's sums closed.
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
