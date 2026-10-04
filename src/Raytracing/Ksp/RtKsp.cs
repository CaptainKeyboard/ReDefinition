using System.Text;
using ReDefinition.Core;
using ReDefinition.Shared;
using ReDefinition.Upscaler;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // How much of the lighting is raytraced (docs/development/raytracing.md, "What
    // raytracing replaces").
    internal enum RaytracingMode
    {
        Off,
        Shadows,
        Light,
        Full,
    }

    // The raytracing in KSP: the scene camera's RtCamera while the setting is on, fed
    // from KSP's scene -- vessels, terrain, the space centre -- with the sun, the sky,
    // the floating origin's shifts and the upscaler's jitter. It needs no other mod:
    // where Deferred, Scatterer or EVE are there, the lighting shaders fit in with
    // them.
    internal sealed class RtKsp
    {
        private RaytracingMode mode;
        private int bounces = 2;
        private RtCamera attached;
        private Camera attachedTo;
        private RtVesselSource vessels;
        private RtTerrainSource terrain;
        private RtScenerySource scenery;
        private RtParallaxScatter scatter;
        private ReflectionProbe probe;
        private readonly SeaFrameAnchor seaFrame = new SeaFrameAnchor();
        private GameScenes lookedUpIn = GameScenes.LOADING;
        private float lookedUpAt = float.NegativeInfinity;
        private string loggedProblem;
        // A camera raytracing failed on, not tried again until the setting changes: each
        // try leaves a packet ring behind (PacketRing, never freed).
        private Camera failedOn;
        private float nextReport;
        // When attaching may be tried again after the proxy or the shaders were not ready:
        // not every frame, which would ask the proxy and load the bundle each time.
        private float retryAt;
        private const float RetrySeconds = 5f;

        internal RaytracingMode Mode { get { return mode; } }

        internal void Apply(RaytracingMode newMode, int newBounces)
        {
            mode = newMode;
            bounces = Mathf.Clamp(newBounces, 1, 8);
            failedOn = null;
            retryAt = 0f;
            if (attached != null) attached.Settings = SettingsFor(mode, bounces);
            if (mode == RaytracingMode.Off) Detach("off");
        }

        internal static RtSettings SettingsFor(RaytracingMode mode, int bounces)
        {
            RtSettings settings = RtSettings.Full();
            settings.Shadows = mode != RaytracingMode.Off;
            settings.Occlusion = mode == RaytracingMode.Light || mode == RaytracingMode.Full;
            settings.Diffuse = settings.Occlusion;
            settings.Reflections = mode == RaytracingMode.Full;
            settings.Bounces = bounces;
            return settings;
        }

        // Every frame, from the add-on's Update: on the scene camera while on.
        internal void Update(Camera sceneCamera)
        {
            if (mode == RaytracingMode.Off || sceneCamera == null)
            {
                Detach(sceneCamera == null ? "no scene camera" : "off");
                return;
            }
            // What the scene camera draws is what the rays see: FreeIva's and
            // ThroughTheEyes' IVA layers stay out, as the internal camera draws them.
            RtFilter.CullingMask = sceneCamera.cullingMask;
            RtFilter.FindOnCameras(FlightCamera.fetch != null ? FlightCamera.fetch.transform : null);
            if (attached != null && attachedTo == sceneCamera && attached.enabled)
            {
                RtKspMods.HoldPlanetShine(mode == RaytracingMode.Light || mode == RaytracingMode.Full);
                RtKspMods.HoldDeferredReflections(attached.Settings.Reflections && RtLighting.ReflectionsReplaced
                                                  && !RtKspMods.OceanNeedsDeferredReflections(), sceneCamera);
                Report();
            }
            else if (sceneCamera != failedOn && Time.realtimeSinceStartup >= retryAt) Attach(sceneCamera);
        }

        private void Attach(Camera sceneCamera)
        {
            Detach("the scene camera changed");
            string why;
            if (!RtNative.Ready(out why))
            {
                Problem("Raytracing does not run: " + why);
                retryAt = Time.realtimeSinceStartup + RetrySeconds;
                return;
            }
            string bundleProblem;
            if (ShaderBundle.LoadShader("Hidden/ReDefinition/RtCapture", out bundleProblem) == null)
            {
                Problem("Raytracing does not run: " + bundleProblem);
                retryAt = Time.realtimeSinceStartup + RetrySeconds;
                return;
            }

            RtCamera camera = sceneCamera.gameObject.AddComponent<RtCamera>();
            camera.enabled = false;
            camera.Settings = SettingsFor(mode, bounces);
            camera.ShaderSource = name =>
            {
                string error;
                return ShaderBundle.LoadShader(name, out error);
            };
            camera.ComputeSource = name =>
            {
                string error;
                return ShaderBundle.LoadCompute(name, out error);
            };
            camera.Sun = () =>
            {
                Light sun = RtKspMods.SunLight();
                Vector3 towards = sun != null ? -sun.transform.forward : Vector3.up;
                return new Vector4(towards.x, towards.y, towards.z, 0.0047f);
            };
            camera.SunColor = () =>
            {
                Light sun = RtKspMods.SunLight();
                return sun != null && sun.enabled ? sun.color * sun.intensity : Color.black;
            };
            camera.SunLight = RtKspMods.SunLight;
            camera.Environment = () =>
            {
                ReflectionProbe found = Probe();
                return found != null ? found.texture : null;
            };
            camera.WorldShift = () => Shift(SharedFrame.OriginShift);
            camera.WorldShiftFar = () => Shift(SharedFrame.BodyShift);
            camera.RenderedProjection = () =>
            {
                Matrix4x4 projection;
                UpscalerRig rig = UpscalerRig.Current;
                return rig != null && rig.RenderedProjection(sceneCamera, out projection) ? projection : sceneCamera.projectionMatrix;
            };
            camera.UnjitteredProjection = () =>
                CameraRedirect.JitterActive ? sceneCamera.nonJitteredProjectionMatrix : sceneCamera.projectionMatrix;
            camera.Jitter = () =>
            {
                UpscalerRig rig = UpscalerRig.Current;
                return rig != null && CameraRedirect.JitterActive ? rig.CurrentJitter : Vector2.zero;
            };
            camera.Cut = () => SharedFrame.ResetReason != null;
            // The trace before the lighting of its own frame (RtCamera.TraceBeforeLighting),
            // with the upscaler's jitter applied first.
            camera.TraceBeforeLighting = true;
            camera.BeforeTrace = () =>
            {
                CameraRedirect redirect = sceneCamera.GetComponent<CameraRedirect>();
                if (redirect != null) redirect.EnsureJitter();
            };
            camera.Body = () =>
            {
                CelestialBody body = RtKspMods.SceneBody();
                if (body == null) return Vector4.zero;
                Vector3d centre = body.position;
                return new Vector4((float)centre.x, (float)centre.y, (float)centre.z, (float)body.Radius);
            };
            camera.LegacySmoothness = () => true;
            camera.Sea = (out Vector3 origin, out Vector3 east, out Vector3 north) =>
                seaFrame.Frame(RtKspMods.SceneBody(), sceneCamera.transform.position, out origin, out east, out north);
            camera.Lights = (lights, surfaces) =>
            {
                CelestialBody body = RtKspMods.SceneBody();
                if (body == null) return 0;
                ReDefinitionAddon addon = ReDefinitionAddon.Instance;
                float percent = addon != null ? addon.Live.RaytracingMoonlight : 10f;
                float mirrored = addon != null ? addon.Live.RaytracingMoonReflection : 100f;
                return RtReflectors.Fill(lights, surfaces, sceneCamera.transform.position, body, percent, mirrored);
            };
            camera.PlanetAlbedo = () =>
            {
                CelestialBody body = RtKspMods.SceneBody();
                return RtReflectors.PlanetAlbedo(body);
            };

            camera.OceanReflections = RtKspMods.OceanReadsHeldReflections;
            camera.StockWater = RtKspMods.StockOcean;
            camera.SunMirror = () =>
            {
                ReDefinitionAddon addon = ReDefinitionAddon.Instance;
                return (addon != null ? addon.Live.SunReflection : 100f) / 100f;
            };
            camera.NightLight = () =>
            {
                ReDefinitionAddon addon = ReDefinitionAddon.Instance;
                return (addon != null ? addon.Live.RaytracingNightAmbient : 0f) / 100f;
            };

            vessels = new RtVesselSource(camera.Scene);
            terrain = new RtTerrainSource(camera.Scene);
            scenery = new RtScenerySource(camera.Scene);
            camera.Scene.AddSource(vessels);
            camera.Scene.AddSource(terrain);
            camera.Scene.AddSource(scenery);
            scatter = new RtParallaxScatter();
            camera.Scene.AddGpuSource(scatter);
            camera.enabled = true;
            if (camera.Problem != null)
            {
                Problem("Raytracing does not run: " + camera.Problem);
                vessels.Clear();
                terrain.Clear();
                Object.Destroy(camera);
                failedOn = sceneCamera;
                return;
            }
            attached = camera;
            attachedTo = sceneCamera;
            loggedProblem = null;
            nextReport = Time.unscaledTime + 5f;
            Debug.Log(Log.Tag + " Raytracing on " + sceneCamera.name + ": " + mode + ", " + bounces + " bounce(s)"
                      + (RtLighting.ReflectionsReplaced ? ", with Deferred's lighting" : "") + ".");
        }

        private static RtPosition Shift(Vector3d shift)
        {
            return new RtPosition(shift.x, shift.y, shift.z);
        }

        // Also after KSP destroyed the scene camera with its RtCamera on a scene change:
        // the sources still hold their baked meshes then.
        internal void Detach(string why)
        {
            RtKspMods.HoldPlanetShine(false);
            RtKspMods.HoldDeferredReflections(false, null);
            if (ReferenceEquals(attached, null)) return;
            vessels?.Clear();
            terrain?.Clear();
            if (attached != null) Object.Destroy(attached);
            attached = null;
            attachedTo = null;
            Debug.Log(Log.Tag + " Raytracing off: " + why + ".");
        }

        private void Problem(string text)
        {
            if (text == loggedProblem) return;
            loggedProblem = text;
            Debug.Log(Log.Tag + " " + text);
        }

        private ReflectionProbe Probe()
        {
            LookUp();
            return probe;
        }

        // The reflection probe, looked up on a scene change and every five seconds, as it
        // may come later.
        private void LookUp()
        {
            if (lookedUpIn == HighLogic.LoadedScene && Time.unscaledTime - lookedUpAt < 5f) return;
            lookedUpIn = HighLogic.LoadedScene;
            lookedUpAt = Time.unscaledTime;
            probe = null;
            foreach (ReflectionProbe found in Object.FindObjectsOfType<ReflectionProbe>())
                if (found.isActiveAndEnabled && found.texture != null)
                {
                    probe = found;
                    break;
                }
        }

        // What went into the scene and what it cost, every thirty seconds into the log.
        private void Report()
        {
            if (Time.unscaledTime < nextReport) return;
            nextReport = Time.unscaledTime + 30f;
            string status = attached.Status();
            var line = new StringBuilder(Log.Tag).Append(" Raytracing: ")
                .Append(attached.Scene.InstanceCount).Append(" instances (")
                .Append(vessels.Parts).Append(" parts, ").Append(terrain.Quads).Append(" terrain quads, ")
                .Append(scenery.Renderers).Append(" scenery renderers, ").Append(scatter.Buffers).Append(" scatter buffers with ")
                .Append(attached.Scene.GpuTotal).Append(" places), ")
                .Append(attached.Scene.MeshCount).Append(" meshes, ").Append(attached.Scene.MaterialCount).Append(" materials");
            if (attached.Scene.Skipped > 0)
                line.Append(", ").Append(attached.Scene.Skipped).Append(" skipped (").Append(attached.Scene.LastSkip).Append(')');
            line.Append("; GPU ms sun ").Append(attached.PassTimings[0].ToString("0.00"))
                .Append(", occlusion ").Append(attached.PassTimings[1].ToString("0.00"))
                .Append(", diffuse ").Append(attached.PassTimings[2].ToString("0.00"))
                .Append(", reflections ").Append(attached.PassTimings[3].ToString("0.00"))
                .Append(", denoiser ").Append(attached.PassTimings[6].ToString("0.00"))
                .Append("; ").Append(RtReflectors.Describe())
                .Append("; ").Append(status);
            Debug.Log(line.ToString());
        }
    }
}
