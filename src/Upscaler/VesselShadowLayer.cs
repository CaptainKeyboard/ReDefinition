using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;
using ReDefinition.Shared;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Upscaler
{
    // The active vessel's shadow kept out of frame generation's motion.
    //
    // Frame generation moves each pixel of the HUD-less image with its motion vector, the
    // motion of the surface. The vessel's shadow on that surface moves with the vessel. On
    // a runway under a vessel the camera follows, the shadow stands on the screen while the
    // ground under it runs, and the generated frames dragged the shadow along with the
    // ground: measured in a recording at 2560x1440, the shadow of a canard 90 px off in each
    // generated frame, or gone. Given the HUD-less image with the shadow taken out, DLSS
    // frame generation treats the difference to the frame, the shadow, like the interface
    // and does not move it (measured in the proxy harness's replay of that recording).
    //
    // Per pixel, since a shadow has no one motion: nearer and farther parts of it, parts on
    // a slope or on a building, the shadows of different parts each move otherwise.
    // - How much of the sun the vessel takes there: Unity's own screen-space shadow mask
    //   for the light (LightEvent.AfterScreenspaceMask), which holds the half shadow,
    //   cascades and filtering as drawn, where the light map says the vessel is in the way.
    //   The light map is the vessel alone drawn from the sun, so the shadows of terrain,
    //   buildings, grass and clouds never count.
    // - Where that bit of shadow lay in the frame before: the occluding point, as far along
    //   the light as the light map says, moved as its renderer moved, and its shadow along
    //   the light onto a plane through the surface point here.
    // - Left in place the shadow is off by its own screen motion, moved with the surface by
    //   the difference to the surface's motion vector; the weight goes over to the smaller
    //   error. A vessel standing still under a turning camera keeps its shadow in the
    //   HUD-less image, as before.
    // - How dark full shadow is in the finished image, per channel: the local mean colour
    //   in full shadow against the lit ground next to it, from mip chains of both, so
    //   whatever the sun's colour, the shadow strength, the atmosphere or the post
    //   processing do to it is measured, not assumed.
    //
    // The light: the brightest enabled directional light casting shadows that lights the
    // local scenery (layer 15); with several stars (Kopernicus) only the brightest one's
    // shadow is handled. Its direction is read in the scene camera's OnPreRender, after
    // every OnPreCull, where Unity has built the shadow cascades from it (CameraRedirect).
    //
    // KSP's floating origin and Krakensbane move the world between frames: the vessel and
    // the camera by minus SharedFrame.OriginShift, the ground and the bodies by minus
    // BodyShift, which holds the Krakensbane step in fast flight. The previous frame's
    // view-projection and part matrices are carried into this frame's coordinates by the
    // first, and the surface the shadow fell on lay off by the difference of the two.
    //
    // Left alone: a frame with a history reset since the one before, and anything drawn
    // over the shadow (engine plumes, dust), which is taken out with it where the shadow
    // is.
    internal sealed class VesselShadowLayer
    {
        private const int LightMapSize = 1024;
        private const int CasterPass = 0;
        private const int WeightPass = 1;
        private const int ShadowedPass = 2;
        private const int LitPass = 3;
        private const int ComposePass = 4;
        private const int LocalLayer = 15;
        private const float LightSearchSeconds = 2f;
        private const float ReportSeconds = 10f;
        // Bias along the light, and how far from the shadow the lit ground it is compared
        // with may lie, both in metres.
        private const float DepthBias = 0.15f;
        private const float NearReachMetres = 3f;
        // How far the vessel may stand from a point in the light and still take part in its
        // shadow: wider than the soft edge Unity draws, metres.
        private const float TakePartMetres = 0.5f;
        // Local means from blocks of about 32 to 256 display pixels.
        private const int FinestMip = 4;
        private const int CoarsestMip = 7;

        // The Debug switch; on at every start.
        internal static bool Enabled = true;

        private static readonly int LightRasterId = Shader.PropertyToID("_VesselShadowLightRaster");
        private static readonly int LightViewProjectionId = Shader.PropertyToID("_VesselShadowLightViewProjection");
        private static readonly int LightId = Shader.PropertyToID("_VesselShadowLight");
        private static readonly int PreviousModelId = Shader.PropertyToID("_VesselShadowPreviousModel");
        private static readonly int DepthId = Shader.PropertyToID("_VesselShadowDepth");
        private static readonly int MotionId = Shader.PropertyToID("_VesselShadowMotion");
        private static readonly int MaskId = Shader.PropertyToID("_VesselShadowMask");
        private static readonly int LightMapId = Shader.PropertyToID("_VesselShadowLightMap");
        private static readonly int RasterInverseId = Shader.PropertyToID("_VesselShadowRasterInverse");
        private static readonly int ViewProjectionId = Shader.PropertyToID("_VesselShadowViewProjection");
        private static readonly int PreviousViewProjectionId = Shader.PropertyToID("_VesselShadowPreviousViewProjection");
        private static readonly int TexelId = Shader.PropertyToID("_VesselShadowTexel");
        private static readonly int ParamsId = Shader.PropertyToID("_VesselShadowParams");
        private static readonly int ReachId = Shader.PropertyToID("_VesselShadowReach");
        private static readonly int ReceiverShiftId = Shader.PropertyToID("_VesselShadowReceiverShift");
        private static readonly int WeightId = Shader.PropertyToID("_VesselShadowWeight");
        private static readonly int ShadowedId = Shader.PropertyToID("_VesselShadowShadowed");
        private static readonly int LitId = Shader.PropertyToID("_VesselShadowLit");
        private static readonly int MipsId = Shader.PropertyToID("_VesselShadowMips");
        private static readonly int ColourId = Shader.PropertyToID("_VesselShadowColour");

        private struct Caster
        {
            internal Renderer Renderer;
            internal int Submeshes;
        }

        private readonly List<Caster> casters = new List<Caster>();
        private readonly Dictionary<Renderer, Matrix4x4> previousModel = new Dictionary<Renderer, Matrix4x4>();
        private Vessel castersOf;
        private int castersPartCount;

        private Material material;
        private string state;
        private RenderTexture lightMap;
        private RenderTexture mask;
        private RenderTexture weight;
        private RenderTexture shadowed;
        private RenderTexture lit;
        private CommandBuffer maskBuffer;
        private Light light;
        private float lightSearch;

        // This frame's, from Begin.
        private bool active;
        private bool continuous;
        private Matrix4x4 viewProjection;
        private Matrix4x4 rasterInverse;
        private Matrix4x4 previousViewProjection;
        // This frame's origin shift, and where the surface lay before against the vessel.
        private Vector3 originShift;
        private Vector3 receiverShift;
        private int previousFrame = -10;
        private Vector2Int size;
        private int framesDrawn;
        private int framesSkipped;

        // What the frame's passes found, read back once a report: a sample grid over the
        // mask and the weight, so the log says which step holds where the shadow is not
        // taken out.
        private const int SampleWidth = 160;
        private const int SampleHeight = 90;
        private RenderTexture maskSample;
        private RenderTexture weightSample;
        private bool sampleRequested;
        private string sampleLine = "not read yet";
        private float windowStart = -1f;

        internal void Disable()
        {
            DetachLight();
            UpscalerRig.Release(ref lightMap);
            UpscalerRig.Release(ref mask);
            UpscalerRig.Release(ref weight);
            UpscalerRig.Release(ref shadowed);
            UpscalerRig.Release(ref lit);
            UpscalerRig.Release(ref maskSample);
            UpscalerRig.Release(ref weightSample);
            if (material != null) Object.Destroy(material);
            material = null;
            casters.Clear();
            previousModel.Clear();
            castersOf = null;
        }

        // In the scene camera's OnPreCull, after SharedFrame has begun the frame: its
        // matrices without the jitter, which CameraRedirect adds in OnPreRender, whether the
        // frame before describes the motion to this one, and the origin shift between them.
        internal void Begin(Camera camera, Vector2Int renderSize)
        {
            active = false;
            if (!Enabled || camera == null || !HighLogic.LoadedSceneIsFlight || FlightGlobals.ActiveVessel == null
                || !Prepare(renderSize))
            {
                previousFrame = -10;
                return;
            }
            if (windowStart < 0f) windowStart = Time.unscaledTime;

            Matrix4x4 view = camera.worldToCameraMatrix;
            Matrix4x4 projection = camera.projectionMatrix;
            Matrix4x4 thisViewProjection = GL.GetGPUProjectionMatrix(projection, false) * view;
            Matrix4x4 jittered = CameraRedirect.JitterActive
                ? Matrix4x4.Translate(new Vector3(CameraRedirect.JitterNdc.x, CameraRedirect.JitterNdc.y, 0f)) * projection
                : projection;
            rasterInverse = (GL.GetGPUProjectionMatrix(jittered, false) * view).inverse;
            continuous = previousFrame == Time.frameCount - 1 && SharedFrame.RigResetReason == null;
            if (!continuous) previousViewProjection = thisViewProjection;
            // A point the shift moved lay at its position now plus the shift.
            originShift = SharedFrame.Shifted ? (Vector3)SharedFrame.OriginShift : Vector3.zero;
            receiverShift = SharedFrame.Shifted ? (Vector3)(SharedFrame.BodyShift - SharedFrame.OriginShift) : Vector3.zero;
            if (continuous && SharedFrame.Shifted)
                previousViewProjection = previousViewProjection * Matrix4x4.Translate(originShift);
            viewProjection = thisViewProjection;
            previousFrame = Time.frameCount;
            size = renderSize;
            active = true;
        }

        // In the scene camera's OnPreRender, after the shadow cascades are built: the light
        // map and the weight, appended to the capture at BeforeImageEffects after its depth,
        // motion vectors and hooks. A frame without a weight clears it, so the HUD-less
        // composition, recorded once, never uses an old one.
        internal void Record(CommandBuffer capture, RenderTexture depth, RenderTexture motionVectors)
        {
            if (!active || depth == null || motionVectors == null)
            {
                Clear(capture);
                return;
            }
            if (Time.unscaledTime - windowStart >= ReportSeconds) Report();

            FindLight();
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (light == null || vessel == null)
            {
                framesSkipped++;
                Clear(capture);
                return;
            }
            if (vessel != castersOf || vessel.parts.Count != castersPartCount) ChooseCasters(vessel);

            Bounds bounds;
            if (!continuous || !CasterBounds(out bounds))
            {
                framesSkipped++;
                Clear(capture);
                KeepModels();
                previousViewProjection = viewProjection;
                return;
            }

            // The light map: an orthographic view from the sun over the vessel's bounds.
            Vector3 towardsLight = -light.transform.forward;
            float radius = bounds.extents.magnitude + 1f;
            Vector3 eye = bounds.center + towardsLight * (radius + 2f);
            Matrix4x4 lightView = Matrix4x4.Scale(new Vector3(1f, 1f, -1f))
                                  * Matrix4x4.TRS(eye, Quaternion.LookRotation(-towardsLight), Vector3.one).inverse;
            Matrix4x4 lightProjection = Matrix4x4.Ortho(-radius, radius, -radius, radius, 0.05f, 2f * radius + 4f);
            Matrix4x4 lightRaster = GL.GetGPUProjectionMatrix(lightProjection, true) * lightView;
            Matrix4x4 lightSample = GL.GetGPUProjectionMatrix(lightProjection, false) * lightView;
            Vector4 lightVector = new Vector4(towardsLight.x, towardsLight.y, towardsLight.z, Vector3.Dot(eye, towardsLight));

            capture.SetRenderTarget(lightMap);
            capture.ClearRenderTarget(true, true, new Color(0f, 0f, 0f, 1e9f));
            capture.SetGlobalMatrix(LightRasterId, lightRaster);
            capture.SetGlobalVector(LightId, lightVector);
            foreach (Caster c in casters)
            {
                if (c.Renderer == null || !c.Renderer.enabled || !c.Renderer.gameObject.activeInHierarchy
                    || c.Renderer.shadowCastingMode == ShadowCastingMode.Off)
                    continue;
                Matrix4x4 before;
                if (previousModel.TryGetValue(c.Renderer, out before))
                    before = Matrix4x4.Translate(-originShift) * before;
                else
                    before = c.Renderer.localToWorldMatrix;
                capture.SetGlobalMatrix(PreviousModelId, before);
                for (int s = 0; s < c.Submeshes; s++) capture.DrawRenderer(c.Renderer, material, s, CasterPass);
            }

            capture.SetGlobalTexture(DepthId, depth);
            capture.SetGlobalTexture(MotionId, motionVectors);
            capture.SetGlobalTexture(MaskId, mask);
            capture.SetGlobalTexture(LightMapId, lightMap);
            capture.SetGlobalMatrix(RasterInverseId, rasterInverse);
            capture.SetGlobalMatrix(ViewProjectionId, viewProjection);
            capture.SetGlobalMatrix(PreviousViewProjectionId, previousViewProjection);
            capture.SetGlobalMatrix(LightViewProjectionId, lightSample);
            capture.SetGlobalVector(TexelId, new Vector4(1f / size.x, 1f / size.y, size.x, size.y));
            capture.SetGlobalVector(ParamsId, new Vector4(Mathf.Max(light.shadowStrength, 1e-3f), 1f / LightMapSize,
                                                              DepthBias, NearReachMetres * LightMapSize / (2f * radius)));
            capture.SetGlobalVector(ReachId, new Vector4(TakePartMetres * LightMapSize / (2f * radius), 0f, 0f, 0f));
            capture.SetGlobalVector(ReceiverShiftId, receiverShift);
            capture.Blit(depth, weight, material, WeightPass);
            if (!sampleRequested) RequestSample(capture);

            KeepModels();
            previousViewProjection = viewProjection;
            framesDrawn++;
        }

        // Into the HUD-less capture, in place of the plain copy: the frame with the
        // vessel's shadow taken out by this frame's weight. False where there is no
        // weight for this frame; the caller then copies as before.
        internal bool Compose(CommandBuffer buffer, RenderTargetIdentifier source, RenderTexture hudLess,
                              Vector2Int renderSize)
        {
            if (hudLess == null || !Prepare(renderSize) || !EnsureDisplayTargets(hudLess)) return false;
            buffer.GetTemporaryRT(ColourId, hudLess.width, hudLess.height, 0, FilterMode.Bilinear, hudLess.format);
            buffer.Blit(source, ColourId);
            buffer.SetGlobalTexture(WeightId, weight);
            buffer.Blit(ColourId, shadowed, material, ShadowedPass);
            buffer.Blit(ColourId, lit, material, LitPass);
            buffer.GenerateMips(shadowed);
            buffer.GenerateMips(lit);
            buffer.SetGlobalTexture(ShadowedId, shadowed);
            buffer.SetGlobalTexture(LitId, lit);
            buffer.SetGlobalVector(MipsId, new Vector4(FinestMip - 1, CoarsestMip - 1, 0f, 0f));
            buffer.Blit(ColourId, hudLess, material, ComposePass);
            buffer.ReleaseTemporaryRT(ColourId);
            return true;
        }

        private void Clear(CommandBuffer capture)
        {
            if (weight == null || capture == null) return;
            capture.SetRenderTarget(weight);
            capture.ClearRenderTarget(false, true, Color.clear);
        }

        private void KeepModels()
        {
            foreach (Caster c in casters)
                if (c.Renderer != null) previousModel[c.Renderer] = c.Renderer.localToWorldMatrix;
        }

        private bool CasterBounds(out Bounds bounds)
        {
            bounds = default(Bounds);
            bool any = false;
            foreach (Caster c in casters)
            {
                if (c.Renderer == null || !c.Renderer.enabled || !c.Renderer.gameObject.activeInHierarchy) continue;
                if (!any) bounds = c.Renderer.bounds;
                else bounds.Encapsulate(c.Renderer.bounds);
                any = true;
            }
            return any;
        }

        private void ChooseCasters(Vessel vessel)
        {
            castersOf = vessel;
            castersPartCount = vessel.parts.Count;
            casters.Clear();
            previousModel.Clear();
            foreach (Part part in vessel.parts)
            {
                if (part == null) continue;
                foreach (Renderer renderer in part.GetComponentsInChildren<Renderer>(true))
                {
                    Mesh mesh = null;
                    MeshRenderer meshRenderer = renderer as MeshRenderer;
                    SkinnedMeshRenderer skinned = renderer as SkinnedMeshRenderer;
                    if (meshRenderer != null)
                    {
                        MeshFilter filter = meshRenderer.GetComponent<MeshFilter>();
                        mesh = filter != null ? filter.sharedMesh : null;
                    }
                    else if (skinned != null)
                        mesh = skinned.sharedMesh;
                    if (mesh == null) continue;
                    Material[] materials = renderer.sharedMaterials;
                    if (materials == null || materials.Length == 0) continue;
                    casters.Add(new Caster
                    {
                        Renderer = renderer,
                        Submeshes = Mathf.Min(materials.Length, mesh.subMeshCount),
                    });
                }
            }
        }

        // The light that casts the local scenery's shadows, looked for every few seconds and
        // whenever the one found is gone or switched off.
        private void FindLight()
        {
            bool usable = light != null && light.isActiveAndEnabled && light.shadows != LightShadows.None;
            if (usable && Time.unscaledTime < lightSearch) return;
            lightSearch = Time.unscaledTime + LightSearchSeconds;

            Light best = null;
            float bestIntensity = 0f;
            foreach (Light candidate in Object.FindObjectsOfType<Light>())
            {
                if (candidate == null || !candidate.isActiveAndEnabled || candidate.type != LightType.Directional
                    || candidate.shadows == LightShadows.None || (candidate.cullingMask & (1 << LocalLayer)) == 0)
                    continue;
                Color c = candidate.color;
                float intensity = candidate.intensity * Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                if (best == null || intensity > bestIntensity)
                {
                    best = candidate;
                    bestIntensity = intensity;
                }
            }
            if (best == light) return;

            DetachLight();
            light = best;
            if (light == null) return;
            maskBuffer = new CommandBuffer { name = "ReDefinition.VesselShadowMask" };
            maskBuffer.Blit(BuiltinRenderTextureType.CurrentActive, mask);
            light.AddCommandBuffer(LightEvent.AfterScreenspaceMask, maskBuffer);
            Debug.Log(Log.Tag + " Vessel shadow for frame generation: the shadows of '" + light.name + "'.");
        }

        private void DetachLight()
        {
            if (maskBuffer != null)
            {
                if (light != null) light.RemoveCommandBuffer(LightEvent.AfterScreenspaceMask, maskBuffer);
                maskBuffer.Release();
                maskBuffer = null;
            }
            light = null;
        }

        private bool Prepare(Vector2Int renderSize)
        {
            if (state != null) return false;
            if (material == null)
            {
                string error;
                Shader shader = FsrShaderBundle.LoadShader(FsrShaderBundle.VesselShadowShaderName, out error);
                if (shader == null)
                {
                    state = error;
                    Debug.Log(Log.Tag + " Vessel shadow for frame generation left out: " + error + ".");
                    return false;
                }
                material = new Material(shader) { name = "ReDefinition vessel shadow", hideFlags = HideFlags.DontSave };
            }
            if (lightMap == null)
            {
                lightMap = new RenderTexture(LightMapSize, LightMapSize, 24, RenderTextureFormat.ARGBFloat)
                {
                    name = "ReDefinition_VesselShadowLightMap",
                    filterMode = FilterMode.Point,
                };
                lightMap.Create();
            }
            bool created;
            if (!UpscalerMasks.Ensure(ref mask, renderSize, RenderTextureFormat.R8, "ReDefinition_VesselShadowMask", out created))
                return false;
            if (created)
            {
                mask.filterMode = FilterMode.Bilinear;
                // The light's copy into the old one is recorded with it: attached anew.
                DetachLight();
            }
            if (!UpscalerMasks.Ensure(ref weight, renderSize, RenderTextureFormat.ARGBHalf, "ReDefinition_VesselShadowWeight",
                                      out created))
                return false;
            if (created) weight.filterMode = FilterMode.Bilinear;
            return true;
        }

        // Half the display size with mips, for the local means.
        private bool EnsureDisplayTargets(RenderTexture hudLess)
        {
            int width = Mathf.Max(1, hudLess.width / 2);
            int height = Mathf.Max(1, hudLess.height / 2);
            return EnsureMipped(ref shadowed, width, height, "ReDefinition_VesselShadowShadowed")
                   && EnsureMipped(ref lit, width, height, "ReDefinition_VesselShadowLit");
        }

        private static bool EnsureMipped(ref RenderTexture texture, int width, int height, string name)
        {
            if (texture != null && texture.width == width && texture.height == height && texture.IsCreated()) return true;
            UpscalerRig.Release(ref texture);
            texture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBHalf)
            {
                name = name,
                useMipMap = true,
                autoGenerateMips = false,
                filterMode = FilterMode.Bilinear,
            };
            return texture.Create();
        }

        private void RequestSample(CommandBuffer capture)
        {
            if (maskSample == null)
            {
                maskSample = new RenderTexture(SampleWidth, SampleHeight, 0, RenderTextureFormat.ARGBHalf)
                    { name = "ReDefinition_VesselShadowMaskSample", filterMode = FilterMode.Point };
                weightSample = new RenderTexture(SampleWidth, SampleHeight, 0, RenderTextureFormat.ARGBHalf)
                    { name = "ReDefinition_VesselShadowWeightSample", filterMode = FilterMode.Point };
                maskSample.Create();
                weightSample.Create();
            }
            sampleRequested = true;
            capture.Blit(mask, maskSample);
            capture.Blit(weight, weightSample);
            NativeArray<ushort> maskData = default(NativeArray<ushort>);
            bool haveMask = false;
            capture.RequestAsyncReadback(maskSample, request =>
            {
                if (request.hasError) return;
                maskData = new NativeArray<ushort>(request.GetData<ushort>(), Allocator.Persistent);
                haveMask = true;
            });
            capture.RequestAsyncReadback(weightSample, request =>
            {
                if (!request.hasError && haveMask)
                    Summarise(maskData, request.GetData<ushort>());
                if (maskData.IsCreated) maskData.Dispose();
            });
        }

        // Per sample: Unity's mask (r), the share of the sun the vessel takes (g), the
        // weight (r), lit ground near the shadow (b).
        private void Summarise(NativeArray<ushort> maskData, NativeArray<ushort> weightData)
        {
            int samples = SampleWidth * SampleHeight, shadowed = 0, taken = 0, weighted = 0, lit = 0;
            float maskMin = 1f;
            double weightSum = 0;
            for (int i = 0; i < samples; i++)
            {
                float m = Mathf.HalfToFloat(maskData[i * 4]);
                float w = Mathf.HalfToFloat(weightData[i * 4]);
                float g = Mathf.HalfToFloat(weightData[i * 4 + 1]);
                float b = Mathf.HalfToFloat(weightData[i * 4 + 2]);
                if (m < maskMin) maskMin = m;
                if (m < 0.9f) shadowed++;
                if (g > 0.1f) taken++;
                if (w > 0.1f)
                {
                    weighted++;
                    weightSum += w;
                }
                if (b > 0.5f) lit++;
            }
            sampleLine = string.Format(CultureInfo.InvariantCulture,
                "of {0} samples, {1} in shadow in Unity's mask (lowest {2:0.00}), {3} in the vessel's shadow, {4} weighted"
                + " (mean {5:0.00}), {6} lit ground to compare with",
                samples, shadowed, maskMin, taken, weighted, weighted > 0 ? weightSum / weighted : 0.0, lit);
        }

        private void Report()
        {
            float elapsed = Time.unscaledTime - windowStart;
            windowStart = Time.unscaledTime;
            Debug.Log(Log.Tag + " Vessel shadow for frame generation, last "
                      + elapsed.ToString("0.0", CultureInfo.InvariantCulture) + " s: " + framesDrawn + " frame(s) drawn, "
                      + framesSkipped + " left out (reset, no light or no vessel); light '"
                      + (light != null ? light.name : "none") + "', " + casters.Count + " caster(s).");
            Debug.Log(Log.Tag + " Vessel shadow for frame generation, one frame: " + sampleLine + ".");
            sampleRequested = false;
            framesDrawn = framesSkipped = 0;
        }
    }
}
