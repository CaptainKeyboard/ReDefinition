using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System;
using ReDefinition.Core;
using UnityEngine.LowLevel;
using UnityEngine.Rendering;
using UnityEngine;

namespace ReDefinition.Proxy
{
    // NVIDIA Reflex and PC latency for the game's frames, which DLSS frame
    // generation requires ("It is required for sl.reflex to be integrated",
    // ProgrammingGuideDLSS_G.md 8.0). The proxy marks Present; the frame's
    // simulation is the main thread's, and only it can mark it.
    //
    // Two systems in Unity's player loop. The last of PostLateUpdate -- the frame
    // handed to the render thread, its Present included -- begins the next frame
    // before Windows' messages are taken for it: Reflex's sleep and
    // eSimulationStart under a new frame index (KspReflexFrameStart), and the index
    // to the render thread as render event 40, ahead of every Direct3D call of the
    // frame, for its Present. The one after PreLateUpdate's LateUpdate ends the
    // simulation (KspReflexSimulationEnd); what follows is rendering.
    //
    // While Streamline has its device -- a swapchain made through it, the one DLSS
    // frame generation presents with -- or the proxy follows the game's frames for its
    // own low latency where it does not -- NVIDIA Reflex through NVAPI, AMD Anti-Lag 2,
    // Intel XeLL, whose frame ids XeSS frame generation presents under (the proxy's
    // LowLatency.h) -- the proxy answers with an index; otherwise with 0, and nothing
    // more is sent. Without the proxy, or with one older than
    // the mod, both systems go quiet for the session.
    internal static class ReflexFrames
    {
        // The proxy's file name, and the render event that carries a frame's index to its Present.
        private const string Proxy = ProxyDll.Name;
        private const int FrameEvent = ProxyEvents.ReflexFrame;

        // Reflex's sleep and the frame's simulation start; the frame's index, or 0 where nothing marks frames.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint KspReflexFrameStart();

        // The end of the frame's simulation, under the index KspReflexFrameStart gave.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern void KspReflexSimulationEnd(uint frameIndex);

        // The proxy's render event callback, shared with frame generation and the upscalers.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr KspFgGetRenderEventFunc();

        // What lowers the latency, as text, and which: 1 Reflex through NVAPI, 2 Streamline's Reflex, 3 AMD
        // Anti-Lag 2, 4 Intel XeLL, 0 none.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspLowLatency(StringBuilder buffer, int size);

        // LowLatencyText's answer: the text it is read into, the kind and text last read and the text shown;
        // and whether the proxy lacks the export.
        private static readonly StringBuilder latencyBuffer = new StringBuilder(256);
        private static int shownLatencyKind = -1;
        private static string shownLatencySource;
        private static string shownLatency;
        private static bool latencyMissing;

        // What lowers the latency, for the settings row, made anew only when the
        // proxy's answer changes: the row asks every frame. Null without the proxy or
        // its export.
        internal static string LowLatencyText()
        {
            if (latencyMissing) return null;
            int kind;
            try
            {
                latencyBuffer.Length = 0;
                kind = KspLowLatency(latencyBuffer, latencyBuffer.Capacity);
            }
            catch (Exception)
            {
                latencyMissing = true;
                return null;
            }
            if (latencyBuffer.Length == 0) return null;
            if (kind != shownLatencyKind || shownLatencySource == null || !Same(latencyBuffer, shownLatencySource))
            {
                shownLatencyKind = kind;
                shownLatencySource = latencyBuffer.ToString();
                shownLatency = char.ToUpperInvariant(shownLatencySource[0]) + shownLatencySource.Substring(1);
            }
            return shownLatency;
        }

        // What lowers the latency in a word for the diagnostics window's strip: "Reflex",
        // "Anti-Lag 2" or "XeLL" (KspLowLatency's kind); null where nothing does or without the
        // proxy.
        internal static string LowLatencyName()
        {
            if (LowLatencyText() == null) return null;
            switch (shownLatencyKind)
            {
                case 1:
                case 2: return "Reflex";
                case 3: return "Anti-Lag 2";
                case 4: return "XeLL";
                default: return null;
            }
        }

        // The latency measure's median and 90th percentile in milliseconds; how many frames they rest on.
        [DllImport(Proxy, CallingConvention = CallingConvention.Cdecl)]
        private static extern int KspLatencyMeasure(out double median, out double percentile90);

        // Set once the proxy lacks KspLatencyMeasure, so it is not asked again.
        private static bool measureMissing;

        // The proxy's last latency report (LowLatency::Measure), from the frame's start to the
        // GPU's end of its Present, in milliseconds; how many frames it rests on, 0 before the
        // first report, or without the proxy or its export.
        internal static int LatencyMeasure(out double median, out double percentile90)
        {
            median = 0.0;
            percentile90 = 0.0;
            if (measureMissing) return 0;
            try
            {
                return KspLatencyMeasure(out median, out percentile90);
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException)
            {
                measureMissing = true;
                return 0;
            }
        }

        // Whether the builder holds the same text, compared without making a string of it.
        private static bool Same(StringBuilder text, string other)
        {
            if (text.Length != other.Length) return false;
            for (int i = 0; i < other.Length; i++)
            {
                if (text[i] != other[i]) return false;
            }
            return true;
        }

