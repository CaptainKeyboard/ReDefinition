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
        private Light sunLight;
        private ReflectionProbe probe;
        private GameScenes lookedUpIn = GameScenes.LOADING;
        private float lookedUpAt = float.NegativeInfinity;
        private string loggedProblem;
        // A camera raytracing failed on, not tried again until the setting changes: each
        // try leaves a packet ring behind (PacketRing, never freed).
        private Camera failedOn;
        private float nextReport;

        internal RaytracingMode Mode { get { return mode; } }

        internal void Apply(RaytracingMode newMode, int newBounces)
        {
            mode = newMode;
            bounces = Mathf.Clamp(newBounces, 1, 8);
            failedOn = null;
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
            if (attached != null && attachedTo == sceneCamera && attached.enabled) Report();
            else if (sceneCamera != failedOn) Attach(sceneCamera);
        }

        private void Attach(Camera sceneCamera)
        {
            Detach("the scene camera changed");
            string why;
            if (!RtNative.Ready(out why))
            {
                Problem("Raytracing does not run: " + why);
                return;
            }
            string bundleProblem;
            if (FsrShaderBundle.LoadShader("Hidden/ReDefinition/RtCapture", out bundleProblem) == null)
            {
                Problem("Raytracing does not run: " + bundleProblem);
                return;
            }

            RtCamera camera = sceneCamera.gameObject.AddComponent<RtCamera>();
            camera.enabled = false;
            camera.Settings = SettingsFor(mode, bounces);
            camera.ShaderSource = name =>
            {
                string error;
                return FsrShaderBundle.LoadShader(name, out error);
            };
            camera.Sun = () =>
            {
                Light sun = SunLight();
                Vector3 towards = sun != null ? -sun.transform.forward : Vector3.up;
                return new Vector4(towards.x, towards.y, towards.z, 0.0047f);
            };
            camera.SunColor = () =>
            {
                Light sun = SunLight();
                return sun != null && sun.enabled ? sun.color * sun.intensity : Color.black;
            };
            camera.SkyColor = () => RenderSettings.ambientSkyColor;
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
            camera.Cut = () => SharedFrame.ResetReason != null;
            camera.Body = () =>
            {
                CelestialBody body = HighLogic.LoadedSceneIsFlight ? FlightGlobals.currentMainBody
                                     : HighLogic.LoadedScene == GameScenes.SPACECENTER ? FlightGlobals.GetHomeBody() : null;
                if (body == null) return Vector4.zero;
                Vector3d centre = body.position;
                return new Vector4((float)centre.x, (float)centre.y, (float)centre.z, (float)body.Radius);
            };

            vessels = new RtVesselSource(camera.Scene);
            terrain = new RtTerrainSource(camera.Scene);
            scenery = new RtScenerySource(camera.Scene);
            camera.Scene.AddSource(vessels);
            camera.Scene.AddSource(terrain);
            camera.Scene.AddSource(scenery);
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

        // The scene's sun: its brightest directional light; with the reflection probe,
        // looked up on a scene change and every five seconds, as either may come later.
        private Light SunLight()
        {
            LookUp();
            return sunLight;
        }

        private ReflectionProbe Probe()
        {
            LookUp();
            return probe;
        }

        private void LookUp()
        {
            if (lookedUpIn == HighLogic.LoadedScene && Time.unscaledTime - lookedUpAt < 5f) return;
            lookedUpIn = HighLogic.LoadedScene;
            lookedUpAt = Time.unscaledTime;
            sunLight = null;
            foreach (Light light in Object.FindObjectsOfType<Light>())
                if (light.type == LightType.Directional && (sunLight == null || light.intensity > sunLight.intensity))
                    sunLight = light;
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
                .Append(scenery.Renderers).Append(" scenery renderers), ")
                .Append(attached.Scene.MeshCount).Append(" meshes, ").Append(attached.Scene.MaterialCount).Append(" materials");
            if (attached.Scene.Skipped > 0)
                line.Append(", ").Append(attached.Scene.Skipped).Append(" skipped (").Append(attached.Scene.LastSkip).Append(')');
            line.Append("; GPU ms sun ").Append(attached.PassTimings[0].ToString("0.00"))
                .Append(", occlusion ").Append(attached.PassTimings[1].ToString("0.00"))
                .Append(", diffuse ").Append(attached.PassTimings[2].ToString("0.00"))
                .Append(", reflections ").Append(attached.PassTimings[3].ToString("0.00"))
                .Append(", denoiser ").Append(attached.PassTimings[6].ToString("0.00"))
                .Append("; ").Append(status);
            Debug.Log(line.ToString());
        }
    }
}
