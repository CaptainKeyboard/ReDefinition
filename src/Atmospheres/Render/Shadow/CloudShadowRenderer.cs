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
        public float Floor;
        // The weight of the light a cloud scatters down (CloudShadowMath.FittedTransmission of the vertical optical depth) in the mask: 1 lifts
        // the mask under a thick cloud to the diffuse share of the open ground's sun, 0 leaves the pure direct beam (the analytic checks).
        public float DiffuseShare = 1f;
        // Forward shading only: the share of the lit colour that is the sun's direct light (CloudShadowMath.DirectShare); the ambient share
        // is not darkened. 1 multiplies the whole colour (the analytic checks).
        public float ForwardDirectShare = 1f;
        // A diagnostic output of the forward pass for the probe (0: the shadow); see the shader's pass 3.
        public int Debug;
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

        private static readonly int IdPlanet = Shader.PropertyToID("_CsPlanet"), IdWorldToMap = Shader.PropertyToID("_CsWorldToMap"),
            IdSun = Shader.PropertyToID("_CsSun"), IdParams = Shader.PropertyToID("_CsParams"), IdParams2 = Shader.PropertyToID("_CsParams2"), IdLayerA = Shader.PropertyToID("_CsLayerA"),
            IdLayerB = Shader.PropertyToID("_CsLayerB"), IdLayerC = Shader.PropertyToID("_CsLayerC"), IdLayerD = Shader.PropertyToID("_CsLayerD"),
            IdTypeProfile = Shader.PropertyToID("_CsTypeProfile"), IdTypeInfo = Shader.PropertyToID("_CsTypeInfo"),
            IdCovMaps = Shader.PropertyToID("_CsCovMaps"), IdTypeMaps = Shader.PropertyToID("_CsTypeMaps"), IdMapDim = Shader.PropertyToID("_CsMapDim"),
            IdDepthSource = Shader.PropertyToID("_CsDepthSource"), IdDepthHalf = Shader.PropertyToID("_CsDepthHalf"),
            IdShadowHalf = Shader.PropertyToID("_CsShadowHalf");

        private readonly Vector4[] layerA = new Vector4[CloudLayerDef.MaxLayers], layerB = new Vector4[CloudLayerDef.MaxLayers],
            layerC = new Vector4[CloudLayerDef.MaxLayers], layerD = new Vector4[CloudLayerDef.MaxLayers];
        private Material material;
        private Texture2D profileTexture, infoTexture;
        private int layerCount;
        private long uploadedVersion = -1;
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
            profileTexture = new Texture2D(LayerTypeTable.ProfileSamples, CloudLayerDef.MaxLayers * CloudLayerDef.MaxTypes, TextureFormat.RFloat, false, true)
            {
                name = "cloud shadow type profiles",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            infoTexture = new Texture2D(CloudLayerDef.MaxTypes, CloudLayerDef.MaxLayers, TextureFormat.RGBAFloat, false, true)
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
                material.SetVector(IdMapDim, new Vector4(maps.Width, maps.Height, 0f, 0f));
            }
            else
            {
                material.SetVector(IdMapDim, new Vector4(2f, 2f, 0f, 0f));
            }
            if (ReferenceEquals(source, uploadedSource) && source.Version == uploadedVersion) return;
            layerCount = source.LayerCount;
            var profile = new float[LayerTypeTable.ProfileSamples * CloudLayerDef.MaxLayers * CloudLayerDef.MaxTypes];
            var info = new Color[CloudLayerDef.MaxTypes * CloudLayerDef.MaxLayers];
            for (int l = 0; l < layerCount; l++)
            {
                CloudLayerDef layer = source.LayerAt(l);
                LayerTypeTable table = source.TableAt(l);
                float tightInner, tightOuter;
                layer.TightAltitudes(out tightInner, out tightOuter);
                layerA[l] = new Vector4(layer.InnerAltitudeM, layer.OuterAltitudeM, tightInner, tightOuter);
                layerB[l] = new Vector4(layer.SigmaPerM, layer.Coverage, layer.EdgeSoftness, layer.ShadowStrength);
                layerC[l] = new Vector4(0f, 0f, layer.CoverageMapHandle < 0 && layer.TypeMapHandle < 0 ? -1f : l, table.Count);
                layerD[l] = new Vector4(0f, 0f, 0f, layer.MapShiftDeg);
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

        // Writes the frame's numbers into the material.
        internal void Apply(CloudShadowFrame f)
        {
            Material m = material;
            m.SetVector(IdPlanet, new Vector4(f.PlanetCenter.x, f.PlanetCenter.y, f.PlanetCenter.z, (float)f.RadiusUnits));
            m.SetMatrix(IdWorldToMap, f.WorldToMap);
            m.SetFloat("_CsDebug", f.Debug);
            m.SetVector(IdSun, new Vector4(f.TowardsSun.x, f.TowardsSun.y, f.TowardsSun.z, f.MetresPerUnit));
            m.SetVector(IdParams, new Vector4(Mathf.Clamp(f.StepsPerSpan, 1, 24), Mathf.Clamp(f.DiscTaps, 1, 8), Mathf.Clamp01(f.Floor), layerCount));
            m.SetVector(IdParams2, new Vector4(Mathf.Clamp01(f.DiffuseShare), Mathf.Clamp01(f.ForwardDirectShare), 0f, 0f));
            m.SetVectorArray(IdLayerA, layerA);
            m.SetVectorArray(IdLayerB, layerB);
            m.SetVectorArray(IdLayerC, layerC);
            m.SetVectorArray(IdLayerD, layerD);
        }

        // The sun's buffer for a camera of that size (deferred rendering): the depth to half size, the shadow at half size, and the multiply into
        // the sun's shadow mask. Two buffers on the sun light at LightEvent.AfterScreenspaceMask, in this order: the build (it changes the
        // render target) and the display (CurrentActive is then the mask again, as EVE's two buffers do it).
        internal void RecordSunBuffer(CommandBuffer build, CommandBuffer display, int width, int height)
        {
            build.Clear();
            RecordShadow(build, width, height);
            display.Clear();
            display.Blit(BuiltinRenderTextureType.None, BuiltinRenderTextureType.CurrentActive, material, PassMask);
            ReleaseShadow(display);
        }

        // The camera's buffer for forward rendering: the same shadow multiplied into the colour (the sun's share of it only,
        // CloudShadowFrame.ForwardDirectShare). Goes on the camera at AfterForwardOpaque. afterShadow runs between the shadow and the
        // multiply, beforeShadow at the start (the cost scene puts its GPU timestamps there).
        internal void RecordForwardBuffer(CommandBuffer cb, int width, int height, Action<CommandBuffer> beforeShadow = null, Action<CommandBuffer> afterShadow = null)
        {
            cb.Clear();
            if (beforeShadow != null) beforeShadow(cb);
            RecordShadow(cb, width, height);
            if (afterShadow != null) afterShadow(cb);
            // The blits above left a temporary texture as the render target: back to the camera's.
            cb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            cb.Blit(BuiltinRenderTextureType.None, BuiltinRenderTextureType.CurrentActive, material, PassColour);
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
        private void RecordShadow(CommandBuffer cb, int fullW, int fullH)
        {
            int w = Mathf.Max(1, fullW / 2), h = Mathf.Max(1, fullH / 2);
            int depthId = Shader.PropertyToID(TextureNameDepth), shadowId = Shader.PropertyToID(TextureNameShadows);
            cb.GetTemporaryRT(depthId, w, h, 0, FilterMode.Point, RenderTextureFormat.RFloat);
            cb.GetTemporaryRT(shadowId, w, h, 0, FilterMode.Bilinear, ShadowFormat);
            // The size the shader divides by is set by hand: a temporary render texture named by an identifier carries no size of its own.
            cb.SetGlobalVector("_CsDepthHalf_TexelSize", new Vector4(1f / w, 1f / h, w, h));
            cb.Blit(BuiltinRenderTextureType.None, depthId, material, PassDownscale);
            cb.SetGlobalTexture(IdDepthHalf, depthId);
            cb.Blit(BuiltinRenderTextureType.None, shadowId, material, PassShadow);
            cb.SetGlobalTexture(IdShadowHalf, shadowId);
        }
    }
}
