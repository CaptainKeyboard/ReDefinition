using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using ReDefinition.Core;
using ReDefinition.Modules;
using ReDefinition.Proxy;
using ReDefinition.Raytracing;
using UnityEngine;

namespace ReDefinition.Probe
{
    // -redefinitionProbeReentry (with -redefinitionProbeOrbit <planet>): the pod comes down on an orbit that dips into the
    // air (LandProbe.EntryOrbit). The air does not slow it (Coast), so its speed and its plasma hold along the pass, which lasts
    // a couple of minutes. Each variant is applied as the settings window would apply it,
    // left for a few seconds, and measured. The variants are frame generation, the upscaler, the raytracing and the other
    // switches in turn, and all of them off. The measure is the mean brightness of a window round the pod, where it and
    // its plasma are, over a run of rendered frames, and how much it jumps from one frame to the next. The frames the
    // monitor shows, the generated ones too, are recorded for the baseline and for frame generation off.
    internal static class ReentryProbe
    {
        internal const string Argument = "-redefinitionProbeReentry";

        // Whether the probe was asked for.
        internal static bool Requested
        {
            get { return CommandLine.Has(Argument); }
        }

        // The rendered frames measured for each variant, the seconds a variant is left to settle, and the window round the pod
        // as a share of the picture's width and height; the effect's strength the variants wait for.
        private const int MeasuredFrames = 48;
        private const float SettleSeconds = 4f;
        private const float WindowWidth = 0.36f, WindowHeight = 0.6f;
        private const float StrongEnough = 2000f;

        // One variant: its name, and how it changes the settings.
        private struct Variant
        {
            internal string Name;
            internal Action<OwnSettings> Edit;
            // Run after the settings are applied: a switch of the add-on itself, by its type and static field.
            internal Action After;
        }

        // A static bool of the add-on's own, set by the type's name and the field's, as the debug switches are.
        private static Action Switch(string type, string field, bool value)
        {
            return () =>
            {
                Type found = TypeLookup.Find(type);
                FieldInfo info = found != null ? found.GetField(field, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static) : null;
                if (info != null) info.SetValue(null, value);
                Debug.Log(Log.Tag + " Probe: reentry: " + type + "." + field + " = " + value + (info != null ? "." : " (not found)."));
            };
        }

        // A field of the settings that holds an enum, set by the name of its value.
        private static void SetEnum(OwnSettings settings, string field, string value)
        {
            FieldInfo info = typeof(OwnSettings).GetField(field);
            if (info != null) info.SetValue(settings, Enum.Parse(info.FieldType, value));
        }

        // The settings the variants change, as the window would apply them: before to after through the add-on.
        private static void Change(OwnSettings before, Action<OwnSettings> edit)
        {
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            OwnSettings from = addon.Live.Clone();
            OwnSettings to = before.Clone();
            if (edit != null) edit(to);
            addon.Apply(from, to);
        }

        // Firefly's effect strength on the vessel (the _EntryStrength its effect material carries), or -1 where it has none.
        private static float FireflyStrength(Vessel vessel)
        {
            Type moduleType = TypeLookup.Find("Firefly.AtmoFxModule");
            if (vessel == null || moduleType == null) return -1f;
            foreach (VesselModule module in vessel.vesselModules)
            {
                if (module == null || module.GetType() != moduleType) continue;
                FieldInfo fxField = moduleType.GetField("fxVessel", BindingFlags.Public | BindingFlags.Instance);
                object fx = fxField != null ? fxField.GetValue(module) : null;
                if (fx == null) return -1f;
                FieldInfo materialField = fx.GetType().GetField("material", BindingFlags.Public | BindingFlags.Instance);
                Material material = materialField != null ? materialField.GetValue(fx) as Material : null;
                return material != null && material.HasProperty("_EntryStrength") ? material.GetFloat("_EntryStrength") : -1f;
            }
            return -1f;
        }

