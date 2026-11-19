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
        // The layer the solo key names (cloud.soloLayer, CloudLookTuning.SoloMask): the only layer drawn while LayerMask is 0. A mask set by a probe wins over it.
        public int SoloMask;
        // The mask the shader gets: the probe's, else the solo key's, else 0 (every layer).
        public int DrawnMask { get { return LayerMask != 0 ? LayerMask : SoloMask; } }
        // The display stage of the final composite (pass 4, CloudOutputStage is its reference): exposure, the gamma the radiance is encoded with (1: the stage is
        // off, the pass writes its radiance as it is) and the white of the stage, the sun's display value of the scene. The weather player leaves it off.
        public float OutputExposure = 1f, OutputGamma = 1f, OutputWhite = 1f;
        // The colour of the stage's white (E40 B3): the sun's colour at the clouds over its luminance, so that a sunlit cloud top comes out neutral. White: the white is the grey
        // OutputWhite (the stage as it was). CloudOutputStage.WhiteTint makes it.
        public Color OutputWhiteTint = Color.white;
        // The cost controls of the march (package T01c). CoverageBound: the noise is evaluated only where the map can reach the threshold
        // (exact). EmptyLeap: the most grid steps one leap over empty samples takes (1: none). LightCutoff: a step whose weight in the
        // ray's colour is below this reuses the sun transmittance of the span's last lit step (0: every step marches to the sun).
        // CheapShare, FogShare: the share of the step budget that cheap and fog layers get. CostDebug: cost attribution only (bit 0 no sun
        // march, bit 1 no coverage noise, bit 2 no erosion).
        public bool CoverageBound = true;
        // E40 C1: the march leaps over the grid points that lie within the empty reach of an empty sample (EmptyReachMaps; exact: the density reads that are left are the same).
        public bool EmptyReach = true;
        // E40 C1: the widest spacing of the grid points of a layer's span in the layer's noise scales (0: off, the span gets the step quality's count), and the fewest grid points the
        // span keeps then. A weather deck (noise scale 10 km) or a cumulonimbus layer (5 km) is searched at a spacing that follows its features, not every 100 m.
        public float SpanSpacing;
        public int SpanSpacingMin = 20;
        // E40 C2: the longest leap over empty grid points in the layer's noise scales (0: off, the leap is EmptyLeap's grid points whatever the layer). A span of a layer with coarse noise
        // (a weather deck 10 km, a cumulonimbus layer 5 km) is stepped every 100 m or less; where the density is empty the march then leaps up to this many scales at a time and goes back to
        // the grid point after the last empty one when a leap lands on cloud, so a cloud is entered at the grid's own resolution and only a feature narrower than the leap can be passed over.
        public float LeapScales;
        // E40 C4 (layers.warpTable): how the untiling warp of the shape lookup gets its lattice values: 0 hashed at every sample (the pass as it was), 1 a table of half floats around the camera
        // (NoiseLattice; the same values inside its window, 84 km at the finest noise), 2 the same table of bytes.
        // Default 0: the table's window steps rewrite and upload 8.5 MB on the main thread (about 8 ms of CPU plus the upload, every few km of camera travel), which is not hitch-free.
        public int WarpTable;
        // The product's values: the leap and the layer-kind shares are off (they gained nothing on the view table), the sun march is reused
        // below a step weight of 0.05 and between every two lit steps.
        public float EmptyLeap = 1f, LightCutoff = 0.05f, CheapShare = 1f, FogShare = 1f;
        // LightStride: a lit step marches to the sun only every this many lit steps of a span; the steps between reuse the last result.
        public int LightStride = 2;
        // The reused result is moved by the ray's progress since, projected on the sun's direction (layers.lightReuseMove; a test for a longer stride).
        public bool LightReuseMove;
        // Cost attribution only (layers.costCutM): the march ends at this distance in m, 0 off; the picture is wrong while set.
        public float CostCutM;
        public int CostDebug;
        // Package T05: inside a cloud the step grows by this share of the transmittance the ray has already lost (0: a fixed step).
        public float TransmittanceGrowth;
        // E40 grain against TVC. SpanTight: the march searches each layer only over the altitudes of the cloud types the camera can see this frame and leaves out a
        // layer whose coverage map cannot reach the threshold there (LayerSpanBound); false: the type table's whole interval (the pass as it was).
        // DitherSequence: the per-pixel step offset, advanced by the golden ratio per frame: 1 an integer hash (white; what the default drew before E40 step (c)), 2 the
        // interleaved gradient noise (lower temporal noise, a few strong spatial lines), 3 (E40 F2, the default) the blue-noise tile of DitherMatrix (the same temporal noise, no line).
        public bool SpanTight;
        public int DitherSequence = 3;
        // E40 F2: the step offset's and the light cone's pixel pattern is read on the march target's own pixel grid (true) or on the camera's pixel grid scaled by the uv
        // (false: what the pass did until F2; a half-size target then read the pattern at every second pixel, so that along one axis the offsets of neighbours differed by
        // 0.11 (half size) or 0.07 (a third), and the picture showed their lines).
        public bool DitherOwnGrid = true;
        // The per-pixel rotation of the light cone (read only where the cone has a spread): 0 the gradient noise (what the pass drew until F2), 2 an integer hash, 3 (the default) the blue-noise tile.
        public int ConePattern = 3;
        // Package T16, the visibility culling and the distance LOD (all switches off until the owner agrees).
        public CullSettings Cull = new CullSettings();
        // A probe: the light cone's rotation is the same for every pixel (no per-pixel dither of the light taps).
        public bool FixedConeRotation;

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
        internal const int PassMarch = 0, PassComposite = 1, PassAux = 2, PassCompositeOut = 4, PassCullTile = 5;

        private static readonly int IdFarRot = Shader.PropertyToID("_FarRot"), IdReuseP = Shader.PropertyToID("_ReuseP");
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
            IdMarchTex = Shader.PropertyToID("_MarchTex"), IdSkipP = Shader.PropertyToID("_SkipP"), IdCostP = Shader.PropertyToID("_CostP"), IdFirstHitP = Shader.PropertyToID("_FirstHitP"),
            IdMarchTexel = Shader.PropertyToID("_MarchTex_TexelSize"), IdDitherP = Shader.PropertyToID("_DitherP");

        // The height variation (package T13): the maps, and per layer the ranges and the ragged boundary's numbers.
        private static readonly int IdHeightMaps = Shader.PropertyToID("_HeightMaps"), IdLayerH = Shader.PropertyToID("_LayerH"),
            IdLayerH2 = Shader.PropertyToID("_LayerH2");
        private readonly Vector4[] layerH = new Vector4[CloudLayerDef.MaxLayers], layerH2 = new Vector4[CloudLayerDef.MaxLayers];
        // The culling (package T16): the bound maps of the coverage, the tile map, and per layer the least vertical optical depth (x; closure test).
        private static readonly int IdCullL = Shader.PropertyToID("_CullL");
        internal static readonly int IdCellE = Shader.PropertyToID("_CellE");
        private readonly Vector4[] cullL = new Vector4[CloudLayerDef.MaxLayers];
        // The layers' cloud cell pitch in m (CloudLayerDef.CellPitchM; CELLS): goes into cullL's w each frame while cloud.cells is on. cellLayer: the layers, whose single cloud's body
        // (_CellE, CloudLookTuning.CellBodyVector) is written each frame with the viewer's overrides.
        private readonly float[] cellPitch = new float[CloudLayerDef.MaxLayers];
        private readonly CloudLayerDef[] cellLayer = new CloudLayerDef[CloudLayerDef.MaxLayers];
        private readonly Vector4[] cellE = new Vector4[CloudLayerDef.MaxLayers];
        internal CullBounds Bounds { get; } = new CullBounds();
        // The empty-reach maps of the march (E40 C1): the distance from a texel of a layer's coverage map to the nearest texel that can hold cloud, which the march leaps over.
        internal EmptyReachMaps Reach { get; } = new EmptyReachMaps();
        private static readonly int IdCovReach = Shader.PropertyToID("_CovReach"), IdSpacingP = Shader.PropertyToID("_SpacingP");
        internal CullTiles Tiles { get; } = new CullTiles();
        private readonly Vector4[] layerA = new Vector4[CloudLayerDef.MaxLayers], layerB = new Vector4[CloudLayerDef.MaxLayers],
            layerC = new Vector4[CloudLayerDef.MaxLayers], layerD = new Vector4[CloudLayerDef.MaxLayers], layerE = new Vector4[CloudLayerDef.MaxLayers];
        private Material material;
        private Texture2D profileTexture, infoTexture;
        // Each layer's light response (CloudLayerDef.LightAmbient; the shader's _LightL.x and .y stay 1), handed to the light settings of the frame (E40 B3).
        private readonly Vector3[] layerLight = { Vector3.one, Vector3.one, Vector3.one, Vector3.one };
        private readonly float[] layerIsotropy = new float[CloudLayerDef.MaxLayers];
        private readonly Vector4[] layerSkyMs = new Vector4[CloudLayerDef.MaxLayers];
        // The cloud motion's per-layer numbers (E40 MVCLOUD, ReDefinitionAtmospheresCloudMotion.cginc): the vertical optical depth of a full column per layer, and the arrays BindMotion fills.
        private readonly float[] layerTau = new float[CloudLayerDef.MaxLayers];
        private readonly Vector4[] motionA = new Vector4[CloudLayerDef.MaxLayers], motionB = new Vector4[CloudLayerDef.MaxLayers], motionD = new Vector4[CloudLayerDef.MaxLayers];
        private static readonly int IdMvA = Shader.PropertyToID("_MvA"), IdMvB = Shader.PropertyToID("_MvB"), IdMvD = Shader.PropertyToID("_MvD"), IdMvN = Shader.PropertyToID("_MvN"),
            IdMvMap = Shader.PropertyToID("_MvMap"), IdMvWorldToMap = Shader.PropertyToID("_MvWorldToMap"), IdMvCov = Shader.PropertyToID("_MvCov"),
            IdMvReach = Shader.PropertyToID("_MvReach"), IdMapReachCov = Shader.PropertyToID("_MapReachCov"), IdMapReachType = Shader.PropertyToID("_MapReachType");
        private RenderTexture target;
        private CommandBuffer commands;
        private Camera attached;
        private CameraEvent attachedEvent;
        private int layerCount;
        private double radius;
        private long uploadedVersion = -1, uploadedShift = -1;
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
            profileTexture = new Texture2D(LayerTypeTable.ProfileSamples, CloudLayerDef.MaxLayers * CloudLayerDef.MaxTypes, TextureFormat.RGBAFloat, false, true)
            {
                name = "layer type profiles",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            // The first MaxLayers rows: base, top, erosion and smoothing of each type; the next MaxLayers rows (CLOUDR1): the heap preset, the exponent of the density rim and the cavity.
            infoTexture = new Texture2D(CloudLayerDef.MaxTypes, 2 * CloudLayerDef.MaxLayers, TextureFormat.RGBAFloat, false, true)
            {
                name = "layer type info",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            material.SetTexture(IdTypeProfile, profileTexture);
            var blue = new Vector4[DitherMatrix.Count / 4];
            float[] tile = DitherMatrix.Values();
            for (int i = 0; i < tile.Length; i++) blue[i >> 2][i & 3] = tile[i];
            material.SetVectorArray(Shader.PropertyToID("_DitherM"), blue);
            material.SetTexture(IdTypeInfo, infoTexture);
            commands = new CommandBuffer { name = "ReDefinition layer pass" };
            Heights.Ensure(2, 2);
            Noise.StartGeneration();
            return true;
        }

        // The pinned capture (master plan phase 0 item 3): a hash of the type table textures as uploaded (the profile, then the info texture; their CPU copies stay
        // readable, Apply(false)), so that pass.json and the player's replay can say whether both drew with the same table. "none" before Init.
        internal string TypeTableHash()
        {
            if (profileTexture == null || infoTexture == null) return "none";
            ulong h = LayerPassSettings.Fnv1a64(profileTexture.GetRawTextureData());
            h = LayerPassSettings.Fnv1a64(infoTexture.GetRawTextureData(), h);
            return LayerPassSettings.Hex(h);
        }

        // True makes Upload queue the height maps and StepHeights write them one slice a frame (the game); false writes them at once in Upload
        // (the weather player and the tests, which read the result in the same frame).
        internal bool SpreadHeights;

        // True makes Apply rebuild one layer's empty-reach map a frame (the game); false rebuilds all that changed at once (the weather player and the tests).
        internal bool SpreadReach;

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
            Bounds.Dispose();
            Reach.Dispose();
            Tiles.Dispose();
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
            if (ReferenceEquals(source, uploadedSource) && source.Version == uploadedVersion && CloudCoverShift.Stamp == uploadedShift) return;
            layerCount = source.LayerCount;
            radius = source.BodyRadiusM;
            // Four channels per sample: r the density profile, g the direct light factor, b the sky-fill factor (E40 B3), a the lift of the coverage threshold (E40 L2 D1, 0: none). Unused rows keep the neutral light factors.
            var profile = new float[LayerTypeTable.ProfileSamples * CloudLayerDef.MaxLayers * CloudLayerDef.MaxTypes * 4];
            for (int i = 0; i < profile.Length; i += 4) { profile[i + 1] = 1f; profile[i + 2] = 1f; }
            var info = new Color[CloudLayerDef.MaxTypes * CloudLayerDef.MaxLayers * 2];
            bool extras = false;
            for (int l = 0; l < layerCount; l++)
            {
                CloudLayerDef layer = source.LayerAt(l);
                LayerTypeTable table = source.TableAt(l);
                float tightInner, tightOuter;
                layer.TightAltitudes(out tightInner, out tightOuter);
                layerA[l] = new Vector4(layer.InnerAltitudeM, layer.OuterAltitudeM, tightInner, tightOuter);
                tightTable[l] = new Vector2(tightInner, tightOuter);
                spanValid[l] = false;
                layerB[l] = new Vector4(layer.EffectiveSigmaPerM, layer.EffectiveCoverage, layer.EffectiveEdgeSoftness, (float)(int)layer.Kind);
                layerC[l] = new Vector4(layer.EffectiveFadeNearM, float.IsPositiveInfinity(layer.EffectiveFadeFarM) ? 0f : layer.EffectiveFadeFarM,
                                        layer.CoverageMapHandle < 0 && layer.TypeMapHandle < 0 ? -1f : l, table.Count);
                layerD[l] = new Vector4(layer.EffectiveNoiseAmount, layer.EffectiveNoiseScaleM > 0f ? 1f / layer.EffectiveNoiseScaleM : 0f, 1f, layer.MapShiftDeg);
                layerE[l] = Vector4.zero;
                layerLight[l] = new Vector3(1f, 1f, layer.LightAmbient);
                layerIsotropy[l] = layer.LightIsotropy;
                layerSkyMs[l] = new Vector4(layer.AmbientMs, layer.AmbientDecay, layer.LightPlateau, layer.LightCellWidth);
                HeightTuning height = source.Heights.TuningOf(l);
                HeightMaps heightMaps = source.Heights.MapsOf(l);
                layerH[l] = height == null ? Vector4.zero
                    : new Vector4(heightMaps == null ? 0f : heightMaps.RootRangeM, heightMaps == null ? 0f : heightMaps.TopRangeM,
                                  heightMaps == null ? 0f : heightMaps.VariationRangeM, height.BaseRagged ? height.RaggedM : 0f);
                layerH2[l] = height == null ? Vector4.zero
                    : new Vector4(1f / Mathf.Max(1f, height.RaggedScaleM), height.MinThicknessM, source.Heights.SeedOf(l) & 0xFFFFFF, heightMaps == null ? 0f : 1f);
                // T16: the least vertical optical depth a ray through the layer's whole height collects (the closure test); none where the heights vary.
                cullL[l] = new Vector4(layerH2[l].y > 0f ? 0f : (float)CullMath.MinVerticalTau(layer.EffectiveSigmaPerM, layer.OuterAltitudeM - layer.InnerAltitudeM, table.Count,
                    t => { float tb, tt, te; table.Info(t, out tb, out tt, out te); return Math.Max(tt - tb, 0f); },
                    t => { double sum = 0; for (int i = 0; i < LayerTypeTable.ProfileSamples; i++) sum += table.ProfileData[t * LayerTypeTable.ProfileSamples + i]; return sum / LayerTypeTable.ProfileSamples; }),
                    // y: the soft density's clear-sky K of the map value (0: off); z: the layer's water exponent minus 1 (0: none): the shader's two per-layer numbers (E40 soft density);
                    // w: the pitch of the layer's cloud cells in m (0: none; CELLS), written by Apply every frame from cellPitch and cloud.cells.
                    layer.ClearSkyK, layer.EffectiveWaterPower - 1f, 0f);
                cellPitch[l] = layer.CellPitchM;
                cellLayer[l] = layer;
                layerTau[l] = (float)CullMath.MeanVerticalTau(layer.EffectiveSigmaPerM, layer.OuterAltitudeM - layer.InnerAltitudeM, table.Count,
                    t => { float tb, tt, te; table.Info(t, out tb, out tt, out te); return Math.Max(tt - tb, 0f); },
                    t => { double sum = 0; for (int i = 0; i < LayerTypeTable.ProfileSamples; i++) sum += table.ProfileData[t * LayerTypeTable.ProfileSamples + i]; return sum / LayerTypeTable.ProfileSamples; });
                float[] density = table.ProfileData, lightData = table.LightData, liftData = table.LiftData;
                for (int i = 0; i < table.Count * LayerTypeTable.ProfileSamples; i++)
                {
                    int texel = (l * CloudLayerDef.MaxTypes * LayerTypeTable.ProfileSamples + i) * 4;
                    profile[texel] = density[i];
                    profile[texel + 1] = lightData[i * 2];
                    profile[texel + 2] = lightData[i * 2 + 1];
                    profile[texel + 3] = liftData[i];
                }
                Noise.SetStretch(l, layer.FibreStretch);
                Noise.SetVertical(l, layer.VerticalScale);
                Noise.SetSmoothing(l, layer.ShapeSmoothing);
                Noise.SetFibre(l, layer.FibreMinWindMs, layer.FibreTurnSeconds);
                for (int t = 0; t < table.Count; t++)
                {
                    float b, top, e;
                    table.Info(t, out b, out top, out e);
                    info[l * CloudLayerDef.MaxTypes + t] = new Color(b, top, e, table.SmoothingOfType(t));
                    int heap = table.HeapOfType(t);
                    if (heap > 0) Noise.RequestHeap();
                    if (heap > 0 || table.EdgeOfType(t) > 0f) extras = true;
                    info[(CloudLayerDef.MaxLayers + l) * CloudLayerDef.MaxTypes + t] = new Color(heap, table.EdgeOfType(t), table.CavityOfType(t), 0f);
                }
            }
            for (int l = layerCount; l < CloudLayerDef.MaxLayers; l++)
            {
                layerH[l] = layerH2[l] = cullL[l] = Vector4.zero;
                cellPitch[l] = 0f;
            }
            if (SpreadHeights) Heights.Queue(source.Heights, layerCount);
            else Heights.Upload(source.Heights, layerCount);
            profileTexture.SetPixelData(profile, 0);
            profileTexture.Apply(false);
            infoTexture.SetPixels(info);
            infoTexture.Apply(false);
            Noise.ExtrasUsed = extras;

            uploadedSource = source;
            uploadedVersion = source.Version;
            uploadedShift = CloudCoverShift.Stamp;
        }

        // The camera for a pass: its position and basis in world space; right and up are scaled by the tangent of the half field of
        // view, and jitterNdc is added to each pixel's ndc before the ray is built (it moves the rays, not the picture; ReconSettings.RayJitter for the upscaler's).
        internal void SetCamera(Vector3 position, Vector3 forward, Vector3 right, Vector3 up, Vector2 jitterNdc)
        {
            cameraPosition = position;
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
            TightenSpans(f);
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
                m.SetVector(IdMapReachCov, Maps.ReachVector(0));
                m.SetVector(IdMapReachType, Maps.ReachVector(1));
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
            UploadedLayerMask = f.DrawnMask;
            m.SetVector(IdLimitP, new Vector4(f.RayLimitM, f.AidMode, UploadedLayerMask, f.UseSceneDepth ? 1f : 0f));
            m.SetVector(IdPhaseP, f.Phase);
            m.SetVector(Shader.PropertyToID("_OutP"), new Vector4(Mathf.Max(0f, f.OutputExposure), 1f / Mathf.Max(1f, f.OutputGamma), Mathf.Max(1e-4f, f.OutputWhite), f.OutputGamma > 1.0001f ? 1f : 0f));
            m.SetVector(Shader.PropertyToID("_OutW"), new Vector4(f.OutputWhiteTint.r, f.OutputWhiteTint.g, f.OutputWhiteTint.b, 0f));
            m.SetVector(IdSkipP, new Vector4(Mathf.Max(1f, f.EmptyLeap), Mathf.Max(0f, f.LightCutoff), Mathf.Clamp01(f.CheapShare), Mathf.Clamp01(f.FogShare)));
            // The block sampling of the temporal reconstruction is switched on by its own Record after this call, never left over.
            m.SetVector(Shader.PropertyToID("_ReconP"), new Vector4(1f, 0f, 0f, 0f));
            // Its sizes too (Record and Prepare set them after this call; 0: a caller that sets none, the shader then reads Unity's _ScreenParams as before).
            m.SetVector(Shader.PropertyToID("_ReconD"), Vector4.zero);
            m.SetVector(IdSpacingP, new Vector4(Mathf.Max(0f, f.SpanSpacing), Mathf.Max(1, f.SpanSpacingMin), Mathf.Max(0f, f.LeapScales), 0f));
            // CLOUDR1 P6: the first hit by bisection and the thin steps inside (cloud.firstHit, cloud.firstHit.thin, cloud.firstHit.grow); 0 iterations: the march as it was.
            m.SetVector(IdFirstHitP, new Vector4(CloudLookTuning.FirstHitIterations, CloudLookTuning.FirstHitThin, CloudLookTuning.FirstHitShell, CloudLookTuning.FirstHitNear));
            m.SetVector(IdReuseP, new Vector4(f.LightReuseMove ? 1f : 0f, Mathf.Max(0f, f.CostCutM), 0f, 0f));
            m.SetVector(IdCostP,new Vector4(f.CoverageBound ? 1f : 0f, f.CostDebug, Mathf.Max(1, f.LightStride), Mathf.Max(0f, f.TransmittanceGrowth)));
            // The phase of the frame (the golden-ratio sequence) in double precision (a float product loses the fraction at the frame counts of a long session); the per-pixel
            // pattern stands still (y: the cone's pattern, z: the step offset's, w: the pixel grid they are read on, LayerFrame.DitherOwnGrid).
            double phase = StepPhase.Of(f.FrameNumber);
            m.SetVector(IdDitherP, new Vector4((float)(phase - Math.Floor(phase)), f.FixedConeRotation ? 1f : (f.ConePattern == 2 || f.ConePattern == 3 ? f.ConePattern : 0f), Mathf.Clamp(f.DitherSequence, 1, 3), f.DitherOwnGrid ? 1f : 0f));
            if (f.Light != null)
            {
                for (int l = 0; l < layerLight.Length && l < f.Light.LayerScale.Length; l++) f.Light.LayerScale[l] = l < layerCount ? layerLight[l] : Vector3.one;
                for (int l = 0; l < layerIsotropy.Length && l < f.Light.LayerIsotropy.Length; l++) f.Light.LayerIsotropy[l] = l < layerCount ? layerIsotropy[l] : 0f;
                for (int l = 0; l < layerSkyMs.Length && l < f.Light.LayerM.Length; l++) f.Light.LayerM[l] = l < layerCount ? layerSkyMs[l] : Vector4.zero;
                f.Light.Upload(m);
            }
            if (f.Far != null) f.Far.Upload(m);
            if (f.Cull != null) f.Cull.Upload(m);
            bool cells = CloudLookTuning.Cells;
            for (int l = 0; l < CloudLayerDef.MaxLayers; l++) cullL[l].w = cells ? cellPitch[l] : 0f;
            m.SetVectorArray(IdCullL, cullL);
            for (int l = 0; l < CloudLayerDef.MaxLayers; l++) cellE[l] = CloudLookTuning.CellBodyVector(l < layerCount ? cellLayer[l] : null);
            m.SetVectorArray(IdCellE, cellE);
            if (f.EmptyReach && Maps.Coverage != null) Reach.Refresh(Maps, layerCount, layerB, layerD, cullL, SpreadReach ? 1 : CloudLayerDef.MaxLayers);
            m.SetTexture(IdCovReach, Reach.Texture(f.EmptyReach));
            Noise.SetPole(f.WorldToMap.GetRow(2));
            Noise.Switches.WarpTable = f.WarpTable;
            Noise.SetViewer(cameraPosition - f.PlanetCenter);
            for (int l = 0; l < layerCount; l++) Noise.SetLatticeLayer(l, layerD[l].y > 0f ? 1.0 / layerD[l].y : 0.0, radius + 0.5 * (layerA[l].x + layerA[l].y));
            Noise.Bind(m, layerCount);
        }

        // E40 grain against TVC (LayerFrame.SpanTight): writes each layer's tight interval of this frame into layerA (z, w) from the maps' bytes around the camera
        // (LayerSpanBound); a layer without a visible cloud gets the outer altitude -1, which the shader's LayerSpans reads as "no span". Off, or where no bound can be
        // taken (no maps, a gas giant, a camera so high that half the map is in view), the table's interval of Upload stays. The scan is repeated only when a map was
        // uploaded, the stack changed or the camera reached another texel.
        private void TightenSpans(LayerFrame f)
        {
            for (int l = 0; l < layerCount; l++)
            {
                Vector2 table = tightTable[l];
                float inner = table.x, outer = table.y;
                bool scanned = false;
                if (f.SpanTight && f.HasSurface && Maps.Coverage != null && layerC[l].z >= 0f && uploadedSource != null)
                {
                    Vector3 q = f.WorldToMap.MultiplyVector(cameraPosition - f.PlanetCenter);
                    double len = Math.Sqrt((double)q.x * q.x + (double)q.y * q.y + (double)q.z * q.z);
                    if (len > 1.0)
                    {
                        double lat = Math.Asin(Math.Max(-1.0, Math.Min(1.0, q.z / len))) * (180.0 / Math.PI);
                        double lon = Math.Atan2(q.y, q.x) * (180.0 / Math.PI) - layerD[l].w;
                        lon -= 360.0 * Math.Floor((lon + 180.0) / 360.0);
                        double region = LayerSpanBound.RegionDeg(radius, len - radius, table.y, f.SunMarchM);
                        long key = Maps.CoverageVersion(l) * 1000003L + Maps.TypeVersion(l) * 7919L + uploadedVersion * 31L + uploadedShift
                                   + (long)Math.Floor(lat / 180.0 * Maps.Height) * 104729L + (long)Math.Floor(lon / 360.0 * Maps.Width) * 1299709L
                                   + (long)Math.Round(region * 4.0) * 15485863L;
                        // The height maps the frame may sample: the ones the GPU array holds and the ones a queued upload brings.
                        HeightMaps held = Heights.AppliedMaps(l), coming = Heights.QueuedMaps(l);
                        key += (held == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(held)) * 86028121L
                               + (coming == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(coming)) * 49979687L;
                        if (key != spanKey[l] || !spanValid[l])
                        {
                            // What the height variation really reaches around the camera (the layer's worst case is the sum of its ranges: 1.3 km down and 2.6 km up
                            // for Kerbin's layers, more than the thickness of the clouds themselves).
                            float down = -1f, up = -1f;
                            if (layerH2[l].y > 0f)
                            {
                                LayerSpanBound.HeightExtremes e = LayerSpanBound.HeightExtremes.Neutral;
                                if (layerH2[l].w > 0.5f)
                                {
                                    // The slice is neutral where the GPU holds no maps of the layer yet.
                                    if (held != null) e = LayerSpanBound.Widen(e, held.Bytes, held.Width, held.Height, lat, lon, region + 0.25);
                                    if (coming != null && !ReferenceEquals(coming, held)) e = LayerSpanBound.Widen(e, coming.Bytes, coming.Width, coming.Height, lat, lon, region + 0.25);
                                }
                                LayerSpanBound.Reach(e, layerH[l].x, layerH[l].y, layerH[l].z, layerH[l].w, layerH2[l].y, out down, out up);
                                // A metre of slack for the rounding of the shader's sums.
                                down += 1f;
                                up += 1f;
                            }
                            byte[] coverA, coverB, typeA, typeB;
                            Maps.CoverageBoundPair(l, out coverA, out coverB);
                            Maps.TypeBoundPair(l, out typeA, out typeB);
                            spanResult[l] = LayerSpanBound.Bound(coverA, typeA, Maps.Width, Maps.Height, lat, lon, region + 0.25,
                                                                 uploadedSource.TableAt(l), uploadedSource.LayerAt(l), layerB[l].y, layerD[l].x, layerB[l].z, table.x, table.y,
                                                                 down, up, ReferenceEquals(coverA, coverB) ? null : coverB, ReferenceEquals(typeA, typeB) ? null : typeB);
                            spanKey[l] = key;
                            spanValid[l] = true;
                        }
                        LayerSpanBound.Result r = spanResult[l];
                        if (r.Tightened)
                        {
                            scanned = true;
                            if (r.Empty) { inner = 0f; outer = -1f; }
                            else { inner = r.InnerM; outer = r.OuterM; }
                        }
                    }
                }
                layerA[l].z = inner;
                layerA[l].w = outer;
                spanScanned[l] = scanned;
            }
        }

        // The intervals of the last Apply as text (the probe's row and the log): per layer "none", "table <inner>-<outer>" or "<inner>-<outer>" in m.
        internal string SpanStats
        {
            get
            {
                var text = new System.Text.StringBuilder();
                for (int l = 0; l < layerCount; l++)
                {
                    if (l > 0) text.Append(' ');
                    text.Append('L').Append(l).Append(' ');
                    if (layerA[l].w < 0f) text.Append("none");
                    else text.Append(spanScanned[l] ? "" : "table ").Append(layerA[l].z.ToString("0", System.Globalization.CultureInfo.InvariantCulture)).Append('-')
                             .Append(layerA[l].w.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
                }
                return text.ToString();
            }
        }

        // The layer mask the last frame's material was given (bit i = layer i; 0 = every layer): what the shader skips layers by. The solo probe reads it back.
        internal int UploadedLayerMask { get; private set; }

        private readonly bool[] spanScanned = new bool[CloudLayerDef.MaxLayers];

        private Vector3 cameraPosition;
        private readonly Vector2[] tightTable = new Vector2[CloudLayerDef.MaxLayers];
        private readonly long[] spanKey = new long[CloudLayerDef.MaxLayers];
        private readonly bool[] spanValid = new bool[CloudLayerDef.MaxLayers];
        private readonly LayerSpanBound.Result[] spanResult = new LayerSpanBound.Result[CloudLayerDef.MaxLayers];

        // Package T16: refreshes the bound maps and records the tile pass (nothing where no skip switch is on). Call after Apply and before the march
        // blit, with the scene depth global set; fullW and fullH are the full target's size, the tile map covers it whatever size the march target has.
        internal void RecordCull(CommandBuffer cb, LayerFrame f, int fullW, int fullH, Action<CommandBuffer> markStart, Action<CommandBuffer> markEnd)
        {
            if (!Ready || f.Cull == null || !f.Cull.TilesNeeded) return;
            Bounds.Refresh(Maps, layerCount);
            Tiles.Record(cb, this, Bounds, f.Cull, fullW, fullH, markStart, markEnd);
        }

        // The middle sphere of the first layer (package T05's reprojection places the cloud of a pixel on it).
        internal CloudLayerFrameInfo FirstLayerShell()
        {
            var info = new CloudLayerFrameInfo();
            if (layerCount > 0) info.MiddleRadius = (float)radius + 0.5f * (layerA[0].x + layerA[0].y);
            return info;
        }

        // The planet's radius of the uploaded stack, and the layers of its cloud motion for the CPU model (LayerMotionModel; Presence 1, drift over driftSeconds); the count.
        internal double PlanetRadiusM { get { return radius; } }

        internal int FillMotionLayers(MotionLayer[] into, float driftSeconds)
        {
            int n = Mathf.Min(Mathf.Min(layerCount, CloudLayerDef.MaxLayers), into.Length);
            for (int l = 0; l < n; l++)
            {
                Vector3 drift = Noise.DriftOf(l) * driftSeconds;
                into[l] = new MotionLayer
                {
                    ShellR = radius + 0.5 * (layerA[l].x + layerA[l].y), TauVertical = layerTau[l], DriftX = drift.x, DriftY = drift.y, DriftZ = drift.z, Presence = 1.0
                };
            }
            return n;
        }

        // The numbers BindMotion last gave the shader for a layer (the player's check of the shader against the CPU model reads them): a = (shell radius, coverage, edge softness, map slice),
        // b = (column optical depth, map shift in degrees, clear-sky K, 0).
        internal void MotionParameters(int layer, out Vector4 a, out Vector4 b)
        {
            a = motionA[layer];
            b = motionB[layer];
        }

        // Writes the layers' places and shares into a material of the reconstruction (ReDefinitionAtmospheresCloudMotion.cginc): the middle sphere, coverage, edge softness, map slice,
        // map shift, clear-sky rule, column optical depth and drift over driftSeconds of each layer, the planet's radius and the map frame. layers false (or no maps, no layers): the
        // shader blends nothing and the caller's first layer shell stands. poseValid: a camera of the frame before exists (the motion vector is drawn only then).
        internal void BindMotion(Material m, LayerFrame f, bool layers, bool poseValid, float driftSeconds)
        {
            int n = layers && Maps.Coverage != null ? Mathf.Min(layerCount, CloudLayerDef.MaxLayers) : 0;
            for (int l = 0; l < CloudLayerDef.MaxLayers; l++)
            {
                if (l >= n)
                {
                    motionA[l] = new Vector4(0f, 0f, 1f, -1f);
                    motionB[l] = motionD[l] = Vector4.zero;
                    continue;
                }
                motionA[l] = new Vector4((float)radius + 0.5f * (layerA[l].x + layerA[l].y), layerB[l].y, Mathf.Max(layerB[l].z, 1e-4f), layerC[l].z);
                motionB[l] = new Vector4(layerTau[l], layerD[l].w, cullL[l].y, 0f);
                Vector3 drift = Noise.DriftOf(l) * driftSeconds;
                motionD[l] = new Vector4(drift.x, drift.y, drift.z, 0f);
            }
            m.SetVectorArray(IdMvA, motionA);
            m.SetVectorArray(IdMvB, motionB);
            m.SetVectorArray(IdMvD, motionD);
            m.SetVector(IdMvN, new Vector4(n, (float)radius, poseValid ? 1f : 0f, 0f));
            m.SetVector(IdMvMap, new Vector4(Maps.Width, Maps.Height, 0f, 0f));
            m.SetMatrix(IdMvWorldToMap, f.WorldToMap);
            if (Maps.Coverage != null) m.SetTexture(IdMvCov, Maps.Coverage);
            m.SetVector(IdMvReach, Maps.ReachVector(0));
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
        // null) are called around the passes so that the player can time them. sceneDepth is the linear eye depth of the opaque scene. rayJitter is the offset of the
        // rays in ndc that matches the projection the depth was rendered with (ReconSettings.RayJitter; zero: the unjittered rays).
        internal void Prepare(Camera camera, LayerFrame f, RenderTargetIdentifier sceneDepth, Action<CommandBuffer> markStart,
                              Action<CommandBuffer> markMarched, Action<CommandBuffer> markEnd, Vector2 rayJitter = default(Vector2))
        {
            if (!Ready) return;
            EnsureTarget(camera, f.Resolution);
            float tanY = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float tanX = tanY * camera.aspect;
            Transform t = camera.transform;
            SetCamera(t.position, t.forward, t.right * tanX, t.up * tanY, rayJitter);
            f.Cull.PixelAngleRad = 2f * tanY / target.height;
            Apply(f);
            // The direct path has no history: the reconstruction's far rotation (LayerReconstructor) must not mark any of its pixels.
            material.SetVector(IdFarRot, Vector4.zero);
            // The camera's pixels as the march's pixel grid: the values _ScreenParams has in the camera's render, which the shader read here before.
            material.SetVector(Shader.PropertyToID("_ReconD"), new Vector4(camera.pixelWidth, camera.pixelHeight, 0f, 0f));
            commands.Clear();
            if (markStart != null) markStart(commands);
            commands.SetGlobalTexture(IdSceneDepth, sceneDepth);
            RecordCull(commands, f, camera.pixelWidth, camera.pixelHeight, null, null);
            LayerReconstructor.Sub(commands, "cull tiles");
            commands.Blit(BuiltinRenderTextureType.None, target, material, PassMarch);
            LayerReconstructor.Sub(commands, "march");
            if (markMarched != null) markMarched(commands);
            SetMarchTexel(target);
            commands.SetGlobalTexture(IdMarchTex, target);
            commands.Blit(target, BuiltinRenderTextureType.CameraTarget, material, PassCompositeOut);
            if (markEnd != null) markEnd(commands);
        }
    }
}
