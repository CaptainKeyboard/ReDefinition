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
    // The active vessel's depth and motion vectors, written by the rig and checked on
    // every pixel.
    //
    // The depth first. Unity's depth sources in the deferred path -- ResolvedDepth, which
    // the rig captures, Depth and _CameraDepthTexture -- hold what the deferred pass drew
    // and nothing of what the forward pass draws after it: measured in MotionVectorCheck
    // with a forward-only cube in front of a deferred sphere, 0 at the cube in all three.
    // Parts KSP draws in the forward pass were missing from the depth every upscaler and
    // frame generation read; in NVIDIA's alignment test the parts under the vessel showed
    // through it, and frame generation placed the whole vessel wrong from some angles.
    // Pass 2 writes each of the vessel's renderers' depth into the captured depth where
    // it is nearer, skinned ones included.
    //
    // Measured on the runway with DLSS presets L and M: while the aircraft rolled fast,
    // 0.5 to 1.2% of its pixels, in up to a quarter of the frames, carried Unity's
    // camera motion -- the motion of standing ground at that depth -- instead of the
    // part's own, off by up to a hundred pixels, most on the canards, the elevons, the
    // nose and the landing gear. At a standstill both agree and it does not show. The
    // upscalers then blend in the wrong history at those edges, which flickers.
    //
    // At the end of the capture each of the vessel's mesh renderers is drawn once more
    // with Hidden/ReDefinition/MotionAudit, rasterised with the scene camera's jittered
    // projection so it covers the pixels it covered in the image. Where its depth is
    // the captured depth -- the surface the camera saw -- the shader computes the motion
    // vector Unity writes for an object, this frame's position against the previous
    // frame's through the renderer's previous matrix and the previous view-projection.
    // Pass 1 writes it into the captured motion vectors, before the other mods' hooks;
    // pass 0, after them, compares it with what the upscalers read and counts, per part,
    // the pixels, those off by more than a pixel and the largest error, read back every
    // frame and logged about every ten seconds. MotionVectorCheck in the Unity project
    // checks the shader against Unity's own motion vectors.
    //
    // Skinned renderers are left out, since their previous pose is not at hand; so are
    // materials above queue 2500, which write no motion vectors. Frames with an origin
    // shift or a history reset since the frame before are left out, since there the
    // previous matrices do not describe the motion. The repair has a Debug switch, not
    // saved, for a look with and without.
    internal sealed class VesselMotionVectors
    {
        private const int AuditPass = 0;
        private const int RepairPass = 1;
        private const int DepthPass = 2;
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

        // The Debug switch; on at every start.
        internal static bool RepairEnabled = true;

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
        // Every opaque renderer of the vessel, skinned ones too: their depth needs no
        // previous pose.
        private readonly List<Drawn> depthDrawn = new List<Drawn>();
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

        // This frame's, from Begin.
        private bool active;
        private bool continuous;
        private bool stepped;
        private Matrix4x4 viewProjection;
        private Matrix4x4 raster;
        private Vector2Int size;
        private bool repairedThisWindow;

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
        // vessel for this frame: the frame's matrices, and whether the previous frame's
        // describe the motion to it.
        internal void Begin(Camera camera, Vector2Int renderSize)
        {
            active = false;
            if (camera == null || !HighLogic.LoadedSceneIsFlight) return;
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null || !Prepare(renderSize)) return;
            active = true;
            size = renderSize;
            if (windowStart < 0f) windowStart = Time.unscaledTime;

            Matrix4x4 projection = camera.projectionMatrix;
            viewProjection = GL.GetGPUProjectionMatrix(projection, false) * camera.worldToCameraMatrix;
            Matrix4x4 jittered = CameraRedirect.JitterActive
                ? Matrix4x4.Translate(new Vector3(CameraRedirect.JitterNdc.x, CameraRedirect.JitterNdc.y, 0f)) * projection
                : projection;
            raster = GL.GetGPUProjectionMatrix(jittered, true) * camera.worldToCameraMatrix;

            if (vessel != drawnFor || vessel.parts.Count != partCount) Choose(vessel);

            stepped = physicsSteps != stepsAtPreviousFrame;
            stepsAtPreviousFrame = physicsSteps;
            continuous = previousFrame == Time.frameCount - 1 && !shifted && SharedFrame.RigResetReason == null;
            shifted = false;
            if (!continuous) skipped++;
        }

        // Into the capture, before the repair, which tests against it.
        internal void RecordDepth(CommandBuffer capture, RenderTexture depth)
        {
            if (!active || !RepairEnabled || depth == null) return;
            capture.SetRenderTarget(depth);
            capture.SetGlobalMatrix(RasterId, raster);
            foreach (Drawn d in depthDrawn)
            {
                if (d.Renderer == null || !d.Renderer.enabled || !d.Renderer.gameObject.activeInHierarchy) continue;
                for (int s = 0; s < d.Submeshes; s++) capture.DrawRenderer(d.Renderer, material, s, DepthPass);
            }
        }

        // Into the capture, before the other mods' motion vector hooks.
        internal void RecordRepair(CommandBuffer capture, RenderTexture motionVectors, RenderTexture depth)
        {
            if (!active || !continuous || !RepairEnabled || motionVectors == null || depth == null) return;
            repairedThisWindow = true;
            capture.SetRenderTarget(motionVectors);
            SetCommon(capture, motionVectors, depth);
            Draw(capture, RepairPass, false);
        }

        // Into the capture, after the hooks: what the upscalers read.
        internal void RecordAudit(CommandBuffer capture, RenderTexture motionVectors, RenderTexture depth)
        {
            if (!active || !continuous || motionVectors == null || depth == null) return;
            stats.SetData(zeros);
            capture.SetRenderTarget(target);
            capture.SetRandomWriteTarget(1, stats);
            SetCommon(capture, motionVectors, depth);
            capture.SetGlobalFloat(BadPixelsId, BadPixels);
            Draw(capture, AuditPass, true);
            capture.ClearRandomWriteTargets();
            bool withStep = stepped;
            capture.RequestAsyncReadback(stats, request => Read(request, withStep));
        }

        // After both: this frame's matrices become the previous ones.
        internal void End()
        {
            if (!active) return;
            foreach (Drawn d in drawn)
                if (d.Renderer != null) previousModel[d.Renderer] = d.Renderer.localToWorldMatrix;
            previousViewProjection = viewProjection;
            previousFrame = Time.frameCount;
            if (Time.unscaledTime - windowStart >= ReportSeconds) Report();
        }

        private void SetCommon(CommandBuffer capture, RenderTexture motionVectors, RenderTexture depth)
        {
            capture.SetGlobalMatrix(RasterId, raster);
            capture.SetGlobalMatrix(ViewProjectionId, viewProjection);
            capture.SetGlobalMatrix(PreviousViewProjectionId, previousViewProjection);
            capture.SetGlobalVector(TexelId, new Vector4(1f / size.x, 1f / size.y, size.x, size.y));
            capture.SetGlobalTexture(MotionId, motionVectors);
            capture.SetGlobalTexture(DepthId, depth);
        }

        private void Draw(CommandBuffer capture, int pass, bool withPart)
        {
            foreach (Drawn d in drawn)
            {
                Matrix4x4 before;
                if (d.Renderer == null || !d.Renderer.enabled || !d.Renderer.gameObject.activeInHierarchy
                    || !previousModel.TryGetValue(d.Renderer, out before))
                    continue;
                capture.SetGlobalMatrix(PreviousModelId, before);
                if (withPart) capture.SetGlobalFloat(PartId, d.Part);
                for (int s = 0; s < d.Submeshes; s++) capture.DrawRenderer(d.Renderer, material, s, pass);
            }
        }

        private bool Prepare(Vector2Int renderSize)
        {
            if (state != null) return false;
            if (material == null)
            {
                string error;
                Shader shader = FsrShaderBundle.LoadShader(FsrShaderBundle.MotionAuditShaderName, out error);
                if (shader == null)
                {
                    state = error;
                    Debug.Log(Log.Tag + " The vessel's own motion vectors left out: " + error + ".");
                    return false;
                }
                material = new Material(shader) { name = "ReDefinition vessel motion", hideFlags = HideFlags.DontSave };
                if (material.passCount <= DepthPass)
                {
                    state = FsrShaderBundle.MotionAuditShaderName + " in the shader bundle is older than this build";
                    Debug.Log(Log.Tag + " The vessel's own motion vectors left out: " + state + ".");
                    return false;
                }
            }
            if (stats == null) stats = new ComputeBuffer(MaxParts * 4, sizeof(uint));
            bool created;
            return UpscalerMasks.Ensure(ref target, renderSize, RenderTextureFormat.R8, "ReDefinition_MotionAudit",
                                        out created);
        }

        private void Choose(Vessel vessel)
        {
            drawnFor = vessel;
            partCount = vessel.parts.Count;
            drawn.Clear();
            depthDrawn.Clear();
            parts.Clear();
            previousModel.Clear();
            for (int p = 0; p < vessel.parts.Count && p < MaxParts; p++)
            {
                Part part = vessel.parts[p];
                parts.Add(part);
                if (part == null) continue;
                foreach (SkinnedMeshRenderer skinned in part.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (skinned == null || skinned.sharedMesh == null || !Opaque(skinned.sharedMaterials)) continue;
                    depthDrawn.Add(new Drawn
                    {
                        Renderer = skinned,
                        Part = p,
                        Submeshes = Mathf.Min(skinned.sharedMaterials.Length, skinned.sharedMesh.subMeshCount),
                    });
                }
                foreach (MeshRenderer renderer in part.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (renderer == null) continue;
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) continue;
                    Material[] materials = renderer.sharedMaterials;
                    if (!Opaque(materials)) continue;
                    Drawn entry = new Drawn
                    {
                        Renderer = renderer,
                        Part = p,
                        Submeshes = Mathf.Min(materials.Length, filter.sharedMesh.subMeshCount),
                    };
                    drawn.Add(entry);
                    depthDrawn.Add(entry);
                }
            }
            for (int i = 0; i < tallies.Length; i++) tallies[i] = null;
        }

        // Materials that write depth and motion vectors: none above queue 2500.
        private static bool Opaque(Material[] materials)
        {
            if (materials == null || materials.Length == 0) return false;
            foreach (Material m in materials)
                if (m == null || m.renderQueue > 2500) return false;
            return true;
        }

        private void Read(AsyncGPUReadbackRequest request, bool withStep)
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
            if (withStep)
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
                sb.Append(Log.Tag).Append(" Vessel motion vectors per pixel, ")
                  .Append(repairedThisWindow ? "written by ReDefinition" : "as Unity wrote them").Append(", last ")
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
            repairedThisWindow = false;
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