        // The pod coasts through the air as if it did not slow it, so that its speed and its plasma hold along the whole
        // pass. The game's drag is set to nothing and the air's heat on the pod is ignored. Drag is set back when `running` ends.
        private static IEnumerator Coast(Vessel vessel, Func<bool> running)
        {
            float drag = PhysicsGlobals.DragMultiplier;
            PhysicsGlobals.DragMultiplier = 0f;
            CheatOptions.IgnoreMaxTemperature = true;
            CheatOptions.NoCrashDamage = true;
            while (running() && vessel != null && vessel.state != Vessel.State.DEAD) yield return null;
            PhysicsGlobals.DragMultiplier = drag;
        }

        // The command buffers on the flight's camera, event by event in the order they run: what hangs there decides what the
        // effects draw over and under.
        private static void LogBuffers(string when)
        {
            Camera camera = FlightCamera.fetch.mainCamera;
            var text = new System.Text.StringBuilder();
            foreach (UnityEngine.Rendering.CameraEvent e in Enum.GetValues(typeof(UnityEngine.Rendering.CameraEvent)))
            {
                UnityEngine.Rendering.CommandBuffer[] buffers = camera.GetCommandBuffers(e);
                if (buffers.Length == 0) continue;
                text.Append(e).Append(": ");
                for (int i = 0; i < buffers.Length; i++) text.Append(i > 0 ? ", " : "").Append(buffers[i].name);
                text.Append("; ");
            }
            Debug.Log(Log.Tag + " Probe: reentry: buffers on " + camera.name + " " + when + ": " + text + "target "
                      + (camera.targetTexture != null ? camera.targetTexture.width + "x" + camera.targetTexture.height : "the screen") + ".");
        }

        // Every quarter of a second while `running`: the time, the pod's height, Firefly's strength, how large its command buffer
        // is, whether its airstream camera runs, and the brightness of the window round the pod. What goes on beside the variants.
        private static IEnumerator Sample(Vessel vessel, Func<bool> running)
        {
            Type moduleType = TypeLookup.Find("Firefly.AtmoFxModule");
            float next = 0f;
            while (running() && vessel != null && vessel.state != Vessel.State.DEAD)
            {
                yield return new WaitForEndOfFrame();
                if (Time.realtimeSinceStartup < next) continue;
                next = Time.realtimeSinceStartup + 0.25f;
                int bytes = -1;
                int airstream = -1;
                foreach (VesselModule module in vessel.vesselModules)
                {
                    if (module == null || module.GetType() != moduleType) continue;
                    FieldInfo fxField = moduleType.GetField("fxVessel", BindingFlags.Public | BindingFlags.Instance);
                    object fx = fxField != null ? fxField.GetValue(module) : null;
                    if (fx == null) continue;
                    FieldInfo bufferField = fx.GetType().GetField("commandBuffer", BindingFlags.Public | BindingFlags.Instance);
                    FieldInfo cameraField = fx.GetType().GetField("airstreamCamera", BindingFlags.Public | BindingFlags.Instance);
                    UnityEngine.Rendering.CommandBuffer buffer = bufferField != null ? bufferField.GetValue(fx) as UnityEngine.Rendering.CommandBuffer : null;
                    Camera camera = cameraField != null ? cameraField.GetValue(fx) as Camera : null;
                    bytes = buffer != null ? buffer.sizeInBytes : -1;
                    airstream = camera != null ? (camera.enabled ? 1 : 0) : -1;
                }
                Texture2D frame = ScreenCapture.CaptureScreenshotAsTexture();
                Camera main = FlightCamera.fetch.mainCamera;
                Vector3 at = main.WorldToViewportPoint((Vector3)vessel.GetWorldPos3D());
                int w = Mathf.RoundToInt(frame.width * WindowWidth), h = Mathf.RoundToInt(frame.height * WindowHeight);
                int x = Mathf.Clamp(Mathf.RoundToInt(at.x * frame.width - w * 0.5f), 0, frame.width - w);
                int y = Mathf.Clamp(Mathf.RoundToInt(at.y * frame.height - h * 0.3f), 0, frame.height - h);
                Color[] pixels = frame.GetPixels(x, y, w, h);
                double sum = 0.0;
                foreach (Color c in pixels) sum += 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b;
                UnityEngine.Object.Destroy(frame);
                Debug.Log(Log.Tag + " Probe: reentry: sample " + Time.realtimeSinceStartup.ToString("0.00", CultureInfo.InvariantCulture) + " s, "
                          + vessel.altitude.ToString("0") + " m, strength " + FireflyStrength(vessel).ToString("0", CultureInfo.InvariantCulture)
                          + ", buffer " + bytes + " bytes, airstream camera " + airstream + ", window "
                          + (sum / pixels.Length).ToString("0.0000", CultureInfo.InvariantCulture)
                          + ", view " + ViewAngle(vessel).ToString("0.0", CultureInfo.InvariantCulture) + " deg from the flight path, camera "
                          + FlightCamera.fetch.mode + " at " + FlightCamera.fetch.Distance.ToString("0.0", CultureInfo.InvariantCulture)
                          + " m, pod at " + at.x.ToString("0.00", CultureInfo.InvariantCulture) + "," + at.y.ToString("0.00", CultureInfo.InvariantCulture) + ".");
            }
        }

