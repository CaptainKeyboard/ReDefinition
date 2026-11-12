using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // What the droplet state machine is told each frame. A plain struct of numbers, no engine type.
    internal struct DropletInput
    {
        // Seconds of game-independent real time since the last step (0 while paused).
        public float Dt;
        // The weather at the camera: the precipitation level 0 to 1 and what falls.
        public float Level;
        public PrecipType Type;
        // 1 where the glass is above freezing (snow melts into drops on it), 0 where it is frozen (the frost effect owns that case).
        public float WarmGlass;
        // How fast the air moves over the glass, m/s (wind plus the vessel's speed through the air).
        public float AirflowMs;
        // True when the camera is sheltered from the weather (a roof over a kerbal's head).
        public bool Sheltered;
        // The direction down the picture in the picture plane (unit; x right, y up) and how much of gravity lies in that plane.
        public float GravityX, GravityY, GravityInPlane;
        // The direction the air pushes a drop across the picture (unit; the vessel's motion through the air, reversed) and how much
        // of that motion lies in the picture plane, 0 to 1.
        public float AirX, AirY, AirInPlane;
    }

    // The water on a window as a state machine, pure maths (package T08, item 2). Wet01 is the share of the glass that is wet: it
    // rises with the rain (a full pane in FillS seconds at level 1), faster for a pane that moves through the air, and falls by drying
    // (slowly in still air, quickly under an airflow that grows with its square). The drops run along the picture's gravity direction
    // and are pushed along the airflow; the slide velocity is smoothed. The draw is a pure function of these numbers and the clock, so
    // that the state needs no list of drops.
    internal sealed class DropletModel
    {
        private float wet;
        private float time;
        private float slideX, slideY;
        private bool started;

        // The wetness 0 to 1.
        internal float Wet01 { get { return wet; } }
        // The clock of the draw in seconds, wrapped at an hour so that float precision holds.
        internal float Time { get { return time; } }
        // The slide velocity in picture heights per second (x right, y up).
        internal float SlideX { get { return slideX; } }
        internal float SlideY { get { return slideY; } }

        internal void Reset()
        {
            wet = 0f;
            slideX = slideY = 0f;
            started = false;
        }

        // The weight of a fall for the glass: how much of it ends up as water.
        internal static float WaterWeight(PrecipType type, float warmGlass, ref DropletTuning t)
        {
            switch (type)
            {
                case PrecipType.Rain:
                case PrecipType.Methane:
                    return 1f;
                case PrecipType.Sleet:
                    return 0.8f;
                case PrecipType.FreezingRain:
                    return 0.5f;
                case PrecipType.Snow:
                    return t.SnowMelt * Clamp01(warmGlass);
                default:
                    return 0f;
            }
        }

        // The rate at which a pane fills in 1/s.
        internal static float FillRate(float level, PrecipType type, float warmGlass, float airflowMs, ref DropletTuning t)
        {
            return Clamp01(level) * WaterWeight(type, warmGlass, ref t) / t.FillS * (1f + Math.Max(airflowMs, 0f) / t.CatchRefMs);
        }

        // The rate at which a wet pane dries in 1/s.
        internal static float DryRate(float airflowMs, ref DropletTuning t)
        {
            float r = Math.Max(airflowMs, 0f) / t.BlowRefMs;
            return 1f / t.DryStillS + r * r / t.BlowS;
        }

        // The seconds a dry pane needs to become fully wet in steady weather: 1 / (fill - dry); infinity where it never gets there.
        internal static float FillTimeS(float level, PrecipType type, float warmGlass, float airflowMs, ref DropletTuning t)
        {
            float net = FillRate(level, type, warmGlass, airflowMs, ref t) - DryRate(airflowMs, ref t);
            return net > 1e-9f ? 1f / net : float.PositiveInfinity;
        }

        // One step. Dt of 0 changes nothing.
        internal void Step(ref DropletInput input, ref DropletTuning t)
        {
            float dt = input.Dt > 0f ? Math.Min(input.Dt, 1f) : 0f;
            float air = Math.Max(input.AirflowMs, 0f);
            float fill = input.Sheltered ? 0f : FillRate(input.Level, input.Type, input.WarmGlass, air, ref t);
            wet = Clamp01(wet + (fill - DryRate(air, ref t)) * dt);
            if (wet < 1e-5f) wet = 0f;

            // Sliding: gravity along the picture's down, the airflow across it; smoothed.
            float gl = (float)Math.Sqrt(input.GravityX * input.GravityX + input.GravityY * input.GravityY);
            float gx = gl > 1e-4f ? input.GravityX / gl : 0f, gy = gl > 1e-4f ? input.GravityY / gl : -1f;
            float al = (float)Math.Sqrt(input.AirX * input.AirX + input.AirY * input.AirY);
            float ax = al > 1e-4f ? input.AirX / al : 0f, ay = al > 1e-4f ? input.AirY / al : 0f;
            float gravity = t.SlideG * (0.4f + 0.6f * wet) * Clamp01(input.GravityInPlane);
            float push = t.SlideAir * Math.Min(air / t.BlowRefMs, 3f) * Clamp01(input.AirInPlane);
            float tx = gx * gravity + ax * push, ty = gy * gravity + ay * push;
            if (!started)
            {
                slideX = tx;
                slideY = ty;
                started = true;
            }
            else if (dt > 0f)
            {
                float k = 1f - (float)Math.Exp(-dt / t.LagS);
                slideX += (tx - slideX) * k;
                slideY += (ty - slideY) * k;
            }
            time += dt;
            if (time > 3600f) time -= 3600f;
        }

        // Whether the pass has anything to draw.
        internal bool NeedsDraw(ref DropletTuning t)
        {
            return wet > t.SkipBelow;
        }

        // The share of the glass the drops cover at a wetness (expected area; the shader's drop population follows the same curve).
        internal static float Coverage(float wet01, ref DropletTuning t)
        {
            return t.CoverMax * (float)Math.Pow(Clamp01(wet01), 0.85);
        }

        private static float Clamp01(float v)
        {
            return float.IsNaN(v) ? 0f : Math.Min(Math.Max(v, 0f), 1f);
        }
    }
}
