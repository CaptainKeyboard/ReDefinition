using System.Collections.Generic;
using System.Globalization;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // The active vessel drawn where it is between the last two physics steps rather
    // than where the last step left it.
    //
    // KSP steps physics at 50 Hz and sets no Rigidbody.interpolation on the parts
    // (decompiled), so the vessel, and the camera riding on it, move only on a physics
    // step. At about 59 rendered frames a second, one in six has no step: the picture
    // stands still for it and moves on with the next (framesWithoutStep). Engines draw a
    // rigidbody between its last two steps for
    // this reason (Unity's own interpolation). That one KSP cannot have: its physics reads
    // part positions from the transforms, which would then lag by up to a step.
    //
    // So the parts are moved only for the length of the frame's drawing (Apply, Restore).
    // Translation only.
    //
    // The camera is never moved by itself (CameraShift).
    //
    // The loaded vessels near the active one are drawn between their steps as well, each on its own way
    // (VesselMotionVectors.NearVessels, tracks). The vessel's way is taken against its main body (Follow).
    // Left alone: a jump of more than four steps' worth of its
    // speed or 50 m (JumpMetres), a packed vessel, IVA and map view. Only
    // while the rig hands frames to an upscaler or to frame generation (UpscalerRig.Feeding).
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    [DefaultExecutionOrder(32000)]
    public sealed class RenderInterpolation : MonoBehaviour
    {
        // The least jump between two frames, in metres, that resets a vessel's track instead of drawing between steps.
        // Follow raises it to four steps' worth of the vessel's speed where that is more.
        private const float JumpMetres = 50f;
        // Seconds between two log lines.
        private const float ReportSeconds = 10f;

        // A vessel's parts were moved by an offset for this frame (Apply), and the parts are back (Restore): for what hangs
        // beside the vessel without hanging on it, as Firefly's airstream camera does (FireflyInterpolation).
        internal static event System.Action<Vessel, Vector3> Shifted;
        internal static event System.Action Restored;

        // The Debug switch; on at every start, off with -redefinitionProbeNoInterpolation.
        internal static bool Enabled = !CommandLine.Has("-redefinitionProbeNoInterpolation");

        // A part transform moved for this frame: its local position before, to restore exactly, and the world offset.
        private struct Moved
        {
            // The transform, its local position before the move, the world offset it was moved by.
            internal Transform Transform;
            internal Vector3 LocalPosition;
            internal Vector3 Offset;
        }

        // One vessel's way between its steps, against a reference that follows it.
        private sealed class Track
        {
            // The vessel; its steps' positions relative to Reference; Reference, the root's position against the
            // main body that positions are taken from; this frame's offset; the frame the track was last followed in.
            internal Vessel Vessel;
            internal readonly StepInterpolator Interpolator = new StepInterpolator();
            internal Vector3d Reference;
            internal Vector3 Offset;
            internal int Frame;

            // The vessel's topmost part transforms (Apply moves these), and what they were worked out from: the part
            // count, the root part and the root's parent, and the frame. -1 for none yet.
            internal readonly List<Transform> Topmost = new List<Transform>();
            internal int TopmostParts = -1;
            internal Part TopmostRoot;
            internal Transform TopmostRootParent;
            internal int TopmostFrame;
            // The part count and root trackOfPart was last filled from for this track; -1 for never.
            internal int MappedParts = -1;
            internal Part MappedRoot;
        }

        // The topmost transforms are worked out again after this many frames at the latest, whatever the cheap
        // checks say: a part moved under another with the part count kept would otherwise stay unseen.
        private const int RebuildFrames = 120;

        // The transforms moved this frame, in the order they were moved.
        private readonly List<Moved> moved = new List<Moved>();
        // One vessel's part transforms at a time, for finding the topmost (Topmost).
        private readonly HashSet<Transform> partTransforms = new HashSet<Transform>();
        // The tracks of the active vessel and the vessels near it, by vessel. The near ones are what the active
        // vessel dropped: boosters, fairings, a stage, which KSP makes vessels of their own as they part. Left on
        // their steps while the active vessel and the camera were not, they jumped against both by
        // up to a step's travel, back and forth from frame to frame. A dropped booster at
        // 350 m/s jumped by up to 7 m, in the rendered frames already and doubled by frame
        // generation (the staging probe, Kerbal X with DLSS frame generation, 2026-10-25).
        private readonly Dictionary<Vessel, Track> tracks = new Dictionary<Vessel, Track>();
        // Which track each part was drawn with the frame before, for a vessel that has just parted. Such a
        // vessel goes on from the steps of the one it was part of, so the frame of the split moves it as the
        // frame before did.
        private readonly Dictionary<Part, Track> trackOfPart = new Dictionary<Part, Track>();
        // Whether trackOfPart must be filled again: a track came or went, or a frame count ran out. Otherwise it is
        // filled again only when a followed vessel's part count or root changed (Update).
        private bool mappingStale = true;
        private int mappedFrame;
        private readonly List<Vessel> near = new List<Vessel>();
        private readonly List<Vessel> gone = new List<Vessel>();
        // The active vessel's track, this frame's; null while nothing is drawn between the steps.
        private Track active;
        // The reference follows the vessel once it is this far from it.
        private const float RebaseMetres = 1000f;
        private int appliedFrame = -1;

        // The log window's counts: the most vessels near the active one, frames drawn between steps, frames without
        // a step of their own of all seen, the active vessel's offset summed and largest, and when the window began.
        // Measured with the screen recording on the runway: the ground's shift between shown frames 4 to 7 px,
        // then twice 0, about nine times a second. Frame generation's worst frames were the ones right after such
        // a standstill.
        private int othersMax;
        private int framesShifted;
        private int framesWithoutStep;
        private int framesSeen;
        private double offsetSum;
        private float offsetMax;
        private float windowStart = -1f;

        // The running addon, for CameraShift.
        private static RenderInterpolation instance;
        // The camera CameraShift last answered for this frame, and its answer.
        private Camera shiftCamera;
        private Vector3 cameraShift;

        // Hooks the first camera's cull and the restore before the next frame's physics and Update.
        private void Start()
        {
            instance = this;
            Camera.onPreCull += OnAnyPreCull;
            // ObscenelyEarly runs in Timing0, at execution order -8008, before
            // FloatingOrigin (21) and every other script that reads the parts
            // (globalgamemanagers.assets, MonoScript.m_ExecutionOrder).
            TimingManager.FixedUpdateAdd(TimingManager.TimingStage.ObscenelyEarly, Restore);
            TimingManager.UpdateAdd(TimingManager.TimingStage.ObscenelyEarly, Restore);
        }

        // Unhooks and puts any moved parts back.
        private void OnDestroy()
        {
            Camera.onPreCull -= OnAnyPreCull;
            TimingManager.FixedUpdateRemove(TimingManager.TimingStage.ObscenelyEarly, Restore);
            TimingManager.UpdateRemove(TimingManager.TimingStage.ObscenelyEarly, Restore);
            Restore();
            if (instance == this) instance = null;
        }

        // After physics, before LateUpdate: where the last step left each vessel, and
        // where the one before it did.
        private void Update()
        {
            active = null;
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (!Enabled || !UpscalerRig.Feeding || vessel == null || vessel.packed || vessel.rootPart == null
                || vessel.mainBody == null || CameraManager.Instance == null
                || CameraManager.Instance.currentCameraMode != CameraManager.CameraMode.Flight)
            {
                tracks.Clear();
                trackOfPart.Clear();
                mappingStale = true;
                return;
            }

            VesselMotionVectors.NearVessels(vessel, near);
            int others = 0;
            foreach (Vessel v in near)
            {
                if (v.rootPart == null || v.mainBody == null) continue;
                Track track = Follow(v);
                if (v == vessel) active = track;
                else others++;
            }
            gone.Clear();
            foreach (KeyValuePair<Vessel, Track> pair in tracks)
                if (pair.Value.Frame != Time.frameCount) gone.Add(pair.Key);
            foreach (Vessel v in gone) tracks.Remove(v);
            if (gone.Count > 0) mappingStale = true;
            MapParts();

            if (others > othersMax) othersMax = others;
            framesSeen++;
            if (active != null && active.Interpolator.Steps == 0) framesWithoutStep++;
        }

        // trackOfPart, filled from the followed vessels' parts where they changed since it was: a track came or went, a
        // vessel's part count or root part is another, or RebuildFrames passed. Follow reads the mapping of the frame
        // before, so a frame in which a vessel parted still sees the old one: this runs after the Follow calls.
        private void MapParts()
        {
            if (Time.frameCount - mappedFrame >= RebuildFrames) mappingStale = true;
            if (!mappingStale)
            {
                foreach (Track track in tracks.Values)
                    if (track.MappedParts != track.Vessel.parts.Count || track.MappedRoot != track.Vessel.rootPart)
                    {
                        mappingStale = true;
                        break;
                    }
            }
            if (!mappingStale) return;

            mappingStale = false;
            mappedFrame = Time.frameCount;
            trackOfPart.Clear();
            foreach (Track track in tracks.Values)
            {
                foreach (Part part in track.Vessel.parts)
                    if (part != null) trackOfPart[part] = track;
                track.MappedParts = track.Vessel.parts.Count;
                track.MappedRoot = track.Vessel.rootPart;
            }
        }

        // The vessel's offset for this frame. Relative to the body: Krakensbane, above a
        // few metres a second, keeps the vessel near the origin and moves the world
        // instead, the body with it, and a floating origin shift moves both. The vessel's
        // way against the world is the same either way. Without this, the jump rule left the vessel alone in
        // flight above a few metres a second: in the log it was never at work in flight, 2026-10-05. In doubles,
        // against a reference that follows the vessel, so that what the interpolator holds stays small.
        private Track Follow(Vessel vessel)
        {
            Vector3d relative = (Vector3d)vessel.rootPart.transform.position - vessel.mainBody.position;
            Track track;
            if (!tracks.TryGetValue(vessel, out track))
            {
                track = new Track { Vessel = vessel, Reference = relative };
                tracks[vessel] = track;
                mappingStale = true;
                Track before;
                if (trackOfPart.TryGetValue(vessel.rootPart, out before) && before.Vessel != null && before.Vessel.rootPart != null
                    && before.Vessel.mainBody == vessel.mainBody && before.Interpolator.Known)
                {
                    // Parted from `before` since the frame before: its steps, held against where its root
                    // part stands from this one's now, which the steps moved the two together to.
                    track.Interpolator.CopyFrom(before.Interpolator);
                    Vector3d beforeRelative = (Vector3d)before.Vessel.rootPart.transform.position - vessel.mainBody.position;
                    track.Reference = before.Reference + (relative - beforeRelative);
                }
            }
            track.Frame = Time.frameCount;
            float jump = Mathf.Max(JumpMetres, (float)vessel.obt_speed * Time.fixedDeltaTime * 4f);
            if (!track.Interpolator.Known) track.Reference = relative;
            Vector3 position = (Vector3)(relative - track.Reference);
            if ((position - track.Interpolator.Current).magnitude > jump)
            {
                track.Reference = relative;
                position = Vector3.zero;
                track.Interpolator.Reset();
            }
            else if (position.sqrMagnitude > RebaseMetres * RebaseMetres)
            {
                track.Interpolator.Rebase(position);
                track.Reference += (Vector3d)position;
                position = Vector3.zero;
            }
            track.Offset = track.Interpolator.Advance(position, Time.time, Time.fixedTime, Time.fixedDeltaTime);
            return track;
        }

        // After every other LateUpdate (the execution order above). With the parts put
        // back at the end of the frame, Unity's previous matrices for the motion vectors
        // were the physics step's while the frame was drawn between steps. In orbit that gave
        // offsets of up to 43 m, the vessel's motion vectors hundreds of pixels off in
        // five of seven frames, and frame generation's frames smeared
        // (tools/analyze_fg_inputs.py). Put back as the next frame begins, the vectors
        // fit in every frame.
        private void LateUpdate()
        {
            OnAnyPreCull(null);
        }

        // As the frame's first camera culls, where the LateUpdate above did not run. Either way the parts are moved
        // after every other LateUpdate or at the first cull.
        private void OnAnyPreCull(Camera camera)
        {
            if (appliedFrame == Time.frameCount) return;
            appliedFrame = Time.frameCount;
            if (active == null) return;
            try
            {
                Apply();
            }
            catch (System.Exception e)
            {
                Restore();
                Enabled = false;
                Debug.LogWarning(Log.Tag + " Render interpolation stopped: " + e.Message);
            }
        }

        // Moves the topmost part transforms of every vessel followed this frame by its track's offset, and counts the
        // active vessel's offset for the log. The offset is from the last step's position to the one interpolated
        // for this frame, (previous - current) * (1 - alpha), alpha being the part of a step since the last one, as
        // Unity interpolates.
        private void Apply()
        {
            foreach (Track track in tracks.Values)
            {
                if (track.Frame != Time.frameCount || track.Vessel == null) continue;
                List<Transform> topmost = Topmost(track);
                for (int i = 0; i < topmost.Count; i++)
                    if (topmost[i] != null) Move(topmost[i], track.Offset);
                Shifted?.Invoke(track.Vessel, track.Offset);
            }

            framesShifted++;
            float length = active.Offset.magnitude;
            offsetSum += length;
            if (length > offsetMax) offsetMax = length;
            if (windowStart < 0f) windowStart = Time.unscaledTime;
            if (Time.unscaledTime - windowStart >= ReportSeconds) Report();
        }

        // The vessel's part transforms that have none of its other part transforms above them, in part order.
        // They are kept with the track. They are worked out again when the part count, the root part or the
        // root's parent changes, or after RebuildFrames.
        private List<Transform> Topmost(Track track)
        {
            Vessel vessel = track.Vessel;
            Part root = vessel.rootPart;
            Transform rootParent = root != null ? root.transform.parent : null;
            int count = vessel.parts.Count;
            if (track.TopmostParts == count && track.TopmostRoot == root && track.TopmostRootParent == rootParent
                && Time.frameCount - track.TopmostFrame < RebuildFrames)
                return track.Topmost;

            partTransforms.Clear();
            foreach (Part part in vessel.parts)
                if (part != null) partTransforms.Add(part.transform);
            track.Topmost.Clear();
            foreach (Transform t in partTransforms)
                if (!UnderAnother(t)) track.Topmost.Add(t);
            track.TopmostParts = count;
            track.TopmostRoot = root;
            track.TopmostRootParent = rootParent;
            track.TopmostFrame = Time.frameCount;
            return track.Topmost;
        }

        // Whether one of the vessel's part transforms is above it: moving that one moves
        // this one along.
        private bool UnderAnother(Transform t)
        {
            for (Transform p = t.parent; p != null; p = p.parent)
                if (partTransforms.Contains(p)) return true;
            return false;
        }

        // Moves a transform by a world offset, noting its local position for Restore.
        private void Move(Transform t, Vector3 offset)
        {
            moved.Add(new Moved { Transform = t, LocalPosition = t.localPosition, Offset = offset });
            t.position += offset;
        }

        // How far the interpolation moved a camera this frame: the offset of the moved part
        // the camera hangs below, as KSP's flight camera does, nothing otherwise. The camera is never moved by
        // itself. Following the vessel, KSP hangs its pivot on the vessel (FlightCamera: pivot.SetParent(target)),
        // so it moves along with the parts. A camera that does not hang on the vessel, one standing free like
        // CameraTools', does not follow it and stays where it is.
        internal static Vector3 CameraShift(Camera camera)
        {
            RenderInterpolation current = instance;
            if (current == null || camera == null || current.moved.Count == 0 || current.appliedFrame != Time.frameCount)
                return Vector3.zero;
            if (current.shiftCamera != camera)
            {
                current.shiftCamera = camera;
                current.cameraShift = Vector3.zero;
                foreach (Moved m in current.moved)
                    if (m.Transform != null && camera.transform.IsChildOf(m.Transform))
                    {
                        current.cameraShift = m.Offset;
                        break;
                    }
            }
            return current.cameraShift;
        }

        // The exact local positions back, in reverse order, as the next frame begins: before its first physics step
        // and its first Update.
        private void Restore()
        {
            for (int i = moved.Count - 1; i >= 0; i--)
                if (moved[i].Transform != null) moved[i].Transform.localPosition = moved[i].LocalPosition;
            moved.Clear();
            shiftCamera = null;
            Restored?.Invoke();
        }

        // Logs the window's counts, if any frame was drawn between steps, and starts a new window.
        private void Report()
        {
            float elapsed = Time.unscaledTime - windowStart;
            windowStart = Time.unscaledTime;
            if (framesShifted > 0)
                Debug.Log(Log.Tag + " Render interpolation, last " + elapsed.ToString("0.0", CultureInfo.InvariantCulture)
                          + " s: " + framesShifted + " frame(s) drawn between the physics steps, "
                          + framesWithoutStep + " of " + framesSeen + " without a step of their own; offset mean "
                          + (offsetSum / framesShifted).ToString("0.000", CultureInfo.InvariantCulture) + " m, max "
                          + offsetMax.ToString("0.000", CultureInfo.InvariantCulture) + " m; up to " + othersMax
                          + " vessel(s) near it with it.");
            framesShifted = framesWithoutStep = framesSeen = othersMax = 0;
            offsetSum = 0;
            offsetMax = 0f;
        }
    }
}
