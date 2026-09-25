using System.Collections;
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
    // interpolates; at the end of the frame their local positions go back exactly as
    // they were, before the next physics step. Translation only.
    //
    // The camera is never moved by itself. Following the vessel, KSP hangs its pivot
    // on the vessel (FlightCamera: pivot.SetParent(target)), so it moves along with the
    // parts; a camera that does not hang on the vessel -- one standing free, CameraTools'
    // -- does not follow it and stays where it is. Other vessels are left as KSP draws
    // them.
    //
    // Left alone: a frame with a floating origin shift, a jump of more than 50 m, a packed
    // vessel, Krakensbane moving the world instead of the vessel, IVA and map view.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class RenderInterpolation : MonoBehaviour
    {
        private const float JumpMetres = 50f;
        private const float ReportSeconds = 10f;

        // The Debug switch; on at every start.
        internal static bool Enabled = true;

        private struct Moved
        {
            internal Transform Transform;
            internal Vector3 LocalPosition;
        }

        private readonly List<Moved> moved = new List<Moved>();
        private readonly HashSet<Transform> partTransforms = new HashSet<Transform>();
        private readonly StepInterpolator interpolator = new StepInterpolator();
        private Vessel tracked;
        private bool shifted;
        private int appliedFrame = -1;
        private Vector3 offset;
        private bool offsetValid;

        private int framesShifted;
        private int framesWithoutStep;
        private int framesSeen;
        private double offsetSum;
        private float offsetMax;
        private float windowStart = -1f;

        private void Start()
        {
            GameEvents.onFloatingOriginShift.Add(OnShift);
            Camera.onPreCull += OnAnyPreCull;
            StartCoroutine(RestoreAtEndOfFrame());
        }

        private void OnDestroy()
        {
            GameEvents.onFloatingOriginShift.Remove(OnShift);
            Camera.onPreCull -= OnAnyPreCull;
            Restore();
        }

        private void OnShift(Vector3d offsetShift, Vector3d nonFrame)
        {
            shifted = true;
        }

        // After physics, before LateUpdate: where the last step left the vessel, and
        // where the one before it did.
        private void Update()
        {
            offsetValid = false;
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (!Enabled || vessel == null || vessel.packed || vessel.rootPart == null
                || CameraManager.Instance == null || CameraManager.Instance.currentCameraMode != CameraManager.CameraMode.Flight
                || Krakensbane.GetFrameVelocity().sqrMagnitude > 0.0)
            {
                interpolator.Reset();
                return;
            }

            Vector3 position = vessel.rootPart.transform.position;
            if (vessel != tracked || !interpolator.Known || shifted
                || (position - interpolator.Current).magnitude > JumpMetres)
            {
                tracked = vessel;
                shifted = false;
                interpolator.Reset();
            }
            offset = interpolator.Advance(position, Time.time, Time.fixedTime, Time.fixedDeltaTime);
            framesSeen++;
            if (interpolator.Steps == 0) framesWithoutStep++;
            offsetValid = true;
        }

        // As the frame's first camera culls, after every LateUpdate has placed the camera.
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

        private IEnumerator RestoreAtEndOfFrame()
        {
            WaitForEndOfFrame end = new WaitForEndOfFrame();
            while (true)
            {
                yield return end;
                Restore();
            }
        }

        // The exact local positions back, in reverse order, before the next physics step.
        private void Restore()
        {
            for (int i = moved.Count - 1; i >= 0; i--)
                if (moved[i].Transform != null) moved[i].Transform.localPosition = moved[i].LocalPosition;
            moved.Clear();
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
