using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Upscaler
{
    // A Debug switch, not saved: the other mods' effects off while the game runs, to
    // tell whether a fault in the image is ReDefinition's or another mod's.
    //
    // Off go the components of other mods on the cameras that draw the scene -- their
    // image effects, cloud and atmosphere renderers, reflections -- and every command
    // buffer on those cameras and on the scene's lights that is not ReDefinition's, which
    // is how EVE's clouds and wet surfaces, Scatterer, Deferred's reflections and Firefly
    // draw. The buffers are taken off again as each camera culls, every frame, since
    // some mods add theirs anew per frame from objects that are not on a camera. What is
    // theirs: a component whose assembly is not Unity's, KSP's or ReDefinition's; a
    // buffer whose name does not begin with "ReDefinition".
    //
    // What a mod changed as the game loaded stays: Deferred's rendering path and
    // shaders, Parallax's terrain shaders, the objects Scatterer's sky and ocean are
    // drawn on. Switched back, the components come on and the buffers go back where
    // they were.
    internal static class ForeignEffects
    {
        private sealed class Removed
        {
            internal Camera Camera;
            internal Light Light;
            internal CameraEvent CameraEvent;
            internal LightEvent LightEvent;
            internal CommandBuffer Buffer;
        }

        private static readonly CameraEvent[] CameraEvents = (CameraEvent[])Enum.GetValues(typeof(CameraEvent));
        private static readonly LightEvent[] LightEvents = (LightEvent[])Enum.GetValues(typeof(LightEvent));

        private static readonly List<Behaviour> disabled = new List<Behaviour>();
        private static readonly List<Removed> removed = new List<Removed>();
        private static readonly HashSet<CommandBuffer> removedBuffers = new HashSet<CommandBuffer>();
        private static readonly Dictionary<string, int> report = new Dictionary<string, int>();
        private static Light[] lights = new Light[0];
        private static float lightsFound = -10f;
        private static bool reportDue;

        internal static bool Off { get; private set; }

        internal static void Set(bool off)
        {
            if (off == Off) return;
            Off = off;
            if (off)
            {
                report.Clear();
                reportDue = true;
                Camera.onPreCull += OnPreCull;
                Debug.Log(Log.Tag + " Other mods' effects switched off for the session.");
            }
            else
            {
                Camera.onPreCull -= OnPreCull;
                Restore();
                Debug.Log(Log.Tag + " Other mods' effects switched back on.");
            }
        }

        private static void OnPreCull(Camera camera)
        {
            try
            {
                if (camera == null || !SceneCamera(camera)) return;
                DisableComponents(camera);
                StripCamera(camera);
                if (Time.unscaledTime - lightsFound > 1f)
                {
                    lightsFound = Time.unscaledTime;
                    lights = UnityEngine.Object.FindObjectsOfType<Light>();
                }
                foreach (Light light in lights)
                    if (light != null) StripLight(light);
                if (reportDue && Time.frameCount % 120 == 0) WriteReport();
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Switching other mods' effects off failed: " + e.Message);
            }
        }

        // The cameras that draw the scene: not KSP's UI and not ReDefinition's own.
        private static bool SceneCamera(Camera camera)
        {
            string name = camera.name;
            return !name.StartsWith("UI", StringComparison.Ordinal)
                   && !name.StartsWith("ReDefinition", StringComparison.Ordinal);
        }

        private static bool Foreign(Type type)
        {
            Assembly assembly = type.Assembly;
            string name = assembly.GetName().Name;
            return !name.StartsWith("UnityEngine", StringComparison.Ordinal)
                   && !name.StartsWith("Unity.", StringComparison.Ordinal)
                   && name != "Assembly-CSharp" && name != "Assembly-CSharp-firstpass"
                   && assembly != typeof(ForeignEffects).Assembly;
        }

        private static void DisableComponents(Camera camera)
        {
            foreach (Behaviour behaviour in camera.GetComponents<Behaviour>())
            {
                if (behaviour == null || !behaviour.enabled || !Foreign(behaviour.GetType())) continue;
                behaviour.enabled = false;
                disabled.Add(behaviour);
                Count(behaviour.GetType().Assembly.GetName().Name + " " + behaviour.GetType().Name + " on " + camera.name);
            }
        }

        private static void StripCamera(Camera camera)
        {
            if (camera.commandBufferCount == 0) return;
            foreach (CameraEvent evt in CameraEvents)
            {
                foreach (CommandBuffer buffer in camera.GetCommandBuffers(evt))
                {
                    if (Ours(buffer)) continue;
                    camera.RemoveCommandBuffer(evt, buffer);
                    if (removedBuffers.Add(buffer))
                        removed.Add(new Removed { Camera = camera, CameraEvent = evt, Buffer = buffer });
                    Count("buffer '" + buffer.name + "' on " + camera.name + " at " + evt);
                }
            }
        }

        private static void StripLight(Light light)
        {
            if (light.commandBufferCount == 0) return;
            foreach (LightEvent evt in LightEvents)
            {
                foreach (CommandBuffer buffer in light.GetCommandBuffers(evt))
                {
                    if (Ours(buffer)) continue;
                    light.RemoveCommandBuffer(evt, buffer);
                    if (removedBuffers.Add(buffer))
                        removed.Add(new Removed { Light = light, LightEvent = evt, Buffer = buffer });
                    Count("buffer '" + buffer.name + "' on light " + light.name + " at " + evt);
                }
            }
        }

        private static bool Ours(CommandBuffer buffer)
        {
            return buffer == null || (buffer.name != null && buffer.name.StartsWith("ReDefinition", StringComparison.Ordinal));
        }

        private static void Count(string what)
        {
            int n;
            report.TryGetValue(what, out n);
            report[what] = n + 1;
        }

        private static void WriteReport()
        {
            reportDue = false;
            Debug.Log(Log.Tag + " Other mods' effects off: " + report.Count + " kind(s) taken off -- "
                      + string.Join("; ", report.Keys.OrderBy(k => k).ToArray()) + ".");
        }

        private static void Restore()
        {
            foreach (Behaviour behaviour in disabled)
                if (behaviour != null) behaviour.enabled = true;
            disabled.Clear();
            foreach (Removed r in removed)
            {
                if (r.Buffer == null) continue;
                try
                {
                    if (r.Camera != null && !r.Camera.GetCommandBuffers(r.CameraEvent).Contains(r.Buffer))
                        r.Camera.AddCommandBuffer(r.CameraEvent, r.Buffer);
                    else if (r.Light != null && !r.Light.GetCommandBuffers(r.LightEvent).Contains(r.Buffer))
                        r.Light.AddCommandBuffer(r.LightEvent, r.Buffer);
                }
                catch (Exception)
                {
                    // A buffer its mod released meanwhile: gone for good, as the mod meant.
                }
            }
            removed.Clear();
            removedBuffers.Clear();
        }
    }
}
