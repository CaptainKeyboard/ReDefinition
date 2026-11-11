using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The look constants of the renderer, every one read through Tuning.Get("precip.<name>", default) by PrecipitationRenderer (the
    // keys, defaults and ranges are in the C22 report for the tuning package). A plain struct, so that the maths and the player use
    // it without the game.
    internal struct PrecipTuning
    {
        // The most flakes one layer holds (before the player's density).
        public float MaxPerLayer;
        // The share of the visibility's extinction the flakes carry (the rest is the fog).
        public float Fill;
        // The size floor in pixels at 1080 lines.
        public float FloorPx;
        // The lowest opacity share a floored flake keeps, Scene and Host.
        public float SceneMinContrast, HostMinContrast;
        // The share of the box at which a wrapping flake fades.
        public float EdgeShare;
        // How hard the gust swings the factor (1 = the depth of WindNoise).
        public float GustScale;
        // The fog's highest rate of change in hertz, the swing of its gust and the period of it.
        public float FogHz, FogGustAmp, FogGustPeriodS;
        // The total of billboards over the layers.
        public float MaxTotal;
        // The most splashes, the range they appear in (m) and the highest ground distance (m) at which they are drawn.
        public float SplashMax, SplashRangeM, SplashMaxGroundM;
        // The factor on the flake colour in Host mode.
        public float HostFlakeGain;
        // The veil colour: the lowest luminance relative colour, and the time the luminance of the image is smoothed over.
        public float VeilLumaFloor, LumaTauS;
        // The share of the image luminance the veil takes (before fogBrightness).
        public float VeilLumaGain;

        internal static PrecipTuning Defaults()
        {
            return new PrecipTuning
            {
                MaxPerLayer = 8000f, Fill = 0.35f, FloorPx = PrecipMath.SizeFloorPx1080, SceneMinContrast = 0.45f, HostMinContrast = 0.85f,
                EdgeShare = 0.12f, GustScale = 1f, FogHz = 3f, FogGustAmp = 0.1f, FogGustPeriodS = 6f, MaxTotal = 20000f, SplashMax = 1500f,
                SplashRangeM = 40f, SplashMaxGroundM = 200f, HostFlakeGain = 1f, VeilLumaFloor = 0.02f, LumaTauS = 0.5f, VeilLumaGain = 1f
            };
        }
    }

    // What the renderer reads of the game for one frame, copied out of WeatherView, the camera and the vessel. The directions are in the
    // local frame of the box: right = east, up = the vessel's up, forward = north.
    internal struct PrecipInput
    {
        public bool Active;
        public PrecipType Type;
        public float Level;
        public float VisibilityM;
        // The wind in the box frame: east, up, north, in m/s, with the gust the field already put in.
        public float WindE, WindU, WindN;
        // Two noise values from 0 to 1 that drive the gust factor and the gust direction (slow, shared with sound).
        public float GustNoise, GustDirNoise;
        // From the view.
        public float FlakeFallMs, FlakeBrightness;
        public int Seed;
        public FlakeLayer Near, Mid, Far;
        public float VeilGroundAlpha, VeilSkyAlpha;
        public Color VeilGroundColour, VeilSkyColour;
        public float SkyGreyStart, SkyGreyFull;
        // The player's settings.
        public float Density;
        public bool TypeEnabled;
        // The camera.
        public float FovYRad, HeightPx;
        // The frame.
        public float Dt;
        // The distance from the camera down to the ground along up, in metres; a large number where unknown.
        public float GroundDistM;
    }

    // One layer's numbers as the shader gets them.
    internal struct PrecipLayerFrame
    {
        public bool Active;
        public int Count;
        public float BoxW, BoxH;
        public float SizeMin, SizeMax, Opacity;
        // The drift of the pattern in box units (0 to 1) on the three axes, and the camera's place in the box.
        public float DriftX, DriftY, DriftZ;
    }

    // Everything the draw needs for this frame.
    internal struct PrecipFrame
    {
        public bool Draw;
        public PrecipType Type;
        public PrecipTypeLook Look;
        public PrecipLayerFrame Near, Mid, Far;
        // The velocity of the particles in the box frame (east, up, north), m/s: fall is negative up.
        public float VelE, VelU, VelN;
        public float GustFactor, GustDirDeg;
        public float VeilGround, VeilSky;
        public float SplashCount;
        public float Colour0, Colour1, Colour2, Light;
        public int TotalCount;
    }

    // The state the maths keeps between frames: the slewed gust, and the drift of each layer's pattern. The only things that are not a
    // function of the current input; both are bounded and continuous.
    internal sealed class PrecipState
    {
        internal float GustFactor = 1f;
        internal float GustDirDeg;
        internal readonly double[] Drift = new double[PrecipMath.LayerCount * 3];
        internal double Time;
        internal bool Primed;

        internal void Reset()
        {
            GustFactor = 1f;
            GustDirDeg = 0f;
            Array.Clear(Drift, 0, Drift.Length);
            Time = 0.0;
            Primed = false;
        }
    }

    // Computes a frame from an input: the pure core of the renderer. It decides what draws (nothing below level 0.04, the near layer
    // from 0.2, nothing for a type the player switched off), how many of each layer (visibility and level), how big (with the floor),
    // and how the pattern drifts, slew-limited so that a gust never changes a thing faster than the rules of WindNoise.
    internal static class PrecipLookParams
    {
        internal static void Compute(ref PrecipInput input, ref PrecipTuning tuning, PrecipState state, out PrecipFrame frame)
        {
            frame = new PrecipFrame { Type = input.Type };
            bool wanted = input.Active && input.TypeEnabled && input.Type != PrecipType.None && input.Level >= PrecipMath.OffBelow && input.Density > 0f;
            if (!wanted)
            {
                // Nothing falls: the gust relaxes so that the next fall starts calm, and no draw is made.
                state.GustFactor = PrecipMath.Slew(state.GustFactor, 1f, PrecipMath.GustSlewPerS, input.Dt);
                state.GustDirDeg = PrecipMath.SlewAngle(state.GustDirDeg, 0f, PrecipMath.GustDirectionRateDegPerS, input.Dt);
                return;
            }

            PrecipTypeLook look = PrecipTypeTable.Get(input.Type);
            frame.Look = look;
            frame.Draw = true;

            // The gust: a factor on the wind and the flake rate, a turn of the direction, both slew-limited.
            float amplitude = PrecipMath.GustAmplitude(input.Level) * tuning.GustScale;
            float factorTarget = PrecipMath.GustTarget(input.GustNoise, amplitude);
            float dirTarget = PrecipMath.GustDirectionTarget(input.GustDirNoise);
            if (!state.Primed)
            {
                state.Primed = true;
                state.GustFactor = 1f;
                state.GustDirDeg = 0f;
            }
            state.GustFactor = PrecipMath.Slew(state.GustFactor, factorTarget, PrecipMath.GustSlewPerS, input.Dt);
            state.GustDirDeg = PrecipMath.SlewAngle(state.GustDirDeg, dirTarget, PrecipMath.GustDirectionRateDegPerS, input.Dt);
            frame.GustFactor = state.GustFactor;
            frame.GustDirDeg = state.GustDirDeg;

            // The velocity: the horizontal wind turned by the gust direction, scaled by the coupling of the type; the fall.
            float rad = state.GustDirDeg * 0.017453292f;
            float c = (float)Math.Cos(rad), s = (float)Math.Sin(rad);
            float wE = input.WindE * look.WindCoupling, wN = input.WindN * look.WindCoupling;
            frame.VelE = wE * c - wN * s;
            frame.VelN = wE * s + wN * c;
            float fall = look.FallMs > 0f ? look.FallMs : input.FlakeFallMs;
            frame.VelU = -Math.Max(fall, 0f);

            // The layers.
            frame.Near = Layer(PrecipMath.Near, ref input.Near, ref input, ref tuning, ref look, state);
            frame.Mid = Layer(PrecipMath.Mid, ref input.Mid, ref input, ref tuning, ref look, state);
            frame.Far = Layer(PrecipMath.Far, ref input.Far, ref input, ref tuning, ref look, state);
            int total = frame.Near.Count + frame.Mid.Count + frame.Far.Count;

            // Within the total budget, every layer gives up the same share.
            if (total > tuning.MaxTotal && total > 0)
            {
                float k = tuning.MaxTotal / total;
                frame.Near.Count = (int)(frame.Near.Count * k);
                frame.Mid.Count = (int)(frame.Mid.Count * k);
                frame.Far.Count = (int)(frame.Far.Count * k);
                total = frame.Near.Count + frame.Mid.Count + frame.Far.Count;
            }
            frame.TotalCount = total;

            // The drift of each layer's pattern, in box units, integrated so that a change of wind never jumps.
            double dt = Math.Max(input.Dt, 0f);
            state.Time += dt;
            Advance(state, 0, ref frame.Near, ref frame, dt);
            Advance(state, 1, ref frame.Mid, ref frame, dt);
            Advance(state, 2, ref frame.Far, ref frame, dt);

            // The veil: only where the view's table gives one; the gust adds to it.
            float gustVeil = PrecipMath.GustVeilAlpha(state.GustFactor);
            frame.VeilGround = input.VeilGroundAlpha > 0f ? Math.Min(input.VeilGroundAlpha + gustVeil, 0.97f) : 0f;
            frame.VeilSky = input.VeilSkyAlpha > 0f ? Math.Min(Math.Max(input.VeilSkyAlpha + gustVeil, frame.VeilGround), 0.97f) : 0f;

            // Splashes: drops that reach the ground, when the ground is near.
            bool splashes = look.Splash > 0f && input.GroundDistM > 0f && input.GroundDistM < tuning.SplashMaxGroundM;
            frame.SplashCount = splashes ? Math.Min(tuning.SplashMax, look.Splash * 0.25f * tuning.SplashMax * PrecipMath.LevelScale(input.Level) * input.Density) : 0f;

            frame.Colour0 = look.R;
            frame.Colour1 = look.G;
            frame.Colour2 = look.B;
            frame.Light = input.FlakeBrightness;
        }


        private static PrecipLayerFrame Layer(int index, ref FlakeLayer l, ref PrecipInput input, ref PrecipTuning tuning, ref PrecipTypeLook look, PrecipState state)
        {
            var f = new PrecipLayerFrame();
            f.Active = PrecipMath.LayerActive(index, input.Level) && l.Width > 0f && l.Share > 0f;
            if (!f.Active) return f;
            f.BoxW = l.Width;
            f.BoxH = l.Width;
            f.SizeMin = l.SizeMin * look.SizeScale;
            f.SizeMax = l.SizeMax * look.SizeScale;
            f.Opacity = Math.Min(l.Opacity * look.Opacity, 1f);
            float meanSize = 0.5f * (f.SizeMin + f.SizeMax);
            float typicalDistance = Math.Max(l.Width * 0.5f, 0.5f);
            float seen = PrecipMath.SizeWithFloor(meanSize, typicalDistance, input.FovYRad, input.HeightPx, tuning.FloorPx);
            // The count comes from the visibility and the level (a floored flake covers more, so fewer carry the same extinction); the
            // gust factor swings it within the slew limit.
            f.Count = (int)Math.Round(PrecipMath.FlakeCount(input.Level, input.VisibilityM, l.Width, l.Width, l.Share, seen,
                tuning.Fill, tuning.MaxPerLayer, input.Density) * Math.Min(state.GustFactor, 1.25f));
            return f;
        }

        private static void Advance(PrecipState state, int index, ref PrecipLayerFrame f, ref PrecipFrame frame, double dt)
        {
            if (!f.Active) return;
            state.Drift[index * 3 + 0] = Wrap01(state.Drift[index * 3 + 0] + frame.VelE * dt / f.BoxW);
            state.Drift[index * 3 + 1] = Wrap01(state.Drift[index * 3 + 1] + frame.VelU * dt / f.BoxH);
            state.Drift[index * 3 + 2] = Wrap01(state.Drift[index * 3 + 2] + frame.VelN * dt / f.BoxW);
            f.DriftX = (float)state.Drift[index * 3 + 0];
            f.DriftY = (float)state.Drift[index * 3 + 1];
            f.DriftZ = (float)state.Drift[index * 3 + 2];
        }

        private static double Wrap01(double v)
        {
            return v - Math.Floor(v);
        }
    }
}
