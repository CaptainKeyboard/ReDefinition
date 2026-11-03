using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System;
using ReDefinition.Core;
using UnityEngine.Rendering;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // Scatterer's and Deferred's SMAA: switched off with its command buffer taken off the camera, and handed back
    // as found (HostStack).
    internal sealed class SmaaControl : IHostFilter
    {
        // Scatterer's SMAA component, by full type name.
        private const string ScattererTypeName = "Scatterer.SubpixelMorphologicalAntialiasing";

        // Deferred brings an SMAA of its own, added only in the editors, to the
        // editor's Main Camera, when useSmaaInEditors is set -- which the
        // shipped Deferred.cfg does (EditorLighting.HandleSMAA). In the VAB and
        // the SPH the rig sits on that camera. Deferred adds a fresh copy on every
        // editor scene load, so Reassert's once-a-second pass keeps it off.
        private const string DeferredTypeName = "Deferred.SubpixelMorphologicalAntialiasing";

        // Switching the component off is not enough. Scatterer's and Deferred's
        // SMAA attach their CommandBuffer to their camera in OnPreCull while
        // their private "initialized" is false, and take it away only in OnDestroy
        // (SubpixelMorphologicalAntialiasing.cs in Scatterer,
        // SubpixelMorphologicalAntiAliasing.cs in Deferred). Scatterer attaches again
        // whenever the camera's HDR setting changes, without taking the old one off. A
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
            // The SMAA component, its camera, its own buffer and the event it attaches at; whether it was enabled and its
            // buffer on the camera when switched off here.
            public Behaviour Owner;
            public Camera Camera;
            public CommandBuffer Buffer;
            public CameraEvent Event;
            public bool WasEnabled;
            public bool WasAttached;
        }

        // Where each mod attaches: its own static SMAACameraEvent, read from the
        // mod -- AfterForwardAlpha in both, with the same comment in both:
        // "BeforeImageEffects doesn't work well".
        private const CameraEvent DefaultEvent = CameraEvent.AfterForwardAlpha;

        // The status line only reads. What renders is a buffer on a camera, so
        // it counts buffers by the names both mods give them, once per camera,
        // plus every enabled component without one yet, which will attach one.
        // Two SMAA components on one camera can share a name.
        private const string ScattererBufferPrefix = "Scatterer SMAA CommandBuffer";
        private const string DeferredBufferPrefix = "Deferred editor SMAA CommandBuffer";

        // Every SMAA switched off here with its buffer taken off, for HandBack.
        private readonly List<SmaaState> states = new List<SmaaState>();

        // The SMAA components found on the cameras, kept between the once-a-second passes.
        private readonly List<Component> scratch = new List<Component>();

        // Components switched off here, so they can be switched back on.
        private readonly List<Behaviour> disabledComponents = new List<Behaviour>();

        // The name in the notes of a step that failed.
        public string Label
        {
            get { return "SMAA"; }
        }

        // Switches off every SMAA of Scatterer and, in the editors, of Deferred, and appends what it did to the report.
        public void Apply(StringBuilder report)
        {
            report.Append(DisableAll()).Append(' ');
        }

        // Switches off again what was switched on since, or created anew.
        public void Reassert()
        {
            DisableAll();
        }

        // Switches back on the components switched off without a buffer to take off, then hands every SMAA back to its
        // owner as found.
        public void Restore(StringBuilder report)
        {
            HostStack.Step(report, "Components", () => ReEnableComponents(report));
            HandBack(report);
        }

        // The rows for Scatterer's SMAA and, in the editors, Deferred's.
        public void AddLines(List<HostStack.Line> lines)
        {
            // SMAA sits at AfterForwardAlpha, before the rig's capture -- an image
            // smoothed there has lost the aliasing the upscaler reconstructs from.
            AddLine(lines, ScattererTypeName, ScattererBufferPrefix, "Scatterer SMAA");
            if (HighLogic.LoadedSceneIsEditor)
                AddLine(lines, DeferredTypeName, DeferredBufferPrefix, "Deferred SMAA (editors)");
        }

        // Scatterer's SMAA hangs a CommandBuffer on Camera 00 at
        // AfterForwardAlpha -- before the rig's capture at BeforeImageEffects. The
        // upscaler would receive an image whose edges are already smoothed, and
        // edge aliasing is what it reconstructs from.
        //
        // Switching the component off rather than the setting -- the setting is
        // only read when Scatterer builds its camera stack at scene load -- and
        // taking its CommandBuffer off the camera as well (DetachOwnBuffer).
        private string DisableAll()
        {
            states.RemoveAll(state => state.Owner == null || state.Camera == null);

            string scatterer = Disable(ScattererTypeName, "Scatterer SMAA");
            // Deferred adds its SMAA only in the editors (EditorLighting, an
            // EditorAny add-on): no scene-wide scan for it anywhere else.
            if (!HighLogic.LoadedSceneIsEditor) return scatterer;
            return scatterer + " " + Disable(DeferredTypeName, "Deferred SMAA");
        }

        // The camera event a mod's SMAA attaches its buffer at, read from its static SMAACameraEvent.
        internal static CameraEvent EventOf(Type type)
        {
            object value;
            return ForeignReflection.TryGetStatic(type, "SMAACameraEvent", out value) && value is CameraEvent
                ? (CameraEvent)value : DefaultEvent;
        }

        // Switches off every running SMAA of one mod's type and takes its buffer off its camera; one without a readable
        // buffer is only switched off. Returns the report.
        private string Disable(string typeName, string label)
        {
            Type type = TypeLookup.Find(typeName);
            if (type == null) return label + " not present.";

            CameraEvent evt = EventOf(type);
            int turnedOff = 0;
            int unreadable = 0;
            HostCameras.OnCameras(type, scratch);
            List<Component> all = scratch;
            for (int i = 0; i < all.Count; i++)
            {
                Behaviour owner = all[i] as Behaviour;
                if (owner == null) continue;

                SmaaState known = Find(owner);
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
                CommandBuffer buffer = OwnBuffer(owner, out readable);
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
                states.Add(state);
                turnedOff++;
            }

            string result = turnedOff + " " + label + " switched off.";
            if (unreadable > 0)
                result += " " + unreadable + " of them without a readable buffer, which may keep running.";
            return result;
        }

        // The state kept for an SMAA component, or null.
        private SmaaState Find(Behaviour owner)
        {
            for (int i = 0; i < states.Count; i++)
                if (states[i].Owner == owner) return states[i];
            return null;
        }

        // The component's SMAACommandBuffer; readable tells whether the field or property is there at all.
        private static CommandBuffer OwnBuffer(Behaviour owner, out bool readable)
        {
            object value;
            readable = ForeignReflection.TryGet(owner, owner.GetType(), "SMAACommandBuffer", out value);
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

        // Switches the components in disabledComponents back on.
        private void ReEnableComponents(StringBuilder report)
        {
            // Same rule for the components switched off here: only what is still
            // off goes back on. One re-enabled meanwhile keeps that state.
            int reEnabled = 0;
            for (int i = 0; i < disabledComponents.Count; i++)
            {
                Behaviour component = disabledComponents[i];
                if (component == null || component.enabled) continue;
                component.enabled = true;
                reEnabled++;
            }
            report.Append(' ').Append(reEnabled).Append(" component(s) re-enabled.");
            disabledComponents.Clear();
        }

        // Hands every SMAA switched off here back to its owner (states).
        private void HandBack(StringBuilder report)
        {
            // Each SMAA back as it was found. One that was running is switched
            // on again with its "initialized" reset, so it builds and attaches
            // its buffer itself on its next frame -- once, for the camera as it
            // is then; any copy of that buffer still on the camera is taken off
            // first. One that was off but still had its buffer on the camera
            // gets the buffer back by hand, since it would never attach it
            // itself; so does a running one that had it attached, in a version
            // without a bool "initialized".
            // Only where the owner still lives: a destroyed one took its buffer
            // off in OnDestroy and cleared it (Deferred) or released it
            // (Scatterer).
            //
            // Unity does not document the order of buffers at one camera event
            // (Camera.AddCommandBuffer); if it follows insertion, a buffer attached
            // anew runs behind passes added after it -- Firefly's re-entry pass on
            // the flight camera, say -- until a restart.
            int handedBack = 0;
            for (int i = 0; i < states.Count; i++)
            {
                SmaaState state = states[i];
                if (state.Owner == null || state.Camera == null) continue;
                try
                {
                    state.Camera.RemoveCommandBuffer(state.Event, state.Buffer);
                    if (state.WasEnabled)
                    {
                        bool reset = ResetInitialized(state.Owner);
                        state.Owner.enabled = true;
                        if (!reset && state.WasAttached)
                            state.Camera.AddCommandBuffer(state.Event, state.Buffer);
                    }
                    else if (state.WasAttached)
                    {
                        state.Camera.AddCommandBuffer(state.Event, state.Buffer);
                    }
                    handedBack++;
                }
                catch (Exception e)
                {
                    Debug.LogWarning(Log.Tag + " SMAA not handed back: " + e);
                }
            }
            states.Clear();
            report.Append(' ').Append(handedBack).Append(" SMAA back as found.");
        }

        // The report row for one mod's SMAA: the buffers of that name on the cameras, plus every enabled component
        // without one yet.
        private static void AddLine(List<HostStack.Line> lines, string typeName, string bufferPrefix, string label)
        {
            Type type = TypeLookup.Find(typeName);
            if (type == null) return;

            CameraEvent evt = EventOf(type);
            int active = 0;
            HashSet<Camera> counted = new HashSet<Camera>();
            UnityEngine.Object[] smaa = UnityEngine.Object.FindObjectsOfType(type);
            for (int i = 0; i < smaa.Length; i++)
            {
                Behaviour behaviour = smaa[i] as Behaviour;
                if (behaviour == null) continue;

                Camera camera = behaviour.GetComponent<Camera>();
                int onCamera = camera != null ? CountBuffers(camera, bufferPrefix, evt) : 0;
                if (camera != null && counted.Add(camera)) active += onCamera;
                if (behaviour.enabled && onCamera == 0) active++;
            }
            lines.Add(new HostStack.Line
            {
                Label = label,
                Value = active == 0 ? "off" : active + " active -- smooths before FSR sees the image",
                Ok = active == 0,
                Present = true,
            });
        }

        // The buffers at `evt` on `camera` whose name starts with `prefix`.
        private static int CountBuffers(Camera camera, string prefix, CameraEvent evt)
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
