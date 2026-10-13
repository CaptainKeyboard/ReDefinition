using System.Collections.Generic;
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
                SelfCheck(sceneCamera);
                // Deferred's G-buffer only: forward shading takes KSP's own values as meant.
                if (HighLogic.LoadedSceneIsEditor && scenery != null && sceneCamera.actualRenderingPath == RenderingPath.DeferredShading)
                    hall.Apply(scenery.Filters);
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
                return (addon != null ? addon.Live.SunReflection : 100f) / 100f * OwnSettings.SunReflectionAt100;
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
            camera.LookDraws = LookDraws;
            // KSP's own sky, without an atmosphere that tints the sun, as neutral light; the
            // daylight then white as in KSP's own lighting (measured on the pad, 2026-10-13:
            // the sky's light 0.065, 0.135, 0.314 linear under a white sun; with Scatterer
            // 0.043, 0.093, 0.194 under a sun of 0.73, 0.57, 0.34).
            camera.SkyNeutral = () => RtKspMods.ScattererInstalled ? 0f : 1f;
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
            hall.Restore();
            foreach (Material look in lookMaterials.Values)
                if (look != null) Object.Destroy(look);
            lookMaterials.Clear();
            lookFilledAt.Clear();
            groundDraws.Clear();
            groundAt = -10f;
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

        // KSP's ground as Deferred colours it, for the forward light without Deferred
        // (RtCamera.LookDraws): the space centre's ground among the scenery, looked through
        // again once a second (ReDefinitionRtTarmac.shader), and the terrain's quads in view
        // each frame (ReDefinitionRtTerrainLook.shader); a material of ours for each of
        // theirs, its values taken again once a second.
        private readonly List<RtLookDraw> lookDraws = new List<RtLookDraw>();
        private readonly List<RtLookDraw> groundDraws = new List<RtLookDraw>();
        private float groundAt = -10f;
        private readonly List<Material> sharedMaterials = new List<Material>();
        private readonly Dictionary<Material, Material> lookMaterials = new Dictionary<Material, Material>();
        private readonly Dictionary<Material, float> lookFilledAt = new Dictionary<Material, float>();
        private readonly Stack<PQ> lookQuads = new Stack<PQ>();
        private static readonly Vector4[] layerValues = new Vector4[32];

        private List<RtLookDraw> LookDraws()
        {
            lookDraws.Clear();
            if (scenery != null && Time.unscaledTime - groundAt >= 1f)
            {
                groundAt = Time.unscaledTime;
                groundDraws.Clear();
                foreach (MeshFilter filter in scenery.Filters)
                {
                    MeshRenderer renderer = filter != null ? filter.GetComponent<MeshRenderer>() : null;
                    if (renderer == null) continue;
                    renderer.GetSharedMaterials(sharedMaterials);
                    for (int i = 0; i < sharedMaterials.Count; i++)
                        if (RtDeferredLook.KscGround(sharedMaterials[i]))
                            groundDraws.Add(new RtLookDraw { Renderer = renderer, SubMesh = i, Material = sharedMaterials[i] });
                }
            }
            foreach (RtLookDraw ground in groundDraws)
            {
                Material look = LookMaterial(ground.Material, true);
                if (look != null) lookDraws.Add(new RtLookDraw { Renderer = ground.Renderer, SubMesh = ground.SubMesh, Material = look });
            }

            CelestialBody body = RtKspMods.SceneBody();
            PQS pqs = body != null ? body.pqsController : null;
            if (pqs == null || pqs.quads == null || !pqs.isActiveAndEnabled) return lookDraws;
            lookQuads.Clear();
            foreach (PQ root in pqs.quads)
                if (root != null) lookQuads.Push(root);
            while (lookQuads.Count > 0)
            {
                PQ quad = lookQuads.Pop();
                if (quad.isSubdivided && quad.subNodes != null)
                {
                    foreach (PQ sub in quad.subNodes)
                        if (sub != null) lookQuads.Push(sub);
                    continue;
                }
                if (!quad.isVisible || !quad.isBuilt || quad.isForcedInvisible || quad.meshRenderer == null) continue;
                Material material = quad.meshRenderer.sharedMaterial;
                if (!RtDeferredLook.Ground(material) || RtDeferredLook.KscGround(material)) continue;
                Material look = LookMaterial(material, false);
                if (look != null) lookDraws.Add(new RtLookDraw { Renderer = quad.meshRenderer, SubMesh = 0, Material = look });
            }
            return lookDraws;
        }

        private Material LookMaterial(Material source, bool ksc)
        {
            Material look;
            if (!lookMaterials.TryGetValue(source, out look) || look == null)
            {
                string error;
                Shader shader = ShaderBundle.LoadShader(ksc ? "Hidden/ReDefinition/RtTarmac" : "Hidden/ReDefinition/RtTerrainLook", out error);
                if (shader == null) return null;
                look = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                lookMaterials[source] = look;
            }
            float filled;
            if (lookFilledAt.TryGetValue(source, out filled) && Time.unscaledTime - filled < 1f) return look;
            lookFilledAt[source] = Time.unscaledTime;
            if (ksc) FillKsc(look, source);
            else FillTerrain(look, source);
            return look;
        }

        private static void FillKsc(Material look, Material source)
        {
            look.SetTexture("_BlendMaskTexture", source.GetTexture("_BlendMaskTexture"));
            look.SetTextureScale("_BlendMaskTexture", source.GetTextureScale("_BlendMaskTexture"));
            look.SetTextureOffset("_BlendMaskTexture", source.GetTextureOffset("_BlendMaskTexture"));
            look.SetFloat("_ReDefinitionTarmac", RtDeferredLook.TarmacFactor(source));
            look.SetColor("_ReDefinitionNearGrass", source.HasProperty("_NearGrassTexture")
                ? RtStockTerrain.Average(source.GetTexture("_NearGrassTexture")) : Color.white);
            look.SetColor("_ReDefinitionFarGrass", source.HasProperty("_FarGrassTexture")
                ? RtStockTerrain.Average(source.GetTexture("_FarGrassTexture")) : Color.white);
            look.SetFloat("_FarGrassBlendDistance", source.HasProperty("_FarGrassBlendDistance")
                ? Mathf.Max(source.GetFloat("_FarGrassBlendDistance"), 1e-3f) : 1e6f);
            look.SetFloat("_ReDefinitionDeferredScale", RtDeferredLook.MetallicShare);
        }

        private static void FillTerrain(Material look, Material source)
        {
            bool atlas = source.HasProperty("_AtlasTex");
            foreach (string name in new[] { "_saturation", "_contrast", "_steepPower", "_lowStart", "_lowEnd", "_highStart", "_highEnd" })
                look.SetFloat(name, source.HasProperty(name) ? source.GetFloat(name) : name == "_saturation" || name == "_contrast" ? 1f : 0f);
            look.SetColor("_tintColor", source.HasProperty("_tintColor") ? source.GetColor("_tintColor") : Color.clear);
            look.SetFloat("_ReDefinitionAtlas", atlas ? 1f : 0f);
            float brightness = source.HasProperty("_albedoBrightness") ? source.GetFloat("_albedoBrightness") : 1f;
            float specular = 0f;
            if (source.HasProperty("_specularColor"))
            {
                Color s = source.GetColor("_specularColor");
                specular = Mathf.Max(s.r, Mathf.Max(s.g, s.b));
            }
            // KSP's Lambert shaders (Main - Optimised, Main Shader) take no brightness; its
            // standard-specular ones the brightness and their specular share.
            bool lambert = !source.HasProperty("_specularColor");
            look.SetFloat("_ReDefinitionStockScale", lambert ? 1f : brightness * (1f - specular));
            look.SetFloat("_ReDefinitionStockLerp", atlas ? (source.HasProperty("_ColorLerpModifier") ? source.GetFloat("_ColorLerpModifier") : 1f)
                                                 : source.HasProperty("_powerNear") ? source.GetFloat("_powerNear") : 1f);
            look.SetFloat("_ReDefinitionDeferredScale", brightness * RtDeferredLook.MetallicShare);
            look.SetColor("_ReDefinitionSteep", source.HasProperty("_steepTex") ? RtStockTerrain.Average(source.GetTexture("_steepTex")) : Color.white);
            if (atlas)
            {
                Color[] layers = RtStockTerrain.Layers(source.GetTexture("_AtlasTex"));
                for (int i = 0; i < layerValues.Length; i++)
                    layerValues[i] = i < layers.Length ? (Vector4)layers[i] : Vector4.one;
                look.SetVectorArray("_ReDefinitionLayers", layerValues);
            }
            else
            {
                look.SetColor("_ReDefinitionLow", source.HasProperty("_lowTex") ? RtStockTerrain.Average(source.GetTexture("_lowTex")) : Color.white);
                look.SetColor("_ReDefinitionMid", source.HasProperty("_midTex") ? RtStockTerrain.Average(source.GetTexture("_midTex")) : Color.white);
                look.SetColor("_ReDefinitionHigh", source.HasProperty("_highTex") ? RtStockTerrain.Average(source.GetTexture("_highTex")) : Color.white);
            }
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
        // What the raytracing works from, as one line in the log whenever it changes, checked
        // every two seconds: what the scene camera draws into, whose sea there is and whether
        // it takes the raytraced reflections, and what is missing. Lines with "missing" name
        // an input the raytracing goes without.
        private string lastCheck;
        private float nextCheck;
        private readonly StringBuilder check = new StringBuilder(256);

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
                    if (attached.Settings.Reflections && !RtLighting.ReflectionsReplaced)
                        check.Append(", missing the reflections' shader (Deferred's own or none)");
                }
            }
            if (RtKspMods.SunLight() == null) check.Append("; missing the sun's light");
            if (RtKspMods.StarBody() == null) check.Append("; missing the star");
            if (attached.Settings.Reflections && Probe() == null) check.Append("; missing the sky's probe");
            string line = check.ToString();
            if (line == lastCheck) return;
            lastCheck = line;
            Debug.Log(line);
        }

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