        // The renders of each camera since the frame's end before, by camera name and target.
        private static readonly Dictionary<string, int> renders = new Dictionary<string, int>();

        private static void CountRender(Camera camera)
        {
            string key = camera.name + (camera.targetTexture != null ? "[" + camera.targetTexture.width + "x" + camera.targetTexture.height + "]" : "[screen]");
            int n;
            renders.TryGetValue(key, out n);
            renders[key] = n + 1;
        }

        // Firefly's airstream texture of the vessel, or null.
        private static RenderTexture AirstreamTexture(Vessel vessel)
        {
            Type moduleType = TypeLookup.Find("Firefly.AtmoFxModule");
            foreach (VesselModule module in vessel.vesselModules)
            {
                if (module == null || module.GetType() != moduleType) continue;
                object fx = moduleType.GetField("fxVessel", BindingFlags.Public | BindingFlags.Instance).GetValue(module);
                if (fx == null) return null;
                return fx.GetType().GetField("airstreamTexture", BindingFlags.Public | BindingFlags.Instance).GetValue(fx) as RenderTexture;
            }
            return null;
        }

        // Firefly's command buffer of the vessel, or null.
        private static UnityEngine.Rendering.CommandBuffer FireflyBuffer(Vessel vessel)
        {
            Type moduleType = TypeLookup.Find("Firefly.AtmoFxModule");
            foreach (VesselModule module in vessel.vesselModules)
            {
                if (module == null || module.GetType() != moduleType) continue;
                object fx = moduleType.GetField("fxVessel", BindingFlags.Public | BindingFlags.Instance).GetValue(module);
                if (fx == null) return null;
                return fx.GetType().GetField("commandBuffer", BindingFlags.Public | BindingFlags.Instance).GetValue(fx) as UnityEngine.Rendering.CommandBuffer;
            }
            return null;
        }

        // The mean of a small copy's red, green and blue.
        private static float Mean(RenderTexture texture)
        {
            RenderTexture active = RenderTexture.active;
            RenderTexture.active = texture;
            var read = new Texture2D(texture.width, texture.height, TextureFormat.RGBAFloat, false);
            read.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            RenderTexture.active = active;
            double sum = 0.0;
            foreach (Color c in read.GetPixels()) sum += c.r + c.g + c.b;
            UnityEngine.Object.Destroy(read);
            return (float)(sum / (texture.width * texture.height * 3.0));
        }

