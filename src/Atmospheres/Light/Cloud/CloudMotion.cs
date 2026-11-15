using System;

namespace ReDefinition.Atmospheres
{
    // The speed of the own clouds' motion as a multiple of the wind (the cloud viewer's control): the tuning key cloud.motionScale scales the time the layers' shape
    // noise drifts, rises and evolves by (LayerNoise.Advance, called by OwnCloudModule every frame). 1, the default, is the wind as the field gives it and changes
    // nothing; 0 holds the clouds still while the game time runs; up to MaxScale they move that many times faster. The weather maps follow the game time and are not
    // scaled (the field computes them forward in real time).
    internal static class CloudMotion
    {
        internal const string TuneScale = "cloud.motionScale";
        internal const float DefaultScale = 1f, MaxScale = 50f;

        // The scale in force: the key, clamped to 0 to MaxScale (NaN or infinity: the default).
        internal static float Scale
        {
            get { return Clamp(Tuning.Get(TuneScale, DefaultScale)); }
        }

        internal static float Clamp(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return DefaultScale;
            return Math.Max(0f, Math.Min(MaxScale, value));
        }
    }
}
