using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // Where the frost can be seen: not at all (external camera, map view), through the visor of a kerbal in first person (the flight
    // camera at the head: the stock view of the kerbal or ThroughTheEyes), or through the windows of a cabin (IVA).
    internal enum FrostView
    {
        Off = 0,
        Eva = 1,
        Iva = 2
    }

    // What the state machine is told each frame. A plain struct of numbers, no engine type.
    internal struct FrostInput
    {
        // Seconds of real time since the last step (0 while the game is paused).
        public float Dt;
        public FrostView View;
        // The weather at the camera: the precipitation level 0 to 1 and what falls.
        public float Level;
        public PrecipType Type;
        // The air temperature in kelvin; NaN where the source does not know it (then nothing freezes).
        public float TemperatureK;
        // The temperature below which frost forms on this body (FrostRules.MaxTemperatureK) and the body's strength factor.
        public float FrostLimitK;
        public float Strength;
        // How fast the air moves over the glass, m/s (wind plus the vessel's speed through the air).
        public float AirflowMs;
        // The gust phase 0 to 1 (WindNoise.Phase01): 1 is the peak of a gust.
        public float GustPhase;
        // True when the camera is sheltered from the weather.
        public bool Sheltered;
        // The direction down the picture, a unit vector in the picture plane (x right, y up), and how much of gravity lies in that
        // plane, 0 (looking straight down or up) to 1 (looking at the horizon).
        public float GravityX, GravityY, GravityInPlane;
        public int Seed;
    }

    // A flake of snow stuck to the glass. Position in picture units (u and v 0 to 1), size as a share of the picture height.
    internal struct FrostFlake
    {
        public float X, Y, Size, Age, Life;
        public int Variant;

        internal bool Alive { get { return Life > 0f; } }
    }

    // A drop on the glass. The head sits at X, Y; behind it, against the way it slides, the trail of wet glass.
    internal struct FrostDrop
    {
        public float X, Y, Size, Trail, Speed, Timer, Life;
        public bool Slipping;

        internal bool Alive { get { return Life > 0f; } }
    }

    // The frost on the camera as a state machine, pure maths. Exposure (0 to 1) builds with the level of the precipitation, the cold of the
    // glass, the air that moves over it and the time spent in the weather; it clears when the camera is sheltered (a full exposure in
    // ClearS seconds, 1/30 s by default) and, faster, when the glass is warm. Behind a cabin window the glass is pulled towards the
    // cabin temperature with a lag, so a warm cabin keeps it clear and only a very cold outside frosts it. On top of the rim frost the
    // state holds the stuck flakes (they stick on gust peaks and melt) and the drops that slide down the picture's gravity direction.
    // Everything is deterministic from the seed and the inputs.
    internal sealed class CameraFrost
    {
        internal const int MaxFlakes = 40;
        internal const int MaxDrops = 24;
        // The level below which no weather reaches the glass at all.
        internal const float WeatherFrom = 0.04f;

        private readonly FrostFlake[] flakes = new FrostFlake[MaxFlakes];
        private readonly FrostDrop[] drops = new FrostDrop[MaxDrops];
        private uint rng = 0x2545F491u;
        private bool seeded;
        private float exposure;
        private float glassK = float.NaN;
        private float weatherS;
        private float prevGust;
        private float dropCarry;

        // The exposure 0 to 1.
        internal float Exposure { get { return exposure; } }

        // The temperature of the glass in kelvin, NaN before the first step with a known temperature.
        internal float GlassK { get { return glassK; } }

        // Seconds spent in weather that freezes, falling to 0 when it is over.
        internal float WeatherSeconds { get { return weatherS; } }

        // The most recent growth (reach of the rim frost), share of the half-diagonal.
        internal float Growth { get; private set; }

        // The stuck flakes and the drops. Slots; Alive says which are in use.
        internal FrostFlake[] Flakes { get { return flakes; } }
        internal FrostDrop[] Drops { get { return drops; } }

        internal int FlakeCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < flakes.Length; i++) if (flakes[i].Alive) n++;
                return n;
            }
        }

        internal int DropCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < drops.Length; i++) if (drops[i].Alive) n++;
                return n;
            }
        }

        // Whether anything is on the glass that needs the pass: the exposure is above skipBelow, or a flake or a drop is there. A frame
        // for which this is false records no command at all (zero cost inactive).
        internal bool NeedsDraw(ref FrostTuning t)
        {
            return exposure >= t.SkipBelow || FlakeCount > 0 || DropCount > 0;
        }

        // Empties the glass (a body change, a scene change).
        internal void Reset()
        {
            exposure = 0f;
            glassK = float.NaN;
            weatherS = 0f;
            prevGust = 0f;
            dropCarry = 0f;
            Growth = 0f;
            seeded = false;
            Array.Clear(flakes, 0, flakes.Length);
            Array.Clear(drops, 0, drops.Length);
        }

        // The weight of a falling type for the frost: how much of it freezes onto glass.
        internal static float TypeWeight(PrecipType type)
        {
            switch (type)
            {
                case PrecipType.Snow: return 1f;
                case PrecipType.FreezingRain: return 0.9f;
                case PrecipType.Sleet: return 0.8f;
                case PrecipType.Rain: return 0.35f;
                default: return 0f;
            }
        }

        // The weight of a type for the drops: how wet it leaves the glass.
        internal static float WetWeight(PrecipType type)
        {
            switch (type)
            {
                case PrecipType.Rain: return 1f;
                case PrecipType.FreezingRain: return 0.5f;
                case PrecipType.Sleet: return 0.6f;
                default: return 0f;
            }
        }

        // The share of the way from clear to frosting that a glass temperature is at: 0 at the frost limit and above, 1 at the limit
        // minus widthK and below. NaN (unknown) is 0.
        internal static float Cold(float glassK, float limitK, float widthK)
        {
            if (float.IsNaN(glassK)) return 0f;
            return FrostShape.Smooth(0f, 1f, (limitK - glassK) / Math.Max(widthK, 1e-3f));
        }

        // The temperature the glass is pulled towards: a visor takes the air's, a window pane sits between the air and the cabin.
        internal static float GlassTarget(FrostView view, float airK, ref FrostTuning t)
        {
            if (view == FrostView.Iva) return airK + t.IvaInsulation * (t.CabinK - airK);
            return airK;
        }

        // One step. Dt of 0 changes nothing.
        internal void Step(ref FrostInput input, ref FrostTuning t)
        {
            if (!seeded)
            {
                rng = unchecked((uint)(input.Seed * 747796405 + 0x2545F491));
                if (rng == 0u) rng = 1u;
                seeded = true;
            }
            float dt = input.Dt > 0f ? Math.Min(input.Dt, 1f) : 0f;
            float level = FrostShape.Clamp01(input.Level);

            // The glass takes the air's temperature (or the cabin's) with a lag.
            if (!float.IsNaN(input.TemperatureK))
            {
                float target = GlassTarget(input.View, input.TemperatureK, ref t);
                if (float.IsNaN(glassK))
                {
                    glassK = target;
                }
                else
                {
                    float lag = input.View == FrostView.Iva ? t.IvaLagS : t.EvaLagS;
                    glassK += (target - glassK) * (1f - (float)Math.Exp(-dt / lag));
                }
            }
            float cold = Cold(glassK, input.FrostLimitK, t.ColdWidthK);
            float warm = float.IsNaN(glassK) ? 0f : FrostShape.Smooth(0f, 6f, glassK - input.FrostLimitK);

            float windF = FrostShape.Clamp01(input.AirflowMs / Math.Max(t.WindRefMs, 0.1f));
            float typeW = TypeWeight(input.Type);
            bool inWeather = level >= WeatherFrom && !input.Sheltered;
            float intensity = inWeather ? FrostShape.Clamp01(level * typeW * cold * (0.55f + 0.45f * windF) * Math.Max(input.Strength, 0f)) : 0f;

            // Time in the weather: grows while it freezes onto the glass, falls twice as fast when it stops.
            if (intensity > 0.02f) weatherS += dt; else weatherS = Math.Max(0f, weatherS - 2f * dt);
            float ramp = FrostShape.Clamp01(weatherS / Math.Max(t.BuildS, 1f));
            float cap = FrostShape.Clamp01(intensity * t.Gain * (0.6f + 0.4f * ramp));

            if (cap > exposure)
            {
                exposure += (cap - exposure) * dt / Math.Max(t.BuildS, 1f) * (1f + 2f * windF);
                if (exposure > cap) exposure = cap;
            }
            else if (exposure > 0f)
            {
                // Sheltered or free of weather: the full clear rate; warm glass melts it twice as fast; weather that has eased
                // takes the exposure down to its cap at a third of the rate.
                bool dry = input.Sheltered || level < WeatherFrom || intensity <= 0.02f;
                float rate = (dry ? 1f : 1f / 3f) / Math.Max(t.ClearS, 1f) * (1f + warm);
                exposure = Math.Max(cap, exposure - rate * dt);
            }
            if (exposure < 1e-4f) exposure = 0f;
            Growth = FrostShape.Growth(exposure, ref t);

            // Wetness: rain, and snow on warm glass, and melting frost, feed the drops.
            float wet = level >= WeatherFrom && !input.Sheltered ? level * Math.Max(WetWeight(input.Type), TypeWeight(input.Type) * warm * 0.6f) : 0f;
            wet = Math.Max(wet, exposure * warm * 0.5f);

            StepFlakes(ref input, ref t, dt, level, typeW, warm);
            StepDrops(ref input, ref t, dt, wet);
            prevGust = input.GustPhase;
        }

        // A random number 0 to 1; xorshift32.
        private float Next()
        {
            uint x = rng;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            rng = x;
            return (x >> 8) * (1f / 16777216f);
        }

        private void StepFlakes(ref FrostInput input, ref FrostTuning t, float dt, float level, float typeW, float warm)
        {
            // Ageing and melting.
            for (int i = 0; i < flakes.Length; i++)
            {
                if (!flakes[i].Alive) continue;
                flakes[i].Age += dt * (1f + 2f * warm);
                if (flakes[i].Age >= flakes[i].Life)
                {
                    // A melted flake leaves a drop when the glass is wet enough to hold one.
                    if (t.DropMax > 0 && Next() < 0.5f) SpawnDrop(ref input, ref t, flakes[i].X, flakes[i].Y, flakes[i].Size * 0.7f);
                    flakes[i] = default(FrostFlake);
                }
            }

            // Flakes stick on the rising edge of a gust peak, in snow-like weather.
            bool peak = prevGust < t.FlakeGust && input.GustPhase >= t.FlakeGust;
            if (!peak || input.Sheltered || level < WeatherFrom || typeW < 0.5f || dt <= 0f || t.FlakeMax <= 0) return;
            int free = t.FlakeMax - FlakeCount;
            int wanted = 1 + (int)(Next() * t.FlakePerGust * level);
            for (int n = 0; n < wanted && free > 0; n++)
            {
                for (int slot = 0; slot < flakes.Length; slot++)
                {
                    if (flakes[slot].Alive) continue;
                    float x = 0.5f, y = 0.5f;
                    // Fewer in the middle of the view: a try is kept with a chance that grows with its distance from the centre.
                    for (int tries = 0; tries < 8; tries++)
                    {
                        x = Next();
                        y = Next();
                        if (Next() < 0.15f + 0.85f * FrostShape.Smooth(0.2f, 0.8f, FrostShape.Radius(x, y, 16f / 9f))) break;
                    }
                    float sizePx = t.FlakePxMin + (t.FlakePxMax - t.FlakePxMin) * (float)Math.Pow(Next(), 2.0);
                    float life = t.FlakeLifeMin + (t.FlakeLifeMax - t.FlakeLifeMin) * Next();
                    flakes[slot] = new FrostFlake
                    {
                        X = x,
                        Y = y,
                        Size = sizePx / 1080f * 0.5f,
                        Age = 0f,
                        Life = life,
                        Variant = (int)(Next() * 15.999f)
                    };
                    free--;
                    break;
                }
            }
        }

        private void SpawnDrop(ref FrostInput input, ref FrostTuning t, float x, float y, float size)
        {
            if (t.DropMax <= 0 || DropCount >= t.DropMax) return;
            for (int i = 0; i < drops.Length; i++)
            {
                if (drops[i].Alive) continue;
                float sizeMin = t.DropPx / 1080f * 0.5f, sizeMax = t.DropPxMax / 1080f * 0.5f;
                if (size <= 0f) size = sizeMin + (sizeMax - sizeMin) * (float)Math.Pow(Next(), 2.0);
                drops[i] = new FrostDrop
                {
                    X = x,
                    Y = y,
                    Size = Math.Min(Math.Max(size, sizeMin), sizeMax),
                    Trail = 0f,
                    Speed = 0f,
                    Timer = 0.5f + 2.5f * Next(),
                    Slipping = false,
                    Life = 25f + 35f * Next()
                };
                return;
            }
        }

        private void StepDrops(ref FrostInput input, ref FrostTuning t, float dt, float wet)
        {
            // New drops: a Poisson stream whose rate follows the wetness.
            if (t.DropMax > 0 && wet > 0.01f && dt > 0f)
            {
                dropCarry += t.DropRate * wet * dt;
                while (dropCarry >= 1f)
                {
                    dropCarry -= 1f;
                    SpawnDrop(ref input, ref t, Next(), Next(), 0f);
                }
            }
            else if (wet <= 0.01f)
            {
                dropCarry = 0f;
            }

            float gx = input.GravityX, gy = input.GravityY;
            float len = (float)Math.Sqrt(gx * gx + gy * gy);
            if (len < 1e-4f) { gx = 0f; gy = -1f; len = 1f; }
            gx /= len;
            gy /= len;
            float inPlane = FrostShape.Clamp01(input.GravityInPlane);
            float aspect = 16f / 9f;

            for (int i = 0; i < drops.Length; i++)
            {
                if (!drops[i].Alive) continue;
                FrostDrop d = drops[i];
                // Stuck and slipping take turns: a drop holds for a while, then runs for a moment, faster the bigger it is.
                d.Timer -= dt;
                if (d.Timer <= 0f)
                {
                    d.Slipping = !d.Slipping;
                    d.Timer = d.Slipping ? 0.6f + 1.8f * Next() : 1f + 4f * Next() / Math.Max(0.3f, wet + 0.3f);
                }
                float sizeFactor = 0.5f + d.Size / (t.DropPxMax / 1080f * 0.5f);
                float targetSpeed = d.Slipping ? t.DropSpeed * sizeFactor * inPlane : 0f;
                d.Speed += (targetSpeed - d.Speed) * (1f - (float)Math.Exp(-dt / 0.25f));
                float step = d.Speed * dt;
                // The picture's x is measured in heights times the aspect, so a step along x is divided by it.
                d.X += gx * step / aspect;
                d.Y += gy * step;
                d.Trail = d.Slipping ? Math.Min(t.DropTrail, d.Trail + d.Speed * dt) : Math.Max(0f, d.Trail - 0.01f * dt);
                d.Life -= dt * (wet > 0.01f ? 0.2f : 1f);
                if (d.X < -0.05f || d.X > 1.05f || d.Y < -0.05f || d.Y > 1.05f || d.Life <= 0f) d = default(FrostDrop);
                drops[i] = d;
            }
        }
    }
}
