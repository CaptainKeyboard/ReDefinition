using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // The numbers of one frame of the layer pass that are not the layers themselves: the body's frame, the light, the march quality and
    // the debug views. A plain object the caller keeps and rewrites each frame (no allocation).
    internal sealed class LayerFrame
    {
        // The planet's centre in world space, and the rotation from world space into the map frame (x towards longitude 0, y towards
        // 90 east, z north). Identity: the world is the map frame.
        public Vector3 PlanetCenter = Vector3.zero;
        public Matrix4x4 WorldToMap = Matrix4x4.identity;
        // The light: towards the sun, the sun's colour (scaled by the scene's brightness) and the sky above and below.
        public Vector3 TowardsSun = Vector3.up;
        public Color SunColour = Color.white, AmbientTop = Color.gray, AmbientBottom = new Color(0.4f, 0.4f, 0.4f);
        // The march: most steps per span, steps towards the sun, the growth of the step with the distance (per m), the shortest step
        // in m, the transmittance at which a ray ends, the jitter share (0: the midpoint rule, 1: the interleaved gradient noise).
        public int MaxSteps = 48, SunSteps = 3;
        public float StepGrowth = 0.0002f, MinStepM = 25f, EarlyOut = 0.01f, Jitter = 1f;
        public float SunMarchM = 4000f;
        public int FrameNumber;
        // The dual-lobe phase function and the single-scattering albedo: forward g, back g, forward weight, albedo.
        public Vector4 Phase = new Vector4(0.8f, -0.3f, 0.9f, 0.9f);
        // A body with a surface ends the ray on the sea-level sphere; a gas giant lets it through the hollow shell (P24).
        public bool HasSurface = true;
        // The march target's size over the camera's, and whether the scene's depth limits the march and the composite (P64).
        public float Resolution = 0.5f;
        public bool UseSceneDepth = true;
        // Debug and measurement: the longest ray in m (0: none), the aid picture (0 natural, 1 optical depth, 2 layer colours) and the
        // layers drawn (bit i = layer i; 0 = all).
        public float RayLimitM;
        public int AidMode, LayerMask;
    }

    // The layer pass's drawing: the material, the type table textures, the maps and the command buffer that marches and composites. It
    // uses nothing of the game, so the weather player links it as it is (the player drives the aux pass directly).
    internal sealed class LayerRenderer
    {
        internal const string ShaderName = "ReDefinition/AtmospheresLayers";
        internal const int PassMarch = 0, PassComposite = 1, PassAux = 2;

        private static readonly int IdPlanetR = Shader.PropertyToID("_PlanetR"), IdPlanetCenter = Shader.PropertyToID("_PlanetCenter"),
            IdWorldToMap = Shader.PropertyToID("_WorldToMap"), IdCamPos = Shader.PropertyToID("_CamPos"),
            IdCamForward = Shader.PropertyToID("_CamForward"), IdCamRight = Shader.PropertyToID("_CamRight"), IdCamUp = Shader.PropertyToID("_CamUp"),
            IdLayerCount = Shader.PropertyToID("_LayerCount"), IdLayerA = Shader.PropertyToID("_LayerA"), IdLayerB = Shader.PropertyToID("_LayerB"),
            IdLayerC = Shader.PropertyToID("_LayerC"), IdLayerD = Shader.PropertyToID("_LayerD"), IdLayerE = Shader.PropertyToID("_LayerE"),
            IdTypeProfile = Shader.PropertyToID("_TypeProfile"), IdTypeInfo = Shader.PropertyToID("_TypeInfo"),
            IdCovMaps = Shader.PropertyToID("_CovMaps"), IdTypeMaps = Shader.PropertyToID("_TypeMaps"), IdMapDim = Shader.PropertyToID("_MapDim"),
            IdSun = Shader.PropertyToID("_Sun"), IdSunColor = Shader.PropertyToID("_SunColor"), IdAmbientTop = Shader.PropertyToID("_AmbientTop"),
            IdAmbientBottom = Shader.PropertyToID("_AmbientBottom"), IdMarchP = Shader.PropertyToID("_MarchP"), IdStepP = Shader.PropertyToID("_StepP"),
            IdLimitP = Shader.PropertyToID("_LimitP"), IdPhaseP = Shader.PropertyToID("_PhaseP"), IdSceneDepth = Shader.PropertyToID("_SceneDepth"),
            IdMarchTex = Shader.PropertyToID("_MarchTex");

        private readonly Vector4[] layerA = new Vector4[CloudLayerDef.MaxLayers], layerB = new Vector4[CloudLayerDef.MaxLayers],
            layerC = new Vector4[CloudLayerDef.MaxLayers], layerD = new Vector4[CloudLayerDef.MaxLayers], layerE = new Vector4[CloudLayerDef.MaxLayers];
        private Material material;
        private Texture2D profileTexture, infoTexture;
        private RenderTexture target;
        private CommandBuffer commands;
        private Camera attached;
        private CameraEvent attachedEvent;
        private int layerCount;
        private double radius;
        private long uploadedVersion = -1;
        private LayerStackSource uploadedSource;

        internal string Problem { get; private set; }
        internal bool Ready { get { return material != null && profileTexture != null; } }
        internal Material Material { get { return material; } }
        internal RenderTexture Target { get { return target; } }
        internal CommandBuffer Commands { get { return commands; } }
        internal LayerMapStore Maps { get; } = new LayerMapStore();

        internal bool Init(Shader shader)
        {
            if (shader == null)
            {
                Problem = "the layer shader is missing";
                return false;
            }
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            if (material.passCount < 3)
            {
                Problem = "the layer shader does not compile (" + material.passCount + " passes)";
                return false;
            }
            profileTexture = new Texture2D(LayerTypeTable.ProfileSamples, CloudLayerDef.MaxLayers * CloudLayerDef.MaxTypes, TextureFormat.RFloat, false, true)
            {
                name = "layer type profiles",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            infoTexture = new Texture2D(CloudLayerDef.MaxTypes, CloudLayerDef.MaxLayers, TextureFormat.RGBAFloat, false, true)
            {
                name = "layer type info",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            material.SetTexture(IdTypeProfile, profileTexture);
            material.SetTexture(IdTypeInfo, infoTexture);
            commands = new CommandBuffer { name = "ReDefinition layer pass" };
            return true;
        }

        // Puts the buffer on the camera at that event.
        internal void Attach(Camera camera, CameraEvent cameraEvent)
        {
            Detach();
            if (camera == null || commands == null) return;
            camera.AddCommandBuffer(cameraEvent, commands);
            attached = camera;
            attachedEvent = cameraEvent;
        }

        internal void Detach()
        {
            if (attached != null && commands != null) attached.RemoveCommandBuffer(attachedEvent, commands);
            attached = null;
        }

        internal void Dispose()
        {
            Detach();
            if (commands != null) commands.Release();
            if (target != null) target.Release();
            Maps.Dispose();
            UnityEngine.Object.Destroy(material);
            UnityEngine.Object.Destroy(profileTexture);
            UnityEngine.Object.Destroy(infoTexture);
            material = null;
            profileTexture = null;
        }

        // Writes the stack's layers, its type tables and its map slices into the material's arrays and textures. Cheap when nothing
        // changed (the source's version is compared); the maps are stepped by Maps.Step, not here.
        internal void Upload(LayerStackSource source)
        {
            if (!Ready || source == null) return;
            if (ReferenceEquals(source, uploadedSource) && source.Version == uploadedVersion) return;
            layerCount = source.LayerCount;
            radius = source.BodyRadiusM;
            var profile = new float[LayerTypeTable.ProfileSamples * CloudLayerDef.MaxLayers * CloudLayerDef.MaxTypes];
            var info = new Color[CloudLayerDef.MaxTypes * CloudLayerDef.MaxLayers];
            for (int l = 0; l < layerCount; l++)
            {
                CloudLayerDef layer = source.LayerAt(l);
                LayerTypeTable table = source.TableAt(l);
                float tightInner, tightOuter;
                layer.TightAltitudes(out tightInner, out tightOuter);
                layerA[l] = new Vector4(layer.InnerAltitudeM, layer.OuterAltitudeM, tightInner, tightOuter);
                layerB[l] = new Vector4(layer.SigmaPerM, layer.Coverage, layer.EdgeSoftness, (float)(int)layer.Kind);
                layerC[l] = new Vector4(layer.FadeNearM, float.IsPositiveInfinity(layer.FadeFarM) ? 0f : layer.FadeFarM,
                                        layer.CoverageMapHandle < 0 && layer.TypeMapHandle < 0 ? -1f : l, table.Count);
                layerD[l] = new Vector4(layer.NoiseAmount, layer.NoiseScaleM > 0f ? 1f / layer.NoiseScaleM : 0f, 1f, layer.MapShiftDeg);
                layerE[l] = Vector4.zero;
                Array.Copy(table.ProfileData, 0, profile, l * CloudLayerDef.MaxTypes * LayerTypeTable.ProfileSamples, table.Count * LayerTypeTable.ProfileSamples);
                for (int t = 0; t < table.Count; t++)
                {
                    float b, top, e;
                    table.Info(t, out b, out top, out e);
                    info[l * CloudLayerDef.MaxTypes + t] = new Color(b, top, e, 0f);
                }
            }
            profileTexture.SetPixelData(profile, 0);
            profileTexture.Apply(false);
            infoTexture.SetPixels(info);
            infoTexture.Apply(false);
            uploadedSource = source;
            uploadedVersion = source.Version;
        }

        // The camera for a pass: its position and basis in world space; right and up are scaled by the tangent of the half field of
        // view, and jitter is the upscaler's jitter in render pixels (it moves the rays, not the picture).
        internal void SetCamera(Vector3 position, Vector3 forward, Vector3 right, Vector3 up, Vector2 jitterNdc)
        {
            material.SetVector(IdCamPos, position);
            material.SetVector(IdCamForward, forward);
            material.SetVector(IdCamRight, new Vector4(right.x, right.y, right.z, jitterNdc.x));
            material.SetVector(IdCamUp, new Vector4(up.x, up.y, up.z, jitterNdc.y));
        }

        // Writes the frame's numbers (not the camera) into the material.
        internal void Apply(LayerFrame f)
        {
            Material m = material;
            m.SetFloat(IdPlanetR, (float)radius);
            m.SetVector(IdPlanetCenter, f.PlanetCenter);
            m.SetMatrix(IdWorldToMap, f.WorldToMap);
            m.SetFloat(IdLayerCount, layerCount);
            m.SetVectorArray(IdLayerA, layerA);
            m.SetVectorArray(IdLayerB, layerB);
            m.SetVectorArray(IdLayerC, layerC);
            m.SetVectorArray(IdLayerD, layerD);
            m.SetVectorArray(IdLayerE, layerE);
            if (Maps.Coverage != null)
            {
                m.SetTexture(IdCovMaps, Maps.Coverage);
                m.SetTexture(IdTypeMaps, Maps.Types);
                m.SetVector(IdMapDim, new Vector4(Maps.Width, Maps.Height, 0f, 0f));
            }
            else
            {
                m.SetVector(IdMapDim, new Vector4(2f, 2f, 0f, 0f));
            }
            m.SetVector(IdSun, new Vector4(f.TowardsSun.x, f.TowardsSun.y, f.TowardsSun.z, f.SunMarchM));
            m.SetVector(IdSunColor, f.SunColour);
            m.SetVector(IdAmbientTop, f.AmbientTop);
            m.SetVector(IdAmbientBottom, f.AmbientBottom);
            m.SetVector(IdMarchP, new Vector4(Mathf.Clamp(f.MaxSteps, 1, 256), Mathf.Clamp(f.SunSteps, 1, 8), f.StepGrowth, f.EarlyOut));
            m.SetVector(IdStepP, new Vector4(f.MinStepM, f.Jitter, f.FrameNumber, f.HasSurface ? 1f : 0f));
            m.SetVector(IdLimitP, new Vector4(f.RayLimitM, f.AidMode, f.LayerMask, f.UseSceneDepth ? 1f : 0f));
            m.SetVector(IdPhaseP, f.Phase);
        }

        private void EnsureTarget(Camera camera, float resolution)
        {
            int w = Mathf.Max(16, Mathf.RoundToInt(camera.pixelWidth * resolution));
            int h = Mathf.Max(16, Mathf.RoundToInt(camera.pixelHeight * resolution));
            if (target != null && target.width == w && target.height == h) return;
            if (target != null) target.Release();
            target = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = "layer march",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            target.Create();
        }

        // Records the march and the depth-aware composite into the command buffer for a camera. markStart/markMarched/markEnd (may be
        // null) are called around the passes so that the player can time them. sceneDepth is the linear eye depth of the opaque scene.
        internal void Prepare(Camera camera, LayerFrame f, RenderTargetIdentifier sceneDepth, Action<CommandBuffer> markStart,
                              Action<CommandBuffer> markMarched, Action<CommandBuffer> markEnd)
        {
            if (!Ready) return;
            EnsureTarget(camera, f.Resolution);
            float tanY = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float tanX = tanY * camera.aspect;
            Transform t = camera.transform;
            SetCamera(t.position, t.forward, t.right * tanX, t.up * tanY,
                      new Vector2(0f, 0f));
            Apply(f);
            commands.Clear();
            if (markStart != null) markStart(commands);
            commands.SetGlobalTexture(IdSceneDepth, sceneDepth);
            commands.Blit(BuiltinRenderTextureType.None, target, material, PassMarch);
            if (markMarched != null) markMarched(commands);
            commands.SetGlobalTexture(IdMarchTex, target);
            commands.Blit(target, BuiltinRenderTextureType.CameraTarget, material, PassComposite);
            if (markEnd != null) markEnd(commands);
        }
    }
}
