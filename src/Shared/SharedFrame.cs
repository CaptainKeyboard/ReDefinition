using System;
using ReDefinition.Proxy;
using ReDefinition.Core;
using ReDefinition.Settings;
using ReDefinition.Upscaler;
using UnityEngine.Rendering;
using UnityEngine;

namespace ReDefinition.Shared
{
    // The frame's state every mod may read and add to (ReDefinition.Api), set once
    // per frame before its first scene camera culls: whether the upscaler and frame
    // generation run, the render and display size, the jitter, the history reset and
    // the floating origin's shifts, as properties and as shader globals. The hooks
    // other mods register live here too; the rig calls them.
    // docs/development/shared-foundation.md.
    internal static class SharedFrame
    {
        // The version of the interface other mods read (ApiInfo.Version), also the fourth value of _ReDefinition_Frame.
        internal const int InterfaceVersion = 2;

        // In a class of their own, initialised when the first frame begins: the state's
        // properties are read outside Unity too (tests), where PropertyToID cannot run.
        private static class Ids
        {
            // The shader globals Begin sets, as ReDefinition.cginc declares them.
            internal static readonly int Frame = Shader.PropertyToID("_ReDefinition_Frame");
            internal static readonly int RenderSize = Shader.PropertyToID("_ReDefinition_RenderSize");
            internal static readonly int DisplaySize = Shader.PropertyToID("_ReDefinition_DisplaySize");
            internal static readonly int Jitter = Shader.PropertyToID("_ReDefinition_Jitter");
            internal static readonly int OriginShift = Shader.PropertyToID("_ReDefinition_OriginShift");
        }

        // Which frames reset the history; whether the camera and shift events are subscribed; the frame Begin last ran in.
        private static readonly HistoryResets resets = new HistoryResets();
        private static bool installed;
        private static int begunFrame = -1;

        // Summed from onFloatingOriginShift until the next frame begins.
        private static Vector3d pendingOffset;
        private static Vector3d pendingBodyOffset;
        private static bool pendingShift;

        // The hooks of ReDefinition.Api: told of history resets, drawing into the motion vectors, after upscaling and
        // over the image, and told of profile changes. A throwing handler is removed and logged as a warning.
        internal static readonly HookList<Action<string>> HistoryResetHandlers =
            new HookList<Action<string>>("history resets", Report);
        internal static readonly HookList<Action<CommandBuffer, RenderTexture, RenderTexture, Camera>> MotionVectorHooks =
            new HookList<Action<CommandBuffer, RenderTexture, RenderTexture, Camera>>("motion vectors", Report);
        internal static readonly HookList<Action<CommandBuffer, RenderTexture, Camera>> AfterUpscalingHooks =
            new HookList<Action<CommandBuffer, RenderTexture, Camera>>("after upscaling", Report);
        internal static readonly HookList<Action<CommandBuffer, Camera>> OverlayHooks =
            new HookList<Action<CommandBuffer, Camera>>("overlays", Report);
        internal static readonly HookList<Action<string>> ProfileChangedHandlers =
            new HookList<Action<string>>("profile changes", Report);

        // Whether the upscaler reconstructs this frame, in any mode.
        internal static bool UpscalerActive { get; private set; }
        // Whether frame generation receives this frame's inputs.
        internal static bool FrameGenerationActive { get; private set; }
        // The upscaler's display name while it is active (UpscalerBackends.Name); null otherwise.
        internal static string Technique { get; private set; }
        // The size the 3D cameras render at; the screen's without a rig.
        internal static Vector2Int RenderSize { get; private set; }
        // The size of the image shown; the screen's without a rig.
        internal static Vector2Int DisplaySize { get; private set; }
        // The jitter of this frame in pixels at render size, as the upscaler is told; zero without it.
        internal static Vector2 Jitter { get; private set; }
        // The same jitter in normalized device coordinates, as CameraRedirect adds it to the projection.
        internal static Vector2 JitterNdc { get; private set; }

        // Why this frame resets the history for the mods (HistoryResets); null where it does not.
        internal static string ResetReason { get; private set; }
        // Why this frame resets the upscaler and frame generation when it begins; null where it does not.
        internal static string RigResetReason { get; private set; }

        // The floating origin's shifts since the frame before, summed: what moved the active vessel, the camera and
        // nearby objects.
        internal static Vector3d OriginShift { get; private set; }
        // The same for the bodies (offset + nonFrame), which holds the Krakensbane step as well.
        internal static Vector3d BodyShift { get; private set; }
        // Whether the floating origin shifted since the frame before.
        internal static bool Shifted { get; private set; }

        // The profile PollProfile saw last, null before one is chosen.
        private static string lastProfile;

        // The handler KSP's GameEvents gets: an instance method. EventData's EvtDelegate
        // reads the handler's Target, which a static method does not have, and throws
        // (NullReferenceException in EvtDelegate..ctor, measured in the game).
        private sealed class ShiftReceiver
        {
            // Hands the shift to OnFloatingOriginShift.
            internal void OnShift(Vector3d offset, Vector3d nonFrame)
            {
                OnFloatingOriginShift(offset, nonFrame);
            }
        }

        // The receiver and the delegate subscribed to onFloatingOriginShift, kept to remove the same one.
        private static readonly ShiftReceiver shiftReceiver = new ShiftReceiver();
        private static EventData<Vector3d, Vector3d>.OnEvent shiftHandler;

        // From the add-on's Awake, once per process.
        internal static void Install()
        {
            if (installed) return;
            installed = true;
            Camera.onPreCull += OnPreCull;
            shiftHandler = shiftReceiver.OnShift;
            GameEvents.onFloatingOriginShift.Add(shiftHandler);
        }

