using ReDefinition.Core;
using ReDefinition.Proxy;
using ReDefinition.Upscaler;
using UnityEngine;

namespace ReDefinition.Diagnostics
{
    // The diagnostics window's screen recording: armed by a click, started by the add-on's Update
    // five seconds later, so that there is time to close the windows and turn the camera. With
    // dumps, the stages', frame generation's inputs' and the vessel shadow's dumps of the same
    // turn go beside it; without, a recording nothing else slows.
    internal sealed class ScreenRecordingTimer
    {
        // The seconds between the click and the start, and the frames a recording holds.
        private const float DelaySeconds = 5f;
        private const int Frames = 120;

        // When the recording starts (unscaled time); 0 when none is waiting.
        private float startAt;
        private bool withDumps;

        // Whether a recording is waiting to start.
        public bool Pending
        {
            get { return startAt > 0f; }
        }

        // The whole seconds left before the recording starts.
        public int SecondsLeft
        {
            get { return Mathf.CeilToInt(startAt - Time.unscaledTime); }
        }

        // Arms a recording, with the dumps of the same turn beside it or without.
        public void Arm(bool dumps)
        {
            startAt = Time.unscaledTime + DelaySeconds;
            withDumps = dumps;
        }

        // Starts the recording once it is due.
        public void Tick()
        {
            if (startAt <= 0f || Time.unscaledTime < startAt) return;

            startAt = 0f;
            bool started = ScreenRecordingBridge.Start(Frames, withDumps);
            // The vessel shadow layer's own inputs and results beside it.
            if (started && withDumps)
            {
                VesselShadowLayer.DumpNextFrame();
                StageDump.Arm();
            }
            Debug.Log(Log.Tag + " Screen recording " + (started ? "started" + (withDumps ? ", with dumps." : ", without dumps.")
                                                                : "not started: " + ScreenRecordingBridge.State() + "."));
        }
    }
}
