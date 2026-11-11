using System;

namespace ReDefinition.Atmospheres
{
    // Decides whether the interior mask of the IVA holds a picture of the cabin, and which way of taking it works. Pure.
    //
    // The mask is a copy of the internal camera's depth buffer thresholded to covered / not covered (FrostInteriorMask). Which event
    // of that camera holds a valid depth, and whether the camera makes the depth texture at all, was not measured when the mask was
    // written (RECON.md P6 measured the flight camera only): a mask that is empty (nothing covered) or full (everything covered)
    // frosts the cabin's props or none of its windows. So the mask is read back a few times and judged, and when the readings are
    // implausible the judge moves on to the next way, in the order of Count; when the last way fails too the mask counts as invalid and
    // the renderer does not use the host composite for the IVA.
    internal sealed class FrostMaskJudge
    {
        // The ways to take the mask, in the order they are tried: the event of the internal camera at which the depth is copied, and
        // whether the camera is asked to make the depth texture (DepthTextureMode.Depth). The rig sets the mode on the scene camera
        // only.
        internal struct Way
        {
            internal readonly string Event;
            internal readonly bool ForceDepth;

            internal Way(string eventName, bool forceDepth)
            {
                Event = eventName;
                ForceDepth = forceDepth;
            }
        }

        internal static readonly Way[] Ways =
        {
            new Way("BeforeForwardAlpha", true),
            new Way("BeforeForwardAlpha", false),
            new Way("AfterForwardOpaque", true),
            new Way("BeforeImageEffects", true)
        };

        // A reading below Min or above Max is implausible: a cabin always shows some of its interior and some window. The share of a
        // window in a pod is far from either end.
        internal const float Min = 0.005f;
        internal const float Max = 0.995f;
        // Consecutive implausible readings after which the way is dropped, and the readings that make a way valid.
        internal const int Strikes = 3;
        internal const int Goods = 2;

        private int way;
        private int bad;
        private int good;
        private bool exhausted;
        private float coverage = -1f;

        // The way in use (an index into Ways).
        internal int Current
        {
            get { return way; }
        }

        // The last reading, or -1.
        internal float Coverage
        {
            get { return coverage; }
        }

        // Whether the readings of the way in use are plausible.
        internal bool Valid
        {
            get { return !exhausted && good >= Goods; }
        }

        // Whether every way has been tried and failed.
        internal bool Exhausted
        {
            get { return exhausted; }
        }

        internal static bool Plausible(float coverage)
        {
            return coverage >= Min && coverage <= Max;
        }

        // Takes a reading; true where the way changed (the caller rebuilds the buffer).
        internal bool Report(float reading)
        {
            coverage = reading;
            if (exhausted) return false;
            if (Plausible(reading))
            {
                bad = 0;
                if (good < Goods) good++;
                return false;
            }
            good = 0;
            bad++;
            if (bad < Strikes) return false;
            bad = 0;
            if (way + 1 < Ways.Length)
            {
                way++;
                return true;
            }
            exhausted = true;
            return false;
        }

        // Starts again from the first way (a new IVA entry).
        internal void Reset()
        {
            way = 0;
            bad = 0;
            good = 0;
            exhausted = false;
            coverage = -1f;
        }

        // Puts the way by hand (the series pins a way to compare them); the readings are kept apart from the judging then.
        internal void Pin(int index)
        {
            int clamped = Math.Max(0, Math.Min(Ways.Length - 1, index));
            if (clamped == way && !exhausted) return;
            way = clamped;
            bad = 0;
            good = 0;
            exhausted = false;
        }
    }
}
