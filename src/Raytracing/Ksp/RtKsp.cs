using System.Text;
using ReDefinition.Api.Internal;
using ReDefinition.Core;
using ReDefinition.Modules;
using ReDefinition.Proxy;
using ReDefinition.Upscaler;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // How much of the lighting is raytraced (docs/development/raytracing.md, "What
    // raytracing replaces").
    internal enum RaytracingMode
    {
        // KSP's and the mods' own lighting alone.
        Off,
        // The sun's shadows raytraced.
        Shadows,
        // Also the occlusion and the light from the sky and the surfaces around.
        Light,
        // What Light raytraces, and the reflections.
        Full,
    }

    // The colour of the raytraced light from the sky and the surfaces around a point
    // (ReDefinitionDeferredReflections.shader, RtCamera.AmbientColour). Its strength is the
    // rays' in every case. The colour is its own, or the colour of Deferred's ambient light, as without
    // the raytracing, or grey, so that a surface in shadow keeps its own colour. Measured
    // 2026-10-19 at the launch pad, against the frame without the raytracing: the
    // vegetation's chromaticity is off by 13 / 14 thousandths by day, 14 / 19 in the evening
    // and 25 / 21 at night with its own. With grey it is off by at most 6.
    internal enum RaytracingLightColour
    {
        // The rays' own colour.
        Raytraced,
        // The colour of Deferred's ambient light, as without the raytracing.
        AsWithout,
        // Grey, so that a surface in shadow keeps its own colour.
        Neutral,
    }

    // The raytracing in KSP: the scene camera's RtCamera while the setting is on, fed
    // from KSP's scene -- vessels, terrain, the space centre -- with the sun, the sky,
    // the floating origin's shifts and the upscaler's jitter. It needs no other mod:
    // where Deferred, Scatterer or EVE are there, the lighting shaders fit in with
    // them.
    internal sealed class RtKsp
    {
        // The setting's default, while no addon holds the player's choice.
        private static readonly RaytracingLightColour DefaultLightColour = new OwnSettings().RaytracingLightColour;

        // The mode and bounces applied; the RtCamera on the scene camera and that camera; the scene's sources.
        private RaytracingMode mode;
        private int bounces = 2;
        private RtCamera attached;
        private Camera attachedTo;
        private RtVesselSource vessels;
        private RtTerrainSource terrain;
        private RtScenerySource scenery;
        // EVE's cloud layer for the rays, where EVE draws one.
        private readonly RtEveClouds clouds = new RtEveClouds();
        private RtParallaxScatter scatter;
        private ReflectionProbe probe;
        private readonly SeaFrameAnchor seaFrame = new SeaFrameAnchor();
        private readonly EditorHallShine hall = new EditorHallShine();
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

        // The mode last applied.
        internal RaytracingMode Mode { get { return mode; } }

        // The player's mode and bounces (1 to 8), handed to the running camera; a camera that failed is tried again.
        internal void Apply(RaytracingMode newMode, int newBounces)
        {
            mode = newMode;
            bounces = Mathf.Clamp(newBounces, 1, 8);
            failedOn = null;
            retryAt = 0f;
            if (attached != null) attached.Settings = SettingsFor(mode, bounces);
            if (mode == RaytracingMode.Off) Detach("off");
        }

        // The passes a mode runs: Shadows the sun's shadows, Light also the occlusion and the diffuse light, Full also
        // the reflections; the rest as RtSettings.Full.
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
                SelfCheck(sceneCamera);
                // Deferred's G-buffer only: forward shading takes KSP's own values as meant.
                if (HighLogic.LoadedSceneIsEditor && scenery != null && sceneCamera.actualRenderingPath == RenderingPath.DeferredShading)
                    hall.Apply(scenery.Filters);
            }
            else if (sceneCamera != failedOn && Time.realtimeSinceStartup >= retryAt) Attach(sceneCamera);
        }

        // Puts an RtCamera on the scene camera with all its inputs from KSP and the mods and its scene's sources.
        // It is tried again in RetrySeconds while the proxy or the bundle is not ready, and never again on a camera it
        // failed on.
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
                return (addon != null ? addon.Live.SunReflection : 100f) / 100f * OwnSettings.SunReflectionAt100;
            };
            camera.NightLight = () =>
            {
                ReDefinitionAddon addon = ReDefinitionAddon.Instance;
                return (addon != null ? addon.Live.RaytracingNightAmbient : 0f) / 100f;
            };
            camera.AmbientColour = () =>
            {
                ReDefinitionAddon addon = ReDefinitionAddon.Instance;
                switch (addon != null ? addon.Live.RaytracingLightColour : DefaultLightColour)
                {
                    case RaytracingLightColour.AsWithout: return 2f;
                    case RaytracingLightColour.Neutral: return 3f;
                    default: return 0f;
                }
            };

            vessels = new RtVesselSource(camera.Scene);
            terrain = new RtTerrainSource(camera.Scene);
            scenery = new RtScenerySource(camera.Scene);
            camera.Scene.AddSource(vessels);
            camera.Scene.AddSource(terrain);
            camera.Scene.AddSource(scenery);
            // KSP's own sky, without an atmosphere that tints the sun, as neutral light; the
            // daylight then white as in KSP's own lighting (measured on the pad, 2026-10-13:
            // the sky's light 0.065, 0.135, 0.314 linear under a white sun; with Scatterer
            // 0.043, 0.093, 0.194 under a sun of 0.73, 0.57, 0.34).
            camera.SkyNeutral = () => RtKspMods.ScattererInstalled ? 0f : 1f;
            // -redefinitionProbeNoRtClouds: the clouds left out of the rays, for the probe's A/B.
            bool noClouds = CommandLine.Has("-redefinitionProbeNoRtClouds");
            camera.Clouds = data => !noClouds && clouds.Fill(data, RtKspMods.SceneBody(), sceneCamera.transform.position)
                ? clouds.Texture : RtEveClouds.None(data);
            scatter = new RtParallaxScatter();
            camera.Scene.AddGpuSource(scatter);
            camera.enabled = true;
            if (camera.Problem != null)
            {
                Problem("Raytracing does not run: " + camera.Problem);
                vessels.Clear();
                terrain.Clear();
                scenery.Clear();
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

        // A floating origin shift as the native side takes it.
        private static RtPosition Shift(Vector3d shift)
        {
            return new RtPosition(shift.x, shift.y, shift.z);
        }

        // Also after KSP destroyed the scene camera with its RtCamera on a scene change:
        // the sources still hold their baked meshes then.
        internal void Detach(string why)
        {
            hall.Restore();
            clouds.Release();
            RtKspMods.HoldPlanetShine(false);
            RtKspMods.HoldDeferredReflections(false, null);
            if (ReferenceEquals(attached, null)) return;
            vessels?.Clear();
            terrain?.Clear();
            scenery?.Clear();
            if (attached != null) Object.Destroy(attached);
            attached = null;
            attachedTo = null;
            Debug.Log(Log.Tag + " Raytracing off: " + why + ".");
        }

        // Logs a reason the raytracing does not run, once until it changes.
        private void Problem(string text)
        {
            if (text == loggedProblem) return;
            loggedProblem = text;
            Debug.Log(Log.Tag + " " + text);
        }

        // The sky's reflection probe for the rays' environment, null while there is none.
        private ReflectionProbe Probe()
        {
            LookUp();
            return probe;
        }

        // Looks up the reflection probe on a scene change and every five seconds, as it
        // may come later, and every half second while there is none. KSP makes the flight's a
        // few seconds into the scene, and until then the rays would see a flat grey sky.
        private void LookUp()
        {
            if (lookedUpIn == HighLogic.LoadedScene && Time.unscaledTime - lookedUpAt < (probe != null ? 5f : 0.5f)) return;
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

        // What the raytracing works from, as one line in the log whenever it changes, checked
        // every two seconds: what the scene camera draws into, whose sea there is and whether
        // it takes the raytraced reflections, and what is missing. Lines with "missing" name
        // an input the raytracing goes without.
        private string lastCheck;
        private float nextCheck;
        private readonly StringBuilder check = new StringBuilder(256);

        // The self-check described above; the fields hold the line last logged, when the next check is due, and the
        // line's buffer.
        private void SelfCheck(Camera sceneCamera)
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 2f;
            check.Length = 0;
            check.Append(Log.Tag).Append(" Raytracing self-check: ").Append(mode).Append(", ")
                 .Append(sceneCamera.actualRenderingPath).Append(" into ")
                 .Append(sceneCamera.targetTexture != null ? "a render texture" : "the screen")
                 .Append(sceneCamera.allowHDR ? " (HDR)" : "");
            CelestialBody body = RtKspMods.SceneBody();
            check.Append("; ").Append(body != null ? body.bodyName : "no body");
            if (body != null && body.ocean)
            {
                if (RtKspMods.ScattererOcean(body))
                {
                    check.Append(", Scatterer's ocean ")
                         .Append(RtKspMods.OceanReadsHeldReflections() ? "with the raytraced reflections" : "with its own reflections");
                }
                else
                {
                    check.Append(", KSP's sea ").Append(RtCamera.StockSeaTraced ? "traced" : "not traced");
                    // Under the deferred path the sea needs the reflections' shader to be ours;
                    // without a G-buffer the forward light composes it.
                    if (attached.Settings.Reflections && sceneCamera.actualRenderingPath == RenderingPath.DeferredShading
                        && !RtLighting.ReflectionsReplaced)
                        check.Append(", missing the reflections' shader (Deferred's own or none)");
                }
            }
            if (RtKspMods.SunLight() == null) check.Append("; missing the sun's light");
            if (RtKspMods.StarBody() == null) check.Append("; missing the star");
            if (attached.Settings.Reflections && Probe() == null) check.Append("; missing the sky's probe (KSP's reflection refresh "
                             + GameSettings.REFLECTION_PROBE_REFRESH_MODE + ", its probe "
                             + (FlightCamera.fetch == null || FlightCamera.fetch.reflectionProbe == null ? "not made"
                                : FlightCamera.fetch.reflectionProbe.probeComponent == null ? "without its component"
                                : FlightCamera.fetch.reflectionProbe.probeComponent.enabled ? "on" : "off") + ")");
            string line = check.ToString();
            if (line == lastCheck) return;
            lastCheck = line;
            Debug.Log(line);
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
                .Append(scenery.Renderers).Append(" scenery renderers (sites looked through ").Append(RtScenerySource.Refreshes).Append(" times), ").Append(scatter.Buffers).Append(" scatter buffers with ")
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
