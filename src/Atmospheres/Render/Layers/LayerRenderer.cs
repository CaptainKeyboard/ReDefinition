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
        // The longest path of the light march through a layer, in m (the taps stay 3: the cost is the same). The shipped value is the short one: the parity
        // rows, the hold-out set and the T06 rows were measured at 4000 m. 4000 m leaves the base and the shaded flank of a deep storm cell as bright as its
        // top (the light comes from the first 4 km of cloud only); 9000 m darkens them (T03v2 look sheets S2, before / after) but was not re-measured by the
        // parity rows. The setting cloudSunMarchLong selects it (OwnCloudOptions); the product default is the owner's decision.
        public const float SunMarchShortM = 4000f, SunMarchLongM = 9000f;
        public float SunMarchM = SunMarchShortM;
        public int FrameNumber;
        // The dual-lobe phase function and the single-scattering albedo: forward g, back g, forward weight, albedo. Light (package T03)
        // writes its own over these when set; the light's numbers (octaves, powder, ambient, extra lights, cache) live there.
        public Vector4 Phase = new Vector4(0.8f, -0.3f, 0.9f, 0.9f);
        public CloudLightSettings Light = new CloudLightSettings();
        // The far view (package T06): the aerial perspective of the clouds and the shape noise's distance LOD; both off until the integration switches them on.
        public FarViewSettings Far = new FarViewSettings();
        // A body with a surface ends the ray on the sea-level sphere; a gas giant lets it through the hollow shell (P24).
        public bool HasSurface = true;
        // The march target's size over the camera's, and whether the scene's depth limits the march and the composite (P64).
        public float Resolution = 0.5f;
        public bool UseSceneDepth = true;
        // Debug and measurement: the longest ray in m (0: none), the aid picture (0 natural, 1 optical depth, 2 layer colours) and the
        // layers drawn (bit i = layer i; 0 = all).
        public float RayLimitM;
        public int AidMode, LayerMask;
        // The display stage of the final composite (pass 4, CloudOutputStage is its reference): exposure, the gamma the radiance is encoded with (1: the stage is
        // off, the pass writes its radiance as it is) and the white of the stage, the sun's display value of the scene. The weather player leaves it off.
        public float OutputExposure = 1f, OutputGamma = 1f, OutputWhite = 1f;
        // The cost controls of the march (package T01c). CoverageBound: the noise is evaluated only where the map can reach the threshold
        // (exact). EmptyLeap: the most grid steps one leap over empty samples takes (1: none). LightCutoff: a step whose weight in the
        // ray's colour is below this reuses the sun transmittance of the span's last lit step (0: every step marches to the sun).
        // CheapShare, FogShare: the share of the step budget that cheap and fog layers get. CostDebug: cost attribution only (bit 0 no sun
        // march, bit 1 no coverage noise, bit 2 no erosion).
        public bool CoverageBound = true;
        // The product's values: the leap and the layer-kind shares are off (they gained nothing on the view table), the sun march is reused
        // below a step weight of 0.05 and between every two lit steps.
        public float EmptyLeap = 1f, LightCutoff = 0.05f, CheapShare = 1f, FogShare = 1f;
        // LightStride: a lit step marches to the sun only every this many lit steps of a span; the steps between reuse the last result.
        public int LightStride = 2;
        public int CostDebug;
        // Package T05: inside a cloud the step grows by this share of the transmittance the ray has already lost (0: a fixed step).
        public float TransmittanceGrowth;
        // The cloud's entry and edge (stream T, the noisy look): RefineBudget is the optical depth one step may add where the ray enters a cloud
        // (it grows with the ray's optical depth so far; 0: off, the step is the grid step), RefineFraction the finest step as a share of the
        // grid step, EdgePower the exponent of the coverage share in the density (1: linear; above 1 the density's tail towards the edge is thinner).
        // The product's budget is 0.5: the entry of a cloud is found to 1/8 of the grid step (the look sheet's A/B: the grain of a single frame
        // falls by 25 to 78 percent, the dither is left to the temporal reconstruction).
        public float RefineBudget = 0f, RefineFraction = 0.25f, EdgePower = 1f;
        // Package T14, the contour gate of the entry refinement (0: off, every ray refines; above 0 the alpha step to a neighbouring pixel of its 2 x 2 quad,
        // or to a neighbouring march sample in the reconstruction's block mode, above which a ray is re-marched with the refinement on; the other rays keep
        // the grid step). RefineGateFirst is the same for the relative step of the first hit's distance (0: not used).
        public float RefineGate = 0f, RefineGateFirst = 0f;
        // Package T15, the surface refinement by bisection: the halvings of the interval between the last empty and the first dense sample that move the
        // sample to the cloud's entry (0: off). It acts only where the entry refinement above is off: on the whole picture, or in the blocks of the edge
        // mode that the edge mask does not flag when the refinement is restricted to the mask (ReconSettings.RefineAtMask).
        public int RefineBisect = 0;
        // The optical depth one step may add inside a cloud without the entry walk (the refinement's step budget alone; 0: off). Acts only where the full
        // refinement is off, like RefineBisect.
        public float RefineStepBudget = 0f;
        public const int MaxRefineBisect = 6;
        // A probe: the light cone's rotation is the same for every pixel (no per-pixel dither of the light taps).
        public bool FixedConeRotation;
        // The per-pixel dither of the step offset and of the light cone is an integer hash (white noise); false: the interleaved gradient noise,
        // whose power spectrum has discrete peaks that show as a regular pattern in a single frame (the look sheet's peak ratio 10 to 13 against 5 to 9).
        public bool HashDither = true;

        // The cost measure of package T05 on top of T01c's: the growth of the step with the lost transmittance (2). The parity jobs leave it off.
        internal void UseProductCost()
        {
            TransmittanceGrowth = 2f;
        }
    }

    // The layer pass's drawing: the material, the type table textures, the maps and the command buffer that marches and composites. It
    // uses nothing of the game, so the weather player links it as it is (the player drives the aux pass directly).
    internal sealed class LayerRenderer
    {
        internal const string ShaderName = "ReDefinition/AtmospheresLayers";
        internal const int PassMarch = 0, PassComposite = 1, PassAux = 2, PassCompositeOut = 4;

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
            IdMarchTex = Shader.PropertyToID("_MarchTex"), IdSkipP = Shader.PropertyToID("_SkipP"), IdCostP = Shader.PropertyToID("_CostP"),
            IdMarchTexel = Shader.PropertyToID("_MarchTex_TexelSize");

        // The height variation (package T13): the maps, and per layer the ranges and the ragged boundary's numbers.
        private static readonly int IdHeightMaps = Shader.PropertyToID("_HeightMaps"), IdLayerH = Shader.PropertyToID("_LayerH"),
            IdLayerH2 = Shader.PropertyToID("_LayerH2");
        private readonly Vector4[] layerH = new Vector4[CloudLayerDef.MaxLayers], layerH2 = new Vector4[CloudLayerDef.MaxLayers];
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
        // The height maps of the layers (package T13); a neutral array until a layer has maps.
        internal HeightMapStore Heights { get; } = new HeightMapStore();
        // The noise volumes and the shape model's numbers (package T02); the driver advances them once per frame (Noise.Advance).
        internal LayerNoise Noise { get; } = new LayerNoise();

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
            Heights.Ensure(2, 2);
            Noise.StartGeneration();
            return true;
        }

        // True makes Upload queue the height maps and StepHeights write them one slice a frame (the game); false writes them at once in Upload
        // (the weather player and the tests, which read the result in the same frame).
        internal bool SpreadHeights;

        // Writes the next queued height slice; cheap when none waits. Call once a frame after Upload.
        internal void StepHeights()
        {
            if (Heights.Pending) Heights.Step(1);
        }

        // Whether the buffer is on that camera at that event now (by this renderer's own bookkeeping, not the camera's list).
        internal bool IsAttachedTo(Camera camera, CameraEvent cameraEvent)
        {
            return attached != null && ReferenceEquals(attached, camera) && attachedEvent == cameraEvent;
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
            if (target != null)
            {
                target.Release();
                UnityEngine.Object.Destroy(target);
                target = null;
            }
            Maps.Dispose();
            Heights.Dispose();
            Noise.Dispose();
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
                HeightTuning height = source.Heights.TuningOf(l);
                HeightMaps heightMaps = source.Heights.MapsOf(l);
                layerH[l] = height == null ? Vector4.zero
                    : new Vector4(heightMaps == null ? 0f : heightMaps.RootRangeM, heightMaps == null ? 0f : heightMaps.TopRangeM,
                                  heightMaps == null ? 0f : heightMaps.VariationRangeM, height.BaseRagged ? height.RaggedM : 0f);
                layerH2[l] = height == null ? Vector4.zero
                    : new Vector4(1f / Mathf.Max(1f, height.RaggedScaleM), height.MinThicknessM, source.Heights.SeedOf(l) & 0xFFFFFF, heightMaps == null ? 0f : 1f);
                Array.Copy(table.ProfileData, 0, profile, l * CloudLayerDef.MaxTypes * LayerTypeTable.ProfileSamples, table.Count * LayerTypeTable.ProfileSamples);
                for (int t = 0; t < table.Count; t++)
                {
                    float b, top, e;
                    table.Info(t, out b, out top, out e);
                    info[l * CloudLayerDef.MaxTypes + t] = new Color(b, top, e, 0f);
                }
            }
            for (int l = layerCount; l < CloudLayerDef.MaxLayers; l++) layerH[l] = layerH2[l] = Vector4.zero;
            if (SpreadHeights) Heights.Queue(source.Heights, layerCount);
            else Heights.Upload(source.Heights, layerCount);
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
            m.SetVectorArray(IdLayerH, layerH);
            m.SetVectorArray(IdLayerH2, layerH2);
            if (Heights.Maps != null) m.SetTexture(IdHeightMaps, Heights.Maps);
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
            m.SetVector(Shader.PropertyToID("_OutP"), new Vector4(Mathf.Max(0f, f.OutputExposure), 1f / Mathf.Max(1f, f.OutputGamma), Mathf.Max(1e-4f, f.OutputWhite), f.OutputGamma > 1.0001f ? 1f : 0f));
            m.SetVector(IdSkipP, new Vector4(Mathf.Max(1f, f.EmptyLeap), Mathf.Max(0f, f.LightCutoff), Mathf.Clamp01(f.CheapShare), Mathf.Clamp01(f.FogShare)));
            // The block sampling of the temporal reconstruction is switched on by its own Record after this call, never left over.
            m.SetVector(Shader.PropertyToID("_ReconP"), new Vector4(1f, 0f, 0f, 0f));
            m.SetVector(IdCostP,new Vector4(f.CoverageBound ? 1f : 0f, f.CostDebug, Mathf.Max(1, f.LightStride), Mathf.Max(0f, f.TransmittanceGrowth)));
            m.SetVector(Shader.PropertyToID("_RefineP"), new Vector4(Mathf.Max(0f, f.RefineBudget), Mathf.Clamp(f.RefineFraction, 0.01f, 1f), Mathf.Max(0.1f, f.EdgePower), f.HashDither ? 2f : (f.FixedConeRotation ? 1f : 0f)));
            m.SetVector(Shader.PropertyToID("_RefineG"), new Vector4(Mathf.Max(0f, f.RefineGate), Mathf.Max(0f, f.RefineGateFirst), Mathf.Clamp(f.RefineBisect, 0, LayerFrame.MaxRefineBisect), Mathf.Max(0f, f.RefineStepBudget)));
            if (f.Light != null) f.Light.Upload(m);
            if (f.Far != null) f.Far.Upload(m);
            Noise.Bind(m, layerCount);
        }

        // The middle sphere of the first layer (package T05's reprojection places the cloud of a pixel on it).
        internal CloudLayerFrameInfo FirstLayerShell()
        {
            var info = new CloudLayerFrameInfo();
            if (layerCount > 0) info.MiddleRadius = (float)radius + 0.5f * (layerA[0].x + layerA[0].y);
            return info;
        }

        // The composite reads the march target's texel size from the material (Unity does not fill it for a texture set globally).
        internal void SetMarchTexel(RenderTexture march)
        {
            material.SetVector(IdMarchTexel, new Vector4(1f / march.width, 1f / march.height, march.width, march.height));
        }

        private void EnsureTarget(Camera camera, float resolution)
        {
            int w = Mathf.Max(16, Mathf.RoundToInt(camera.pixelWidth * resolution));
            int h = Mathf.Max(16, Mathf.RoundToInt(camera.pixelHeight * resolution));
            if (target != null && target.width == w && target.height == h) return;
            if (target != null)
            {
                target.Release();
                UnityEngine.Object.Destroy(target);
            }
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
            SetMarchTexel(target);
            commands.SetGlobalTexture(IdMarchTex, target);
            commands.Blit(target, BuiltinRenderTextureType.CameraTarget, material, PassCompositeOut);
            if (markEnd != null) markEnd(commands);
        }
    }
}
