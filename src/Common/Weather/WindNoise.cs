using System;
using UnityEngine;

namespace ReDefinition.Core
{
    // The one gust function of the mod and the rates at which what a gust changes may change. A gust is a travelling wave keyed to
    // the position along the wind, so a camera and a vessel that stand together feel the same gust; forces, pictures and sound read
    // the same value. Pure, no allocation, any thread.
    public static class WindNoise
    {
        // The depth of the gusts (their share of the mean wind) at the bottom of the scale and at the top.
        public const float DepthAtFlurries = 0.1f;
        public const float DepthAtBlizzard = 0.4f;
        // The most the visibility may change per second, as a share of its value.
        public const float MaxVisibilitySlewPerS = 0.25f;
        // The most the direction of the falling flakes may turn per second, in degrees.
        public const float MaxFlakeDirectionDegPerS = 30f;
        // The most the wind may change per second, in m/s per s.
        public const float MaxWindSlewMsPerS = 8f;

        // The two components of the wave: periods in seconds, and the speed at which the pattern travels along the wind.
        private const double PeriodA = 9.0, PeriodB = 5.3, TravelMs = 12.0;
        private const double TwoPi = 6.283185307179586;

        // The depth of the gusts at a level: 0.1 up to the first band and rising to 0.4 at level 1.
        public static float Depth(float level01)
        {
            float t = (level01 - PrecipScale.FlurriesFrom) / (1f - PrecipScale.FlurriesFrom);
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            return DepthAtFlurries + (DepthAtBlizzard - DepthAtFlurries) * t;
        }

        // The wave at a time and place, from -1 to 1. windDir is the direction the wind blows to (it need not be normalised; a zero
        // vector leaves the wave a function of time only).
        public static float Wave(double ut, Vector3 bodyFixedPos, Vector3 windDir, int seed)
        {
            double along = 0.0;
            double len = Math.Sqrt((double)windDir.x * windDir.x + (double)windDir.y * windDir.y + (double)windDir.z * windDir.z);
            if (len > 1e-9)
            {
                along = ((double)bodyFixedPos.x * windDir.x + (double)bodyFixedPos.y * windDir.y + (double)bodyFixedPos.z * windDir.z) / len;
            }
            double a = TwoPi * (along / (PeriodA * TravelMs) - ut / PeriodA) + Phase(seed, 1);
            double b = TwoPi * (along / (PeriodB * TravelMs) - ut / PeriodB) + Phase(seed, 2);
            return (float)(0.6 * Math.Sin(a) + 0.4 * Math.Sin(b));
        }

        // The factor on the mean wind speed at a time and place: 1 plus the depth for the level times the wave.
        public static float Gust(double ut, Vector3 bodyFixedPos, Vector3 windDir, float level01, int seed)
        {
            return 1f + Depth(level01) * Wave(ut, bodyFixedPos, windDir, seed);
        }

        // The wave as a phase from 0 to 1, the number WeatherView.GustPhase publishes for sound and pictures.
        public static float Phase01(double ut, Vector3 bodyFixedPos, Vector3 windDir, int seed)
        {
            return 0.5f + 0.5f * Wave(ut, bodyFixedPos, windDir, seed);
        }

        // The visibility one step on: it moves towards the target by at most MaxVisibilitySlewPerS of its value per second.
        public static float LimitVisibility(float previousM, float targetM, float dtSeconds)
        {
            float step = previousM * MaxVisibilitySlewPerS * dtSeconds;
            return Toward(previousM, targetM, step);
        }

        // The flake direction in degrees one step on: it turns towards the target the short way by at most MaxFlakeDirectionDegPerS. The
        // result is not wrapped into 0 to 360; the caller wraps it where it needs to.
        public static float LimitFlakeDirection(float previousDeg, float targetDeg, float dtSeconds)
        {
            float delta = (float)(((targetDeg - previousDeg) % 360.0 + 540.0) % 360.0 - 180.0);
            float step = MaxFlakeDirectionDegPerS * dtSeconds;
            float move = delta < -step ? -step : delta > step ? step : delta;
            return previousDeg + move;
        }

        // The wind speed one step on: it moves towards the target by at most MaxWindSlewMsPerS per second.
        public static float LimitWind(float previousMs, float targetMs, float dtSeconds)
        {
            return Toward(previousMs, targetMs, MaxWindSlewMsPerS * dtSeconds);
        }

        private static float Toward(float from, float to, float maxStep)
        {
            if (maxStep < 0f) maxStep = 0f;
            float d = to - from;
            return d > maxStep ? from + maxStep : d < -maxStep ? from - maxStep : to;
        }

        // A fixed phase in radians for each seed and component.
        private static double Phase(int seed, int component)
        {
            uint h = (uint)seed * 2654435761u + (uint)component * 2246822519u;
            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;
            h *= 3266489917u;
            h ^= h >> 16;
            return TwoPi * (h / 4294967296.0);
        }
    }
}
