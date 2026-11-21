using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // How the layer pass fills the full target each frame (package T05).
    internal enum ReconMode
    {
        // Every pixel marched every frame: the reference and the cost of the unreduced pass.
        Full,
        // Prototype A: one pixel of every N x N block per frame, the rest from the reprojected, variance-clipped history (LayerReconstructor).
        Block,
        // Prototype B: a march target of Resolution times the full one, laid up with the depth-aware composite; the history is the upscaler's.
        Low
    }

    // The numbers of the reconstruction. A plain object the caller keeps.
    internal sealed class ReconSettings
    {
        public ReconMode Mode = ReconMode.Block;
        // Block: the block's side (2: a quarter of the rays, 3: a ninth, 4: a sixteenth).
        public int Block = 2;
        // Low: the march target's size over the full one, and whether its rays carry the sub-pixel Halton jitter the upscaler would give them.
        public float Resolution = 0.5f;
        public bool Jitter;
        // Low (E40 grain): above 0 the reduced march target is blended into a history of its own size before it is laid up (the weight of the new frame; the
        // history is reprojected and clipped like the block mode's, ReDefinitionAtmospheresReconstruct.shader with a block of 1). 0: no history, every frame is
        // the march alone (the mode as it was).
        public float Accumulate;
        // Block: the weight of the new sample in a pixel that has one this frame, in a pixel that has none (its value is then the bilinear
        // upsample of the new samples), and the width of the clip box in standard deviations of the 3 x 3 new samples.
        // The weights are the sweep's pick (T05, tools/weather_metrics/temporal.py T05-temporal): 0.05 for the pixels without a new sample
        // gives the lowest shimmer at the 4 x 4 block with no more ghosting than 0.1 on the turns.
        public float FreshWeight = 0.5f, StaleWeight = 0.05f, ClipSigma = 1.0f;
        // CLOUDR3 (P7): the history is read with a five-tap Catmull-Rom filter instead of the bilinear one, which blurs the history a little with every sub-pixel move (a slow wind, a
        // turn). Off: bilinear, as before.
        public bool CatmullHistory;
        // The cloud's drift in the planet frame per frame (m): the wind that moves the pattern.
        public Vector3 DriftPerFrame;
        // E40 MVCLOUD: the cloud's place in the previous frame is taken on every layer's middle sphere and blended by the layers' share of the pixel (history and motion vector;
        // ReDefinitionAtmospheresCloudMotion.cginc). False: the first layer's middle sphere for every pixel, as before. DriftSeconds: the interval the layers' wind drift is taken over
        // (the frame's; 0: no drift).
        public bool LayerMotion = true;
        public float DriftSeconds;
        // The far rotation (Low mode with Accumulate above 0; the owner's question 2026-11-19, a test): a pixel whose ray enters the first layer beyond FarRotateM (0: off) is marched
        // once in FarRotateBlock^2 frames and keeps its reprojected history in between; the nearer pixels are marched every frame.
        public float FarRotateM;
        public int FarRotateBlock = 2;
        // The block mode as the full-size march's rotation (recon.rotate): the history's own weights, never the edge pattern.
        public bool Rotation;
        // recon.trueDistance (Block mode without the edge pattern): the march writes the mean distance of each pixel's cloud into a second target and the history is moved by it
        // instead of by the layers' middle spheres (ReDefinitionAtmospheresReconstruct.shader, TruePrevious). False: the middle spheres, as before.
        public bool TrueDistance;
        // recon.age (Block mode without the edge pattern): the reconstruction keeps the number of marched samples each pixel's history holds (a second target of its pass 3):
        // the n-th sample enters at max(FreshWeight, 1 / n) and a pixel without a marched sample shows the current upsample. False: the fixed weights, as before.
        public bool Age;
        // recon.distanceReject (with TrueDistance): the history keeps the distance of the cloud it showed (the count target's second channel) and is dropped where that is
        // more than this many octaves from the distance at which the previous camera saw the pixel's cloud (a disocclusion). 0: off, the colour clip alone.
        public float DistanceReject;
        // recon.sceneDepth (Block mode without the edge pattern; needs the frame's scene depth, LayerFrame.UseSceneDepth): the reconstruction weighs the new samples by
        // the agreement of the scene's depth at their pixel with the depth at the pixel it builds (the upsample and the clip box) and drops a history whose cloud lies
        // behind the pixel's scene. False: the scene's depth takes no part, as before.
        public bool SceneDepth;
        // recon.marchTriangle: the march without TrueDistance is drawn as one procedural triangle (the layer shader's pass 7) instead of the Blit's quad. False: the Blit,
        // as before.
        public bool ProcMarch;
        // 1: the new samples only (upsampled), 2: the history only (a trail test).
        public int Debug;
        // Package T12: the edge-adaptive resolution on top of the 4 x 4 block mode (null or not Enabled: the block mode as it was).
        public EdgeSettings Edge;

        internal ReconSettings Clone()
        {
            return (ReconSettings)MemberwiseClone();
        }

        // Probe camera policy (P61): a camera that is not the player's view (a reflection probe, a map or screenshot camera) renders rarely and from
        // poses that jump, so it has no history to keep: its own LayerReconstructor, one march at the reduced size, no jitter, composited at
        // once. The player's camera keeps one reconstructor of its own across frames and Resets it on a scene change or a camera cut.
        internal static ReconSettings ForProbe()
        {
            return new ReconSettings { Mode = ReconMode.Low, Resolution = 0.5f, Jitter = false };
        }
    }

    // Prototype A of the temporal work (and the plumbing of prototype B): marches the layer pass into a block-sampled or reduced target and
    // writes the full target (premultiplied radiance, alpha) that the layer shader's composite lays over the camera target. The reconstruction
    // keeps the last full target as its history, so the camera's motion and the wind's carry it (ReDefinitionAtmospheresReconstruct.shader).
    internal sealed partial class LayerReconstructor
    {
        internal const string ShaderName = "ReDefinition/AtmospheresReconstruct";

        private static readonly int IdFarRot = Shader.PropertyToID("_FarRot");
        private static readonly int IdReconP = Shader.PropertyToID("_ReconP"), IdReconD = Shader.PropertyToID("_ReconD"),
            IdReconK = Shader.PropertyToID("_ReconK"), IdCurTex = Shader.PropertyToID("_CurTex"), IdCurTexel = Shader.PropertyToID("_CurTex_TexelSize"),
            IdHistTex = Shader.PropertyToID("_HistTex"), IdCamPos = Shader.PropertyToID("_CamPos"), IdCamForward = Shader.PropertyToID("_CamForward"),
            IdCamRight = Shader.PropertyToID("_CamRight"), IdCamUp = Shader.PropertyToID("_CamUp"), IdPrevPos = Shader.PropertyToID("_PrevPos"),
            IdPrevF = Shader.PropertyToID("_PrevF"), IdPrevR = Shader.PropertyToID("_PrevR"), IdPrevU = Shader.PropertyToID("_PrevU"),
            IdShellR = Shader.PropertyToID("_ShellR"), IdDrift = Shader.PropertyToID("_Drift"), IdMarchTex = Shader.PropertyToID("_MarchTex"),
            IdSceneDepth = Shader.PropertyToID("_SceneDepth"), IdMarchTexel = Shader.PropertyToID("_MarchTex_TexelSize"),
            IdEdgeP =Shader.PropertyToID("_EdgeP"), IdEdgeMask = Shader.PropertyToID("_EdgeMask"),
            IdEdgeTex = Shader.PropertyToID("_EdgeTex"), IdEdgeTexel = Shader.PropertyToID("_EdgeTex_TexelSize"), IdEdgeK = Shader.PropertyToID("_EdgeK"),
            IdEdgeD = Shader.PropertyToID("_EdgeD"), IdReconE = Shader.PropertyToID("_ReconE");
        internal const string EdgeShaderName = "ReDefinition/AtmospheresEdge";
        // E40 MVCLOUD: pass 1 of the reconstruction shader draws the cloud's motion vectors; its inputs are the frame's full target and the host's copy of the scene depth.
        internal const int PassMotion = 1;
        // The far rotation (ReconSettings.FarRotateM): pass 2 writes the distance to each pixel's cloud, which the next frame's march reads.
        internal const int PassDepth = 2;
        private static readonly int IdFarDepth = Shader.PropertyToID("_FarDepth");
        // recon.trueDistance: the layer shader's pass 6 (the march into two targets), the second target and the pair the command buffer binds.
        internal const int PassMarchDistance = 6;
        // Test switch ReconSettings.ProcMarch (HIST2): the layer shader's pass 7, pass 0 drawn as one procedural triangle.
        internal const int PassMarchTriangle = 7;
        private static readonly int IdDistTex = Shader.PropertyToID("_DistTex"), IdReconT = Shader.PropertyToID("_ReconT");
        private RenderTexture lowDistance;
        private RenderTargetIdentifier[] marchTargets;
        // recon.age: the reconstruction shader's pass 3 (pass 0 into two targets), the sample counts of the two full targets (ageA with the one named "a", ageB with "b")
        // and the two pairs the command buffer binds.
        internal const int PassReconstructAge = 3;
        // What the last Record ran (the perf starts' rows, T09.view.<view>.recon_*): the mode, the block side (1 outside the block mode), whether the edge pattern drew, the layer
        // shader's pass the march drew with (0 the Blit, 6 the march with its distance target, 7 the procedural triangle), and whether the count targets were bound (pass 3).
        internal ReconMode LastMode { get; private set; }
        internal int LastBlock { get; private set; }
        internal bool LastEdge { get; private set; }
        internal int LastMarchPass { get; private set; }
        internal bool LastCountTarget { get; private set; }
        // Whether the block reconstruction read the scene's depth (recon.sceneDepth with the frame's scene depth bound).
        internal bool LastSceneDepth { get; private set; }
        // The game frame of the last Record (-1: none since ClearReadouts), the camera's render size and the march target's size in it.
        internal int LastRecordFrame { get; private set; } = -1;
        internal int LastRenderWidth { get; private set; }
        internal int LastRenderHeight { get; private set; }
        internal int LastMarchWidth { get; private set; }
        internal int LastMarchHeight { get; private set; }

        // The read-outs back to "nothing recorded" (a caller that reads them after a stretch of frames knows then that a value is this stretch's).
        internal void ClearReadouts()
        {
            LastMode = ReconMode.Full;
            LastBlock = 0;
            LastEdge = false;
            LastMarchPass = -1;
            LastCountTarget = false;
            LastSceneDepth = false;
            LastRecordFrame = -1;
            LastRenderWidth = LastRenderHeight = LastMarchWidth = LastMarchHeight = 0;
        }
        private static readonly int IdAgeTex = Shader.PropertyToID("_AgeTex");
        private RenderTexture fullA, ageA, ageB;
        private bool ageWasOn;   // the count targets were written last frame: a frame that turns them on again finds counts and distances of an older picture in them
        private RenderTargetIdentifier[] targetsA, targetsB;
        private RenderTexture farDepth;
        private bool haveFarDepth;
        private static readonly int IdMvCloud = Shader.PropertyToID("_MvCloud"), IdMvDepth = Shader.PropertyToID("_MvDepth");

        private Material material;
        // Package T12: the edge shader's material, the layer material's copy for the edge draw, the edge pattern's samples, the mask's state (last
        // frame's and this frame's), the dilation's middle step and the final mask, and the counter of the edge pattern.
        private Material edgeMaterial, edgeLayers;
        private RenderTexture edgeLow, stateLast, stateNew, dilated, mask;
        private bool haveEdgeState;
        private int edgeIndex;
        // The march target and the two full targets: the last finished one is the history, the other receives the frame.
        private RenderTexture low, latest, spare, output;
        // The history of the reduced target and its spare (ReconSettings.Accumulate; null until the mode Low accumulates).
        private RenderTexture lowLatest, lowSpare;
        private Vector3 previousPosition, previousForward, previousRight, previousUp;
        private float previousTanX = 1f, previousTanY = 1f;
        private bool haveHistory, haveLowHistory;
        // E40 MVCLOUD: a camera of the frame before exists (the previous pose is the one of the last Record, cleared by Reset), and whether the last Record had one (the motion vector of
        // that frame is drawn only then).
        private bool havePose, lastPoseValid;
        private int blockIndex;

        internal bool Ready { get { return material != null; } }

        // The rotation's block counter (0 after Reset, one step per Block or far-rotation frame): the pinned capture writes it into pass.json.
        internal int BlockIndex { get { return blockIndex; } }

        // The full target of the last Record (valid until the next one).
        internal RenderTexture Output { get { return output; } }

        // The march target of the last Record (the reduced one).
        internal RenderTexture March { get { return low; } }

        // The block offset the last Record marched (Block mode), in full pixels.
        internal Vector2 LastOffset { get; private set; }

        // Package T12: the edge mask of the last Record (one texel per 4 x 4 block; null before the first edge frame), the edge pattern's samples,
        // the blocks the dilation widened it by, and the extra marks (may be null) after the mask passes and after the edge march.
        internal RenderTexture Mask { get { return mask; } }
        internal RenderTexture EdgeMarch { get { return edgeLow; } }
        // The mask's state before the dilation (this frame's, after Record) and the coarse samples, for the measurements.
        internal RenderTexture MaskState { get { return stateLast; } }
        internal RenderTexture MaskHorizontal { get { return dilated; } }
        internal int LastDilation { get; private set; }
        internal Action<CommandBuffer> MarkMask, MarkEdge;
        // The timing marks around the culling's tile pass (package T16; the player sets them, the game leaves them null).
        internal Action<CommandBuffer> MarkCull, MarkCullEnd;
        // Called right after the light cache's or the light volume's fill (null: no mark; the weather player's cost scene times the fill as its own segment).
        internal Action<CommandBuffer> MarkCacheFilled;
        // The light volume (cloud.lightVolume): made when the key is first on; its textures are freed while it is off.
        private LightVolume lightVolume;
        internal LightVolume CurrentLightVolume { get { return lightVolume; } }
        // Whether this reconstruction may use the light volume: the player's view does; a probe camera's (reset every frame, LayerReconstructDriver.ProbeFor) does not, since
        // each reset would lay and fill its own volume completely every frame. It marches with today's light.
        internal bool LightVolumeAllowed = true;
        // Whether the layer material had the light volume's keyword (LightVolume.Keyword) right after this reconstruction's last Record bound or unbound it: the pinned capture
        // writes it into pass.json (another camera's Record may change the shared material's keyword before the frame's end).
        internal bool LightVolumeKeywordAtRecord { get; private set; }
        // GPU profiler sub-markers of the reconstruction (labels "ReDefinition clouds: <part>"); the module sets it to the host's marker seam, null alone (the weather player).
        internal static Action<CommandBuffer, string> SubMark;

        internal static void Sub(CommandBuffer cb, string part)
        {
            if (SubMark != null) SubMark(cb, "ReDefinition clouds: " + part);
        }

        internal bool Init(Shader shader)
        {
            if (shader == null) return false;
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return material.shader.isSupported;
        }

        // The edge shader (package T12); the edge mode of Record needs it.
        internal bool InitEdge(Shader shader)
        {
            if (shader == null) return false;
            edgeMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return edgeMaterial.shader.isSupported;
        }

        // The next frame has no history (a teleport, a scene change, a changed layer stack).
        internal void Reset()
        {
            haveHistory = false;
            haveLowHistory = false;
            haveFarDepth = false;
            haveEdgeState = false;
            havePose = false;
            blockIndex = 0;
            edgeIndex = 0;
            if (lightVolume != null) lightVolume.Invalidate();   // the next frame lays the light volume anew (a teleport leaves its content elsewhere)
        }

        internal void Dispose()
        {
            foreach (RenderTexture rt in new[] { low, latest, spare, edgeLow, stateLast, stateNew, dilated, mask, lowLatest, lowSpare, farDepth, lowDistance, ageA, ageB })
                RenderTargets.Dispose(rt);
            lowLatest = lowSpare = farDepth = lowDistance = ageA = ageB = fullA = null;
            marchTargets = targetsA = targetsB = null;
            if (material != null) UnityEngine.Object.Destroy(material);
            if (edgeMaterial != null) UnityEngine.Object.Destroy(edgeMaterial);
            if (edgeLayers != null) UnityEngine.Object.Destroy(edgeLayers);
            material = edgeMaterial = edgeLayers = null;
            if (lightVolume != null) lightVolume.Dispose();
            lightVolume = null;
            low = latest = spare = output = null;
            edgeLow = stateLast = stateNew = dilated = mask = null;
        }

        private static RenderTexture MakeMask(string name, int w, int h)
        {
            var rt = new RenderTexture(Mathf.Max(w, 2), Mathf.Max(h, 2), 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            rt.Create();
            return rt;
        }

        private static RenderTexture Make(string name, int w, int h, FilterMode filter)
        {
            var rt = new RenderTexture(Mathf.Max(w, 2), Mathf.Max(h, 2), 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = name,
                filterMode = filter,
                wrapMode = TextureWrapMode.Clamp
            };
            rt.Create();
            return rt;
        }

        private static RenderTexture MakeCount(string name, int w, int h)
        {
            var rt = new RenderTexture(Mathf.Max(w, 2), Mathf.Max(h, 2), 0, RenderTextureFormat.RGHalf, RenderTextureReadWrite.Linear)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            rt.Create();
            return rt;
        }

        private static bool Fits(RenderTexture rt, int w, int h)
        {
            return rt != null && rt.width == Mathf.Max(w, 2) && rt.height == Mathf.Max(h, 2);
        }

        // Lays the finished full target over the target (the layer shader's depth-aware composite at full size). The mark may be null.
        internal void Composite(CommandBuffer cb, LayerRenderer renderer, RenderTargetIdentifier destination, Action<CommandBuffer> markEnd)
        {
            if (!Ready || output == null) return;
            Material layers = renderer.Material;
            cb.SetGlobalVector(IdMarchTexel, new Vector4(1f / output.width, 1f / output.height, output.width, output.height));
            cb.SetGlobalTexture(IdMarchTex, output);
            cb.Blit(output, destination, layers, LayerRenderer.PassCompositeOut);
            if (markEnd != null) markEnd(cb);
        }

        // Records the frame's march and reconstruction into the buffer: the full target is Output afterwards. The camera is the player's or
        // the game's, the planet centre is in the same space as its position (floating-origin shifts move both and change nothing). The marks (may be
        // null) are called before the march, after it and after the reconstruction.
        internal void Record(CommandBuffer cb, LayerRenderer renderer, LayerFrame f, Camera camera, ReconSettings s, int frameNumber,
                             Action<CommandBuffer> markStart, Action<CommandBuffer> markMarched, Action<CommandBuffer> markEnd)
        {
            if (!Ready || !renderer.Ready) return;
            int w = camera.pixelWidth, h = camera.pixelHeight;
            if (!Fits(latest, w, h))
            {
                RenderTargets.Dispose(latest);
                RenderTargets.Dispose(spare);
                latest = Make("layer reconstruct a", w, h, FilterMode.Bilinear);
                spare = Make("layer reconstruct b", w, h, FilterMode.Bilinear);
                haveHistory = false;
                fullA = latest;
                targetsA = targetsB = null;
            }
            // recon.age: one sample count per full target, made with the first frame that asks for it. A count made anew holds no history: the frame runs as after a cut.
            bool ageOn = (s.Age || (s.TrueDistance && s.DistanceReject > 0f)) && s.Mode == ReconMode.Block && !(s.Edge != null && s.Edge.Enabled && edgeMaterial != null);
            // Turned on again after frames without it (the key, the edge mode, another size in between): the targets still fit but hold the counts and distances of the
            // picture they were last written for. The frame runs as after a cut, which writes them anew.
            if (ageOn && !ageWasOn) haveHistory = false;
            ageWasOn = ageOn;
            if (ageOn && (!Fits(ageA, w, h) || targetsA == null))
            {
                if (!Fits(ageA, w, h))
                {
                    RenderTargets.Dispose(ageA);
                    RenderTargets.Dispose(ageB);
                    ageA = MakeCount("layer reconstruct count a", w, h);
                    ageB = MakeCount("layer reconstruct count b", w, h);
                    haveHistory = false;
                }
                RenderTexture fullB = fullA == latest ? spare : latest;
                targetsA = new[] { new RenderTargetIdentifier(fullA.colorBuffer), new RenderTargetIdentifier(ageA.colorBuffer) };
                targetsB = new[] { new RenderTargetIdentifier(fullB.colorBuffer), new RenderTargetIdentifier(ageB.colorBuffer) };
            }
            RenderTexture history = latest;
            output = spare;
            bool edgeOn = s.Mode == ReconMode.Block && s.Edge != null && s.Edge.Enabled && edgeMaterial != null;
            int n = edgeOn ? EdgeSettings.CoarseBlock : (s.Mode == ReconMode.Block ? Mathf.Clamp(s.Block, 2, 4) : 1);
            LastMode = s.Mode;
            LastBlock = n;
            LastEdge = edgeOn;
            LastCountTarget = false;
            LastSceneDepth = false;
            LastRecordFrame = Time.frameCount;
            LastRenderWidth = w;
            LastRenderHeight = h;
            float scale = s.Mode == ReconMode.Low ? Mathf.Clamp(s.Resolution, 0.1f, 1f) : (s.Mode == ReconMode.Full ? 1f : 1f / n);
            int lw = s.Mode == ReconMode.Block ? (w + n - 1) / n : Mathf.RoundToInt(w * scale);
            int lh = s.Mode == ReconMode.Block ? (h + n - 1) / n : Mathf.RoundToInt(h * scale);
            if (!Fits(low, lw, lh))
            {
                RenderTargets.Dispose(low);
                low = Make("layer march", lw, lh, s.Mode == ReconMode.Block ? FilterMode.Bilinear : FilterMode.Point);
                marchTargets = null;
            }
            LastMarchWidth = low.width;
            LastMarchHeight = low.height;
            // recon.trueDistance: the second target of the march, one distance per new sample.
            bool distanceOn = s.TrueDistance && s.Mode == ReconMode.Block && !edgeOn;
            if (distanceOn && (!Fits(lowDistance, lw, lh) || marchTargets == null))
            {
                if (!Fits(lowDistance, lw, lh))
                {
                    RenderTargets.Dispose(lowDistance);
                    lowDistance = new RenderTexture(Mathf.Max(lw, 2), Mathf.Max(lh, 2), 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
                    {
                        name = "layer march distance",
                        filterMode = FilterMode.Point,
                        wrapMode = TextureWrapMode.Clamp
                    };
                    lowDistance.Create();
                }
                marchTargets = new[] { new RenderTargetIdentifier(low.colorBuffer), new RenderTargetIdentifier(lowDistance.colorBuffer) };
            }

            float tanY = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float tanX = tanY * camera.aspect;
            Transform t = camera.transform;
            Vector2 jitterNdc = Vector2.zero;
            if (s.Mode == ReconMode.Low && s.Jitter)
            {
                int phase = frameNumber % 16 + 1;
                jitterNdc = new Vector2((ReconPattern.Halton(phase, 2) - 0.5f) * 2f / low.width, (ReconPattern.Halton(phase, 3) - 0.5f) * 2f / low.height);
            }
            renderer.SetCamera(t.position, t.forward, t.right * tanX, t.up * tanY, jitterNdc);
            f.FrameNumber = frameNumber;
            f.Cull.PixelAngleRad = 2f * tanY / (s.Mode == ReconMode.Low ? low.height : h);   // T16: the pixel the detail LOD resolves
            renderer.Apply(f);
            Material layers = renderer.Material;
            // The light cache's box follows the camera (the body's radius is the renderer's after Apply); bound again with the box's new place.
            LightCache cache = f.Light.Cache != null && f.Light.Cache.Enabled ? f.Light.Cache : null;
            // The light volume replaces the cache while cloud.lightVolume is on (the march's keyword drops the cache's read). With the key off, at a hand-over distance of infinity
            // without the lateral spread (which reads it at every distance) or above the warp limit no sample reads it: the march keeps its no-keyword variant, nothing is filled, and the volume's textures are freed (the next use lays it anew).
            LightVolume volume = null;
            bool volumeOn = LightVolumeAllowed && CloudLookTuning.LightVolume && (CloudLookTuning.LvHandOverM >= 0f || CloudLookTuning.LvSpread > 0);
            if (volumeOn)
            {
                if (lightVolume == null) lightVolume = new LightVolume();
                lightVolume.Configure();
                volumeOn = lightVolume.WarpAllows;
            }
            if (volumeOn)
            {
                volume = lightVolume;
                volume.PixelAngleRad = f.Cull.PixelAngleRad;
                int sunSteps = f.Light.SunStepsOverride > 0 ? f.Light.SunStepsOverride : f.SunSteps;
                volume.Follow(t.position - f.PlanetCenter, (float)renderer.PlanetRadiusM, renderer.ShellAltitudes(), sunSteps, f.SunMarchM, f.Light.TapRatio);
                volume.Bind(layers);
                cache = null;
            }
            else
            {
                LightVolume.Unbind(layers);
                if (lightVolume != null) lightVolume.Dispose();
            }
            LightVolumeKeywordAtRecord = layers.IsKeywordEnabled(LightVolume.Keyword);
            if (cache != null)
            {
                cache.Follow(t.position - f.PlanetCenter, (float)renderer.PlanetRadiusM);
                cache.Bind(layers);
            }
            Vector2 offset = Vector2.zero;
            if (s.Mode == ReconMode.Block)
            {
                int ox, oy;
                ReconPattern.BlockOffset(n, blockIndex++, out ox, out oy);
                offset = new Vector2(ox, oy);
                layers.SetVector(IdReconP, new Vector4(n, offset.x, offset.y, 0f));
            }
            else
            {
                layers.SetVector(IdReconP, new Vector4(1f, 0f, 0f, 0f));
            }
            // The far rotation (Low mode with a history only): beyond FarRotateM a pixel is marched once a block of FarRotateBlock frames squared, at this frame's offset.
            // The distance of each pixel's cloud comes from the last frame (farDepth, the reconstruction's PassDepth): without it the frame marches every pixel.
            Vector4 farRot = Vector4.zero;
            bool farOn = s.Mode == ReconMode.Low && s.Accumulate > 0f && s.FarRotateM > 0f;
            if (farOn && haveFarDepth && Fits(farDepth, low.width, low.height))
            {
                int fb = Mathf.Clamp(s.FarRotateBlock, 2, 4), fx, fy;
                ReconPattern.BlockOffset(fb, blockIndex++, out fx, out fy);
                farRot = new Vector4(s.FarRotateM, fb, fx, fy);
                layers.SetTexture(IdFarDepth, farDepth);
            }
            layers.SetVector(IdFarRot, farRot);
            layers.SetVector(IdReconD, new Vector4(w, h, low.width, low.height));
            LastOffset = offset;
            layers.SetVector(IdEdgeP, new Vector4(0f, EdgeSettings.CoarseBlock, 0f, 0f));
            if (edgeOn)
            {
                // The edge draw is the layer material's copy with the edge pattern's numbers.
                if (edgeLayers == null || edgeLayers.shader != layers.shader) edgeLayers = new Material(layers.shader) { hideFlags = HideFlags.HideAndDontSave };
                edgeLayers.CopyPropertiesFromMaterial(layers);
            }

            // E40 MVCLOUD: whether the reconstruction material holds the frame's pose and the layers' places after the branch below (the paths that run its pass 0 set them).
            bool materialBound = false;
            lastPoseValid = havePose;
            if (markStart != null) markStart(cb);
            if (cache != null)
            {
                cache.Record(cb, layers);
                Sub(cb, "light cache");
                if (MarkCacheFilled != null) MarkCacheFilled(cb);
            }
            if (volume != null)
            {
                volume.Record(cb, layers, f.TowardsSun);
                volume.ApplyStamps(edgeOn ? edgeLayers : null);
                Sub(cb, "light volume");
                if (MarkCacheFilled != null) MarkCacheFilled(cb);
            }
            renderer.RecordCull(cb, f, w, h, MarkCull, MarkCullEnd);   // T16: the tile pass; nothing while no skip switch is on
            Sub(cb, "cull tiles");
            LastMarchPass = distanceOn ? PassMarchDistance : s.ProcMarch ? PassMarchTriangle : LayerRenderer.PassMarch;
            if (distanceOn)
            {
                cb.SetRenderTarget(marchTargets, low.depthBuffer);
                cb.DrawProcedural(Matrix4x4.identity, layers, PassMarchDistance, MeshTopology.Triangles, 3, 1);
            }
            else if (s.ProcMarch)
            {
                // Test switch (HIST2): pass 0 drawn as one procedural triangle into the one target (pass 7), to time the draw path against the Blit's quad.
                cb.SetRenderTarget(low);
                cb.DrawProcedural(Matrix4x4.identity, layers, PassMarchTriangle, MeshTopology.Triangles, 3, 1);
            }
            else
            {
                cb.Blit(BuiltinRenderTextureType.None, low, layers, LayerRenderer.PassMarch);
            }
            Sub(cb, "march");
            if (markMarched != null) markMarched(cb);
            if (s.Mode == ReconMode.Full)
            {
                cb.Blit(low, output);
                Sub(cb, "copy to full size");
            }
            else if (s.Mode == ReconMode.Low)
            {
                RenderTexture source = low;
                if (s.Accumulate > 0f)
                {
                    // The history of the reduced target (E40 grain): the reconstruction pass with a block of 1, every pixel fresh, at the march target's size.
                    if (!Fits(lowLatest, low.width, low.height))
                    {
                        RenderTargets.Dispose(lowLatest);
                        RenderTargets.Dispose(lowSpare);
                        lowLatest = Make("layer accumulate a", low.width, low.height, FilterMode.Bilinear);
                        lowSpare = Make("layer accumulate b", low.width, low.height, FilterMode.Bilinear);
                        haveLowHistory = false;
                    }
                    CloudLayerFrameInfo shell = renderer.FirstLayerShell();
                    shell.FallbackDistance = 50000f;
                    Material a = material;
                    a.SetTexture(IdCurTex, low);
                    a.SetVector(IdCurTexel, new Vector4(1f / low.width, 1f / low.height, low.width, low.height));
                    a.SetTexture(IdHistTex, lowLatest);
                    a.SetVector(IdReconP, new Vector4(1f, 0f, 0f, s.Debug));
                    a.SetVector(IdReconD, new Vector4(low.width, low.height, 0f, 0f));
                    a.SetVector(IdReconK, new Vector4(Mathf.Clamp01(s.Accumulate), Mathf.Clamp01(s.Accumulate), s.ClipSigma, s.CatmullHistory ? 1f : 0f));
                    a.SetVector(IdCamPos, t.position - f.PlanetCenter);
                    a.SetVector(IdCamForward, t.forward);
                    a.SetVector(IdCamRight, t.right * tanX);
                    a.SetVector(IdCamUp, t.up * tanY);
                    a.SetVector(IdPrevPos, previousPosition);
                    a.SetVector(IdPrevF, previousForward);
                    a.SetVector(IdPrevR, previousRight / previousTanX);
                    a.SetVector(IdPrevU, previousUp / previousTanY);
                    a.SetVector(IdShellR, new Vector4(shell.MiddleRadius, shell.FallbackDistance, 0f, 0f));
                    a.SetVector(IdDrift, new Vector4(s.DriftPerFrame.x, s.DriftPerFrame.y, s.DriftPerFrame.z, haveLowHistory ? 1f : 0f));
                    a.SetVector(IdReconT, Vector4.zero);
                    renderer.BindMotion(a, f, s.LayerMotion, havePose, s.DriftSeconds);
                    materialBound = true;
                    cb.Blit(BuiltinRenderTextureType.None, lowSpare, a, 0);
                    Sub(cb, "accumulate");
                    if (farOn)
                    {
                        if (!Fits(farDepth, low.width, low.height))
                        {
                            RenderTargets.Dispose(farDepth);
                            farDepth = new RenderTexture(low.width, low.height, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
                            {
                                name = "layer far depth",
                                filterMode = FilterMode.Point,
                                wrapMode = TextureWrapMode.Clamp
                            };
                            farDepth.Create();
                        }
                        cb.Blit(BuiltinRenderTextureType.None, farDepth, a, PassDepth);
                        Sub(cb, "far depth");
                        haveFarDepth = true;
                    }
                    else
                    {
                        haveFarDepth = false;
                    }
                    source = lowSpare;
                    lowSpare = lowLatest;
                    lowLatest = source;
                    haveLowHistory = true;
                }
                else
                {
                    haveLowHistory = false;
                }
                cb.SetGlobalVector(IdMarchTexel, new Vector4(1f / source.width, 1f / source.height, source.width, source.height));
                cb.SetGlobalTexture(IdMarchTex, source);
                cb.SetRenderTarget(output);
                cb.ClearRenderTarget(false, true, Color.clear);
                cb.Blit(source, output, layers, LayerRenderer.PassComposite);
                Sub(cb, "upsample to full size");
            }
            else if (edgeOn)
            {
                RecordEdge(cb, renderer, f, camera, s, w, h, offset, history, tanX, tanY);
                Sub(cb, "edge reconstruction");
                haveHistory = true;
            }
            else
            {
                Vector3 position = t.position - f.PlanetCenter;
                CloudLayerFrameInfo info = renderer.FirstLayerShell();
                info.FallbackDistance = 50000f;
                Material m = material;
                m.SetTexture(IdCurTex, low);
                m.SetVector(IdCurTexel, new Vector4(1f / low.width, 1f / low.height, low.width, low.height));
                m.SetTexture(IdHistTex, history);
                m.SetVector(IdReconP, new Vector4(n, offset.x, offset.y, s.Debug));
                m.SetVector(IdReconD, new Vector4(w, h, 0f, 0f));
                m.SetVector(Shader.PropertyToID("_ReconK"), new Vector4(s.FreshWeight, s.StaleWeight, s.ClipSigma, s.CatmullHistory ? 1f : 0f));
                m.SetVector(IdCamPos, position);
                m.SetVector(IdCamForward, t.forward);
                m.SetVector(IdCamRight, t.right * tanX);
                m.SetVector(IdCamUp, t.up * tanY);
                m.SetVector(IdPrevPos, previousPosition);
                m.SetVector(IdPrevF, previousForward);
                m.SetVector(IdPrevR, previousRight / previousTanX);
                m.SetVector(IdPrevU, previousUp / previousTanY);
                m.SetVector(IdShellR, new Vector4(info.MiddleRadius, info.FallbackDistance, 0f, 0f));
                m.SetVector(IdDrift, new Vector4(s.DriftPerFrame.x, s.DriftPerFrame.y, s.DriftPerFrame.z, haveHistory ? 1f : 0f));
                m.SetVector(IdReconT, new Vector4(distanceOn ? 1f : 0f, ageOn && s.Age ? 1f : 0f, ageOn && distanceOn ? Mathf.Max(0f, s.DistanceReject) : 0f,
                                                  s.SceneDepth && f.UseSceneDepth ? 1f : 0f));
                LastSceneDepth = s.SceneDepth && f.UseSceneDepth;
                if (distanceOn) m.SetTexture(IdDistTex, lowDistance);
                renderer.BindMotion(m, f, s.LayerMotion, havePose, s.DriftSeconds);
                materialBound = true;
                if (ageOn)
                {
                    // The history's count is the one of the full target that is the history; the frame's goes with the output.
                    bool outputIsA = output == fullA;
                    m.SetTexture(IdAgeTex, outputIsA ? ageB : ageA);
                    cb.SetRenderTarget(outputIsA ? targetsA : targetsB, output.depthBuffer);
                    cb.DrawProcedural(Matrix4x4.identity, m, PassReconstructAge, MeshTopology.Triangles, 3, 1);
                    LastCountTarget = true;
                }
                else
                cb.Blit(BuiltinRenderTextureType.None, output, m, 0);
                Sub(cb, "block reconstruction");
                haveHistory = true;
            }
            // The paths that do not run pass 0 of this material still give the motion pass (RecordMotion) the camera and the layers.
            if (!materialBound) BindPose(material, renderer, f, t, tanX, tanY, s);
            // The finished target is the next frame's history.
            spare = latest;
            latest = output;
            if (markEnd != null) markEnd(cb);
            lastCurrent = PoseOf(t.position - f.PlanetCenter, t.forward, t.right, t.up, tanX, tanY);
            lastPrevious = PoseOf(previousPosition, previousForward, previousRight, previousUp, previousTanX, previousTanY);
            havePose = true;
            previousPosition = t.position - f.PlanetCenter;
            previousForward = t.forward;
            previousRight = t.right;
            previousUp = t.up;
            previousTanX = tanX;
            previousTanY = tanY;
        }

        // E40 MVCLOUD: the cameras of the last Record in the planet frame (the current one and the frame before's) for the audit of the motion vectors; false where the last Record had no
        // frame before it.
        internal bool LastPoses(out ReconCamera current, out ReconCamera previous)
        {
            current = lastCurrent;
            previous = lastPrevious;
            return lastPoseValid;
        }

        private ReconCamera lastCurrent, lastPrevious;

        private static ReconCamera PoseOf(Vector3 position, Vector3 forward, Vector3 right, Vector3 up, float tanX, float tanY)
        {
            return new ReconCamera
            {
                Px = position.x, Py = position.y, Pz = position.z, Fx = forward.x, Fy = forward.y, Fz = forward.z,
                Rx = right.x, Ry = right.y, Rz = right.z, Ux = up.x, Uy = up.y, Uz = up.z, TanX = tanX, TanY = tanY
            };
        }

        // The camera, the previous camera and the layers' places in the reconstruction material, for a path that does not run its pass 0 (the motion pass reads them).
        private void BindPose(Material mat, LayerRenderer renderer, LayerFrame f, Transform t, float tanX, float tanY, ReconSettings s)
        {
            CloudLayerFrameInfo shell = renderer.FirstLayerShell();
            shell.FallbackDistance = 50000f;
            mat.SetVector(IdCamPos, t.position - f.PlanetCenter);
            mat.SetVector(IdCamForward, t.forward);
            mat.SetVector(IdCamRight, t.right * tanX);
            mat.SetVector(IdCamUp, t.up * tanY);
            mat.SetVector(IdPrevPos, previousPosition);
            mat.SetVector(IdPrevF, previousForward);
            mat.SetVector(IdPrevR, previousRight / previousTanX);
            mat.SetVector(IdPrevU, previousUp / previousTanY);
            mat.SetVector(IdShellR, new Vector4(shell.MiddleRadius, shell.FallbackDistance, 0f, 0f));
            mat.SetVector(IdDrift, new Vector4(s.DriftPerFrame.x, s.DriftPerFrame.y, s.DriftPerFrame.z, havePose ? 1f : 0f));
            renderer.BindMotion(mat, f, s.LayerMotion, havePose, s.DriftSeconds);
        }

        // E40 MVCLOUD: records the cloud's motion vectors of the last Record over the host's motion vectors (pass 1 of the reconstruction shader). Each pixel the cloud covers gets the
        // vector of its place in the frame before, blended by the cloud's alpha where the cloud is in front of the scene depth (a copy of the scene's raw depth at the render size);
        // the clear sky and the geometry in front keep what the host's vectors hold. False where nothing is recorded: no frame of the pass before it, or no full target.
        internal bool RecordMotion(CommandBuffer cb, RenderTexture motionVectors, RenderTexture sceneDepth)
        {
            if (!Ready || output == null || !lastPoseValid || motionVectors == null || sceneDepth == null) return false;
            material.SetTexture(IdMvCloud, output);
            material.SetTexture(IdMvDepth, sceneDepth);
            cb.Blit(BuiltinRenderTextureType.None, motionVectors, material, PassMotion);
            Sub(cb, "motion vectors");
            return true;
        }
    }

    // The sphere the reprojection places a pixel's cloud on: the middle of the first layer, and a distance for rays that miss it.
    internal struct CloudLayerFrameInfo
    {
        public float MiddleRadius, FallbackDistance;
    }
}
