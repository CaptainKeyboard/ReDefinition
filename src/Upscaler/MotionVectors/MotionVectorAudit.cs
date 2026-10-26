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
    // An instrument: whether the motion vectors every upscaler reads are right on the
    // active vessel and on the ground around it, logged about every ten seconds in
    // flight while the rig runs with the diagnostic measurements on
    // (ReDefinitionAddon.DebugMode).
    //
    // Every fifth frame, as the scene camera culls, the capture gets readbacks of the
    // finished motion vectors and depth at a few pixels: the centres of three of the
    // vessel's parts (the root and the two farthest from its centre of mass, the
    // extremities) and four points low in the picture, mostly ground. From the depth
    // the pixel's world position is taken back through this frame's view-projection;
    // where it lies is where it was a frame before: for a part moved by the part's change
    // of transform, for the ground not at all. Both positions go through this frame's
    // and the previous frame's view-projection, as Unity's motion vectors do, and the
    // difference is compared with what was read. A vessel pixel counts only where the
    // position lies within that part's bounds, a ground pixel only away from the vessel
    // and short of the far plane. Frames with a floating origin shift or a history
    // reset since the frame before are left out.
    //
    // The pixel's row is its viewport y times the height, as the capture's render
    // texture is laid out; MotionVectorCheck in the Unity project checks that
    // addressing against a rendered sphere. The motion vectors are the rig's
    // encoding, y up (ReDefinitionMotionVector).
    //
    // Alongside, every frame: how far Unity's own record of the previous frame's
    // camera (Camera.previousViewProjectionMatrix), from which it works out the
    // camera's motion, places the vessel and a point 1 km ahead from where the
    // previous frame's camera placed them. When the ground is off by more than
    // SourcesFromError on average, the camera's state and everything that writes into
    // its frame goes to the log once a minute (CameraSurvey.WriteMotionSources).
    internal sealed class MotionVectorAudit
    {
        // Sampled in every fifth frame.
        private const int FrameSpacing = 5;
        // Seconds between two log lines.
        private const float ReportSeconds = 10f;
        // How many of the vessel's parts are sampled: the root and the farthest from the centre of mass.
        private const int VesselPoints = 3;
        // Metres a part's renderer bounds are grown by on every side before a pixel's position is tested against them.
        private const float BoundsMargin = 0.3f;

        // The four ground points, in viewport coordinates low in the picture.
        private static readonly Vector2[] GroundViewport =
        {
            new Vector2(0.5f, 0.1f), new Vector2(0.3f, 0.18f), new Vector2(0.7f, 0.18f), new Vector2(0.5f, 0.28f),
        };

        // One pixel read back: where and when it was taken, what it should show, and what came back.
        private sealed class Sample
        {
            // A part's pixel or a ground pixel and its name for the log; the pixel's viewport position; this frame's
            // and the previous frame's view-projection; Motion, which takes a world position now to where it was a
            // frame before; the part's bounds, or the vessel's centre and radius a ground pixel must lie outside; the
            // texture size; then the depth and motion vector read back (NaN until they came) and whether a read failed.
            internal bool Vessel;
            internal string Name;
            internal Vector2 Viewport;
            internal Matrix4x4 InverseViewProjection;
            internal Matrix4x4 ViewProjection;
            internal Matrix4x4 PreviousViewProjection;
            internal Matrix4x4 Motion;
            internal Bounds Bounds;
            internal Vector3 VesselCenter;
            internal float VesselRadius;
            internal Vector2Int Size;
            internal float Depth = float.NaN;
            internal Vector2 Read = new Vector2(float.NaN, float.NaN);
            internal bool Failed;
            // Kept and used again, with its read-backs' callbacks: back once both came,
            // and taken again only some frames later. A camera that renders twice in a
            // frame (a supersized screenshot) runs the capture buffer twice; the
            // callbacks past the two awaited find Pending at 0 and change nothing.
            internal int Pending;
            internal int ReleasedFrame;
            internal System.Action<AsyncGPUReadbackRequest> DepthRead;
            internal System.Action<AsyncGPUReadbackRequest> MotionRead;

            // As a new one.
            internal void Clear()
            {
                Vessel = false;
                Name = null;
                Viewport = Vector2.zero;
                InverseViewProjection = default(Matrix4x4);
                ViewProjection = default(Matrix4x4);
                PreviousViewProjection = default(Matrix4x4);
                Motion = default(Matrix4x4);
                Bounds = default(Bounds);
                VesselCenter = Vector3.zero;
                VesselRadius = 0f;
                Size = Vector2Int.zero;
                Depth = float.NaN;
                Read = new Vector2(float.NaN, float.NaN);
                Failed = false;
            }
        }

        // Samples whose read-backs are both back, oldest first, to be taken again.
        private readonly Queue<Sample> idleSamples = new Queue<Sample>();
        // Frames a released sample waits before it is used again, so a late second callback finds it unchanged.
        private const int ReuseAfterFrames = 8;
        // A part's renderers, read into this list rather than a new array each time.
        private static readonly List<Renderer> partRenderers = new List<Renderer>();

        // The vessel's or the ground's results over one report window.
        private sealed class Tally
        {
            // Samples counted and those not on the vessel or ground; the error's sum and maximum and the expected
            // motion's sum, in pixels; the worst sample as text for the log.
            internal int Count;
            internal int Rejected;
            internal double ErrorSum;
            internal float ErrorMax;
            internal double MotionSum;
            internal string Worst;

            // Empties the tally for the next window.
            internal void Clear()
            {
                Count = 0;
                Rejected = 0;
                ErrorSum = 0;
                ErrorMax = 0f;
                MotionSum = 0;
                Worst = null;
            }
        }

        // The ground's mean error, in pixels, above which the motion sources are logged.
        private const float SourcesFromError = 10f;
        // Seconds between two logs of the motion sources.
        private const float SourcesInterval = 60f;

        // The vessel's results in this window.
        private readonly Tally vessel = new Tally();
        // The most Unity's previous camera was off in this window, in pixels, and over how many frames.
        private float unityPreviousOff;
        private int unityPreviousFrames;
        // When the motion sources may be logged again.
        private float nextSources;
        // The scene camera of the last frame, whose motion sources are logged.
        private Camera reportCamera;
        // The ground's results in this window.
        private readonly Tally ground = new Tally();
        // The sampled parts, their transforms in the frame before, and the vessel they were chosen on.
        private readonly List<Part> parts = new List<Part>();
        private readonly Dictionary<Part, Matrix4x4> previousPart = new Dictionary<Part, Matrix4x4>();
        private Vessel partsOf;
        // The frame before's view-projection and frame number, to tell a continuous frame.
        private Matrix4x4 previousViewProjection;
        private int previousFrame = -10;
        // Frames left out in this window for an origin shift, a reset or a gap.
        private int skippedFrames;
        // When this report window began (-1: not yet).
        private float windowStart = -1f;

        // In the scene camera's OnPreCull, after the motion vector hooks: the readbacks
        // go at the end of the capture, which has written the motion vectors by then.
        internal void Record(CommandBuffer capture, Camera camera, RenderTexture motionVectors, RenderTexture depth,
                             Vector2Int size)
        {
            if (camera == null || motionVectors == null || depth == null || !HighLogic.LoadedSceneIsFlight) return;
            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null) return;
            if (windowStart < 0f) windowStart = Time.unscaledTime;

            Matrix4x4 viewProjection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, false)
                                       * camera.worldToCameraMatrix;
            if (active != partsOf) ChooseParts(active);

            // A shift of the floating origin since the previous frame (SharedFrame,
            // begun before this): the previous frame's matrices no longer fit.
            bool continuous = previousFrame == Time.frameCount - 1 && !SharedFrame.Shifted
                              && SharedFrame.RigResetReason == null;
            bool sampling = continuous && Time.frameCount % FrameSpacing == 0;
            if (!continuous) skippedFrames++;

            if (sampling)
            {
                Matrix4x4 inverse = viewProjection.inverse;
                Vector3 center = active.CoMD;
                float radius = Mathf.Max(active.vesselSize.magnitude * 0.5f, 1f);
                foreach (Part part in parts)
                {
                    Matrix4x4 before;
                    if (part == null || !previousPart.TryGetValue(part, out before)) continue;
                    Bounds bounds;
                    if (!PartBounds(part, out bounds)) continue;
                    Vector3 viewport = camera.WorldToViewportPoint(bounds.center);
                    if (viewport.z <= 0f || viewport.x <= 0f || viewport.x >= 1f || viewport.y <= 0f || viewport.y >= 1f)
                        continue;
                    Sample sample = TakeSample();
                    sample.Vessel = true;
                    sample.Name = part.partInfo != null ? part.partInfo.name : part.name;
                    sample.Viewport = viewport;
                    sample.InverseViewProjection = inverse;
                    sample.ViewProjection = viewProjection;
                    sample.PreviousViewProjection = previousViewProjection;
                    sample.Motion = before * part.transform.localToWorldMatrix.inverse;
                    sample.Bounds = bounds;
                    sample.Size = size;
                    Issue(capture, motionVectors, depth, sample);
                }
                foreach (Vector2 viewport in GroundViewport)
                {
                    Sample sample = TakeSample();
                    sample.Vessel = false;
                    sample.Name = "ground";
                    sample.Viewport = viewport;
                    sample.InverseViewProjection = inverse;
                    sample.ViewProjection = viewProjection;
                    sample.PreviousViewProjection = previousViewProjection;
                    sample.Motion = Matrix4x4.identity;
                    sample.VesselCenter = center;
                    sample.VesselRadius = radius;
                    sample.Size = size;
                    Issue(capture, motionVectors, depth, sample);
                }
            }

            if (continuous)
            {
                // Viewport positions: GL.GetGPUProjectionMatrix(..., false) changes only
                // the depth, so x and y compare with Unity's matrix as they are.
                Matrix4x4 unityPrevious = camera.previousViewProjectionMatrix;
                Vector3 ahead = camera.transform.position + camera.transform.forward * 1000f;
                float off = Mathf.Max(
                    Vector2.Distance(Viewport(unityPrevious, active.CoMD), Viewport(previousViewProjection, active.CoMD)),
                    Vector2.Distance(Viewport(unityPrevious, ahead), Viewport(previousViewProjection, ahead)));
                unityPreviousOff = Mathf.Max(unityPreviousOff, float.IsNaN(off) ? float.MaxValue : off * size.x);
                unityPreviousFrames++;
            }
            reportCamera = camera;

            foreach (Part part in parts)
                if (part != null) previousPart[part] = part.transform.localToWorldMatrix;
            previousViewProjection = viewProjection;
            previousFrame = Time.frameCount;

            if (Time.unscaledTime - windowStart >= ReportSeconds) Report();
        }

        // The parts to sample on a newly active vessel: its root, then the farthest from its centre of mass.
        private void ChooseParts(Vessel active)
        {
            partsOf = active;
            parts.Clear();
            previousPart.Clear();
            if (active.rootPart != null) parts.Add(active.rootPart);
            Vector3 center = active.CoMD;
            List<Part> others = new List<Part>(active.parts);
            others.Remove(active.rootPart);
            others.Sort((a, b) => (b.transform.position - center).sqrMagnitude
                .CompareTo((a.transform.position - center).sqrMagnitude));
            for (int i = 0; i < others.Count && parts.Count < VesselPoints; i++) parts.Add(others[i]);
        }

        // The world bounds of a part's enabled renderers, particles aside, grown by BoundsMargin; false if it has none.
        private static bool PartBounds(Part part, out Bounds bounds)
        {
            bounds = default(Bounds);
            bool any = false;
            part.GetComponentsInChildren(false, partRenderers);
            foreach (Renderer renderer in partRenderers)
            {
                if (renderer == null || !renderer.enabled || renderer is ParticleSystemRenderer) continue;
                if (!any) bounds = renderer.bounds;
                else bounds.Encapsulate(renderer.bounds);
                any = true;
            }
            if (any) bounds.Expand(BoundsMargin * 2f);
            return any;
        }

        // Snaps the sample to its pixel and asks the capture for that pixel's depth and motion vector.
        private void Issue(CommandBuffer capture, RenderTexture motionVectors, RenderTexture depth, Sample sample)
        {
            int x = Mathf.Clamp(Mathf.FloorToInt(sample.Viewport.x * sample.Size.x), 0, sample.Size.x - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(sample.Viewport.y * sample.Size.y), 0, sample.Size.y - 1);
            // The pixel's centre, as it is read.
            sample.Viewport = new Vector2((x + 0.5f) / sample.Size.x, (y + 0.5f) / sample.Size.y);
            sample.Pending = 2;
            capture.RequestAsyncReadback(depth, 0, x, 1, y, 1, 0, 1, sample.DepthRead);
            capture.RequestAsyncReadback(motionVectors, 0, x, 1, y, 1, 0, 1, sample.MotionRead);
        }

        // The oldest idle sample once it has waited ReuseAfterFrames, cleared; otherwise a new one.
        private Sample TakeSample()
        {
            if (idleSamples.Count == 0 || Time.frameCount - idleSamples.Peek().ReleasedFrame < ReuseAfterFrames)
                return NewSample();
            Sample sample = idleSamples.Dequeue();
            sample.Clear();
            return sample;
        }

        // A sample with its two read-back callbacks, made once: each stores its value, then Finish and Release.
        private Sample NewSample()
        {
            Sample sample = new Sample();
            sample.DepthRead = request =>
            {
                if (sample.Pending <= 0) return;
                if (request.hasError) sample.Failed = true;
                else sample.Depth = request.GetData<float>()[0];
                Finish(sample);
                Release(sample);
            };
            sample.MotionRead = request =>
            {
                if (sample.Pending <= 0) return;
                if (request.hasError)
                {
                    sample.Failed = true;
                }
                else
                {
                    NativeArray<ushort> data = request.GetData<ushort>();
                    sample.Read = new Vector2(Mathf.HalfToFloat(data[0]), Mathf.HalfToFloat(data[1]));
                }
                Finish(sample);
                Release(sample);
            };
            return sample;
        }

        // Counts one read-back as back; after the second the sample goes to the idle queue.
        private void Release(Sample sample)
        {
            if (--sample.Pending != 0) return;
            sample.ReleasedFrame = Time.frameCount;
            idleSamples.Enqueue(sample);
        }

        // Once both values are in: the pixel's world position from its depth, the motion it should show, and the
        // difference to what was read into the vessel's or the ground's tally -- or a rejection where the position
        // lies at the far plane, off the part or on the vessel.
        private void Finish(Sample sample)
        {
            if (sample.Failed || float.IsNaN(sample.Depth) || float.IsNaN(sample.Read.x)) return;
            Tally tally = sample.Vessel ? vessel : ground;

            // Reversed depth on Direct3D 11: 0 is the far plane, and there nothing stands.
            if (sample.Depth <= 0f)
            {
                tally.Rejected++;
                return;
            }
            Vector4 clip = new Vector4(sample.Viewport.x * 2f - 1f, sample.Viewport.y * 2f - 1f, sample.Depth, 1f);
            Vector4 world = sample.InverseViewProjection * clip;
            if (Mathf.Abs(world.w) < 1e-12f)
            {
                tally.Rejected++;
                return;
            }
            Vector3 now = new Vector3(world.x, world.y, world.z) / world.w;
            bool belongs = sample.Vessel
                ? sample.Bounds.Contains(now)
                : (now - sample.VesselCenter).magnitude > sample.VesselRadius;
            if (!belongs)
            {
                tally.Rejected++;
                return;
            }
            Vector3 before = sample.Motion.MultiplyPoint3x4(now);
            Vector2 expected = Viewport(sample.ViewProjection, now) - Viewport(sample.PreviousViewProjection, before);
            Vector2 pixels = new Vector2(sample.Size.x, sample.Size.y);
            Vector2 error = Vector2.Scale(sample.Read - expected, pixels);
            float errorLength = error.magnitude;

            tally.Count++;
            tally.ErrorSum += errorLength;
            tally.MotionSum += Vector2.Scale(expected, pixels).magnitude;
            if (errorLength >= tally.ErrorMax)
            {
                tally.ErrorMax = errorLength;
                tally.Worst = sample.Name + " read " + Show(Vector2.Scale(sample.Read, pixels)) + " expected "
                              + Show(Vector2.Scale(expected, pixels)) + " px";
            }
        }

        // Where a world position lies on the screen through a view-projection, in viewport coordinates (0 to 1).
        private static Vector2 Viewport(Matrix4x4 viewProjection, Vector3 position)
        {
            Vector4 clip = viewProjection * new Vector4(position.x, position.y, position.z, 1f);
            return new Vector2(clip.x / clip.w * 0.5f + 0.5f, clip.y / clip.w * 0.5f + 0.5f);
        }

        // Logs the window's results, the motion sources too when the ground is far off, and starts a new window.
        private void Report()
        {
            float elapsed = Time.unscaledTime - windowStart;
            windowStart = Time.unscaledTime;
            if (vessel.Count + vessel.Rejected + ground.Count + ground.Rejected == 0)
            {
                skippedFrames = 0;
                return;
            }
            StringBuilder sb = new StringBuilder();
            sb.Append(Log.Tag).Append(" Motion vectors against the true motion, last ")
              .Append(elapsed.ToString("0.0", CultureInfo.InvariantCulture)).Append(" s: vessel ");
            Append(sb, vessel);
            sb.Append("; ground ");
            Append(sb, ground);
            sb.Append("; ").Append(skippedFrames).Append(" frame(s) left out for an origin shift or a reset");
            if (unityPreviousFrames > 0)
                sb.Append("; Unity's previous camera off by up to ")
                  .Append(unityPreviousOff.ToString("0.00", CultureInfo.InvariantCulture)).Append(" px");
            sb.Append('.');
            Debug.Log(sb.ToString());
            if (ground.Count > 0 && ground.ErrorSum / ground.Count > SourcesFromError
                && Time.unscaledTime >= nextSources && reportCamera != null)
            {
                nextSources = Time.unscaledTime + SourcesInterval;
                CameraSurvey.WriteMotionSources(reportCamera);
            }
            unityPreviousOff = 0f;
            unityPreviousFrames = 0;
            vessel.Clear();
            ground.Clear();
            skippedFrames = 0;
        }

        // One tally's part of the log line: count, mean and largest error, mean motion, worst sample, rejections.
        private static void Append(StringBuilder sb, Tally tally)
        {
            sb.Append(tally.Count).Append(" sample(s)");
            if (tally.Count > 0)
            {
                sb.Append(", error mean ").Append((tally.ErrorSum / tally.Count).ToString("0.00", CultureInfo.InvariantCulture))
                  .Append(" px, max ").Append(tally.ErrorMax.ToString("0.00", CultureInfo.InvariantCulture))
                  .Append(" px, motion mean ").Append((tally.MotionSum / tally.Count).ToString("0.00", CultureInfo.InvariantCulture))
                  .Append(" px, worst: ").Append(tally.Worst);
            }
            sb.Append(", ").Append(tally.Rejected).Append(" not on it");
        }

        // A vector as "(x, y)" with two decimals, for the log.
        private static string Show(Vector2 value)
        {
            return "(" + value.x.ToString("0.00", CultureInfo.InvariantCulture) + ", "
                   + value.y.ToString("0.00", CultureInfo.InvariantCulture) + ")";
        }
    }
}
