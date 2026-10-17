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
    // So only for the length of the frame's drawing: as the first camera culls, the
    // vessel's topmost part transforms are moved by the offset from the last step's
    // position to the one interpolated for this frame, (previous - current) *
    // (1 - alpha), alpha being the part of a step since the last one, as Unity
    // interpolates; as the next frame begins, before its first physics step and its
    // first Update, their local positions go back exactly as they were. Translation
    // only.
    //
    // The camera is never moved by itself. Following the vessel, KSP hangs its pivot
    // on the vessel (FlightCamera: pivot.SetParent(target)), so it moves along with the
    // parts; a camera that does not hang on the vessel -- one standing free, CameraTools'
    // -- does not follow it and stays where it is. Other vessels are left as KSP draws
    // them.
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
        private const float JumpMetres = 50f;
        private const float ReportSeconds = 10f;

        // The Debug switch; on at every start, off with -redefinitionProbeNoInterpolation.
        internal static bool Enabled = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-redefinitionProbeNoInterpolation") < 0;

        private struct Moved
        {
            internal Transform Transform;
            internal Vector3 LocalPosition;
        }

        private readonly List<Moved> moved = new List<Moved>();
        private readonly HashSet<Transform> partTransforms = new HashSet<Transform>();
        private readonly StepInterpolator interpolator = new StepInterpolator();
        private Vessel tracked;
        private Vector3d reference;
        // The reference follows the vessel once it is this far from it.
        private const float RebaseMetres = 1000f;
        private int appliedFrame = -1;
        private Vector3 offset;
        private bool offsetValid;

        private int framesShifted;
        private int framesWithoutStep;
        private int framesSeen;
        private double offsetSum;
        private float offsetMax;
        private float windowStart = -1f;

        private static RenderInterpolation instance;
        private Camera shiftCamera;
        private bool cameraMoved;

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

        private void OnDestroy()
        {
            Camera.onPreCull -= OnAnyPreCull;
            TimingManager.FixedUpdateRemove(TimingManager.TimingStage.ObscenelyEarly, Restore);
            TimingManager.UpdateRemove(TimingManager.TimingStage.ObscenelyEarly, Restore);
            Restore();
            if (instance == this) instance = null;
        }

        // After physics, before LateUpdate: where the last step left the vessel, and
        // where the one before it did.
        private void Update()
        {
            offsetValid = false;
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (!Enabled || !UpscalerRig.Feeding || vessel == null || vessel.packed || vessel.rootPart == null
                || vessel.mainBody == null || CameraManager.Instance == null
                || CameraManager.Instance.currentCameraMode != CameraManager.CameraMode.Flight)
            {
                interpolator.Reset();
                return;
            }

            // Relative to the body: Krakensbane, above a few metres a second, keeps the
            // vessel near the origin and moves the world instead, the body with it, and a
            // floating origin shift moves both; the vessel's way against the world is the
            // same either way. In doubles, against a reference that follows the vessel, so
            // that what the interpolator holds stays small.
            Vector3d relative = (Vector3d)vessel.rootPart.transform.position - vessel.mainBody.position;
            float jump = Mathf.Max(JumpMetres, (float)vessel.obt_speed * Time.fixedDeltaTime * 4f);
            if (vessel != tracked || !interpolator.Known)
            {
                tracked = vessel;
                reference = relative;
                interpolator.Reset();
            }
            Vector3 position = (Vector3)(relative - reference);
            if ((position - interpolator.Current).magnitude > jump)
            {
                reference = relative;
                position = Vector3.zero;
                interpolator.Reset();
            }
            else if (position.sqrMagnitude > RebaseMetres * RebaseMetres)
            {
                interpolator.Rebase(position);
                reference += (Vector3d)position;
                position = Vector3.zero;
            }
            offset = interpolator.Advance(position, Time.time, Time.fixedTime, Time.fixedDeltaTime);
            framesSeen++;
            if (interpolator.Steps == 0) framesWithoutStep++;
            offsetValid = true;
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
            if (!offsetValid || tracked == null) return;
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

        private void Apply()
        {
            partTransforms.Clear();
            foreach (Part part in tracked.parts)
                if (part != null) partTransforms.Add(part.transform);

            foreach (Transform t in partTransforms)
                if (!UnderAnother(t)) Move(t);

            framesShifted++;
            float length = offset.magnitude;
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

        private void Move(Transform t)
        {
            moved.Add(new Moved { Transform = t, LocalPosition = t.localPosition });
            t.position += offset;
        }

        // How far the interpolation moved a camera this frame: the offset where the camera
        // hangs below a moved part, as KSP's flight camera does, nothing otherwise.
        internal static Vector3 CameraShift(Camera camera)
        {
            RenderInterpolation current = instance;
            if (current == null || camera == null || current.moved.Count == 0 || current.appliedFrame != Time.frameCount)
                return Vector3.zero;
            if (current.shiftCamera != camera)
            {
                current.shiftCamera = camera;
                current.cameraMoved = false;
                foreach (Moved m in current.moved)
                    if (m.Transform != null && camera.transform.IsChildOf(m.Transform))
                    {
                        current.cameraMoved = true;
                        break;
                    }
            }
            return current.cameraMoved ? current.offset : Vector3.zero;
        }

        // The exact local positions back, in reverse order, before the next physics step.
        private void Restore()
        {
            for (int i = moved.Count - 1; i >= 0; i--)
                if (moved[i].Transform != null) moved[i].Transform.localPosition = moved[i].LocalPosition;
            moved.Clear();
            shiftCamera = null;
        }

        private void Report()
        {
            float elapsed = Time.unscaledTime - windowStart;
            windowStart = Time.unscaledTime;
            if (framesShifted > 0)
                Debug.Log(Log.Tag + " Render interpolation, last " + elapsed.ToString("0.0", CultureInfo.InvariantCulture)
                          + " s: " + framesShifted + " frame(s) drawn between the physics steps, "
                          + framesWithoutStep + " of " + framesSeen + " without a step of their own; offset mean "
                          + (offsetSum / framesShifted).ToString("0.000", CultureInfo.InvariantCulture) + " m, max "
                          + offsetMax.ToString("0.000", CultureInfo.InvariantCulture) + " m.");
            framesShifted = framesWithoutStep = framesSeen = 0;
            offsetSum = 0;
            offsetMax = 0f;
        }
    }
}
