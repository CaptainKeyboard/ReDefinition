using System;

namespace ReDefinition.Atmospheres.Audio
{
    // One step the stride integrator found.
    internal struct StepEvent
    {
        internal bool LeftFoot;
        internal bool Run;
        // The speed over the ground at the step in m/s.
        internal float Speed;
    }

    // Finds steps without a hook: KerbalEVA has no step sound event and no OnGround member (RECON P5), so the distance a grounded kerbal
    // walks is integrated and a step falls every stride (walk 0.7 m, run 1.5 m), alternating the feet. The first step after standing is
    // skipped (the kerbal is still starting). Pure.
    internal sealed class StrideIntegrator
    {
        internal const float WalkStrideM = 0.7f;
        internal const float RunStrideM = 1.5f;
        // Below this speed the kerbal stands; it must stand this long before the next step is a first step again.
        internal const float StandSpeedMs = 0.15f;
        internal const float StandSeconds = 0.35f;

        private float travelled;
        private float standing = StandSeconds;
        private bool left;
        private bool skipFirst = true;

        // The speed from which a step counts as a run (the middle of the walk and run speeds of the kerbal).
        internal float RunSpeedMs = 3.0f;

        // Feeds one frame: the distance moved over the ground in metres, the speed, whether the kerbal is on the ground (and not on a ladder,
        // swimming or flying with the jetpack), the frame time. Returns true and fills the event when a step fell.
        internal bool Feed(float distanceM, float speedMs, bool grounded, float dt, out StepEvent step)
        {
            step = default(StepEvent);
            if (!grounded || speedMs < StandSpeedMs)
            {
                standing += dt;
                if (standing >= StandSeconds)
                {
                    travelled = 0f;
                    skipFirst = true;
                }
                return false;
            }
            standing = 0f;
            bool run = speedMs >= RunSpeedMs;
            float stride = run ? RunStrideM : WalkStrideM;
            travelled += distanceM < 0f ? 0f : distanceM;
            if (travelled < stride) return false;
            travelled -= stride;
            if (travelled > stride) travelled = 0f;
            left = !left;
            if (skipFirst)
            {
                skipFirst = false;
                return false;
            }
            step.LeftFoot = left;
            step.Run = run;
            step.Speed = speedMs;
            return true;
        }
    }

    // What the ground under the foot is, from the field's surface layer.
    internal enum StepSurface
    {
        None,
        Snow,
        // The body's own clip (SoundRules.FootstepSurface), for sand, dust or anything else a config names.
        Custom
    }

    internal struct SurfaceQuery
    {
        // Snow cover at the foot position, 0 to 1.
        internal float Cover01;
        internal bool Water;
        // Sea ice or a frozen pond (the ice fraction map above half).
        internal bool IceRink;
        // The body's SOUND FootstepSurface entry is set.
        internal bool HasCustomClip;
    }

    // The gating of the steps (plan 07 section 3.5): the sound is made on snow deeper than a threshold, never on water or an ice rink; with the
    // surface setting on, a body's own footstep clip covers the bare ground too.
    internal static class FootstepGate
    {
        // Cover at which a step crunches (a thin dusting makes no sound of its own).
        internal const float SnowDepthThreshold = 0.12f;

        internal static StepSurface Decide(SurfaceQuery q, bool surfaceSetting)
        {
            if (q.Water || q.IceRink) return StepSurface.None;
            if (q.Cover01 >= SnowDepthThreshold) return StepSurface.Snow;
            if (surfaceSetting && q.HasCustomClip) return StepSurface.Custom;
            return StepSurface.None;
        }
    }

    // A value that is looked up at most every interval (the cover at the foot, cached for half a second).
    internal struct TimedValue
    {
        private float value;
        private float nextAt;
        private bool has;

        internal bool NeedsUpdate(float now)
        {
            return !has || now >= nextAt;
        }

        internal float Value { get { return value; } }

        internal void Set(float v, float now, float intervalSeconds)
        {
            value = v;
            nextAt = now + intervalSeconds;
            has = true;
        }

        internal void Reset()
        {
            has = false;
        }
    }

    // How one step sounds: the intensity class of the clip (0 soft, 1 medium, 2 hard), the gain, the pitch and the procedural low thump.
    internal struct StepVoice
    {
        internal int Class;
        internal float Gain;
        internal float Pitch;
        internal float ThumpGain;
        internal float ThumpPitch;
    }

    internal static class StepShaper
    {
        // The cover at which the snow counts as deep (lower pass, longer).
        internal const float DeepCover = 0.6f;
        // The mass of a kerbal in suit in tonnes, the reference of the thump.
        internal const float ReferenceMassT = 0.094f;

        // r1 and r2 are random numbers from 0 to 1 (the module's generator; a test passes fixed ones).
        internal static StepVoice Shape(StepEvent step, float cover01, float massTonnes, float r1, float r2)
        {
            var v = new StepVoice();
            bool deep = cover01 >= DeepCover;
            // Run is hard, a walk in deep snow medium, a walk on a thin cover soft.
            v.Class = step.Run ? 2 : deep ? 1 : 0;
            float speedT = step.Speed / 6f;
            speedT = speedT < 0f ? 0f : speedT > 1f ? 1f : speedT;
            v.Gain = (0.55f + 0.45f * speedT) * (0.9f + 0.2f * r1);
            // Deep snow is dull: the pitch falls; a little random spread keeps the repeats apart.
            v.Pitch = (deep ? 0.88f : 1f) * (0.95f + 0.1f * r2);
            float mass = massTonnes <= 0f ? ReferenceMassT : massTonnes;
            float massT = mass / ReferenceMassT;
            massT = massT < 0.5f ? 0.5f : massT > 2f ? 2f : massT;
            v.ThumpGain = 0.35f * massT * (0.6f + 0.4f * Math.Min(1f, cover01 + (step.Run ? 0.3f : 0f)));
            v.ThumpPitch = 1f / (float)Math.Sqrt(massT);
            return v;
        }
    }
}