        // Frame by frame for `frames` frames: how often the flight camera rendered, the camera target as it was just before
        // Firefly's command buffer and just after it (a small copy of each), and the window's brightness. Tells whether
        // Firefly's draw changes the frame at all, and whether what it draws is still there at the end.
        // The mean brightness of the window round a viewport point in a render texture, which holds the frame as the camera drew
        // it (the upscaler's input under the rig), or -1 where there is none.
        private static float WindowMean(RenderTexture texture, Vector3 viewport)
        {
            if (texture == null) return -1f;
            int w = Mathf.RoundToInt(texture.width * WindowWidth), h = Mathf.RoundToInt(texture.height * WindowHeight);
            int x = Mathf.Clamp(Mathf.RoundToInt(viewport.x * texture.width - w * 0.5f), 0, texture.width - w);
            int y = Mathf.Clamp(Mathf.RoundToInt(viewport.y * texture.height - h * 0.3f), 0, texture.height - h);
            RenderTexture active = RenderTexture.active;
            RenderTexture.active = texture;
            var read = new Texture2D(w, h, TextureFormat.RGBAFloat, false);
            read.ReadPixels(new Rect(x, y, w, h), 0, 0);
            RenderTexture.active = active;
            double sum = 0.0;
            foreach (Color c in read.GetPixels()) sum += 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b;
            UnityEngine.Object.Destroy(read);
            return (float)(sum / (w * h));
        }

        // One change to what Firefly's draw sees: `build` fills a buffer that runs just before Firefly's, each frame, with the
        // flight camera's matrices of that frame; `perRender` runs as the flight camera starts to render.
        private struct Injection
        {
            internal string Name;
            internal Action<UnityEngine.Rendering.CommandBuffer, Camera> Build;
            internal Action<Camera> PerRender;
        }

        private static IEnumerator Trace(Vessel vessel, string variant, int frames, Injection injection = default(Injection))
        {
            const bool Bracket = false;
            Camera main = FlightCamera.fetch.mainCamera;
            UnityEngine.Rendering.CommandBuffer firefly = FireflyBuffer(vessel);
            const UnityEngine.Rendering.CameraEvent Where = UnityEngine.Rendering.CameraEvent.AfterForwardAlpha;
            var before = new RenderTexture(320, 180, 0, RenderTextureFormat.ARGBFloat);
            var after = new RenderTexture(320, 180, 0, RenderTextureFormat.ARGBFloat);
            var beforeBuffer = new UnityEngine.Rendering.CommandBuffer { name = "ReDefinition probe before Firefly" };
            var afterBuffer = new UnityEngine.Rendering.CommandBuffer { name = "ReDefinition probe after Firefly" };
            beforeBuffer.Blit(UnityEngine.Rendering.BuiltinRenderTextureType.CameraTarget, before);
            beforeBuffer.SetRenderTarget(UnityEngine.Rendering.BuiltinRenderTextureType.CameraTarget);
            afterBuffer.Blit(UnityEngine.Rendering.BuiltinRenderTextureType.CameraTarget, after);
            var injected = new UnityEngine.Rendering.CommandBuffer { name = "ReDefinition probe injection" };
            Action<Camera> onRender = camera =>
            {
                if (camera != main) return;
                renderState = CameraState(camera, vessel) + " | " + AirstreamPlace(vessel);
                if (injection.PerRender != null) injection.PerRender(camera);
                injected.Clear();
                if (injection.Build != null) injection.Build(injected, camera);
            };
            if (Bracket && firefly != null)
            {
                main.RemoveCommandBuffer(Where, firefly);
                main.AddCommandBuffer(Where, beforeBuffer);
                main.AddCommandBuffer(Where, injected);
                main.AddCommandBuffer(Where, firefly);
                main.AddCommandBuffer(Where, afterBuffer);
            }
            Camera.onPreRender += CountRender;
            Camera.onPreRender += onRender.Invoke;
            renders.Clear();
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < frames; i++)
            {
                yield return new WaitForEndOfFrame();
                float drawn = Bracket && firefly != null ? Mean(after) - Mean(before) : -1f;
                Texture2D frame = ScreenCapture.CaptureScreenshotAsTexture();
                Vector3 at = main.WorldToViewportPoint((Vector3)vessel.GetWorldPos3D());
                int w = Mathf.RoundToInt(frame.width * WindowWidth), h = Mathf.RoundToInt(frame.height * WindowHeight);
                int x = Mathf.Clamp(Mathf.RoundToInt(at.x * frame.width - w * 0.5f), 0, frame.width - w);
                int y = Mathf.Clamp(Mathf.RoundToInt(at.y * frame.height - h * 0.3f), 0, frame.height - h);
                double total = 0.0;
                Color[] pixels = frame.GetPixels(x, y, w, h);
                foreach (Color c in pixels) total += 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b;
                UnityEngine.Object.Destroy(frame);
                int cameras;
                renders.TryGetValue("Camera 00[1707x960]", out cameras);
                int onScreen;
                renders.TryGetValue("Camera 00[screen]", out onScreen);
                float input = WindowMean(main.targetTexture, at);
                string endState = AirstreamPlace(vessel);
                text.Append(Time.frameCount).Append(" input ").Append(input.ToString("0.0000", CultureInfo.InvariantCulture)).Append(" drawn by Firefly ").Append(drawn.ToString("0.00000", CultureInfo.InvariantCulture)).Append(" window ")
                    .Append((total / pixels.Length).ToString("0.0000", CultureInfo.InvariantCulture)).Append(" camera renders ").Append(cameras + onScreen).Append(" | at render: ").Append(renderState).Append(" | at end: ").Append(endState)
                    .Append((char)10);
                renders.Clear();
            }
            Camera.onPreRender -= CountRender;
            Camera.onPreRender -= onRender.Invoke;
            if (Bracket && firefly != null)
            {
                main.RemoveCommandBuffer(Where, beforeBuffer);
                main.RemoveCommandBuffer(Where, injected);
                main.RemoveCommandBuffer(Where, afterBuffer);
            }
            injected.Release();
            beforeBuffer.Release();
            afterBuffer.Release();
            before.Release();
            after.Release();
            Debug.Log(Log.Tag + " Probe: reentry: trace " + variant + ":" + (char)10 + text);
        }

