using System;

namespace ReDefinition.Atmospheres.Audio
{
    // What the weather sound does about EVE's own snow-storm loop (key eveSound). EVE keeps one 2D looping source per cloud layer whose
    // volume is its coverage times its ambient volume; it does not look at the wind. The table decides who carries the bed.
    internal struct EveSoundDecision
    {
        // Factor on the wind bed and the howl of the mixer (1 is untouched, 0 is muted).
        internal float BedFactor;
        // Factor on the hiss and grit of the mixer.
        internal float HissFactor;
        // Whether the source of EVE is written to zero after EVE's update (policy Ours only).
        internal bool SilenceEve;
        // Whether EVE's loop counts as the bed this frame.
        internal bool EveCarriesBed;
    }

    internal static class EveSoundPolicyTable
    {
        // EVE's loop is audible above this volume (plan 07 section 3.7).
        internal const float AudibleVolume = 0.05f;
        // The bed band of our mixer while EVE carries the storm bed: -8 dB.
        internal const float BedUnderEve = 0.398f;

        // clipAudible: EVE has a snow-storm source that exists, plays and has a volume above AudibleVolume.
        internal static EveSoundDecision Decide(EveSoundPolicy policy, bool clipAudible)
        {
            var d = new EveSoundDecision { BedFactor = 1f, HissFactor = 1f, SilenceEve = false, EveCarriesBed = false };
            switch (policy)
            {
                case EveSoundPolicy.Auto:
                    // Never writes EVE's source. EVE's loop is the bed when it is audible: ours runs as gusts and howl only (bed band
                    // -8 dB, no static hiss). With no audible loop ours is whole.
                    if (clipAudible)
                    {
                        d.BedFactor = BedUnderEve;
                        d.HissFactor = 0f;
                        d.EveCarriesBed = true;
                    }
                    break;
                case EveSoundPolicy.Eve:
                    // EVE's loop is the bed and the hiss; ours keeps ticks, footsteps, rumble and the quiet ambience. The gusts live in
                    // the bed band of the mixer, so they go with it.
                    d.BedFactor = 0f;
                    d.HissFactor = 0f;
                    d.EveCarriesBed = clipAudible;
                    break;
                case EveSoundPolicy.Ours:
                    d.SilenceEve = true;
                    break;
                default:
                    // Both: no policy.
                    d.EveCarriesBed = clipAudible;
                    break;
            }
            return d;
        }

        // The sum of two uncorrelated beds, in linear RMS: used by the series row "our bed plus EVE's within 3 dB of the larger one".
        internal static double SumRms(double a, double b)
        {
            return Math.Sqrt(a * a + b * b);
        }
    }

    // Who muffles the weather sound in a cabin or a helmet (key audioMuffling) and whether Rocket Sound Enhancement (RSE) is there. The
    // procedural stream has no clip path, so RSE cannot route it: its muffling is always ours. The sample clips (footsteps) are routed by the
    // MM patch (CustomMixerRouting, Exterior) when RSE is present; the own low-pass on exactly those sources is then off, so that nothing is
    // muffled twice.
    internal struct MufflingPlan
    {
        // The interior muffler of the procedural stream.
        internal bool OwnOnProcedural;
        // The own low-pass on the sample clips.
        internal bool OwnOnSamples;
        // Whether RSE carries the sample clips.
        internal bool RseCarriesSamples;
        // Whether the step sources leave RSE's mixer group (their output group is set to none each frame), so that the own low-pass
        // is the only muffler.
        internal bool DetachFromRse;
    }

    internal static class MufflingTable
    {
        // rseRoutesOurClips: the routing node of RSE's settings holds every one of our clips. rseGroupObserved: a step source of ours has
        // really been given an output mixer group (RSE took the clip); until then the own low-pass stays on.
        internal static MufflingPlan Decide(AudioMuffling mode, bool rsePresent, bool rseRoutesOurClips, bool rseGroupObserved)
        {
            var plan = new MufflingPlan { OwnOnProcedural = true, OwnOnSamples = true, RseCarriesSamples = false, DetachFromRse = false };
            switch (mode)
            {
                case AudioMuffling.Own:
                    // The patch routes the clips whenever RSE is installed, whatever this setting says: in this mode the step sources
                    // leave RSE's group at runtime, so a clip is muffled by the own low-pass alone.
                    plan.DetachFromRse = rsePresent;
                    break;
                case AudioMuffling.Auto:
                    // RSE carries the samples only where the routing node of our clips is in its settings and RSE has been seen to
                    // route a step source of ours.
                    if (rsePresent && rseRoutesOurClips && rseGroupObserved)
                    {
                        plan.OwnOnSamples = false;
                        plan.RseCarriesSamples = true;
                    }
                    break;
                case AudioMuffling.Rse:
                    // RSE is trusted with the samples whenever it is installed.
                    if (rsePresent)
                    {
                        plan.OwnOnSamples = false;
                        plan.RseCarriesSamples = true;
                    }
                    break;
            }
            return plan;
        }
    }
}
