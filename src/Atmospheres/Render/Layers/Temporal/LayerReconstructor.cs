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
        // Block: the weight of the new sample in a pixel that has one this frame, in a pixel that has none (its value is then the bilinear
        // upsample of the new samples), and the width of the clip box in standard deviations of the 3 x 3 new samples.
        // The weights are the sweep's pick (T05, tools/weather_metrics/temporal.py T05-temporal): 0.05 for the pixels without a new sample
        // gives the lowest shimmer at the 4 x 4 block with no more ghosting than 0.1 on the turns.
        public float FreshWeight = 0.5f, StaleWeight = 0.05f, ClipSigma = 1.0f;
        // The cloud's drift in the planet frame per frame (m): the wind that moves the pattern.
        public Vector3 DriftPerFrame;
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

        private static readonly int IdReconP = Shader.PropertyToID("_ReconP"), IdReconD = Shader.PropertyToID("_ReconD"),
            IdReconK = Shader.PropertyToID("_ReconK"), IdCurTex = Shader.PropertyToID("_CurTex"), IdCurTexel = Shader.PropertyToID("_CurTex_TexelSize"),
            IdHistTex = Shader.PropertyToID("_HistTex"), IdCamPos = Shader.PropertyToID("_CamPos"), IdCamForward = Shader.PropertyToID("_CamForward"),
            IdCamRight = Shader.PropertyToID("_CamRight"), IdCamUp = Shader.PropertyToID("_CamUp"), IdPrevPos = Shader.PropertyToID("_PrevPos"),
            IdPrevF = Shader.PropertyToID("_PrevF"), IdPrevR = Shader.PropertyToID("_PrevR"), IdPrevU = Shader.PropertyToID("_PrevU"),
            IdShellR = Shader.PropertyToID("_ShellR"), IdDrift = Shader.PropertyToID("_Drift"), IdMarchTex = Shader.PropertyToID("_MarchTex"),
            IdSceneDepth = Shader.PropertyToID("_SceneDepth"), IdMarchTexel = Shader.PropertyToID("_MarchTex_TexelSize"),
            IdRefineP = Shader.PropertyToID("_RefineP"), IdEdgeP = Shader.PropertyToID("_EdgeP"), IdEdgeMask = Shader.PropertyToID("_EdgeMask"),
            IdEdgeTex = Shader.PropertyToID("_EdgeTex"), IdEdgeTexel = Shader.PropertyToID("_EdgeTex_TexelSize"), IdEdgeK = Shader.PropertyToID("_EdgeK"),
            IdEdgeD = Shader.PropertyToID("_EdgeD"), IdReconE = Shader.PropertyToID("_ReconE");
        internal const string EdgeShaderName = "ReDefinition/AtmospheresEdge";

        private Material material;
        // Package T12: the edge shader's material, the layer material's copy for the edge draw, the edge pattern's samples, the mask's state (last
        // frame's and this frame's), the dilation's middle step and the final mask, and the counter of the edge pattern.
        private Material edgeMaterial, edgeLayers;
        private RenderTexture edgeLow, stateLast, stateNew, dilated, mask;
        private bool haveEdgeState;
        private int edgeIndex;
        // The march target and the two full targets: the last finished one is the history, the other receives the frame.
        private RenderTexture low, latest, spare, output;
        private Vector3 previousPosition, previousForward, previousRight, previousUp;
        private float previousTanX = 1f, previousTanY = 1f;
        private bool haveHistory;
        private int blockIndex;

        internal bool Ready { get { return material != null; } }

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
            haveEdgeState = false;
            blockIndex = 0;
            edgeIndex = 0;
        }

        internal void Dispose()
        {
            if (low != null) { low.Release(); UnityEngine.Object.Destroy(low); }
            if (latest != null) { latest.Release(); UnityEngine.Object.Destroy(latest); }
            if (spare != null) { spare.Release(); UnityEngine.Object.Destroy(spare); }
            foreach (RenderTexture rt in new[] { edgeLow, stateLast, stateNew, dilated, mask })
                if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
            if (material != null) UnityEngine.Object.Destroy(material);
            if (edgeMaterial != null) UnityEngine.Object.Destroy(edgeMaterial);
            if (edgeLayers != null) UnityEngine.Object.Destroy(edgeLayers);
            material = edgeMaterial = edgeLayers = null;
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
            cb.Blit(output, destination, layers, LayerRenderer.PassComposite);
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
                if (latest != null) latest.Release();
                if (spare != null) spare.Release();
                latest = Make("layer reconstruct a", w, h, FilterMode.Bilinear);
                spare = Make("layer reconstruct b", w, h, FilterMode.Bilinear);
                haveHistory = false;
            }
            RenderTexture history = latest;
            output = spare;
            bool edgeOn = s.Mode == ReconMode.Block && s.Edge != null && s.Edge.Enabled && edgeMaterial != null;
            int n = edgeOn ? EdgeSettings.CoarseBlock : (s.Mode == ReconMode.Block ? Mathf.Clamp(s.Block, 2, 4) : 1);
            float scale = s.Mode == ReconMode.Low ? Mathf.Clamp(s.Resolution, 0.1f, 1f) : (s.Mode == ReconMode.Full ? 1f : 1f / n);
            int lw = s.Mode == ReconMode.Block ? (w + n - 1) / n : Mathf.RoundToInt(w * scale);
            int lh = s.Mode == ReconMode.Block ? (h + n - 1) / n : Mathf.RoundToInt(h * scale);
            if (!Fits(low, lw, lh))
            {
                if (low != null) low.Release();
                low = Make("layer march", lw, lh, s.Mode == ReconMode.Block ? FilterMode.Bilinear : FilterMode.Point);
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
            renderer.Apply(f);
            Material layers = renderer.Material;
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
            layers.SetVector(IdReconD, new Vector4(w, h, low.width, low.height));
            LastOffset = offset;
            layers.SetVector(IdEdgeP, new Vector4(0f, EdgeSettings.CoarseBlock, 0f, 0f));
            if (edgeOn)
            {
                // The edge draw is the layer material's copy with the edge pattern's numbers; the coarse draw runs without the entry refinement
                // when the step band is on (the refinement is paid in the flagged blocks only).
                if (edgeLayers == null || edgeLayers.shader != layers.shader) edgeLayers = new Material(layers.shader) { hideFlags = HideFlags.HideAndDontSave };
                edgeLayers.CopyPropertiesFromMaterial(layers);
                if (s.Edge.StepBand)
                {
                    Vector4 refine = layers.GetVector(IdRefineP);
                    layers.SetVector(IdRefineP, new Vector4(0f, refine.y, refine.z, refine.w));
                }
            }

            if (markStart != null) markStart(cb);
            cb.Blit(BuiltinRenderTextureType.None, low, layers, LayerRenderer.PassMarch);
            if (markMarched != null) markMarched(cb);
            if (s.Mode == ReconMode.Full)
            {
                cb.Blit(low, output);
            }
            else if (s.Mode == ReconMode.Low)
            {
                cb.SetGlobalVector(IdMarchTexel, new Vector4(1f / low.width, 1f / low.height, low.width, low.height));
                cb.SetGlobalTexture(IdMarchTex, low);
                cb.SetRenderTarget(output);
                cb.ClearRenderTarget(false, true, Color.clear);
                cb.Blit(low, output, layers, LayerRenderer.PassComposite);
            }
            else if (edgeOn)
            {
                RecordEdge(cb, renderer, f, camera, s, w, h, offset, history, tanX, tanY);
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
                m.SetVector(Shader.PropertyToID("_ReconK"), new Vector4(s.FreshWeight, s.StaleWeight, s.ClipSigma, 0f));
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
                cb.Blit(BuiltinRenderTextureType.None, output, m, 0);
                haveHistory = true;
            }
            // The finished target is the next frame's history.
            spare = latest;
            latest = output;
            if (markEnd != null) markEnd(cb);
            previousPosition = t.position - f.PlanetCenter;
            previousForward = t.forward;
            previousRight = t.right;
            previousUp = t.up;
            previousTanX = tanX;
            previousTanY = tanY;
        }
    }

    // The sphere the reprojection places a pixel's cloud on: the middle of the first layer, and a distance for rays that miss it.
    internal struct CloudLayerFrameInfo
    {
        public float MiddleRadius, FallbackDistance;
    }
}