        // What the flight camera showed of the pod as it started to render, for Trace: the pod's viewport point and the
        // projection's centre terms and scale, and the camera's place.
        private static string renderState = "";

        private static string CameraState(Camera camera, Vessel vessel)
        {
            Vector3 at = camera.WorldToViewportPoint((Vector3)vessel.GetWorldPos3D());
            Matrix4x4 p = camera.projectionMatrix;
            Vector3 position = camera.transform.position;
            Vector3 toPod = camera.transform.InverseTransformPoint((Vector3)vessel.GetWorldPos3D());
            return "vp " + at.x.ToString("0.000", CultureInfo.InvariantCulture) + "," + at.y.ToString("0.000", CultureInfo.InvariantCulture)
                   + " proj " + p.m00.ToString("0.0000", CultureInfo.InvariantCulture) + " " + p.m11.ToString("0.0000", CultureInfo.InvariantCulture)
                   + " " + p.m02.ToString("0.0000", CultureInfo.InvariantCulture) + " " + p.m12.ToString("0.0000", CultureInfo.InvariantCulture)
                   + " aspect " + camera.aspect.ToString("0.000", CultureInfo.InvariantCulture)
                   + " pod in camera " + toPod.x.ToString("0.0", CultureInfo.InvariantCulture) + "," + toPod.y.ToString("0.0", CultureInfo.InvariantCulture)
                   + "," + toPod.z.ToString("0.0", CultureInfo.InvariantCulture);
        }

