using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // The numbers of one frame of the cloud shadow that are not the layers themselves: the body's frame in the space the shader works in,
    // the sun, and the quality. A plain object the caller keeps and rewrites each frame.
    internal sealed class CloudShadowFrame
    {
        // The planet's centre in the space the buffers draw in (world space for the scene's cameras, scaled space for the disc), the unit of
        // that space in metres (1 in the scene), and the rotation into the map frame (x towards longitude 0, y towards 90 east, z north).
        public Vector3 PlanetCenter = Vector3.zero;
        public double RadiusUnits = 600000.0;
        public float MetresPerUnit = 1f;
        public Matrix4x4 WorldToMap = Matrix4x4.identity;
        // Towards the sun, unit length.
        public Vector3 TowardsSun = Vector3.up;
        // The midpoint steps in each span of a layer (1 to 24), the directions across the sun's disc (1 to 8), and the share of the direct
        // light that always remains (0: the pure Beer-Lambert shadow).
        public int StepsPerSpan = 8, DiscTaps = 4;
        // How the steps scale with a layer's interval (CloudShadowStepPolicy; the tuning key shadow.stepPolicy).
        public CloudShadowSteps StepPolicy = CloudShadowStepPolicy.Default;
        public float Floor;
        // The weight of the light a cloud scatters down (CloudShadowMath.FittedTransmission of the vertical optical depth) in the mask: 1 lifts
        // the mask under a thick cloud to the diffuse share of the open ground's sun, 0 leaves the pure direct beam (the analytic checks).
        public float DiffuseShare = 1f;
        // Forward shading only: the share of the lit colour that is the sun's direct light (CloudShadowMath.DirectShare); the ambient share
        // is not darkened. 1 multiplies the whole colour (the analytic checks).
        public float ForwardDirectShare = 1f;
        // A diagnostic output of the forward pass for the probe (0: the shadow); see the shader's pass 3.
        public int Debug;
        // Whether the shader reads the shape noise of the layer pass (CloudShadowShapeNoise; the tuning key shadow.shapeNoise). Needs CloudShadowRenderer.Noise.
        public bool ShapeNoise;
        // Whether the shader lowers the map by the types' LIFT as the pass does (with ShapeNoise; off only for the before/after pictures of the weather player).
        public bool MacroLift = true;
        // OPT2: the layer pass's empty-reach maps (LayerRenderer.Reach) and whether the march leaps over them: only on a body with a surface (the chord argument of the reach) and only
        // where the layer pass has them on (LayerFrame.EmptyReach). The sum is the same with or without: the midpoints skipped add nothing.
        public Texture EmptyReach;
        public bool EmptyReachOn;
        // OPT4: the shadow is marched at the camera's size over this (2: half size, the shadow as it was; 3, 4 a third, a quarter); the depth-aware upsample takes it back to full size.
        public int Divisor = 2;
    }

    // The cloud shadow's drawing (package T04): the material, the type table and map textures it reads, and the command buffers that build the
    // half-size shadow and multiply it into the sun's shadow mask (deferred), into the colour (forward), onto the planet's disc (scaled
    // space) or into the ocean's shadow texture.
    //
    // Credit: the structure of the buffers (the depth downscaled to half size, a half-size R8 shadow target, the multiply into the sun's
    // mask at LightEvent.AfterScreenspaceMask, the camera buffer at CameraEvent.AfterForwardOpaque for forward rendering, and the ocean
    // variant that Scatterer asks for) is adapted from EnvironmentalVisualEnhancements (EVE Redux, MIT):
    // Atmosphere/RaymarchedClouds/LightVolume/ScreenSpaceShadowsManager.cs, classes ScreenSpaceShadowsManager and ScreenSpaceShadowsRenderer.
    internal sealed class CloudShadowRenderer
    {
        internal const string ShaderName = "ReDefinition/AtmospheresCloudShadow";
        internal const int PassDownscale = 0, PassShadow = 1, PassMask = 2, PassColour = 3, PassDisc = 4, PassDownscaleSource = 5;

        internal static readonly string TextureNameShadows = "ReDefinitionCloudShadows";
        internal static readonly string TextureNameDepth = "ReDefinitionCloudShadowsDepth";
        // The names Scatterer's ocean reads (render-contract-scatterer.md); the ocean path sets them.
        internal static readonly string OceanShadowsName = "ScattererOceanShadowsTexture";
        internal static readonly string OceanDepthName = "ScattererDownscaledOceanDepthTexture";

        // The shader's local keyword of the test lattice (passes 1 and 4), on while cloud.field is off.
        internal const string LatticeKeyword = "CLOUD_LATTICE";

        private static readonly int IdPlanet = Shader.PropertyToID("_CsPlanet"), IdWorldToMap = Shader.PropertyToID("_CsWorldToMap"),
            IdSun = Shader.PropertyToID("_CsSun"), IdParams = Shader.PropertyToID("_CsParams"), IdParams2 = Shader.PropertyToID("_CsParams2"), IdLayerA = Shader.PropertyToID("_CsLayerA"),
            IdLayerB = Shader.PropertyToID("_CsLayerB"), IdLayerC = Shader.PropertyToID("_CsLayerC"), IdLayerD = Shader.PropertyToID("_CsLayerD"),
            IdTypeProfile = Shader.PropertyToID("_CsTypeProfile"), IdTypeInfo = Shader.PropertyToID("_CsTypeInfo"),
            IdCovMaps = Shader.PropertyToID("_CsCovMaps"), IdTypeMaps = Shader.PropertyToID("_CsTypeMaps"), IdMapDim = Shader.PropertyToID("_CsMapDim"),
            IdReachCov = Shader.PropertyToID("_CsReachCov"), IdReachType = Shader.PropertyToID("_CsReachType"),
            IdDepthSource = Shader.PropertyToID("_CsDepthSource"), IdDepthHalf = Shader.PropertyToID("_CsDepthHalf"),
            IdShadowHalf = Shader.PropertyToID("_CsShadowHalf"), IdNoiseD = Shader.PropertyToID("_CsNoiseD"), IdPlanetR = Shader.PropertyToID("_CsPlanetR"),
            IdCovReach = Shader.PropertyToID("_CsCovReach"), IdFormP = Shader.PropertyToID("_CsFormP");

        private readonly Vector4[] layerA = new Vector4[CloudLayerDef.MaxLayers], layerB = new Vector4[CloudLayerDef.MaxLayers],
            layerC = new Vector4[CloudLayerDef.MaxLayers], layerD = new Vector4[CloudLayerDef.MaxLayers],
            noiseD = new Vector4[CloudLayerDef.MaxLayers], noiseOff = new Vector4[CloudLayerDef.MaxLayers], cellE = new Vector4[CloudLayerDef.MaxLayers], cellS = new Vector4[CloudLayerDef.MaxLayers],
            formP = new Vector4[CloudLayerDef.MaxLayers];

        // The layer pass's noise (volumes, offsets, drift): the shadow reads the shape share from the same volumes (CloudShadowFrame.ShapeNoise). null: no noise.
        internal LayerNoise Noise;
        private Material material;
        private Texture2D profileTexture, infoTexture;
        private int layerCount;
        private long uploadedVersion = -1, uploadedShift = -1;
        private LayerStackSource uploadedSource;

        // The half-size shadow target: 16 bits where the platform has them (the factor under a thick cloud, exp(-6) and less, is below the
        // 8-bit step of 1/255), else 8 bits as EVE's. The sun's mask it is multiplied into is 8 bits anyway; the colour multiply of the forward
        // path and the ocean texture keep the finer steps.
        internal static RenderTextureFormat ShadowFormat
        {
            get { return SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R16) ? RenderTextureFormat.R16 : RenderTextureFormat.R8; }
        }

        internal string Problem { get; private set; }
        internal bool Ready { get { return material != null && profileTexture != null; } }
        internal Material Material { get { return material; } }

        internal bool Init(Shader shader)
        {
            if (shader == null)
            {
                Problem = "the cloud shadow shader is missing";
                return false;
            }
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            if (material.passCount < 6)
            {
                Problem = "the cloud shadow shader does not compile (" + material.passCount + " passes)";
                return false;
            }
            profileTexture = new Texture2D(LayerTypeTable.ProfileSamples, CloudLayerDef.MaxLayers * CloudLayerDef.MaxTypes, TextureFormat.RGBAFloat, false, true)
            {
                name = "cloud shadow type profiles",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            // MaxLayers rows of base, top, edge exponent, cumuliform, then MaxLayers rows of the type's shapeSmoothing (x).
            infoTexture = new Texture2D(CloudLayerDef.MaxTypes, 2 * CloudLayerDef.MaxLayers, TextureFormat.RGBAFloat, false, true)
            {
                name = "cloud shadow type info",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            material.SetTexture(IdTypeProfile, profileTexture);
            material.SetTexture(IdTypeInfo, infoTexture);
            return true;
        }

        internal void Dispose()
        {
            UnityEngine.Object.Destroy(material);
            UnityEngine.Object.Destroy(profileTexture);
            UnityEngine.Object.Destroy(infoTexture);
            material = null;
            profileTexture = null;
        }

        // Writes the stack's layers and type tables into the material; the maps are the layer pass's (the same textures, so that the shadow and
        // the picture read one field). Cheap when nothing changed (the source's version is compared).
        internal void Upload(LayerStackSource source, LayerMapStore maps)
        {
            if (!Ready || source == null) return;
            if (maps != null && maps.Coverage != null)
            {
                material.SetTexture(IdCovMaps, maps.Coverage);
                material.SetTexture(IdTypeMaps, maps.Types);
                material.SetVector(IdReachCov, maps.ReachVector(0));
                material.SetVector(IdReachType, maps.ReachVector(1));
                material.SetVector(IdMapDim, new Vector4(maps.Width, maps.Height, 0f, 0f));
            }
            else
            {
                material.SetVector(IdMapDim, new Vector4(2f, 2f, 0f, 0f));
            }
            if (ReferenceEquals(source, uploadedSource) && source.Version == uploadedVersion && CloudCoverShift.Stamp == uploadedShift) return;
            layerCount = source.LayerCount;
            // Four channels a sample, as the layer pass's (master plan 43): the type's density profile (r), its erosion gain (g; the shadow has no erosion and leaves it), its extra noise amount (b), its LIFT (a).
            var profile = new float[LayerTypeTable.ProfileSamples * CloudLayerDef.MaxLayers * CloudLayerDef.MaxTypes * 4];
            var info = new Color[CloudLayerDef.MaxTypes * CloudLayerDef.MaxLayers * 2];
            for (int l = 0; l < layerCount; l++)
            {
                CloudLayerDef layer = source.LayerAt(l);
                LayerTypeTable table = source.TableAt(l);
                float tightInner, tightOuter;
                layer.TightAltitudes(out tightInner, out tightOuter);
                layerA[l] = new Vector4(layer.InnerAltitudeM, layer.OuterAltitudeM, tightInner, tightOuter);
                layerB[l] = new Vector4(layer.EffectiveSigmaPerM, layer.EffectiveCoverage, layer.EffectiveEdgeSoftness, layer.ShadowStrength);
                layerC[l] = new Vector4(layer.ClearSkyK, layer.EffectiveWaterPower - 1f, layer.CoverageMapHandle < 0 && layer.TypeMapHandle < 0 ? -1f : l, table.Count);
                layerD[l] = new Vector4(0f, 0f, 0f, layer.MapShiftDeg);
                // The noise numbers as the layer pass has them (LayerRenderer: amount, 1 / scale, erosion, map shift), the erosion 0: the shape alone, as the pass's light march reads it.
                noiseD[l] = new Vector4(layer.EffectiveNoiseAmount, layer.EffectiveNoiseScaleM > 0f ? 1f / layer.EffectiveNoiseScaleM : 0f, 0f, layer.MapShiftDeg);
                noiseOff[l] = new Vector4(0f, 0f, 0f, layer.MapShiftDeg);
                float[] density = table.ProfileData, liftData = table.LiftData, scaleData = table.ErosionGainData, amountData = table.NoiseAmountData;
                for (int i = 0; i < table.Count * LayerTypeTable.ProfileSamples; i++)
                {
                    int texel = (l * CloudLayerDef.MaxTypes * LayerTypeTable.ProfileSamples + i) * 4;
                    profile[texel] = density[i];
                    profile[texel + 1] = scaleData[i];
                    profile[texel + 2] = amountData[i];
                    profile[texel + 3] = liftData[i];
                }
                // x: the layer's largest extra noise amount (the shadow's empty-space bounds, as the layer pass's _LayerE.w).
                formP[l] = new Vector4(table.MaxNoiseAmount, 0f, 0f, 0f);
                for (int t = 0; t < table.Count; t++)
                {
                    float b, top, e;
                    table.Info(t, out b, out top, out e);
                    // z: the exponent of the type's density rim (0: the layer's; the shadow reads no erosion, so the erosion channel is free), w: the type's cumuliform preset (CLOUDR1).
                    int cumuliform = table.CumuliformOfType(t);
                    if (cumuliform > 0 && Noise != null) Noise.RequestCumuliform();
                    info[l * CloudLayerDef.MaxTypes + t] = new Color(b, top, table.EdgeOfType(t), cumuliform);
                    // w: the type's cloud form (cloud.field, CloudField.cs), as the layer pass's second row: the shadow draws the field's single clouds where the pass does.
                    info[(CloudLayerDef.MaxLayers + l) * CloudLayerDef.MaxTypes + t] = new Color(table.SmoothingOfType(t), 0f, 0f, table.FormOfType(t));
                }
            }
            profileTexture.SetPixelData(profile, 0);
            profileTexture.Apply(false);
            material.SetVectorArray(IdFormP, formP);
            infoTexture.SetPixels(info);
            infoTexture.Apply(false);
            uploadedSource = source;
            uploadedVersion = source.Version;
            uploadedShift = CloudCoverShift.Stamp;
        }

        // Writes the frame's numbers into the material.
        internal void Apply(CloudShadowFrame f)
        {
            Material m = material;
            m.SetVector(IdPlanet, new Vector4(f.PlanetCenter.x, f.PlanetCenter.y, f.PlanetCenter.z, (float)f.RadiusUnits));
            m.SetMatrix(IdWorldToMap, f.WorldToMap);
            m.SetFloat("_CsDebug", f.Debug);
            m.SetVector(IdSun, new Vector4(f.TowardsSun.x, f.TowardsSun.y, f.TowardsSun.z, f.MetresPerUnit));
            int steps = Mathf.Clamp(f.StepsPerSpan, 1, 24);
            m.SetVector(IdParams, new Vector4(steps, Mathf.Clamp(f.DiscTaps, 1, 8), Mathf.Clamp01(f.Floor), layerCount));
            // The steps of each layer's spans: the policy's count (the CPU twin CloudShadowField takes the same number), read from the layer as it stands.
            // y: the pitch of the layer's cloud cells (CELLS; 0 while cloud.cells is off), z: the layer's seed, both as the layer pass has them (the seed is the pass's _NoiseOffset.w, which
            // this material holds only while the shape noise is bound; a 24-bit number, exact as a float).
            bool cells = CloudLookTuning.Cells && Noise != null;
            for (int l = 0; l < layerCount; l++)
            {
                layerD[l].x = uploadedSource == null ? steps : CloudShadowStepPolicy.Count(f.StepPolicy, steps, uploadedSource.LayerAt(l));
                layerD[l].y = cells && uploadedSource != null ? uploadedSource.LayerAt(l).CellPitchM : 0f;
                layerD[l].z = Noise != null ? NoiseSeeds.LayerSeed(Noise.Switches.Seed, l) : 0f;
            }
            // The single cloud's body per layer, as the layer pass has it (CloudCells.cginc CellBody).
            for (int l = 0; l < CloudLayerDef.MaxLayers; l++) cellE[l] = CloudLookTuning.CellBodyVector(uploadedSource != null && l < layerCount ? uploadedSource.LayerAt(l) : null);
            m.SetVectorArray(LayerRenderer.IdCellE, cellE);
            // The map-driven sky's connected stratiform types, as the layer pass has them.
            for (int l = 0; l < CloudLayerDef.MaxLayers; l++)
                cellS[l] = uploadedSource != null && l < layerCount ? MapSkyRegions.StratiformTypes(uploadedSource.LayerAt(l)) : Vector4.zero;
            m.SetVectorArray(LayerRenderer.IdCellS, cellS);
            // The shadow casts the clouds the layer pass draws: the CLOUD FIELD's single clouds (the field's texture and uniforms are globals of CloudFieldGpu), or with cloud.field off the
            // test lattice, which is the shader's variant so the product's shader holds none of its code.
            bool lattice = !CloudLookTuning.Field;
            if (lattice != m.IsKeywordEnabled(LatticeKeyword))
            {
                if (lattice) m.EnableKeyword(LatticeKeyword);
                else m.DisableKeyword(LatticeKeyword);
            }
            bool reach = f.EmptyReachOn && f.EmptyReach != null;
            if (reach) m.SetTexture(IdCovReach, f.EmptyReach);
            m.SetVector(IdParams2, new Vector4(Mathf.Clamp01(f.DiffuseShare), Mathf.Clamp01(f.ForwardDirectShare), f.ShapeNoise && f.MacroLift && Noise != null ? 1f : 0f, reach ? 1f : 0f));
            m.SetVectorArray(IdLayerA, layerA);
            m.SetVectorArray(IdLayerB, layerB);
            m.SetVectorArray(IdLayerC, layerC);
            m.SetVectorArray(IdLayerD, layerD);
            // The shape noise: the layer pass's volumes and offsets bound to this material, the amounts as the layers have them; the amounts 0 (switch off, or no noise object)
            // leave the shader on the map's threshold, exactly the shadow before the noise.
            bool noise = f.ShapeNoise && Noise != null;
            if (noise) Noise.Bind(m, layerCount);
            m.SetVectorArray(IdNoiseD, noise ? noiseD : noiseOff);
            m.SetFloat(IdPlanetR, (float)(f.RadiusUnits * f.MetresPerUnit));
            // The test switches' variant (CloudDevVariant): the noise's switches are on this material only while the noise is bound; without it the noise is not read.
            if (noise) CloudDevVariant.Apply(m, CloudDevVariant.Shadow);
            else if (m.IsKeywordEnabled(CloudDevVariant.Keyword)) m.DisableKeyword(CloudDevVariant.Keyword);
        }

        // The sun's buffer for a camera of that size (deferred rendering): the depth to half size, the shadow at half size, and the multiply into
        // the sun's shadow mask. Two buffers on the sun light at LightEvent.AfterScreenspaceMask, in this order: the build (it changes the
        // render target) and the display (CurrentActive is then the mask again, as EVE's two buffers do it).
        internal void RecordSunBuffer(CommandBuffer build, CommandBuffer display, int width, int height, int divisor = 2)
        {
            build.Clear();
            RecordShadow(build, width, height, divisor);
            display.Clear();
            Sub(display, "begin");
            display.Blit(BuiltinRenderTextureType.None, BuiltinRenderTextureType.CurrentActive, material, PassMask);
            Sub(display, "sun mask multiply");
            ReleaseShadow(display);
        }

        // The camera's buffer for forward rendering: the same shadow multiplied into the colour (the sun's share of it only,
        // CloudShadowFrame.ForwardDirectShare). Goes on the camera at AfterForwardOpaque. afterShadow runs between the shadow and the
        // multiply, beforeShadow at the start (the cost scene puts its GPU timestamps there).
        internal void RecordForwardBuffer(CommandBuffer cb, int width, int height, Action<CommandBuffer> beforeShadow = null, Action<CommandBuffer> afterShadow = null, int divisor = 2)
        {
            cb.Clear();
            if (beforeShadow != null) beforeShadow(cb);
            RecordShadow(cb, width, height, divisor);
            if (afterShadow != null) afterShadow(cb);
            // The blits above left a temporary texture as the render target: back to the camera's.
            cb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            cb.Blit(BuiltinRenderTextureType.None, BuiltinRenderTextureType.CurrentActive, material, PassColour);
            Sub(cb, "forward colour multiply");
            ReleaseShadow(cb);
        }

        private static void ReleaseShadow(CommandBuffer cb)
        {
            cb.ReleaseTemporaryRT(Shader.PropertyToID(TextureNameDepth));
            cb.ReleaseTemporaryRT(Shader.PropertyToID(TextureNameShadows));
        }

        // The planet's disc in scaled space (map view, orbit): one pass multiplies the shadow into the colour. No half-size target.
        internal void RecordDiscBuffer(CommandBuffer cb)
        {
            cb.Clear();
            cb.Blit(BuiltinRenderTextureType.None, BuiltinRenderTextureType.CurrentActive, material, PassDisc);
        }

        // The ocean's shadow texture, the way EVE's AddOceanShadowCommands builds it for Scatterer: the ocean's depth to half size, the shadow at
        // half size into ScattererOceanShadowsTexture, ScattererDownscaledOceanDepthTexture for the ocean shader to upsample with. The textures
        // stay allocated for the frame (Scatterer reads them later in its own buffer). oceanDepth is the ocean's depth texture. [VERIFY] which
        // global holds the ocean depth and in which event Scatterer reads the texture (K-T1).
        internal void RecordOceanShadows(CommandBuffer cb, int width, int height, RenderTargetIdentifier oceanDepth)
        {
            int w = Mathf.Max(1, width / 2), h = Mathf.Max(1, height / 2);
            int depthId = Shader.PropertyToID(OceanDepthName), shadowId = Shader.PropertyToID(OceanShadowsName);
            cb.GetTemporaryRT(depthId, w, h, 0, FilterMode.Point, RenderTextureFormat.RFloat);
            cb.GetTemporaryRT(shadowId, w, h, 0, FilterMode.Bilinear, RenderTextureFormat.R8);
            cb.SetGlobalVector("_CsDepthSource_TexelSize", new Vector4(1f / width, 1f / height, width, height));
            cb.SetGlobalVector("_CsDepthHalf_TexelSize", new Vector4(1f / w, 1f / h, w, h));
            cb.SetGlobalTexture(IdDepthSource, oceanDepth);
            cb.Blit(BuiltinRenderTextureType.None, depthId, material, PassDownscaleSource);
            cb.SetGlobalTexture(IdDepthHalf, depthId);
            cb.Blit(BuiltinRenderTextureType.None, shadowId, material, PassShadow);
            cb.SetGlobalTexture(OceanShadowsName, shadowId);
        }

        // The shadow at half size from the camera's depth texture (a global texture of Unity's, read by name in the shader: it is not a
        // temporary render texture, so a command buffer cannot bind it by an identifier).
        // GPU profiler sub-markers (labels "ReDefinition clouds shadow: <part>"); the module sets it to the host's marker seam, null alone.
        internal static Action<CommandBuffer, string> SubMark;

        private static void Sub(CommandBuffer cb, string part)
        {
            if (SubMark != null) SubMark(cb, "ReDefinition clouds shadow: " + part);
        }

        private void RecordShadow(CommandBuffer cb, int fullW, int fullH, int divisor)
        {
            Sub(cb, "begin");
            int k = Mathf.Clamp(divisor, 2, 4);
            int w = Mathf.Max(1, fullW / k), h = Mathf.Max(1, fullH / k);
            int depthId = Shader.PropertyToID(TextureNameDepth), shadowId = Shader.PropertyToID(TextureNameShadows);
            cb.GetTemporaryRT(depthId, w, h, 0, FilterMode.Point, RenderTextureFormat.RFloat);
            cb.GetTemporaryRT(shadowId, w, h, 0, FilterMode.Bilinear, ShadowFormat);
            // The size the shader divides by is set by hand: a temporary render texture named by an identifier carries no size of its own.
            cb.SetGlobalVector("_CsDepthHalf_TexelSize", new Vector4(1f / w, 1f / h, w, h));
            cb.Blit(BuiltinRenderTextureType.None, depthId, material, PassDownscale);
            Sub(cb, "depth to half size");
            cb.SetGlobalTexture(IdDepthHalf, depthId);
            cb.Blit(BuiltinRenderTextureType.None, shadowId, material, PassShadow);
            Sub(cb, "shadow march");
            cb.SetGlobalTexture(IdShadowHalf, shadowId);
        }
    }
}
