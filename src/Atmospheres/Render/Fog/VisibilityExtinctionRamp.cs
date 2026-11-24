using System;

namespace ReDefinition.Atmospheres
{
    // The values the visibility extinction writer puts into Unity's fog, one frame at a time: the view's density and colour followed with a
    // limit on how fast they may move (nothing changes faster than a 12 % swing at 3 Hz), the gust of the density on top (at most 12 %, a
    // period of 3 seconds or more), and the player's visibilityExtinctionBrightness on the colour. Pure.
    internal sealed class VisibilityExtinctionRamp
    {
        private float density;
        private float r, g, b;
        private bool started;
        private double clock;

        // The density and colour that go into the fog now.
        internal float Density
        {
            get { return density; }
        }

        internal float R { get { return r; } }
        internal float G { get { return g; } }
        internal float B { get { return b; } }

        // True once a value has been followed (until Reset).
        internal bool Started
        {
            get { return started; }
        }

        internal void Reset()
        {
            started = false;
            density = 0f;
            clock = 0.0;
        }

        // One frame on. targetDensity and the colour come from the view, level is the precipitation level (the gust of the density grows
        // with it), gustPeriodS and gustAmplitude are the tuning of the gust, hertz the ramp limit, brightness the player's
        // visibilityExtinctionBrightness. The first call takes the target at once (a scene that starts in low visibility does not fade in from nothing).
        internal void Step(float targetDensity, float tr, float tg, float tb, float level, float gustAmplitude, float gustPeriodS, float hertz,
            float brightness, float dt)
        {
            clock += Math.Max(dt, 0f);
            float amplitude = Math.Min(Math.Max(level, 0f), 1f) * Math.Min(gustAmplitude, PrecipMath.VisibilityGustAmplitudeMax);
            float modulated = targetDensity * PrecipMath.VisibilityGustFactor(clock, amplitude, gustPeriodS);
            float k = Math.Max(brightness, 0f);
            if (!started)
            {
                started = true;
                density = modulated;
                r = tr * k;
                g = tg * k;
                b = tb * k;
                return;
            }
            density = PrecipMath.VisibilityStep(density, modulated, hertz, dt);
            r = PrecipMath.VisibilityStep(r, tr * k, hertz, dt);
            g = PrecipMath.VisibilityStep(g, tg * k, hertz, dt);
            b = PrecipMath.VisibilityStep(b, tb * k, hertz, dt);
        }
    }
}