        // Where Firefly's first envelope renderer lies in its airstream texture right now, as x,y of the texture (0..1).
        // That is the renderer's bounds centre through the _AirstreamVP of the effect material. It also gives how far the
        // renderer is from the vessel's centre of mass and from the airstream camera.
        private static string AirstreamPlace(Vessel vessel)
        {
            Type moduleType = TypeLookup.Find("Firefly.AtmoFxModule");
            foreach (VesselModule module in vessel.vesselModules)
            {
                if (module == null || module.GetType() != moduleType) continue;
                object fx = moduleType.GetField("fxVessel", BindingFlags.Public | BindingFlags.Instance).GetValue(module);
                if (fx == null) return "no fx";
                var material = fx.GetType().GetField("material", BindingFlags.Public | BindingFlags.Instance).GetValue(fx) as Material;
                var list = fx.GetType().GetField("fxEnvelope", BindingFlags.Public | BindingFlags.Instance).GetValue(fx) as System.Collections.IList;
                var camera = fx.GetType().GetField("airstreamCamera", BindingFlags.Public | BindingFlags.Instance).GetValue(fx) as Camera;
                if (material == null || list == null || list.Count == 0 || camera == null) return "no envelope";
                object item = list[0];
                var renderer = item.GetType().GetField("renderer").GetValue(item) as Renderer;
                if (renderer == null) return "no renderer";
                Matrix4x4 m = material.GetMatrix("_AirstreamVP");
                Vector3 centre = renderer.bounds.center;
                Vector4 clip = m * new Vector4(centre.x, centre.y, centre.z, 1f);
                float u = clip.x / clip.w * 0.5f + 0.5f, v = clip.y / clip.w * 0.5f + 0.5f;
                float away = Vector3.Distance(centre, (Vector3)vessel.GetWorldPos3D());
                float fromCamera = Vector3.Distance(centre, camera.transform.position);
                return "envelope in airstream tex " + u.ToString("0.000", CultureInfo.InvariantCulture) + "," + v.ToString("0.000", CultureInfo.InvariantCulture)
                       + " (" + list.Count + " envelope(s), " + away.ToString("0.0", CultureInfo.InvariantCulture) + " m from the CoM, "
                       + fromCamera.ToString("0.0", CultureInfo.InvariantCulture) + " m from the airstream camera, ortho size "
                       + camera.orthographicSize.ToString("0.0", CultureInfo.InvariantCulture) + ", renderer enabled " + renderer.enabled + ")";
            }
            return "no module";
        }

        // The vessel whose Firefly buffer FireflyLast moves.
        private static Vessel lastFor;

