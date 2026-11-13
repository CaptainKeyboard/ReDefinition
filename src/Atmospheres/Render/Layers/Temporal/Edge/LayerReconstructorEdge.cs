using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // The edge-adaptive part of the reconstruction (package T12, the plan's T05b): after the coarse march of one ray per 4 x 4 block it builds the
    // edge mask (ReDefinitionAtmospheresEdge.shader passes 0 to 2), marches a 2 x 2 pattern of rays in the flagged blocks (the layer shader's edge draw,
    // MarchStep.cginc) and reconstructs the full target from both patterns and the reprojected history (pass 3).
    internal sealed partial class LayerReconstructor
    {
        // The pixels the picture moved since the last frame, for the mask's widening: the camera's turn at the pixel scale, the camera's own motion
        // and the cloud's drift seen from the distance of the first layer's middle (a standing camera and calm air give zero).
        private float MotionPixels(Vector3 position, Vector3 forward, float tanY, int height, ReconSettings s, CloudLayerFrameInfo shell)
        {
            if (!haveHistory) return 0f;
            float pixelsPerRadian = height / (2f * tanY);
            float turn = Mathf.Acos(Mathf.Clamp(Vector3.Dot(previousForward, forward), -1f, 1f)) * pixelsPerRadian;
            float distance = Mathf.Max(shell.MiddleRadius - position.magnitude, 500f);
            float move = ((position - previousPosition).magnitude + s.DriftPerFrame.magnitude) / distance * pixelsPerRadian;
            return turn + move;
        }

        private void RecordEdge(CommandBuffer cb, LayerRenderer renderer, LayerFrame f, Camera camera, ReconSettings s, int w, int h, Vector2 coarseOffset,
                                RenderTexture history, float tanX, float tanY)
        {
            Transform t = camera.transform;
            EdgeSettings edge = s.Edge;
            int bw = EdgeMath.Blocks(w), bh = EdgeMath.Blocks(h);
            int ew = (w + EdgeSettings.EdgeBlock - 1) / EdgeSettings.EdgeBlock, eh = (h + EdgeSettings.EdgeBlock - 1) / EdgeSettings.EdgeBlock;
            if (!Fits(mask, bw, bh))
            {
                foreach (RenderTexture rt in new[] { stateLast, stateNew, dilated, mask })
                    RenderTargets.Dispose(rt);
                stateLast = MakeMask("edge state a", bw, bh);
                stateNew = MakeMask("edge state b", bw, bh);
                dilated = MakeMask("edge dilated", bw, bh);
                mask = MakeMask("edge mask", bw, bh);
                haveEdgeState = false;
            }
            if (!Fits(edgeLow, ew, eh))
            {
                RenderTargets.Dispose(edgeLow);
                edgeLow = Make("layer march edge", ew, eh, FilterMode.Point);
            }

            Vector3 position = t.position - f.PlanetCenter;
            CloudLayerFrameInfo shell = renderer.FirstLayerShell();
            shell.FallbackDistance = 50000f;
            int dilation = EdgeMath.DilationFor(edge.Dilation, MotionPixels(position, t.forward, tanY, h, s, shell));
            LastDilation = dilation;
            bool stateValid = haveEdgeState && haveHistory;

            Material m = edgeMaterial;
            m.SetVector(IdReconD, new Vector4(w, h, 0f, 0f));
            m.SetTexture(IdCurTex, low);
            m.SetVector(IdCurTexel, new Vector4(1f / low.width, 1f / low.height, low.width, low.height));
            m.SetTexture(IdHistTex, history);
            m.SetVector(IdEdgeK, new Vector4(edge.Threshold, dilation, edge.Persist, stateValid ? 1f : 0f));
            m.SetVector(IdEdgeD, new Vector4(bw, bh, 0f, 0f));
            int idState = Shader.PropertyToID("_StateTex");
            cb.SetGlobalTexture(idState, stateLast);
            cb.Blit(BuiltinRenderTextureType.None, stateNew, m, 0);
            cb.SetGlobalTexture(idState, stateNew);
            cb.Blit(BuiltinRenderTextureType.None, dilated, m, 1);
            cb.SetGlobalTexture(idState, dilated);
            cb.Blit(BuiltinRenderTextureType.None, mask, m, 2);
            cb.SetGlobalTexture(IdEdgeMask, mask);
            if (MarkMask != null) MarkMask(cb);

            // The edge draw: the 2 x 2 pattern, only in the flagged blocks; it keeps the entry refinement the frame asks for.
            int ox, oy;
            ReconPattern.BlockOffset(EdgeSettings.EdgeBlock, edgeIndex++, out ox, out oy);
            edgeLayers.SetVector(IdReconP, new Vector4(EdgeSettings.EdgeBlock, ox, oy, 0f));
            edgeLayers.SetVector(IdReconD, new Vector4(w, h, edgeLow.width, edgeLow.height));
            edgeLayers.SetVector(IdEdgeP, new Vector4(1f, EdgeSettings.CoarseBlock, 0f, 0f));
            cb.Blit(BuiltinRenderTextureType.None, edgeLow, edgeLayers, LayerRenderer.PassMarch);
            if (MarkEdge != null) MarkEdge(cb);

            // The reconstruction.
            m.SetTexture(IdEdgeTex, edgeLow);
            m.SetVector(IdEdgeTexel, new Vector4(1f / edgeLow.width, 1f / edgeLow.height, edgeLow.width, edgeLow.height));
            m.SetVector(IdReconP, new Vector4(EdgeSettings.CoarseBlock, coarseOffset.x, coarseOffset.y, s.Debug));
            m.SetVector(IdReconE, new Vector4(ox, oy, 0f, 0f));
            m.SetVector(IdReconK, new Vector4(s.FreshWeight, s.StaleWeight, s.ClipSigma, 0f));
            m.SetVector(IdCamPos, position);
            m.SetVector(IdCamForward, t.forward);
            m.SetVector(IdCamRight, t.right * tanX);
            m.SetVector(IdCamUp, t.up * tanY);
            m.SetVector(IdPrevPos, previousPosition);
            m.SetVector(IdPrevF, previousForward);
            m.SetVector(IdPrevR, previousRight / previousTanX);
            m.SetVector(IdPrevU, previousUp / previousTanY);
            m.SetVector(IdShellR, new Vector4(shell.MiddleRadius, shell.FallbackDistance, 0f, 0f));
            m.SetVector(IdDrift, new Vector4(s.DriftPerFrame.x, s.DriftPerFrame.y, s.DriftPerFrame.z, haveHistory ? 1f : 0f));
            cb.Blit(BuiltinRenderTextureType.None, output, m, 3);

            // The state of this frame is the next frame's last.
            RenderTexture swap = stateLast;
            stateLast = stateNew;
            stateNew = swap;
            haveEdgeState = true;
        }
    }
}
