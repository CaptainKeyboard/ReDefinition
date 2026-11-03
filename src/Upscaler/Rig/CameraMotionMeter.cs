using System.Collections.Generic;
using System.Text;
using ReDefinition.Api.Internal;
using ReDefinition.Proxy;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // The instrument for camera motion and the floating origin, and the Debug switch for fast turns. The cuts
    // that reset the history: CameraCuts and SharedFrame.
    internal sealed class CameraMotionMeter
    {
        // A turn of that many degrees in one frame is a fast one, for the Debug switch that asks the proxy for
        // its motion vector check (UpscalerRig.RecordFastTurns).
        private const float FastTurnDegrees = 2f;
        // Each check reads the frame back and scans it under the proxy's lock.
        private const float TurnRecordSpacing = 5f;
        private float nextTurnRecord;
        private string pendingTurnLine;

        // An instrument: numbers for two open questions of
        // docs/reference/graphics-mod-compatibility.md, logged about every ten seconds
        // while the rig dispatches with the diagnostic measurements on.
        private const float MotionReportSeconds = 10f;
        private const int ShiftFramesListed = 20;
        private float motionWindowStart = -1f;
        private bool motionSampleValid;
        private Quaternion lastRotation;
        private Vector3 lastPosition;
        private Vector3 lastRelativePosition;
        private Transform lastSampleTarget;
        private int sampledFrames;
        // The camera: in ordinary frames (no cut, no origin shift) its
        // largest turn (roll included) and its largest move against the
        // camera's target. Each comes with the frame time it happened in, so a hitch
        // cannot pass for a jump. At the cuts that reset the history, the turn and the
        // jump in world space: across a change of target, a move against the
        // target compares two different targets and means nothing. This
        // measures KSP's own camera, which rides on its target. A free camera
        // such as CameraTools' holds still while the target flies on and would
        // need a measure of its own.
        private float largestTurn;
        private float largestTurnDt;
        private float largestMove;
        private float largestMoveDt;
        private int detectedCuts;
        private float largestCutTurn;
        private float largestCutJump;
        // The floating origin: KSP re-centres it when the vessel strays too
        // far, and with Krakensbane engaged it shifts on every physics tick
        // (FloatingOrigin.FixedUpdate and Krakensbane, decompiled). It is counted in
        // rendered frames that contained a shift, next to the ticks, with the
        // re-centring offset and the Krakensbane step apart. When the
        // shifts are rare, the frame numbers are listed too. They are the proxy's own: its
        // check lines read "HUD-less check (frame N ...)".
        private bool shiftedSinceLastSample;
        private int shiftTicks;
        private int framesWithShift;
        private double largestOffset;
        private double largestKrakensbaneStep;
        private readonly List<int> shiftFrames = new List<int>();

        // Set when a sample threw: the instrument says so once and stops.
        internal bool Failed { get; private set; }

        // offset moves the active vessel, the camera and nearby physics
        // objects; offset + nonFrame moves the bodies. nonFrame is the
        // Krakensbane step (FloatingOrigin.setOffset, decompiled).
        internal void OnFloatingOriginShift(Vector3d offset, Vector3d nonFrame)
        {
            shiftTicks++;
            shiftedSinceLastSample = true;
            double o = offset.magnitude;
            double k = nonFrame.magnitude;
            if (o > largestOffset) largestOffset = o;
            if (k > largestKrakensbaneStep) largestKrakensbaneStep = k;
        }

        // At the end of Present, once the frame is out: after the frame is out, and fenced, an instrument does not
        // stop a frame. On a failure it says so once and stops.
        internal void TrySample(Camera cam, bool atCut, bool turnCheckWanted)
        {
            try
            {
                Sample(cam, atCut, turnCheckWanted);
            }
            catch (System.Exception e)
            {
                Failed = true;
                Debug.LogWarning(Log.Tag + " Camera instrument stopped: " + e.Message);
            }
        }

        // Reads the camera, counts the frame and, where the Debug switch for fast turns is on, asks the proxy for
        // its motion vector check after a fast turn.
        private void Sample(Camera cam, bool atCut, bool turnCheckWanted)
        {
            if (cam == null) return;
            if (motionWindowStart < 0f) motionWindowStart = Time.unscaledTime;

            Transform t = cam.transform;
            Transform target = CameraCuts.Target;   // at the frame's last look
            Quaternion rotation = t.rotation;
            Vector3 position = t.position;
            Vector3 relative = target != null ? position - target.position : position;
            float dt = Time.unscaledDeltaTime;

            float turn;
            bool shifted;
            bool followed = Accumulate(rotation, position, relative, target, dt, atCut, Time.frameCount, out turn, out shifted);
            if (followed && turnCheckWanted && TurnCheckDue(turn, Time.unscaledTime))
                RequestTurnCheck(turn, dt, shifted);
        }

        // Counts one sampled frame: the turn since the last one, and its move against the target in ordinary frames,
        // or the turn and jump at a cut. Returns whether the camera followed the same target without a cut since
        // the last sample, which is where a fast turn counts; the turn and whether the origin shifted come out.
        internal bool Accumulate(Quaternion rotation, Vector3 position, Vector3 relative, Transform target, float dt,
                                 bool atCut, int frame, out float turn, out bool shifted)
        {
            turn = 0f;
            shifted = shiftedSinceLastSample;
            shiftedSinceLastSample = false;
            if (shifted)
            {
                framesWithShift++;
                if (shiftFrames.Count < ShiftFramesListed) shiftFrames.Add(frame);
            }

            bool followed = false;
            if (motionSampleValid)
            {
                turn = Quaternion.Angle(lastRotation, rotation);
                followed = !atCut && target == lastSampleTarget;
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
            return followed;
        }

        // Whether a turn of that many degrees asks for the proxy's check now: a fast one, and not within the spacing
        // of the last request, which this one then starts.
        internal bool TurnCheckDue(float turn, float now)
        {
            if (turn < FastTurnDegrees || now < nextTurnRecord) return false;
            nextTurnRecord = now + TurnRecordSpacing;
            return true;
        }

        // The Debug switch for the motion vectors in flight: a fast turn asks the proxy
        // for its motion vector check at once rather than on its ten-second rhythm.
        // A line says what the camera did, with its frame number. The check starts
        // in this frame or one of the next, and the proxy's check line names the frame
        // it was written in, a few frames later. Also in frames the
        // floating origin shifted, nearly all of them in flight. With frame
        // generation only: the check reads its inputs. Written from Update, not here in
        // Present, where a log write would show in the frames being checked.
        private void RequestTurnCheck(float turn, float dt, bool shifted)
        {
            FrameGenerationBridge.RequestCheck();
            pendingTurnLine = Log.Tag + " Fast turn in frame " + Time.frameCount + ": " + turn.ToString("0.0")
                              + " deg in a " + (dt * 1000f).ToString("0") + " ms frame ("
                              + (dt > 0f ? (1f / dt).ToString("0") : "?") + " fps)"
                              + (shifted ? ", floating origin shifted in this frame" : "")
                              + " -- the proxy's motion vector check follows in ReDefinitionProxy.log.";
        }

        // The line RequestTurnCheck wrote, taken so that Update logs it; null when there is none.
        internal string TakeTurnLine()
        {
            string line = pendingTurnLine;
            pendingTurnLine = null;
            return line;
        }

        // From Update, not from the render path: a log write there would show
        // up in the very frame times the proxy reports on the same rhythm.
        // Forced at teardown, so a rebuilt rig does not swallow its last window.
        internal void ReportIfDue(bool force)
        {
            if (motionWindowStart < 0f) return;
            float elapsed = Time.unscaledTime - motionWindowStart;
            if (!force && elapsed < MotionReportSeconds) return;

            string report = Describe(elapsed);
            if (report != null) Debug.Log(report);

            motionWindowStart = Time.unscaledTime;
            sampledFrames = detectedCuts = shiftTicks = framesWithShift = 0;
            largestTurn = largestTurnDt = largestMove = largestMoveDt = 0f;
            largestCutTurn = largestCutJump = 0f;
            largestOffset = largestKrakensbaneStep = 0.0;
            shiftFrames.Clear();
        }

        // The log line for the window that lasted that many seconds; null where it saw no frame and no shift.
        internal string Describe(float elapsed)
        {
            if (sampledFrames == 0 && shiftTicks == 0) return null;

            StringBuilder sb = new StringBuilder();
            sb.Append(Log.Tag).Append(" Camera and origin, last ").Append(elapsed.ToString("0.0"))
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
            return sb.ToString();
        }
    }
}