        // Firefly's buffer last at AfterForwardAlpha as the flight camera starts to render.
        private static void FireflyLast(Camera camera)
        {
            UnityEngine.Rendering.CommandBuffer buffer = lastFor != null ? FireflyBuffer(lastFor) : null;
            if (buffer == null || camera != Main()) return;
            camera.RemoveCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterForwardAlpha, buffer);
            camera.AddCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterForwardAlpha, buffer);
        }

        // The angle between the camera's line of sight and the pod's path through the air.
        private static float ViewAngle(Vessel vessel)
        {
            Vector3 path = ((Vector3)vessel.srf_velocity).normalized;
            return Vector3.Angle(FlightCamera.fetch.mainCamera.transform.forward, path);
        }

        // The flight camera.
        private static Camera Main()
        {
            return FlightCamera.fetch.mainCamera;
        }

        // The camera at the side and a little behind the pod, 25 m out.
        private static void Look(Vessel vessel)
        {
            Vector3d position = vessel.GetWorldPos3D();
            Vector3 up = (Vector3)(position - vessel.mainBody.position).normalized;
            Vector3 heading = Vector3.ProjectOnPlane((Vector3)vessel.srf_velocity, up).normalized;
            if (heading.sqrMagnitude < 0.5f) heading = Vector3.Cross(up, (Vector3)vessel.mainBody.RotationAxis).normalized;
            Vector3 side = Vector3.Cross(up, heading);
            FlightCamera.fetch.SetDistance(25f);
            FlightCamera.fetch.SetCamCoordsFromPosition(-heading * 8f + side * 20f + up * 6f);
        }

        // For each of MeasuredFrames rendered frames, the mean brightness of the window round the pod (its tail of plasma
        // runs up and back) and how much its pixels changed since the frame before, as a share of that mean.
        private static IEnumerator Measure(Vessel vessel, List<float> means, List<float> changes)
        {
            means.Clear();
            changes.Clear();
            float[] previous = null;
            for (int i = 0; i < MeasuredFrames; i++)
            {
                yield return new WaitForEndOfFrame();
                Texture2D frame = ScreenCapture.CaptureScreenshotAsTexture();
                Camera camera = FlightCamera.fetch.mainCamera;
                Vector3 at = camera.WorldToViewportPoint((Vector3)vessel.GetWorldPos3D());
                int w = Mathf.RoundToInt(frame.width * WindowWidth), h = Mathf.RoundToInt(frame.height * WindowHeight);
                int x = Mathf.Clamp(Mathf.RoundToInt(at.x * frame.width - w * 0.5f), 0, frame.width - w);
                int y = Mathf.Clamp(Mathf.RoundToInt(at.y * frame.height - h * 0.3f), 0, frame.height - h);
                Color[] pixels = frame.GetPixels(x, y, w, h);
                var lum = new float[pixels.Length];
                double sum = 0.0;
                for (int p = 0; p < pixels.Length; p++)
                {
                    lum[p] = 0.2126f * pixels[p].r + 0.7152f * pixels[p].g + 0.0722f * pixels[p].b;
                    sum += lum[p];
                }
                float mean = (float)(sum / pixels.Length);
                means.Add(mean);
                if (previous != null && previous.Length == lum.Length)
                {
                    double change = 0.0;
                    for (int p = 0; p < lum.Length; p++) change += Math.Abs(lum[p] - previous[p]);
                    changes.Add(mean > 1e-6f ? (float)(change / lum.Length) / mean : 0f);
                }
                previous = lum;
                UnityEngine.Object.Destroy(frame);
            }
        }

        // The mean of the means, their standard deviation, and the mean change from one frame to the next over the mean.
        private static void Describe(List<float> means, out float mean, out float deviation, out float flicker)
        {
            mean = 0f;
            foreach (float m in means) mean += m;
            mean /= Mathf.Max(1, means.Count);
            double square = 0.0, jump = 0.0;
            for (int i = 0; i < means.Count; i++)
            {
                square += (means[i] - mean) * (means[i] - mean);
                if (i > 0) jump += Math.Abs(means[i] - means[i - 1]);
            }
            deviation = (float)Math.Sqrt(square / Mathf.Max(1, means.Count));
            flicker = means.Count > 1 && mean > 1e-6f ? (float)(jump / (means.Count - 1)) / mean : 0f;
        }

        // The run: the wait for the air, the hold, the variants.
        internal static IEnumerator Run(string folder, string shot)
        {
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (addon == null || vessel == null || FlightCamera.fetch == null) yield break;
            GameEvents.onHideUI.Fire();
            float until = Time.realtimeSinceStartup + 240f;
            while (vessel.altitude > 85000.0 && Time.realtimeSinceStartup < until) yield return null;
            Debug.Log(Log.Tag + " Probe: reentry: the pod at " + vessel.altitude.ToString("0") + " m, " + vessel.srfSpeed.ToString("0") + " m/s; coasting.");
            bool running = true;
            addon.StartCoroutine(Coast(vessel, () => running));
            addon.StartCoroutine(Sample(vessel, () => running));
            until = Time.realtimeSinceStartup + 90f;
            while (FireflyStrength(vessel) < StrongEnough && Time.realtimeSinceStartup < until) yield return null;
            Debug.Log(Log.Tag + " Probe: reentry: Firefly's strength " + FireflyStrength(vessel).ToString("0.0", CultureInfo.InvariantCulture) + " at "
                      + vessel.altitude.ToString("0") + " m, " + vessel.srfSpeed.ToString("0") + " m/s.");
            lastFor = vessel;
            OwnSettings original = addon.Live.Clone();
            // In an order that never has to switch the upscaler or frame generation on again: the window refuses that where no
            // profile was chosen, and the variants after would run without them.
            var variants = new List<Variant>
            {
                new Variant { Name = "fix-on", Edit = null },
                new Variant { Name = "fix-off", Edit = null, After = Switch("ReDefinition.Scenery.FireflyInterpolation", "Enabled", false) },
                new Variant { Name = "fix-on-again", Edit = null, After = Switch("ReDefinition.Scenery.FireflyInterpolation", "Enabled", true) },
                new Variant { Name = "interpolation-off", Edit = null, After = Switch("ReDefinition.Upscaler.RenderInterpolation", "Enabled", false) },
                new Variant { Name = "upscaler-off", Edit = s => { s.FrameGeneration = false; s.Enabled = false; } },
            };
            var means = new List<float>();
            var changes = new List<float>();
            foreach (Variant variant in variants)
            {
                Debug.Log(Log.Tag + " Probe: reentry: variant " + variant.Name + " starts at " + Time.realtimeSinceStartup.ToString("0.00", CultureInfo.InvariantCulture) + " s.");
                Change(original, variant.Edit);
                if (variant.After != null) variant.After();
                Look(vessel);
                yield return new WaitForSecondsRealtime(SettleSeconds);
                LogBuffers("in " + variant.Name);
                // Firefly's buffer behind every other at its event each frame: the order before the rig kept its own buffer last.
                bool last = variant.Name == "firefly-last";
                if (last) Camera.onPreRender += FireflyLast;
                yield return Trace(vessel, variant.Name, 20);
                float strength = FireflyStrength(vessel);
                float started = Time.realtimeSinceStartup;
                yield return Measure(vessel, means, changes);
                float seconds = Time.realtimeSinceStartup - started;
                float mean, deviation, flicker, change = 0f;
                Describe(means, out mean, out deviation, out flicker);
                foreach (float c in changes) change += c;
                change /= Mathf.Max(1, changes.Count);
                string series = "";
                for (int i = 0; i < means.Count; i++) series += (i > 0 ? " " : "") + means[i].ToString("0.0000", CultureInfo.InvariantCulture);
                Debug.Log(Log.Tag + " Probe: reentry: " + variant.Name + ": strength " + strength.ToString("0.0", CultureInfo.InvariantCulture) + ", "
                          + vessel.srfSpeed.ToString("0") + " m/s at " + vessel.altitude.ToString("0") + " m; the window round the pod " + mean.ToString("0.0000", CultureInfo.InvariantCulture)
                          + ", deviation " + deviation.ToString("0.0000", CultureInfo.InvariantCulture) + ", flicker " + flicker.ToString("0.000", CultureInfo.InvariantCulture)
                          + ", pixels change " + change.ToString("0.000", CultureInfo.InvariantCulture)
                          + ", " + (means.Count / Mathf.Max(seconds, 1e-3f)).ToString("0") + " rendered frames a second; frames: " + series + ".");
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, shot + "-reentry-" + variant.Name + ".png"));
                yield return null;
                if (variant.Name == "fix-on" || variant.Name == "fix-off" || variant.Name == "upscaler-off")
                {
                    bool started2 = ScreenRecordingBridge.Start(120, false);
                    Debug.Log(Log.Tag + " Probe: reentry: the frames the monitor shows, " + variant.Name + (started2 ? ", recording" : ": not recorded: "
                              + ScreenRecordingBridge.State()) + ".");
                    yield return ProbeSteps.RecordingSaved(60f);
                    Debug.Log(Log.Tag + " Probe: reentry: recording " + variant.Name + ": " + ScreenRecordingBridge.State());
                }
                if (last) Camera.onPreRender -= FireflyLast;
            }
            Change(original, null);
            running = false;
            Debug.Log(Log.Tag + " Probe: reentry done.");
        }
    }
}