        // Removes what Install subscribed; a second call does nothing.
        internal static void Uninstall()
        {
            if (!installed) return;
            installed = false;
            Camera.onPreCull -= OnPreCull;
            if (shiftHandler != null) GameEvents.onFloatingOriginShift.Remove(shiftHandler);
            shiftHandler = null;
        }

        // A mod's cut (ReDefinition.Api.Frame.RequestHistoryReset), decided when the frame begins or at its end.
        internal static void RequestHistoryReset(string reason)
        {
            resets.Request(reason);
        }

        // Where the frame's state is needed before a camera of the 3D stack culled: the
        // rig's scene camera culls, or the rig presents.
        internal static void EnsureBegun()
        {
            if (begunFrame != Time.frameCount) Begin();
        }

        // At the rig's Present: a cut or request since the frame began, which resets
        // the upscaler and frame generation now and reaches the mods in the next
        // frame. Null where there is none.
        internal static string LateReset()
        {
            return resets.Late(CameraCuts.Detect());
        }

        // From the add-on's Update.
        internal static void PollProfile()
        {
            string current;
            try
            {
                current = BundledSettings.ProfileChosen ? BundledSettings.ProfileName : null;
            }
            catch (Exception)
            {
                return;
            }
            if (current == lastProfile) return;
            lastProfile = current;
            ProfileChanged(current);
        }

        // In a method of its own: a lambda in PollProfile capturing its local would be
        // allocated in every call, the profile changed or not.
        private static void ProfileChanged(string profile)
        {
            ProfileChangedHandlers.Invoke(handler => handler(profile));
        }

        // The chosen bundled profile's name as of the last poll; null before one is chosen.
        internal static string Profile
        {
            get { return lastProfile; }
        }

        // Begins the frame when the first of the 3D scene cameras culls in it (UpscalerRig.IsSceneCamera).
        private static void OnPreCull(Camera camera)
        {
            if (begunFrame == Time.frameCount || camera == null || !UpscalerRig.IsSceneCamera(camera.name)) return;
            Begin();
        }

        // offset moves the active vessel, the camera and nearby objects;
        // offset + nonFrame moves the bodies (FloatingOrigin.setOffset, as
        // KSPCommunityFixes' FloatingOriginPerf reproduces it).
        private static void OnFloatingOriginShift(Vector3d offset, Vector3d nonFrame)
        {
            pendingOffset += offset;
            pendingBodyOffset += offset + nonFrame;
            pendingShift = true;
        }

        // Decides the frame's state once: the history resets, the shifts summed since the last frame, the rig's state,
        // the shader globals; then tells the mods' handlers of a reset.
        private static void Begin()
        {
            begunFrame = Time.frameCount;

            string forMods, forRig;
            resets.Begin(CameraCuts.Detect(), out forMods, out forRig);
            ResetReason = forMods;
            RigResetReason = forRig;

            OriginShift = pendingOffset;
            BodyShift = pendingBodyOffset;
            Shifted = pendingShift;
            pendingOffset = Vector3d.zero;
            pendingBodyOffset = Vector3d.zero;
            pendingShift = false;

            UpscalerRig rig = UpscalerRig.Current;
            if (rig != null && rig.TornDown) rig = null;

            UpscalerActive = rig != null && rig.UpscalerRuns;
            FrameGenerationActive = rig != null && rig.FrameGenerationReceives;
            Technique = UpscalerActive ? UpscalerBackends.Name(rig.Backend) : null;
            Vector2Int screen = new Vector2Int(Screen.width, Screen.height);
            RenderSize = rig != null ? rig.RenderSize : screen;
            DisplaySize = rig != null ? rig.DisplaySize : screen;
            bool jittered = UpscalerActive && CameraRedirect.JitterActive;
            Jitter = jittered ? rig.CurrentJitter : Vector2.zero;
            JitterNdc = jittered ? CameraRedirect.JitterNdc : Vector2.zero;

            Shader.SetGlobalVector(Ids.Frame, new Vector4(UpscalerActive ? 1f : 0f, FrameGenerationActive ? 1f : 0f,
                                                        ResetReason != null ? 1f : 0f, InterfaceVersion));
            Shader.SetGlobalVector(Ids.RenderSize, SizeVector(RenderSize));
            Shader.SetGlobalVector(Ids.DisplaySize, SizeVector(DisplaySize));
            Shader.SetGlobalVector(Ids.Jitter, new Vector4(Jitter.x, Jitter.y, JitterNdc.x, JitterNdc.y));
            Shader.SetGlobalVector(Ids.OriginShift, new Vector4((float)OriginShift.x, (float)OriginShift.y,
                                                              (float)OriginShift.z, Shifted ? 1f : 0f));

            if (RigResetReason != null && rig != null)
                Debug.Log(Log.Tag + " History reset: " + RigResetReason + ".");
            if (ResetReason != null)
            {
                string reason = ResetReason;
                HistoryResetHandlers.Invoke(handler => handler(reason));
            }
        }

        // A size as a shader vector: width, height, and their reciprocals (zero for an empty size).
        private static Vector4 SizeVector(Vector2Int size)
        {
            return new Vector4(size.x, size.y, size.x > 0 ? 1f / size.x : 0f, size.y > 0 ? 1f / size.y : 0f);
        }

        // Logs a hook list's removal of a handler as a warning.
        private static void Report(string message)
        {
            Debug.LogWarning(Log.Tag + " " + message);
        }
    }
}
