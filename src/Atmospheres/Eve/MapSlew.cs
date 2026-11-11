using System;

namespace ReDefinition.Atmospheres
{
    // The slew limit of the painted cloud maps, pure. EVE renders a fraction of its rays per frame (temporal x12 reconstruction: a map
    // change shows after 12 frames at the earliest), refreshes the direct light volume in about 16 frames and the ambient volume over
    // hundreds, and the upscaler's history follows the cloud edge with the motion EVE reports. A texel that jumps from 0 to 1 in one
    // update is a flash in all of them. So no texel of a painted map moves faster than Rate (a share of full scale per second): the
    // painter pass applies Step to every texel with the time since its last update, and the CPU mirror applies the same function.
    //
    // The maps are 8 bit. A step smaller than one level would never move a texel, so the update interval is chosen with
    // StepLevels: at least MinLevelsPerUpdate levels per update (Quantises tells), and a texel closer to its target than one step
    // snaps onto it.
    internal static class MapSlew
    {
        // The default rate, a share of full scale per second: a texel crosses the whole scale in 12.5 s.
        internal const float DefaultRate = 0.08f;

        // The default time between two paint updates, in seconds.
        internal const float DefaultIntervalS = 0.25f;

        // The least number of 8 bit levels a limited step must be, so that the limit moves a texel at all.
        internal const float MinLevelsPerUpdate = 2f;

        // The most a texel moves in dt seconds.
        internal static float MaxDelta(float rate, float dt)
        {
            return rate <= 0f || dt <= 0f ? 0f : rate * dt;
        }

        // The value after one limited step toward the target.
        internal static float Step(float current, float target, float maxDelta)
        {
            float d = target - current;
            if (d > maxDelta) return current + maxDelta;
            if (d < -maxDelta) return current - maxDelta;
            return target;
        }

        // The same on a byte map (levels 0 to 255): current moves toward target by at most maxDelta (a share of full scale), rounded,
        // and a step of less than one level snaps onto the target.
        internal static byte StepByte(byte current, byte target, float maxDelta)
        {
            float stepLevels = maxDelta * 255f;
            int d = target - current;
            if (Math.Abs(d) <= Math.Max(1f, stepLevels)) return target;
            int move = (int)Math.Round(stepLevels);
            if (move < 1) move = 1;
            return (byte)(d > 0 ? current + move : current - move);
        }

        // Steps a whole map; the arrays have the same length. Returns the number of texels that are not at their target afterwards.
        internal static int StepMap(byte[] current, byte[] target, float rate, float dt)
        {
            float maxDelta = MaxDelta(rate, dt);
            int open = 0;
            for (int i = 0; i < current.Length; i++)
            {
                current[i] = StepByte(current[i], target[i], maxDelta);
                if (current[i] != target[i]) open++;
            }
            return open;
        }

        // The number of 8 bit levels one update of that interval may move a texel.
        internal static float StepLevels(float rate, float intervalS)
        {
            return MaxDelta(rate, intervalS) * 255f;
        }

        // Whether the limit moves a texel at all with that interval (at least MinLevelsPerUpdate levels).
        internal static bool Quantises(float rate, float intervalS)
        {
            return StepLevels(rate, intervalS) >= MinLevelsPerUpdate;
        }

        // The seconds a texel needs to cross a change of that size (a share of full scale).
        internal static float SecondsFor(float change, float rate)
        {
            return rate <= 0f ? float.PositiveInfinity : Math.Abs(change) / rate;
        }

        // The number of frames a change takes at a frame rate; EVE's x12 reconstruction needs 12 and the direct light about 16 to
        // follow, so a limited change spans more than that.
        internal static float FramesFor(float change, float rate, float fps)
        {
            return SecondsFor(change, rate) * fps;
        }
    }
}
