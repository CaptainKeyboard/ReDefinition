using System;
using System.Globalization;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // An instrument (E40 MVCLOUD, cloud.mvAudit): whether the motion vectors every upscaler and frame generation read are right on the pixels the cloud pass covers. Every fifth frame,
    // in the host's motion vector capture after the cloud's vectors were drawn (or would have been: cloud.motionVectors 0 leaves the host's own), it reads back the vector, the
    // scene depth and the cloud's alpha at a few pixels of the sky; a pixel counts where the alpha is at least 0.95 (the vector there is the cloud's up to a twentieth of the host's) and nothing stands in front of it. The expected vector of such a pixel is the
    // model's (LayerMotionModel) for the layer the read vector fits best: the cloud of a pixel is on one layer, or on a blend of two, and the audit does not know the share (the
    // coverage maps are on the GPU), so the error is the distance to the nearest layer's vector, a lower bound for a blended pixel and exact for a pixel of one layer.
    // The pass criterion of the package: below 0.3 px at 300 m/s with the vectors drawn; with cloud.motionVectors 0 the number reproduces what the host's sky vector leaves
    // (0.8 px at 5 km and 300 m/s, 6.7 px at 2400 m/s, the table of E40-upscaler-fg-check.md).
    internal sealed class CloudMotionAudit
    {
        private const int FrameSpacing = 5;
        private const float ReportSeconds = 10f;
        private const float MinAlpha = 0.95f;

        // The sampled pixels in viewport coordinates: the upper part of the picture, where the sky is.
        private static readonly Vector2[] Points =
        {
            new Vector2(0.2f, 0.6f), new Vector2(0.5f, 0.6f), new Vector2(0.8f, 0.6f),
            new Vector2(0.2f, 0.75f), new Vector2(0.5f, 0.75f), new Vector2(0.8f, 0.75f),
            new Vector2(0.2f, 0.9f), new Vector2(0.5f, 0.9f), new Vector2(0.8f, 0.9f),
        };

        private sealed class Slot
        {
            internal Vector2 Viewport;
            internal Vector2Int Size;
            internal ReconCamera Current, Previous;
            internal readonly MotionLayer[] Layers = new MotionLayer[LayerMotionModel.MaxLayers];
            internal int LayerCount;
            internal double PlanetRadius;
            internal Vector2 Read = new Vector2(float.NaN, float.NaN);
            internal float Depth = float.NaN, Alpha = float.NaN;
            internal bool Failed;
            internal int Pending;
            internal Action<AsyncGPUReadbackRequest> DepthRead, MotionRead, CloudRead;
        }

        private readonly Slot[] slots = new Slot[Points.Length];
        private readonly MotionLayer[] single = new MotionLayer[1];
        private int count, notCloud, notSky, failed, frames, drawnFrames;
        private double errorSum, motionSum;
        private float errorMax;
        private string worst;
        private float windowStart = -1f;
        private int frameCounter;

        internal CloudMotionAudit()
        {
            for (int i = 0; i < slots.Length; i++) slots[i] = MakeSlot();
        }

        private Slot MakeSlot()
        {
            var slot = new Slot();
            slot.DepthRead = request =>
            {
                if (slot.Pending <= 0) return;
                if (request.hasError) slot.Failed = true;
                else slot.Depth = request.GetData<float>()[0];
                Back(slot);
            };
            slot.MotionRead = request =>
            {
                if (slot.Pending <= 0) return;
                if (request.hasError)
                {
                    slot.Failed = true;
                }
                else
                {
                    NativeArray<ushort> data = request.GetData<ushort>();
                    slot.Read = new Vector2(Mathf.HalfToFloat(data[0]), Mathf.HalfToFloat(data[1]));
                }
                Back(slot);
            };
            slot.CloudRead = request =>
            {
                if (slot.Pending <= 0) return;
                if (request.hasError)
                {
                    slot.Failed = true;
                }
                else
                {
                    NativeArray<ushort> data = request.GetData<ushort>();
                    slot.Alpha = Mathf.HalfToFloat(data[3]);
                }
                Back(slot);
            };
            return slot;
        }

        // Called once a frame of the pass with the frame's state; every fifth frame it asks for the readbacks. cloud is the pass's full target of this frame.
        internal void Frame(CommandBuffer cb, RenderTexture vectors, RenderTexture depth, RenderTexture cloud, LayerReconstructor reconstructor, LayerRenderer renderer, float driftSeconds,
                            bool vectorsDrawn)
        {
            if (windowStart < 0f) windowStart = Time.unscaledTime;
            frames++;
            if (vectorsDrawn) drawnFrames++;
            ReconCamera current, previous;
            if (cb != null && vectors != null && depth != null && cloud != null && ++frameCounter % FrameSpacing == 0 && reconstructor.LastPoses(out current, out previous)
                && vectors.width == cloud.width && vectors.height == cloud.height && depth.width == cloud.width)
            {
                for (int i = 0; i < slots.Length; i++)
                {
                    Slot slot = slots[i];
                    if (slot.Pending > 0) continue;
                    int x = Mathf.Clamp(Mathf.FloorToInt(Points[i].x * cloud.width), 0, cloud.width - 1);
                    int y = Mathf.Clamp(Mathf.FloorToInt(Points[i].y * cloud.height), 0, cloud.height - 1);
                    slot.Viewport = new Vector2((x + 0.5f) / cloud.width, (y + 0.5f) / cloud.height);
                    slot.Size = new Vector2Int(cloud.width, cloud.height);
                    slot.Current = current;
                    slot.Previous = previous;
                    slot.LayerCount = renderer.FillMotionLayers(slot.Layers, driftSeconds);
                    slot.PlanetRadius = renderer.PlanetRadiusM;
                    slot.Read = new Vector2(float.NaN, float.NaN);
                    slot.Depth = slot.Alpha = float.NaN;
                    slot.Failed = false;
                    slot.Pending = 3;
                    cb.RequestAsyncReadback(depth, 0, x, 1, y, 1, 0, 1, slot.DepthRead);
                    cb.RequestAsyncReadback(vectors, 0, x, 1, y, 1, 0, 1, slot.MotionRead);
                    cb.RequestAsyncReadback(cloud, 0, x, 1, y, 1, 0, 1, slot.CloudRead);
                }
            }
            if (Time.unscaledTime - windowStart >= ReportSeconds) Report();
        }

        private void Back(Slot slot)
        {
            if (--slot.Pending != 0) return;
            Finish(slot);
        }

        private void Finish(Slot slot)
        {
            if (slot.Failed || float.IsNaN(slot.Depth) || float.IsNaN(slot.Read.x) || float.IsNaN(slot.Alpha))
            {
                failed++;
                return;
            }
            // The vector over a pixel is the cloud's blended into the host's by the cloud's alpha; at an alpha of at least MinAlpha the host's share (the sky's rotation-only vector, whose
            // difference to the cloud's is at most a few px) is at most a twentieth of it.
            if (slot.Alpha < MinAlpha)
            {
                notCloud++;
                return;
            }
            // The scene's depth: reversed Z (Direct3D 11) holds 0 at the far plane, the other way 1.
            bool sky = SystemInfo.usesReversedZBuffer ? slot.Depth <= 0f : slot.Depth >= 1f;
            if (!sky)
            {
                notSky++;
                return;
            }
            Vector2 pixels = new Vector2(slot.Size.x, slot.Size.y);
            double best = double.MaxValue, bestX = 0, bestY = 0;
            for (int l = 0; l < slot.LayerCount; l++)
            {
                single[0] = slot.Layers[l];
                double pu, pv, depth;
                if (!LayerMotionModel.Previous(slot.Current, slot.Previous, slot.Viewport.x, slot.Viewport.y, single, 1, slot.PlanetRadius, out pu, out pv, out depth)) continue;
                double ex = (slot.Viewport.x - pu) * pixels.x, ey = (slot.Viewport.y - pv) * pixels.y;
                double dx = slot.Read.x * pixels.x - ex, dy = slot.Read.y * pixels.y - ey;
                double error = Math.Sqrt(dx * dx + dy * dy);
                if (error < best)
                {
                    best = error;
                    bestX = ex;
                    bestY = ey;
                }
            }
            if (best == double.MaxValue)
            {
                notCloud++;
                return;
            }
            count++;
            errorSum += best;
            motionSum += Math.Sqrt(bestX * bestX + bestY * bestY);
            if (best >= errorMax)
            {
                errorMax = (float)best;
                worst = "read (" + (slot.Read.x * pixels.x).ToString("0.00", CultureInfo.InvariantCulture) + ", " + (slot.Read.y * pixels.y).ToString("0.00", CultureInfo.InvariantCulture)
                        + ") expected (" + bestX.ToString("0.00", CultureInfo.InvariantCulture) + ", " + bestY.ToString("0.00", CultureInfo.InvariantCulture) + ") px";
            }
        }

        private void Report()
        {
            float elapsed = Time.unscaledTime - windowStart;
            windowStart = Time.unscaledTime;
            var sb = new StringBuilder();
            sb.Append("own clouds: motion vectors against the layers' model, last ").Append(elapsed.ToString("0.0", CultureInfo.InvariantCulture)).Append(" s: ").Append(count).Append(" cloud pixel(s)");
            if (count > 0)
            {
                sb.Append(", cloud px error mean ").Append((errorSum / count).ToString("0.00", CultureInfo.InvariantCulture)).Append(" px, max ")
                  .Append(errorMax.ToString("0.00", CultureInfo.InvariantCulture)).Append(" px, motion mean ").Append((motionSum / count).ToString("0.00", CultureInfo.InvariantCulture))
                  .Append(" px, worst: ").Append(worst);
            }
            sb.Append("; ").Append(notCloud).Append(" without cloud, ").Append(notSky).Append(" in front of geometry, ").Append(failed).Append(" failed reads; the cloud's vectors drawn in ")
              .Append(drawnFrames).Append(" of ").Append(frames).Append(" frames");
            AtmospheresLog.Say(sb.ToString());
            count = notCloud = notSky = failed = frames = drawnFrames = 0;
            errorSum = motionSum = 0.0;
            errorMax = 0f;
            worst = null;
        }
    }
}
