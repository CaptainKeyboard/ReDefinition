using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ReDefinition.Core;
using ReDefinition.Shared;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Upscaler
{
    // An instrument: the active vessel's motion vectors checked on every pixel it
    // covers, every frame in flight while the rig runs, logged about every ten seconds.
    //
    // At the end of the capture each of the vessel's mesh renderers is drawn once more
    // with Hidden/ReDefinition/MotionAudit, rasterised with the scene camera's jittered
    // projection so it covers the pixels it covered in the image. Where its depth is the
    // captured depth -- the surface the camera saw -- the shader computes the motion
    // vector Unity writes for it, this frame's position against the previous frame's
    // through the renderer's previous matrix and the previous view-projection, and
    // compares it with the captured one. Per part a buffer counts the pixels, those off
    // by more than a pixel, the sum and the largest error; it is read back each frame.
    // Skinned renderers are left out, since their previous pose is not at hand; so are
    // materials above queue 2500, which write no motion vectors. Frames with an origin
    // shift or a history reset since the frame before are left out, since there the
    // previous matrices do not describe the motion.
    //
    // The line counts the frames with more than half a percent of the vessel off, apart
    // for frames with and without a physics step since the frame before: KSP moves the
    // vessel in FixedUpdate, so a frame without one sees the vessel where it was. It
    // names the parts most off, with their largest error.
    internal sealed class VesselMotionAudit
    {
        private const int MaxParts = 256;
        private const float BadPixels = 1f;
        private const float BadFrameShare = 0.005f;
        private const float ReportSeconds = 10f;
        private const int PartsListed = 5;

        private static readonly int RasterId = Shader.PropertyToID("_AuditRasterViewProjection");
        private static readonly int ViewProjectionId = Shader.PropertyToID("_AuditViewProjection");
        private static readonly int PreviousViewProjectionId = Shader.PropertyToID("_AuditPreviousViewProjection");
        private static readonly int PreviousModelId = Shader.PropertyToID("_AuditPreviousModel");
        private static readonly int PartId = Shader.PropertyToID("_AuditPart");
        private static readonly int BadPixelsId = Shader.PropertyToID("_AuditBadPixels");
        private static readonly int TexelId = Shader.PropertyToID("_AuditTexel");
        private static readonly int MotionId = Shader.PropertyToID("_AuditMotion");
        private static readonly int DepthId = Shader.PropertyToID("_AuditDepth");

        private struct Drawn
        {
            internal Renderer Renderer;
            internal int Part;
            internal int Submeshes;
        }

        private sealed class PartTally
        {
            internal long Pixels;
            internal long Bad;
            internal uint LargestSixteenths;
        }

        private readonly List<Drawn> drawn = new List<Drawn>();
        private readonly List<Part> parts = new List<Part>();
        private readonly Dictionary<Renderer, Matrix4x4> previousModel = new Dictionary<Renderer, Matrix4x4>();
        private readonly PartTally[] tallies = new PartTally[MaxParts];
        private readonly uint[] zeros = new uint[MaxParts * 4];
        private Vessel drawnFor;
        private int partCount;
        private ComputeBuffer stats;
        private Material material;
        private RenderTexture target;
        private string state;
        private Matrix4x4 previousViewProjection;
        private int previousFrame = -10;
        private bool shifted;
        private bool subscribed;
        private float windowStart = -1f;

        private static int physicsSteps;
        private int stepsAtPreviousFrame;

        private int framesRead;
        private int framesWithStep;
        private int framesWithoutStep;
        private int badFramesWithStep;
        private int badFramesWithoutStep;
        private long pixelsRead;
        private long badRead;
        private int skipped;

        // From the rig's FixedUpdate.
        internal static void CountPhysicsStep()
        {
            physicsSteps++;
        }

        internal void Enable()
        {
            if (subscribed) return;
            GameEvents.onFloatingOriginShift.Add(OnShift);
            subscribed = true;
        }

        internal void Disable()
        {
            if (subscribed)
            {
                GameEvents.onFloatingOriginShift.Remove(OnShift);
                subscribed = false;
            }
            if (stats != null) stats.Release();
            stats = null;
            UpscalerRig.Release(ref target);
            if (material != null) Object.Destroy(material);
            material = null;
        }

        private void OnShift(Vector3d offset, Vector3d nonFrame)
        {
            shifted = true;
        }

        // In the scene camera's OnPreCull, after LateUpdate, where KSP has placed the
        // vessel for this frame; the commands go at the end of the capture.
        internal void Record(CommandBuffer capture, Camera camera, RenderTexture motionVectors, RenderTexture depth,
                             Vector2Int size)
        {
            if (camera == null || motionVectors == null || depth == null || !HighLogic.LoadedSceneIsFlight) return;
            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null || !Prepare(size)) return;
            if (windowStart < 0f) windowStart = Time.unscaledTime;

            Matrix4x4 projection = camera.projectionMatrix;
            Matrix4x4 viewProjection = GL.GetGPUProjectionMatrix(projection, false) * camera.worldToCameraMatrix;
            Matrix4x4 jittered = CameraRedirect.JitterActive
                ? Matrix4x4.Translate(new Vector3(CameraRedirect.JitterNdc.x, CameraRedirect.JitterNdc.y, 0f)) * projection
                : projection;
            Matrix4x4 raster = GL.GetGPUProjectionMatrix(jittered, true) * camera.worldToCameraMatrix;

            if (active != drawnFor || active.parts.Count != partCount) Choose(active);

            int steps = physicsSteps - stepsAtPreviousFrame;
            stepsAtPreviousFrame = physicsSteps;
            bool continuous = previousFrame == Time.frameCount - 1 && !shifted && SharedFrame.RigResetReason == null;
            shifted = false;

            if (continuous)
            {
                stats.SetData(zeros);
                capture.SetRenderTarget(target);
                capture.SetRandomWriteTarget(1, stats);
                capture.SetGlobalMatrix(RasterId, raster);
                capture.SetGlobalMatrix(ViewProjectionId, viewProjection);
                capture.SetGlobalMatrix(PreviousViewProjectionId, previousViewProjection);
                capture.SetGlobalFloat(BadPixelsId, BadPixels);
                capture.SetGlobalVector(TexelId, new Vector4(1f / size.x, 1f / size.y, size.x, size.y));
                capture.SetGlobalTexture(MotionId, motionVectors);
                capture.SetGlobalTexture(DepthId, depth);
                foreach (Drawn d in drawn)
                {
                    Matrix4x4 before;
                    if (d.Renderer == null || !d.Renderer.enabled || !d.Renderer.gameObject.activeInHierarchy
                        || !previousModel.TryGetValue(d.Renderer, out before))
                        continue;
                    capture.SetGlobalMatrix(PreviousModelId, before);
                    capture.SetGlobalFloat(PartId, d.Part);
                    for (int s = 0; s < d.Submeshes; s++) capture.DrawRenderer(d.Renderer, material, s, 0);
                }
                capture.ClearRandomWriteTargets();
                bool stepped = steps > 0;
                capture.RequestAsyncReadback(stats, request => Read(request, stepped));
            }
            else
            {
                skipped++;
            }

            foreach (Drawn d in drawn)
                if (d.Renderer != null) previousModel[d.Renderer] = d.Renderer.localToWorldMatrix;
            previousViewProjection = viewProjection;
            previousFrame = Time.frameCount;

            if (Time.unscaledTime - windowStart >= ReportSeconds) Report();
        }

        private bool Prepare(Vector2Int size)
        {
            if (state != null) return false;
            if (material == null)
            {
                string error;
                Shader shader = FsrShaderBundle.LoadShader(FsrShaderBundle.MotionAuditShaderName, out error);
                if (shader == null)
                {
                    state = error;
                    Debug.Log(Log.Tag + " Vessel motion vector check left out: " + error + ".");
                    return false;
                }
                material = new Material(shader) { name = "ReDefinition motion audit", hideFlags = HideFlags.DontSave };
            }
            if (stats == null) stats = new ComputeBuffer(MaxParts * 4, sizeof(uint));
            bool created;
            if (!UpscalerMasks.Ensure(ref target, size, RenderTextureFormat.R8, "ReDefinition_MotionAudit", out created))
                return false;
            return true;
        }

        private void Choose(Vessel active)
        {
            drawnFor = active;
            partCount = active.parts.Count;
            drawn.Clear();
            parts.Clear();
            previousModel.Clear();
            for (int p = 0; p < active.parts.Count && p < MaxParts; p++)
            {
                Part part = active.parts[p];
                parts.Add(part);
                if (part == null) continue;
                foreach (MeshRenderer renderer in part.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (renderer == null) continue;
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) continue;
                    Material[] materials = renderer.sharedMaterials;
                    bool opaque = materials.Length > 0;
                    foreach (Material m in materials)
                        if (m == null || m.renderQueue > 2500) opaque = false;
                    if (!opaque) continue;
                    drawn.Add(new Drawn
                    {
                        Renderer = renderer,
                        Part = p,
                        Submeshes = Mathf.Min(materials.Length, filter.sharedMesh.subMeshCount),
                    });
                }
            }
            for (int i = 0; i < tallies.Length; i++) tallies[i] = null;
        }

        private void Read(AsyncGPUReadbackRequest request, bool stepped)
        {
            if (request.hasError) return;
            NativeArray<uint> data = request.GetData<uint>();
            long pixels = 0, bad = 0;
            for (int p = 0; p < parts.Count; p++)
            {
                uint count = data[p * 4];
                if (count == 0) continue;
                PartTally tally = tallies[p] ?? (tallies[p] = new PartTally());
                tally.Pixels += count;
                tally.Bad += data[p * 4 + 1];
                if (data[p * 4 + 3] > tally.LargestSixteenths) tally.LargestSixteenths = data[p * 4 + 3];
                pixels += count;
                bad += data[p * 4 + 1];
            }
            if (pixels == 0) return;
            framesRead++;
            pixelsRead += pixels;
            badRead += bad;
            bool badFrame = bad > BadFrameShare * pixels;
            if (stepped)
            {
                framesWithStep++;
                if (badFrame) badFramesWithStep++;
            }
            else
            {
                framesWithoutStep++;
                if (badFrame) badFramesWithoutStep++;
            }
        }

        private void Report()
        {
            float elapsed = Time.unscaledTime - windowStart;
            windowStart = Time.unscaledTime;
            if (framesRead > 0)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(Log.Tag).Append(" Vessel motion vectors per pixel, last ")
                  .Append(elapsed.ToString("0.0", CultureInfo.InvariantCulture)).Append(" s, ")
                  .Append(framesRead).Append(" frame(s): ").Append(pixelsRead).Append(" pixels, ")
                  .Append(Percent(badRead, pixelsRead)).Append(" off by more than ")
                  .Append(BadPixels.ToString("0", CultureInfo.InvariantCulture)).Append(" px; frames with more than ")
                  .Append(Percent(1, (long)(1f / BadFrameShare))).Append(" off: ")
                  .Append(badFramesWithStep).Append(" of ").Append(framesWithStep).Append(" with a physics step, ")
                  .Append(badFramesWithoutStep).Append(" of ").Append(framesWithoutStep).Append(" without; ")
                  .Append(skipped).Append(" frame(s) left out for an origin shift or a reset; parts most off: ");
                AppendParts(sb);
                Debug.Log(sb.ToString());
            }
            framesRead = framesWithStep = framesWithoutStep = badFramesWithStep = badFramesWithoutStep = skipped = 0;
            pixelsRead = badRead = 0;
            for (int i = 0; i < tallies.Length; i++) tallies[i] = null;
        }

        private void AppendParts(StringBuilder sb)
        {
            List<int> order = new List<int>();
            for (int p = 0; p < parts.Count; p++)
                if (tallies[p] != null && tallies[p].Pixels > 0) order.Add(p);
            order.Sort((a, b) => ((double)tallies[b].Bad / tallies[b].Pixels)
                .CompareTo((double)tallies[a].Bad / tallies[a].Pixels));
            for (int i = 0; i < order.Count && i < PartsListed; i++)
            {
                int p = order[i];
                Part part = parts[p];
                PartTally tally = tallies[p];
                if (i > 0) sb.Append(", ");
                sb.Append(part != null && part.partInfo != null ? part.partInfo.name : "part " + p)
                  .Append(' ').Append(Percent(tally.Bad, tally.Pixels))
                  .Append(" (max ").Append((tally.LargestSixteenths / 16f).ToString("0.0", CultureInfo.InvariantCulture))
                  .Append(" px)");
            }
            if (order.Count == 0) sb.Append("none");
            sb.Append('.');
        }

        private static string Percent(long part, long whole)
        {
            if (whole <= 0) return "0%";
            return (100.0 * part / whole).ToString("0.###", CultureInfo.InvariantCulture) + "%";
        }
    }
}
