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
    // stands still for it and moves on with the next. Measured with the screen recording
    // on the runway: the ground's shift between shown frames 4 to 7 px, then twice 0,
    // about nine times a second; frame generation's worst frames were the ones right
    // after such a standstill. Engines draw a rigidbody between its last two steps for
    // this reason (Unity's own interpolation). That one KSP cannot have: its physics reads
    // part positions from the transforms, which would then lag by up to a step.
    //
    // So only for the length of the frame's drawing: after every other LateUpdate (or,
    // where that did not run, as the first camera culls), the vessel's topmost part
    // transforms are moved by the offset from the last step's
    // position to the one interpolated for this frame, (previous - current) *
    // (1 - alpha), alpha being the part of a step since the last one, as Unity
    // interpolates; as the next frame begins, before its first physics step and its
    // first Update, their local positions go back exactly as they were. Translation
    // only.
    //
    // The camera is never moved by itself. Following the vessel, KSP hangs its pivot
    // on the vessel (FlightCamera: pivot.SetParent(target)), so it moves along with the
    // parts; a camera that does not hang on the vessel -- one standing free, CameraTools'
    // -- does not follow it and stays where it is.
    //
    // The loaded vessels near the active one -- what it dropped: boosters, fairings, a
    // stage, which KSP makes vessels of their own as they part -- are drawn between their
    // steps as well, each on its own way (VesselMotionVectors.NearVessels). Left on their
    // steps while the active vessel and the camera were not, they jumped against both by
    // up to a step's travel, back and forth from frame to frame: a dropped booster at
    // 350 m/s by up to 7 m, in the rendered frames already and doubled by frame
    // generation (the staging probe, Kerbal X with DLSS frame generation, 2026-10-25). A
    // vessel that has just parted goes on from the steps of the one it was part of, so
    // the frame of the split moves it as the frame before did.
    //
    // The vessel's way is taken against its main body, so that it holds where Krakensbane
    // moves the world instead of the vessel (in flight above a few metres a second, where
    // it was left alone before: in the log never at work in flight, 2026-10-05) and over a
    // floating origin shift. Left alone: a jump of more than four steps' worth of its
    // speed or 50 m, a packed vessel, IVA and map view. Only
    // while the rig hands frames to an upscaler or to frame generation (UpscalerRig.Feeding).
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    [DefaultExecutionOrder(32000)]
    public sealed class RenderInterpolation : MonoBehaviour
    {
        // The least jump between two frames, in metres, that resets a vessel's track instead of drawing between steps.
        private const float JumpMetres = 50f;
        // Seconds between two log lines.
        private const float ReportSeconds = 10f;

        // The Debug switch; on at every start, off with -redefinitionProbeNoInterpolation.
        internal static bool Enabled = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-redefinitionProbeNoInterpolation") < 0;

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
        }

        // The transforms moved this frame, in the order they were moved.
        private readonly List<Moved> moved = new List<Moved>();
        // One vessel's part transforms at a time, for finding the topmost (Apply).
        private readonly HashSet<Transform> partTransforms = new HashSet<Transform>();
        // The tracks of the active vessel and the vessels near it, by vessel.
        private readonly Dictionary<Vessel, Track> tracks = new Dictionary<Vessel, Track>();
        // Which track each part was drawn with the frame before, for a vessel that has just parted.
        private readonly Dictionary<Part, Track> trackOfPart = new Dictionary<Part, Track>();
        private readonly List<Vessel> near = new List<Vessel>();
        private readonly List<Vessel> gone = new List<Vessel>();
        // The active vessel's track, this frame's; null while nothing is drawn between the steps.
        private Track active;
        // The reference follows the vessel once it is this far from it.
        private const float RebaseMetres = 1000f;
        private int appliedFrame = -1;

        // The log window's counts: the most vessels near the active one, frames drawn between steps, frames without
        // a step of their own of all seen, the active vessel's offset summed and largest, and when the window began.
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
            trackOfPart.Clear();
            foreach (Track track in tracks.Values)
                foreach (Part part in track.Vessel.parts)
                    if (part != null) trackOfPart[part] = track;

            if (others > othersMax) othersMax = others;
            framesSeen++;
            if (active != null && active.Interpolator.Steps == 0) framesWithoutStep++;
        }

        // The vessel's offset for this frame. Relative to the body: Krakensbane, above a
        // few metres a second, keeps the vessel near the origin and moves the world
        // instead, the body with it, and a floating origin shift moves both; the vessel's
        // way against the world is the same either way. In doubles, against a reference
        // that follows the vessel, so that what the interpolator holds stays small.
        private Track Follow(Vessel vessel)
        {
            Vector3d relative = (Vector3d)vessel.rootPart.transform.position - vessel.mainBody.position;
            Track track;
            if (!tracks.TryGetValue(vessel, out track))
            {
                track = new Track { Vessel = vessel, Reference = relative };
                tracks[vessel] = track;
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
        // were the physics step's while the frame was drawn between steps: in orbit,
        // offsets of up to 43 m, the vessel's motion vectors hundreds of pixels off in
        // five of seven frames, and frame generation's frames smeared
        // (tools/analyze_fg_inputs.py). Put back as the next frame begins, the vectors
        // fit in every frame.
        private void LateUpdate()
        {
            OnAnyPreCull(null);
        }

        // As the frame's first camera culls, where the LateUpdate above did not run.
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
        // active vessel's offset for the log.
        private void Apply()
        {
            foreach (Track track in tracks.Values)
            {
                if (track.Frame != Time.frameCount || track.Vessel == null) continue;
                partTransforms.Clear();
                foreach (Part part in track.Vessel.parts)
                    if (part != null) partTransforms.Add(part.transform);

                foreach (Transform t in partTransforms)
                    if (!UnderAnother(t)) Move(t, track.Offset);
            }

            framesShifted++;
            float length = active.Offset.magnitude;
            offsetSum += length;
            if (length > offsetMax) offsetMax = length;
            if (windowStart < 0f) windowStart = Time.unscaledTime;
            if (Time.unscaledTime - windowStart >= ReportSeconds) Report();
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
        // the camera hangs below, as KSP's flight camera does, nothing otherwise.
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

        // The exact local positions back, in reverse order, before the next physics step.
        private void Restore()
        {
            for (int i = moved.Count - 1; i >= 0; i--)
                if (moved[i].Transform != null) moved[i].Transform.localPosition = moved[i].LocalPosition;
            moved.Clear();
            shiftCamera = null;
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