        // The loop tells systems apart by type; Unity's profiler shows the names.
        private struct ReDefinitionReflexFrameStart { }
        // The system that ends the frame's simulation, after LateUpdate.
        private struct ReDefinitionReflexSimulationEnd { }

        // Set once the proxy lacks the exports or a call failed: both systems go quiet. The render event, the
        // buffer that sends the index, the frame's index from BeginFrame (0 for none), and whether it was logged.
        private static bool missing;
        private static IntPtr renderEvent = IntPtr.Zero;
        private static CommandBuffer buffer;
        private static uint frame;
        private static bool logged;

        // From the addon's Awake.
        public static void Install()
        {
            try
            {
                PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
                if (Contains(loop, typeof(ReDefinitionReflexFrameStart))) return;

                if (!InsertAfter(ref loop, typeof(UnityEngine.PlayerLoop.PreLateUpdate.ScriptRunBehaviourLateUpdate),
                                 typeof(ReDefinitionReflexSimulationEnd), EndSimulation)
                    || !Append(ref loop, typeof(UnityEngine.PlayerLoop.PostLateUpdate),
                               typeof(ReDefinitionReflexFrameStart), BeginFrame))
                {
                    Debug.LogWarning(Log.Tag + " Unity's player loop lacks LateUpdate or PostLateUpdate:"
                                     + " Reflex goes by the proxy's Presents alone.");
                    return;
                }

                PlayerLoop.SetPlayerLoop(loop);
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Reflex could not be added to Unity's player loop: " + e);
            }
        }

        // Whether the system or one beneath it is of the type.
        private static bool Contains(PlayerLoopSystem system, Type type)
        {
            if (system.type == type) return true;
            if (system.subSystemList == null) return false;
            foreach (PlayerLoopSystem child in system.subSystemList)
            {
                if (Contains(child, type)) return true;
            }
            return false;
        }

        // The new system right after the anchor's, wherever in the loop that stands; false where there is none.
        private static bool InsertAfter(ref PlayerLoopSystem system, Type anchor, Type type,
                                        PlayerLoopSystem.UpdateFunction update)
        {
            PlayerLoopSystem[] children = system.subSystemList;
            if (children == null) return false;

            for (int i = 0; i < children.Length; i++)
            {
                if (children[i].type == anchor)
                {
                    List<PlayerLoopSystem> list = new List<PlayerLoopSystem>(children);
                    list.Insert(i + 1, new PlayerLoopSystem { type = type, updateDelegate = update });
                    system.subSystemList = list.ToArray();
                    return true;
                }

                if (InsertAfter(ref children[i], anchor, type, update)) return true;
            }
            return false;
        }

        // As the last of the parent's systems.
        private static bool Append(ref PlayerLoopSystem system, Type parent, Type type,
                                   PlayerLoopSystem.UpdateFunction update)
        {
            PlayerLoopSystem[] children = system.subSystemList;
            if (children == null) return false;

            for (int i = 0; i < children.Length; i++)
            {
                if (children[i].type == parent)
                {
                    List<PlayerLoopSystem> list = children[i].subSystemList != null
                        ? new List<PlayerLoopSystem>(children[i].subSystemList)
                        : new List<PlayerLoopSystem>();
                    list.Add(new PlayerLoopSystem { type = type, updateDelegate = update });
                    children[i].subSystemList = list.ToArray();
                    return true;
                }

                if (Append(ref children[i], parent, type, update)) return true;
            }
            return false;
        }

        // Both run inside Unity's player loop: nothing may escape.
        private static void BeginFrame()
        {
            frame = 0;
            if (missing) return;
            try
            {
                if (renderEvent == IntPtr.Zero)
                {
                    renderEvent = KspFgGetRenderEventFunc();
                    if (renderEvent == IntPtr.Zero)
                    {
                        missing = true;
                        return;
                    }
                    buffer = new CommandBuffer { name = "ReDefinition.Reflex frame" };
                }

                frame = KspReflexFrameStart();
                if (frame == 0) return;

                // Executed now, behind the Present just handed to the render thread
                // and ahead of everything the frame draws.
                buffer.Clear();
                buffer.IssuePluginEventAndData(renderEvent, FrameEvent, new IntPtr(frame));
                Graphics.ExecuteCommandBuffer(buffer);

                if (!logged)
                {
                    logged = true;
                    Debug.Log(Log.Tag + " Reflex: the game's frames go to the proxy by index, from frame "
                              + frame + ".");
                }
            }
            catch (Exception e)
            {
                missing = true;
                frame = 0;
                if (!(e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException))
                    Debug.LogWarning(Log.Tag + " Reflex stops marking the game's frames: " + e);
            }
        }

        // The frame's simulation is done: rendering follows.
        private static void EndSimulation()
        {
            if (frame == 0) return;
            try
            {
                KspReflexSimulationEnd(frame);
            }
            catch (Exception e)
            {
                missing = true;
                frame = 0;
                if (!(e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException))
                    Debug.LogWarning(Log.Tag + " Reflex stops marking the game's frames: " + e);
            }
        }
    }
}
