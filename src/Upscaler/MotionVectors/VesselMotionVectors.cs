using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ReDefinition.Api.Internal;
using ReDefinition.Core;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Upscaler
{
    // The active vessel's depth and motion vectors, and those of the vessels it dropped near it, written by the
    // rig and checked on every pixel.
    //
    // Parts KSP draws in the forward pass were missing from the depth every upscaler and
    // frame generation read (RecordDepth). In NVIDIA's alignment test the parts under the vessel showed
    // through it, and frame generation placed the whole vessel wrong from some angles.
    // While the aircraft rolled fast, part of its pixels carried Unity's camera
    // motion instead of the part's own (RepairEnabled).
    //
    // At the end of the capture each of the vessel's mesh renderers is drawn once more
    // with Hidden/ReDefinition/MotionAudit, rasterised with the scene camera's jittered
    // projection so it covers the pixels it covered in the image (Draw). The repair has a Debug switch, not
    // saved, for a look with and without.
    internal sealed class VesselMotionVectors
    {
        // The shader's passes (audit, repair, depth), and the most parts tallied. Then the error in pixels that
        // counts a pixel as off, and the share of them that counts a frame as off. Last how often the audit
        // reports, and how many of the parts most off the report names.
        private const int AuditPass = 0;
        private const int RepairPass = 1;
        private const int DepthPass = 2;
        private const int MaxParts = 256;
        private const float BadPixels = 1f;
        private const float BadFrameShare = 0.005f;
        private const float ReportSeconds = 10f;
        private const int PartsListed = 5;

        // The MotionAudit shader's properties, set on the capture before each pass.
        private static readonly int RasterId = Shader.PropertyToID("_AuditRasterViewProjection");
        private static readonly int ViewProjectionId = Shader.PropertyToID("_AuditViewProjection");
        private static readonly int PreviousViewProjectionId = Shader.PropertyToID("_AuditPreviousViewProjection");
        private static readonly int PreviousModelId = Shader.PropertyToID("_AuditPreviousModel");
        private static readonly int PartId = Shader.PropertyToID("_AuditPart");
        private static readonly int BadPixelsId = Shader.PropertyToID("_AuditBadPixels");
        private static readonly int TexelId = Shader.PropertyToID("_AuditTexel");
        private static readonly int MotionId = Shader.PropertyToID("_AuditMotion");
        private static readonly int DepthId = Shader.PropertyToID("_AuditDepth");

        // The Debug switch; on at every start. Measured on the runway with DLSS presets L and M. While the aircraft
        // rolled fast, 0.5 to 1.2% of its pixels, in up to a quarter of the frames, carried Unity's camera motion
        // (the motion of standing ground at that depth) instead of the part's own. They were off by up to a
        // hundred pixels, most on the canards, the elevons, the nose and the landing gear. At a standstill both
        // agree and it does not show. The upscalers then blend in the wrong history at those edges, which flickers.
        internal static bool RepairEnabled = true;

        // A renderer drawn with the shader, the index of its part in parts, how many of its submeshes are drawn, and
        // what Begin read of it for this frame. Every pass of the frame reads the entry rather than the engine, which
        // answered the same four questions in each of them.
        private struct Entry
        {
            // The renderer, its part's index (the audit's tally slot), and the submeshes that have a material.
            internal Renderer Renderer;
            internal int Part;
            internal int Submeshes;
            // A skinned renderer is drawn for the depth only: its previous pose is not at hand.
            internal bool Skinned;
            // This frame's: whether the renderer is enabled and active, whether it is there to read (Current holds its
            // matrix), and its matrix.
            internal bool Live;
            internal bool Read;
            internal Matrix4x4 Current;
            // The matrix End stored the frame before, if it did; kept across Choose for the renderers that stay.
            internal bool HasPrevious;
            internal Matrix4x4 Previous;
        }

        // One part's audit over the report window.
        private sealed class PartTally
        {
            // Pixels checked, those off by more than BadPixels, and the largest error in sixteenths of a pixel.
            internal long Pixels;
            internal long Bad;
            internal uint LargestSixteenths;
        }

        // Every opaque renderer of the vessels, skinned ones too (their depth needs no previous pose); the mesh
        // renderers among them, opaque and not skinned, are the ones whose motion is repaired and audited.
        private Entry[] entries = new Entry[64];
        private int entryCount;
        private readonly List<Part> parts = new List<Part>();
        // Choose's scratch: the parts of all the vessels, a part's renderers by kind, and the previous matrices of the
        // renderers that stay.
        private readonly List<Part> chosenParts = new List<Part>();
        private readonly List<SkinnedMeshRenderer> skinnedScratch = new List<SkinnedMeshRenderer>();
        private readonly List<MeshRenderer> meshScratch = new List<MeshRenderer>();
        private readonly Dictionary<Renderer, Matrix4x4> carried = new Dictionary<Renderer, Matrix4x4>();
        private readonly PartTally[] tallies = new PartTally[MaxParts];
        private readonly uint[] zeros = new uint[MaxParts * 4];
        private Vessel drawnFor;
        // The signature of the vessels the renderers were chosen from (LoadedSignature).
        private int partCount;
        private ComputeBuffer stats;
        private Material material;
        private RenderTexture target;
        private string state;
        private Matrix4x4 previousViewProjection;
        private int previousFrame = -10;
        private float windowStart = -1f;

        // This frame's, from Begin.
        private bool active;
        private bool continuous;
        private bool stepped;
        private Matrix4x4 viewProjection;
        private Matrix4x4 raster;
        private Vector2Int size;
        private bool repairedThisWindow;

        // KSP's physics steps counted so far, and the count at the frame before: whether a step fell in between.
        private static int physicsSteps;
        private int stepsAtPreviousFrame;

        // The report window's audit: frames read back, with and without a physics step and how many of each were
        // off, pixels read and those off, and frames left out for an origin shift or a reset.
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

        // Frees the readback buffer, the audit's target and the material; Prepare makes them again.
        internal void Disable()
        {
            if (stats != null) stats.Release();
            stats = null;
            RenderTextures.Release(ref target);
            if (material != null) Object.Destroy(material);
            material = null;
        }

        // In the scene camera's OnPreCull, after LateUpdate, where KSP has placed the
        // vessel for this frame: the frame's matrices, and whether the previous frame's
        // describe the motion to it. Frames with an origin shift or a history reset since the frame before are
        // left out, since there the previous matrices do not describe the motion.
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
            Matrix4x4 jittered = CameraRedirect.JitterActive ? CameraRedirect.Jittered(projection) : projection;
            raster = GL.GetGPUProjectionMatrix(jittered, true) * camera.worldToCameraMatrix;

            int signature = LoadedSignature(vessel);
            if (vessel != drawnFor || signature != partCount) Choose(vessel, signature);
            ReadEntries();

            stepped = physicsSteps != stepsAtPreviousFrame;
            stepsAtPreviousFrame = physicsSteps;
            continuous = previousFrame == Time.frameCount - 1 && !SharedFrame.Shifted && SharedFrame.RigResetReason == null;
            if (!continuous) skipped++;
        }

        // Into the capture, before the repair, which tests against it. Unity's depth sources in the deferred path
        // (ResolvedDepth, which the rig captures, Depth and _CameraDepthTexture) hold what the deferred pass drew
        // and nothing of what the forward pass draws after it. Measured in MotionVectorCheck with a forward-only
        // cube in front of a deferred sphere, 0 at the cube in all three. Pass 2 writes each of the vessel's
        // renderers' depth into the captured depth where it is nearer, skinned ones included.
        internal void RecordDepth(CommandBuffer capture, RenderTexture depth)
        {
            if (!active || !RepairEnabled || depth == null) return;
            capture.SetRenderTarget(depth);
            capture.SetGlobalMatrix(RasterId, raster);
            for (int i = 0; i < entryCount; i++)
            {
                if (!entries[i].Live) continue;
                for (int s = 0; s < entries[i].Submeshes; s++) capture.DrawRenderer(entries[i].Renderer, material, s, DepthPass);
            }
        }

        // Into the capture, before the other mods' motion vector hooks. Pass 1 writes the shader's motion vector
        // into the captured motion vectors.
        internal void RecordRepair(CommandBuffer capture, RenderTexture motionVectors, RenderTexture depth)
        {
            if (!active || !continuous || !RepairEnabled || motionVectors == null || depth == null) return;
            // Where none of the vessel's renderers moved since the frame before, each part's
            // motion is the camera's alone, which is what Unity's motion vectors (or the
            // repair of them, UnityMotionRepair) hold there already: the repair would write
            // the same again. On the pad that was 1 ms of the GPU's frame for some 600 draws
            // (RenderDoc, 2026-10-06); the matrices are compared on the CPU in microseconds.
            if (!AnyMoved())
            {
                standing++;
                return;
            }
            repairedThisWindow = true;
            capture.SetRenderTarget(motionVectors);
            SetCommon(capture, motionVectors, depth);
            Draw(capture, RepairPass, false);
        }

        // One frame in AuditShare, picked at random, gets the audit. That way the frames with a physics
        // step and those without are both sampled however the two rates line up. The
        // audit draws the vessel again and reads back, a diagnostic not worth every frame.
        private const int AuditShare = 6;
        private uint auditPick = 2463534242u;
        // The read-back's callbacks, with and without a physics step, made once: a
        // lambda capturing the frame's flag would be allocated in every call.
        private System.Action<AsyncGPUReadbackRequest> readWithStep;
        private System.Action<AsyncGPUReadbackRequest> readWithoutStep;

        // Into the capture, after the hooks: what the upscalers read. Pass 0 compares the shader's motion vector
        // with what the upscalers read and counts, per part, the pixels, those off by more than a pixel and the
        // largest error. They are read back and logged about every ten seconds with the diagnostic measurements on
        // (ReDefinitionAddon.DebugMode). MotionVectorCheck in the Unity project checks the shader against Unity's
        // own motion vectors.
        internal void RecordAudit(CommandBuffer capture, RenderTexture motionVectors, RenderTexture depth)
        {
            if (!active || !continuous || motionVectors == null || depth == null) return;
            auditPick ^= auditPick << 13;
            auditPick ^= auditPick >> 17;
            auditPick ^= auditPick << 5;
            if (auditPick % AuditShare != 0) return;
            stats.SetData(zeros);
            capture.SetRenderTarget(target);
            capture.SetRandomWriteTarget(1, stats);
            SetCommon(capture, motionVectors, depth);
            capture.SetGlobalFloat(BadPixelsId, BadPixels);
            Draw(capture, AuditPass, true);
            capture.ClearRandomWriteTargets();
            if (readWithStep == null)
            {
                readWithStep = request => Read(request, true);
                readWithoutStep = request => Read(request, false);
            }
            capture.RequestAsyncReadback(stats, stepped ? readWithStep : readWithoutStep);
        }

        // Frames in the report window the repair was left out because nothing of the vessels moved.
        private int standing;

        // What the frame's passes ask of every renderer, read once: whether it is enabled and active (Live), and for the
        // mesh renderers its matrix. These are the engine's calls the passes made again and again: the depth, the
        // movement test, the drawing and the storing at the end each asked.
        private void ReadEntries()
        {
            for (int i = 0; i < entryCount; i++)
            {
                Renderer renderer = entries[i].Renderer;
                bool there = renderer != null;
                entries[i].Live = there && renderer.enabled && renderer.gameObject.activeInHierarchy;
                entries[i].Read = there && !entries[i].Skinned;
                if (entries[i].Read) entries[i].Current = renderer.localToWorldMatrix;
            }
        }

        // Whether any drawn renderer's matrix differs from the frame before's, or has none from it.
        private bool AnyMoved()
        {
            for (int i = 0; i < entryCount; i++)
            {
                if (entries[i].Skinned || !entries[i].Live) continue;
                if (!entries[i].HasPrevious || entries[i].Current != entries[i].Previous) return true;
            }
            return false;
        }

        // After both: this frame's matrices become the previous ones.
        internal void End()
        {
            if (!active) return;
            for (int i = 0; i < entryCount; i++)
            {
                if (!entries[i].Read) continue;
                entries[i].Previous = entries[i].Current;
                entries[i].HasPrevious = true;
            }
            previousViewProjection = viewProjection;
            previousFrame = Time.frameCount;
            if (Time.unscaledTime - windowStart >= ReportSeconds) Report();
        }

        // The matrices, texel size and textures the repair and the audit pass both read.
        private void SetCommon(CommandBuffer capture, RenderTexture motionVectors, RenderTexture depth)
        {
            capture.SetGlobalMatrix(RasterId, raster);
            capture.SetGlobalMatrix(ViewProjectionId, viewProjection);
            capture.SetGlobalMatrix(PreviousViewProjectionId, previousViewProjection);
            capture.SetGlobalVector(TexelId, new Vector4(1f / size.x, 1f / size.y, size.x, size.y));
            capture.SetGlobalTexture(MotionId, motionVectors);
            capture.SetGlobalTexture(DepthId, depth);
        }

        // Every visible mesh renderer of the entries with its previous matrix in that pass, and its part's index for the audit;
        // a renderer without a matrix from the frame before is left out. Where a renderer's depth is the captured
        // depth (the surface the camera saw) the shader computes the motion vector Unity writes for an object.
        // That is this frame's position against the previous frame's through the renderer's previous matrix and the
        // previous view-projection.
        private void Draw(CommandBuffer capture, int pass, bool withPart)
        {
            for (int i = 0; i < entryCount; i++)
            {
                if (entries[i].Skinned || !entries[i].Live || !entries[i].HasPrevious) continue;
                capture.SetGlobalMatrix(PreviousModelId, entries[i].Previous);
                if (withPart) capture.SetGlobalFloat(PartId, entries[i].Part);
                for (int s = 0; s < entries[i].Submeshes; s++) capture.DrawRenderer(entries[i].Renderer, material, s, pass);
            }
        }

        // The material, the readback buffer and the audit's target at render size; false where the shader is
        // missing (said once, and left out for good) or the target could not be made.
        private bool Prepare(Vector2Int renderSize)
        {
            if (state != null) return false;
            if (material == null)
            {
                string error;
                bool outdated;
                material = ShaderBundle.MakeMaterial(ShaderBundle.MotionAuditShaderName, "ReDefinition vessel motion", DepthPass + 1,
                    out error, out outdated);
                if (material == null)
                {
                    state = error;
                    Debug.Log(Log.Tag + " The vessel's own motion vectors left out: " + error + ".");
                    return false;
                }
            }
            if (stats == null) stats = new ComputeBuffer(MaxParts * 4, sizeof(uint));
            bool created;
            return RenderTextures.Ensure(ref target, renderSize, RenderTextureFormat.R8, "ReDefinition_MotionAudit",
                                        out created);
        }

        // How far from the active vessel another loaded vessel's parts are drawn too. These are what it dropped:
        // boosters, fairings, a stage, which KSP makes vessels of their own the moment they part. Frame generation
        // and the upscalers otherwise read them with the ground's motion and without their forward-drawn depth.
        // RenderInterpolation draws the same vessels between the physics steps.
        private const float NearbyMetres = 2500f;

        // -redefinitionProbeActiveVesselOnly: the active vessel alone, as before, to compare.
        private static readonly bool ActiveOnly =
            CommandLine.Has("-redefinitionProbeActiveVesselOnly");

        // The active vessel and the loaded vessels near it, the active one first.
        private readonly List<Vessel> near = new List<Vessel>();

        // Fills the near list, which LoadedSignature and Choose read, with vessel and the loaded vessels around it
        // (NearVessels).
        private void Near(Vessel vessel)
        {
            NearVessels(vessel, near);
        }

        // Into `into`: vessel and the loaded vessels within NearbyMetres of it, vessel first.
        internal static void NearVessels(Vessel vessel, List<Vessel> into)
        {
            into.Clear();
            into.Add(vessel);
            if (ActiveOnly) return;
            foreach (Vessel other in FlightGlobals.VesselsLoaded)
            {
                if (other == null || other == vessel || other.packed || other.parts == null) continue;
                if ((other.CoMD - vessel.CoMD).sqrMagnitude > (double)NearbyMetres * NearbyMetres) continue;
                into.Add(other);
            }
        }

        // Changes when a vessel near the active one comes or goes or its parts change: the renderers chosen again.
        private int LoadedSignature(Vessel vessel)
        {
            Near(vessel);
            int signature = 17;
            foreach (Vessel v in near) signature = signature * 31 + v.persistentId.GetHashCode() * 7 + v.parts.Count;
            return signature;
        }

        // The renderers of vessel and the vessels near it chosen anew, up to MaxParts parts: skinned ones for the
        // depth only, opaque mesh renderers for depth, repair and audit; the tallies cleared. Skinned renderers
        // are left out of the repair, since their previous pose is not at hand; so are materials above queue 2500,
        // which write no motion vectors.
        private void Choose(Vessel vessel, int signature)
        {
            drawnFor = vessel;
            partCount = signature;
            // The previous matrices kept: a renderer that was drawn the frame before -- a booster that has just
            // become a vessel of its own -- has its motion from it in this frame too. Without, the frame the vessel
            // parts carried the ground's motion on every part, a ghost in the frames generated from it. Those of
            // renderers no longer drawn are let go with the old entries.
            carried.Clear();
            for (int i = 0; i < entryCount; i++)
                if (entries[i].HasPrevious && !entries[i].Skinned && entries[i].Renderer != null)
                    carried[entries[i].Renderer] = entries[i].Previous;
            entryCount = 0;
            parts.Clear();
            // The vessels near the active one are those LoadedSignature just listed in this same call from Begin.
            chosenParts.Clear();
            foreach (Vessel v in near) chosenParts.AddRange(v.parts);
            for (int p = 0; p < chosenParts.Count && p < MaxParts; p++)
            {
                Part part = chosenParts[p];
                parts.Add(part);
                if (part == null) continue;
                part.GetComponentsInChildren(true, skinnedScratch);
                foreach (SkinnedMeshRenderer skinned in skinnedScratch)
                {
                    if (skinned == null || skinned.sharedMesh == null || !Opaque(skinned.sharedMaterials)) continue;
                    AddEntry(new Entry
                    {
                        Renderer = skinned,
                        Part = p,
                        Submeshes = Mathf.Min(skinned.sharedMaterials.Length, skinned.sharedMesh.subMeshCount),
                        Skinned = true,
                    });
                }
                part.GetComponentsInChildren(true, meshScratch);
                foreach (MeshRenderer renderer in meshScratch)
                {
                    if (renderer == null) continue;
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) continue;
                    Material[] materials = renderer.sharedMaterials;
                    if (!Opaque(materials)) continue;
                    Entry entry = new Entry
                    {
                        Renderer = renderer,
                        Part = p,
                        Submeshes = Mathf.Min(materials.Length, filter.sharedMesh.subMeshCount),
                    };
                    Matrix4x4 before;
                    entry.HasPrevious = carried.TryGetValue(renderer, out before);
                    if (entry.HasPrevious) entry.Previous = before;
                    AddEntry(entry);
                }
            }
            skinnedScratch.Clear();
            meshScratch.Clear();
            chosenParts.Clear();
            carried.Clear();
            for (int i = 0; i < tallies.Length; i++) tallies[i] = null;
        }

        // Appends an entry, the array grown where it is full.
        private void AddEntry(Entry entry)
        {
            if (entryCount == entries.Length) System.Array.Resize(ref entries, entries.Length * 2);
            entries[entryCount++] = entry;
        }

        // Materials that write depth and motion vectors: none above queue 2500.
        private static bool Opaque(Material[] materials)
        {
            if (materials == null || materials.Length == 0) return false;
            foreach (Material m in materials)
                if (m == null || m.renderQueue > 2500) return false;
            return true;
        }

        // An audit's readback: four counters per part -- pixels, those off, the summed error and the largest, in
        // sixteenths of a pixel; all but the sum added to the tallies and the window's frame counts.
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

        // The window's log lines -- frames the repair was left out, and the audit with the parts most off -- and the
        // window started afresh.
        private void Report()
        {
            float elapsed = Time.unscaledTime - windowStart;
            windowStart = Time.unscaledTime;
            if (standing > 0)
                Debug.Log(Log.Tag + " Vessel motion vectors, last " + elapsed.ToString("0.0", CultureInfo.InvariantCulture) + " s: the repair left out in "
                          + standing + " frame(s), nothing of the vessel moved.");
            standing = 0;
            if (framesRead > 0)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(Log.Tag).Append(" Vessel motion vectors per pixel, ")
                  .Append(repairedThisWindow ? "written by ReDefinition" : "as Unity wrote them").Append(", last ")
                  .Append(elapsed.ToString("0.0", CultureInfo.InvariantCulture)).Append(" s, ")
                  .Append(framesRead).Append(" frame(s) sampled, one in ").Append(AuditShare).Append(": ")
                  .Append(pixelsRead).Append(" pixels, ")
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

        // The PartsListed parts with the largest share of pixels off, with that share and their largest error.
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

        // part of whole as a percentage with up to three decimals; 0% for nothing.
        private static string Percent(long part, long whole)
        {
            if (whole <= 0) return "0%";
            return (100.0 * part / whole).ToString("0.###", CultureInfo.InvariantCulture) + "%";
        }
    }
}
