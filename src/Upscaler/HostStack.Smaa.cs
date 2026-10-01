using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System;
using ReDefinition.Core;
using ReDefinition.Settings;
using UnityEngine.Rendering;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // Scatterer's and Deferred's SMAA (HostStack).
    internal static partial class HostStack
    {
        // Scatterer's SMAA hangs a CommandBuffer on Camera 00 at
        // AfterForwardAlpha -- before the rig's capture at BeforeImageEffects. The
        // upscaler would receive an image whose edges are already smoothed, and
        // edge aliasing is what it reconstructs from.
        //
        // Switching the component off rather than the setting -- the setting is
        // only read when Scatterer builds its camera stack at scene load -- and
        // taking its CommandBuffer off the camera as well (DetachOwnBuffer).
        private static string DisableSmaa()
        {
            smaaStates.RemoveAll(state => state.Owner == null || state.Camera == null);

            string scatterer = DisableSmaa(ScattererSmaaTypeName, "Scatterer SMAA");
            // Deferred adds its SMAA only in the editors (EditorLighting, an
            // EditorAny add-on): no scene-wide scan for it anywhere else.
            if (!HighLogic.LoadedSceneIsEditor) return scatterer;
            return scatterer + " " + DisableSmaa(DeferredSmaaTypeName, "Deferred SMAA");
        }

        // Switching the component off is not enough. Scatterer's and Deferred's
        // SMAA attach their CommandBuffer to their camera in OnPreCull while
        // their private "initialized" is false -- Scatterer again whenever the
        // camera's HDR setting changes, without taking the old one off -- and
        // take it away only in OnDestroy (SubpixelMorphologicalAntialiasing.cs
        // in Scatterer, SubpixelMorphologicalAntiAliasing.cs in Deferred). A
        // disabled component attaches nothing new, but the buffer already on
        // the camera keeps running. So the component's own buffer, read from
        // its SMAACommandBuffer field, is taken off its camera as well.
        // Both create that buffer once, in Awake, and rebuild it in place, so
        // the field names the very buffer their camera runs; RemoveCommandBuffer
        // takes it with every copy ("all occurrences of it will be removed",
        // Unity's scripting reference). Without the field -- another version --
        // the component is only switched off, and the report counts those.
        private sealed class SmaaState
        {
            public Behaviour Owner;
            public Camera Camera;
            public CommandBuffer Buffer;
            public CameraEvent Event;
            public bool WasEnabled;
            public bool WasAttached;
        }

        private static readonly List<SmaaState> smaaStates = new List<SmaaState>();

        // Where each mod attaches: its own static SMAACameraEvent, read from the
        // mod -- AfterForwardAlpha in both, with the same comment in both:
        // "BeforeImageEffects doesn't work well".
        private const CameraEvent DefaultSmaaEvent = CameraEvent.AfterForwardAlpha;

        private static CameraEvent SmaaEventOf(Type type)
        {
            object value;
            return TryGetStatic(type, "SMAACameraEvent", out value) && value is CameraEvent
                ? (CameraEvent)value : DefaultSmaaEvent;
        }

        private static string DisableSmaa(string typeName, string label)
        {
            Type type = TypeLookup.Find(typeName);
            if (type == null) return label + " not present.";

            CameraEvent evt = SmaaEventOf(type);
            int turnedOff = 0;
            int unreadable = 0;
            UnityEngine.Object[] all = UnityEngine.Object.FindObjectsOfType(type);
            for (int i = 0; i < all.Length; i++)
            {
                Behaviour owner = all[i] as Behaviour;
                if (owner == null) continue;

                SmaaState known = FindSmaaState(owner);
                if (known != null)
                {
                    // Already switched off here. Work only if something switched it back on
                    // meanwhile: then off again, and its buffer off again --
                    // remembered as running, so Restore gives it back running, as
                    // whoever switched it on wanted.
                    if (!owner.enabled) continue;
                    known.WasEnabled = true;
                    owner.enabled = false;
                    DetachOwnBuffer(known);
                    turnedOff++;
                    continue;
                }

                bool readable;
                CommandBuffer buffer = OwnSmaaBuffer(owner, out readable);
                Camera camera = owner.GetComponent<Camera>();
                if (buffer == null || camera == null)
                {
                    if (!owner.enabled) continue;
                    owner.enabled = false;
                    if (!disabledComponents.Contains(owner)) disabledComponents.Add(owner);
                    if (!readable) unreadable++;
                    turnedOff++;
                    continue;
                }

                SmaaState state = new SmaaState
                {
                    Owner = owner,
                    Camera = camera,
                    Buffer = buffer,
                    Event = evt,
                    WasEnabled = owner.enabled,
                };
                state.WasAttached = DetachOwnBuffer(state);
                if (!state.WasEnabled && !state.WasAttached) continue;   // nothing running

                owner.enabled = false;
                smaaStates.Add(state);
                turnedOff++;
            }

            string result = turnedOff + " " + label + " switched off.";
            if (unreadable > 0)
                result += " " + unreadable + " of them without a readable buffer, which may keep running.";
            return result;
        }

        private static SmaaState FindSmaaState(Behaviour owner)
        {
            for (int i = 0; i < smaaStates.Count; i++)
                if (smaaStates[i].Owner == owner) return smaaStates[i];
            return null;
        }

        private static CommandBuffer OwnSmaaBuffer(Behaviour owner, out bool readable)
        {
            object value;
            readable = TryGet(owner, owner.GetType(), "SMAACommandBuffer", out value);
            return value as CommandBuffer;
        }

        // Whether the buffer was on the camera: the buffers at the event before
        // and after taking it off.
        private static bool DetachOwnBuffer(SmaaState state)
        {
            int before = state.Camera.GetCommandBuffers(state.Event).Length;
            state.Camera.RemoveCommandBuffer(state.Event, state.Buffer);
            return state.Camera.GetCommandBuffers(state.Event).Length < before;
        }

        // Only a bool field of that name, and set before the component runs
        // again; for anything else the buffer goes back by hand.
        private static bool ResetInitialized(Behaviour owner)
        {
            FieldInfo field = owner.GetType().GetField("initialized",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null || field.FieldType != typeof(bool)) return false;
            field.SetValue(owner, false);
            return true;
        }

        // The status line only reads. What renders is a buffer on a camera, so
        // it counts buffers by the names both mods give them, once per camera
        // -- two SMAA components on one camera can share a name -- plus every
        // enabled component without one yet, which will attach one.
        private const string ScattererSmaaBufferPrefix = "Scatterer SMAA CommandBuffer";
        private const string DeferredSmaaBufferPrefix = "Deferred editor SMAA CommandBuffer";

        private static void AddSmaaLine(List<Line> lines, string typeName, string bufferPrefix, string label)
        {
            Type type = TypeLookup.Find(typeName);
            if (type == null) return;

            CameraEvent evt = SmaaEventOf(type);
            int active = 0;
            HashSet<Camera> counted = new HashSet<Camera>();
            UnityEngine.Object[] smaa = UnityEngine.Object.FindObjectsOfType(type);
            for (int i = 0; i < smaa.Length; i++)
            {
                Behaviour behaviour = smaa[i] as Behaviour;
                if (behaviour == null) continue;

                Camera camera = behaviour.GetComponent<Camera>();
                int onCamera = camera != null ? CountSmaaBuffers(camera, bufferPrefix, evt) : 0;
                if (camera != null && counted.Add(camera)) active += onCamera;
                if (behaviour.enabled && onCamera == 0) active++;
            }
            lines.Add(new Line
            {
                Label = label,
                Value = active == 0 ? "off" : active + " active -- smooths before FSR sees the image",
                Ok = active == 0,
                Present = true,
            });
        }

        private static int CountSmaaBuffers(Camera camera, string prefix, CameraEvent evt)
        {
            int count = 0;
            CommandBuffer[] buffers = camera.GetCommandBuffers(evt);
            for (int i = 0; i < buffers.Length; i++)
            {
                if (buffers[i] != null && buffers[i].name != null
                    && buffers[i].name.StartsWith(prefix, StringComparison.Ordinal)) count++;
            }
            return count;
        }
    }
}
