using System.Collections.Generic;
using System.Text;
using ReDefinition.Bridges;
using ReDefinition.Shared;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // The rig's part for the camera: the instrument for camera motion and the
    // floating origin, and the Debug switch for fast turns. The cuts that reset the
    // history: CameraCuts and SharedFrame.
    public partial class UpscalerRig
    {
        // The Debug switch that asks the proxy for its motion vector check on every
        // fast camera turn (RecordFastTurn); off at every start.
        public bool RecordFastTurns;
        private const float FastTurnDegrees = 2f;
        // Each check reads the frame back and scans it under the proxy's lock.
        private const float TurnRecordSpacing = 5f;
        private float nextTurnRecord;
        private string pendingTurnLine;

        // An instrument: numbers for two open questions of
        // docs/reference/graphics-mod-compatibility.md, logged about every ten seconds
        // while the rig dispatches.
        //
        // The camera: in ordinary frames -- no cut, no origin shift -- its
        // largest turn (roll included) and its largest move against the
        // camera's target, each with the frame time it happened in, so a hitch
        // cannot pass for a jump. At the cuts that reset the history, the turn and the
        // jump in world space: across a change of target, a move against the
        // target compares two different targets and means nothing. This
        // measures KSP's own camera, which rides on its target; a free camera
        // such as CameraTools' holds still while the target flies on and would
        // need a measure of its own.
        //
        // The floating origin: KSP re-centres it when the vessel strays too
        // far, and with Krakensbane engaged it shifts on every physics tick
        // (FloatingOrigin.FixedUpdate and Krakensbane, decompiled). Counted in
        // rendered frames that contained a shift, next to the ticks, with the
        // re-centring offset and the Krakensbane step apart -- and, when the
        // shifts are rare, the frame numbers, which are the proxy's own: its
        // check lines read "HUD-less check (frame N ...)".
        private const float MotionReportSeconds = 10f;
        private const int ShiftFramesListed = 20;
        private float motionWindowStart = -1f;
        private bool motionSampleValid;
        private bool instrumentFailed;
        private Quaternion lastRotation;
        private Vector3 lastPosition;
        private Vector3 lastRelativePosition;
        private Transform lastSampleTarget;
        private int sampledFrames;
        private float largestTurn;
        private float largestTurnDt;
        private float largestMove;
        private float largestMoveDt;
        private int detectedCuts;
        private float largestCutTurn;
        private float largestCutJump;
        private bool shiftedSinceLastSample;
        private int shiftTicks;
        private int framesWithShift;
        private double largestOffset;
        private double largestKrakensbaneStep;
        private readonly List<int> shiftFrames = new List<int>();

        // offset moves the active vessel, the camera and nearby physics
        // objects; offset + nonFrame moves the bodies. nonFrame is the
        // Krakensbane step (FloatingOrigin.setOffset, decompiled).
        private void OnFloatingOriginShift(Vector3d offset, Vector3d nonFrame)
        {
            shiftTicks++;
            shiftedSinceLastSample = true;
            double o = offset.magnitude;
            double k = nonFrame.magnitude;
            if (o > largestOffset) largestOffset = o;
            if (k > largestKrakensbaneStep) largestKrakensbaneStep = k;
        }

        // Called at the end of Present, once the frame is out.
        private void SampleCameraMotion(bool atCut)
        {
            if (cam == null) return;
            if (motionWindowStart < 0f) motionWindowStart = Time.unscaledTime;

            Transform t = cam.transform;
            Transform target = CameraCuts.Target;   // at the frame's last look
            Quaternion rotation = t.rotation;
            Vector3 position = t.position;
            Vector3 relative = target != null ? position - target.position : position;
            float dt = Time.unscaledDeltaTime;

            bool shifted = shiftedSinceLastSample;
            shiftedSinceLastSample = false;
            if (shifted)
            {
                framesWithShift++;
                if (shiftFrames.Count < ShiftFramesListed) shiftFrames.Add(Time.frameCount);
            }

            if (motionSampleValid)
            {
                float turn = Quaternion.Angle(lastRotation, rotation);
                if (!atCut && target == lastSampleTarget) RecordFastTurn(turn, dt, shifted);
                if (atCut)
                {
                    detectedCuts++;
                    largestCutTurn = Mathf.Max(largestCutTurn, turn);
                    largestCutJump = Mathf.Max(largestCutJump, (position - lastPosition).magnitude);
                }
                else if (!shifted && target == lastSampleTarget)
                {
                    float move = (relative - lastRelativePosition).magnitude;
                    if (turn > largestTurn) { largestTurn = turn; largestTurnDt = dt; }
                    if (move > largestMove) { largestMove = move; largestMoveDt = dt; }
                }
                sampledFrames++;
            }

            lastRotation = rotation;
            lastPosition = position;
            lastRelativePosition = relative;
            lastSampleTarget = target;
            motionSampleValid = true;
        }

        // The Debug switch for the motion vectors in flight: a fast turn asks the proxy
        // for its motion vector check at once rather than on its ten-second rhythm,
        // and a line says what the camera did, with its frame number: the check starts
        // in this frame or one of the next, and the proxy's check line names the frame
        // it was written in, a few frames later. Also in frames the
        // floating origin shifted, which in flight is nearly all of them. With frame
        // generation only: the check reads its inputs. Written from Update, not here in
        // Present, where a log write would show in the frames being checked.
        private void RecordFastTurn(float turn, float dt, bool shifted)
        {
            if (!RecordFastTurns || !FrameGeneration || hudLessBuffer == null || turn < FastTurnDegrees
                || Time.unscaledTime < nextTurnRecord)
                return;
            nextTurnRecord = Time.unscaledTime + TurnRecordSpacing;
            FrameGenerationBridge.RequestCheck();
            pendingTurnLine = UpscalerProbe.Tag + " Fast turn in frame " + Time.frameCount + ": " + turn.ToString("0.0")
                              + " deg in a " + (dt * 1000f).ToString("0") + " ms frame ("
                              + (dt > 0f ? (1f / dt).ToString("0") : "?") + " fps)"
                              + (shifted ? ", floating origin shifted in this frame" : "")
                              + " -- the proxy's motion vector check follows in ReDefinitionProxy.log.";
        }

        // From Update, not from the render path: a log write there would show
        // up in the very frame times the proxy reports on the same rhythm.
        // Forced at teardown, so a rebuilt rig does not swallow its last window.
        private void ReportCameraMotionIfDue(bool force)
        {
            if (motionWindowStart < 0f) return;
            float elapsed = Time.unscaledTime - motionWindowStart;
            if (!force && elapsed < MotionReportSeconds) return;

            if (sampledFrames > 0 || shiftTicks > 0)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(UpscalerProbe.Tag).Append(" Camera and origin, last ").Append(elapsed.ToString("0.0"))
                  .Append(" s, ").Append(sampledFrames).Append(" frames. Ordinary frames: largest turn ")
                  .Append(largestTurn.ToString("0.00")).Append(" deg in a ").Append((largestTurnDt * 1000f).ToString("0"))
                  .Append(" ms frame, largest move against the target ").Append(largestMove.ToString("0.00"))
                  .Append(" m in a ").Append((largestMoveDt * 1000f).ToString("0")).Append(" ms frame");
                if (detectedCuts > 0)
                    sb.Append("; ").Append(detectedCuts).Append(" detected cut(s), up to ")
                      .Append(largestCutTurn.ToString("0.00")).Append(" deg and a ")
                      .Append(largestCutJump.ToString("0.0")).Append(" m jump");
                else
                    sb.Append("; no detected cut");
                sb.Append(". Floating origin: ").Append(shiftTicks).Append(" shift(s) in ")
                  .Append(framesWithShift).Append(" of ").Append(sampledFrames).Append(" frames");
                if (shiftTicks > 0)
                {
                    sb.Append(", largest offset ").Append(largestOffset.ToString("0.0"))
                      .Append(" m, largest Krakensbane step ").Append(largestKrakensbaneStep.ToString("0.0")).Append(" m");
                    if (framesWithShift < sampledFrames && shiftFrames.Count > 0)
                        sb.Append(", in frames ").Append(string.Join(", ", shiftFrames))
                          .Append(framesWithShift > shiftFrames.Count ? ", ..." : "");
                }
                sb.Append('.');
                Debug.Log(sb.ToString());
            }

            motionWindowStart = Time.unscaledTime;
            sampledFrames = detectedCuts = shiftTicks = framesWithShift = 0;
            largestTurn = largestTurnDt = largestMove = largestMoveDt = 0f;
            largestCutTurn = largestCutJump = 0f;
            largestOffset = largestKrakensbaneStep = 0.0;
            shiftFrames.Clear();
        }
    }
}
