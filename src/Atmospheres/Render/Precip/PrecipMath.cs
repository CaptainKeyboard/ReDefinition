using System;

namespace ReDefinition.Atmospheres
{
    // The pure numbers behind the precipitation renderer: the hash that places a flake, the counts a level and a visibility allow, the
    // size floor, and the rate limits of everything a gust changes. No engine type, no allocation, any thread; the player links this
    // file as it is, and the unit tests run it without the game.
    //
    // The renderer places every flake from a hash of its lattice cell and the seed, in a box that is centred on the camera and wraps,
    // so a flake needs no state: the same cell, seed and time always give the same place (deterministic, no per-frame random).
    internal static class PrecipMath
    {
        // The three layers, near to far.
        internal const int LayerCount = 3;
        internal const int Near = 0, Mid = 1, Far = 2;

        // Below this level nothing is drawn: no draw call, no marker.
        internal const float OffBelow = 0.04f;
        // The near layer draws from this level.
        internal const float NearFrom = 0.2f;
        // The counts rise with level to this power.
        internal const float CountExponent = 1.3f;

        // The smallest a flake may look on screen at 1080 lines, in pixels; other heights scale it.
        internal const float SizeFloorPx1080 = 3f;

        // Visibility is the distance at which a contrast of 2 % is left (Koschmieder): extinction = 3.912 / visibility.
        internal const float KoschmiederNumerator = 3.912f;

        // What a gust may change, and how fast. Gust factor: 25 % of the factor a second; direction: at most 12 degrees off the wind,
        // turning 30 degrees a second; the veil gains 0.03 per 0.1 of gust; the fog density moves at most 12 % with a period of 3
        // seconds or longer.
        internal const float GustSlewPerS = 0.25f;
        internal const float GustDirectionMaxDeg = 12f;
        internal const float GustDirectionRateDegPerS = 30f;
        internal const float VeilAlphaPerGust = 0.3f;
        internal const float FogAmplitudeMax = 0.12f;
        internal const float FogPeriodMinS = 3f;

        // --- The hash ---

        // A 32-bit integer hash with good avalanche: the lowbias32 mixer of Chris Wellons' hash-prospector (The Unlicense,
        // https://github.com/skeeto/hash-prospector). The same function is in the shader.
        internal static uint Hash(uint x)
        {
            unchecked
            {
                x ^= x >> 16;
                x *= 0x7feb352dU;
                x ^= x >> 15;
                x *= 0x846ca68bU;
                x ^= x >> 16;
                return x;
            }
        }

        // The hash of a lattice cell and a seed.
        internal static uint Hash(int cell, int seed, int salt)
        {
            unchecked
            {
                return Hash(Hash((uint)cell + 0x9e3779b9U * (uint)(salt + 1)) ^ (uint)seed * 0x85ebca6bU);
            }
        }

        // A hash as a number from 0 up to but not including 1.
        internal static float Unit(uint hash)
        {
            return (hash >> 8) * (1f / 16777216f);
        }

        // The place of a flake in the box, each axis from 0 to 1 (before the drift and the camera are applied).
        internal static void Place(int cell, int seed, out float x, out float y, out float z)
        {
            x = Unit(Hash(cell, seed, 0));
            y = Unit(Hash(cell, seed, 1));
            z = Unit(Hash(cell, seed, 2));
        }

        // The fractional part, always from 0 to 1 (also for negative numbers).
        internal static float Frac(float v)
        {
            return v - (float)Math.Floor(v);
        }

        // Where a flake is in the box after the drift moved it and the camera moved on: its place plus the drift, minus the camera's
        // place, wrapped into 0..1 on one axis. The box is centred on the camera, so the offset from the camera is (result - 0.5) times
        // the box size.
        internal static float Wrap(float place, float drift, float camera)
        {
            return Frac(place + drift - camera);
        }

        // The fade at the box's edge, so that a flake that wraps does not pop: 0 at the edge, 1 from the given share inwards.
        internal static float EdgeFade(float wrapped, float edgeShare)
        {
            float d = Math.Min(wrapped, 1f - wrapped) / Math.Max(edgeShare, 1e-4f);
            if (d <= 0f) return 0f;
            if (d >= 1f) return 1f;
            return d * d * (3f - 2f * d);
        }

        // --- Counts and sizes ---

        // The level's count factor: level ^ 1.3.
        internal static float LevelScale(float level)
        {
            if (!(level > 0f)) return 0f;
            return (float)Math.Pow(Math.Min(level, 1f), CountExponent);
        }

        // Whether a layer draws at a level: nothing below 0.04, the near layer only from 0.2.
        internal static bool LayerActive(int layer, float level)
        {
            if (!(level >= OffBelow)) return false;
            if (layer == Near) return level >= NearFrom;
            return true;
        }

        // The size a flake looks, in metres: its own size, raised so that it covers at least floorPx pixels. distanceM is the distance
        // from the camera along the view axis, fovYRad the vertical field of view and heightPx the height of the picture it is drawn in.
        internal static float SizeWithFloor(float sizeM, float distanceM, float fovYRad, float heightPx, float floorPxAt1080)
        {
            if (!(distanceM > 0f) || !(heightPx > 0f)) return sizeM;
            float metresPerPixel = 2f * distanceM * (float)Math.Tan(fovYRad * 0.5f) / heightPx;
            float floorM = floorPxAt1080 * (heightPx / 1080f) * metresPerPixel;
            return Math.Max(sizeM, floorM);
        }

        // The opacity share a flake that was raised to the floor keeps: its area shrinks relative to the raised one, so the brightness it
        // adds to a pixel stays about the same; it never falls below minContrast (low contrast in Scene mode, where the upscaler
        // would otherwise smear a hard 3 px dot).
        internal static float FloorContrast(float sizeM, float sizeFlooredM, float minContrast)
        {
            if (!(sizeFlooredM > 0f) || sizeFlooredM <= sizeM) return 1f;
            float ratio = sizeM / sizeFlooredM;
            return Math.Max(ratio * ratio, minContrast);
        }

        // The extinction per metre that the visibility asks for.
        internal static float ExtinctionPerM(float visibilityM)
        {
            return KoschmiederNumerator / Math.Max(visibilityM, 1f);
        }

        // The number of flakes in one layer's box. Two limits apply and the smaller wins: the count that makes the flakes carry the
        // share `fill` of the extinction the visibility asks for (extinction x box volume / cross-section of one flake), and the
        // count the level allows (maxPerLayer x share x level^1.3). Fog and flakes stay consistent in clear air (few flakes where
        // the visibility is high); at the top of the scale the cap rules. density is the player's precipDensity.
        internal static int FlakeCount(float level, float visibilityM, float boxWidthM, float boxHeightM, float share, float sizeM,
            float fill, float maxPerLayer, float density)
        {
            if (!(level >= OffBelow) || !(share > 0f) || !(density > 0f)) return 0;
            float volume = boxWidthM * boxWidthM * boxHeightM;
            float cross = 0.7853982f * sizeM * sizeM;
            float byVisibility = fill * ExtinctionPerM(visibilityM) * volume / Math.Max(cross, 1e-9f) * share;
            float byLevel = maxPerLayer * share * LevelScale(level);
            float count = Math.Min(byVisibility, byLevel) * density;
            if (count < 1f) return level >= OffBelow ? 1 : 0;
            return (int)Math.Min(count, maxPerLayer * 2f);
        }

        // The alpha one of two cross-fading layers draws with so that the two together cover `alpha`: 1 - (1 - alpha) ^ weight.
        internal static float VeilShare(float alpha, float weight)
        {
            if (!(alpha > 0f) || !(weight > 0f)) return 0f;
            if (weight >= 1f) return alpha;
            if (alpha >= 1f) return weight;
            return 1f - (float)Math.Pow(1f - alpha, weight);
        }

        // --- Gusts ---

        // The gust factor a noise value gives: 1 + amplitude x (noise - 0.5) x 2, noise from 0 to 1, so the factor runs over
        // 1 +- amplitude. The amplitude rises with the level.
        internal static float GustTarget(float noise01, float amplitude)
        {
            return 1f + amplitude * (noise01 * 2f - 1f);
        }

        // The amplitude of the gust at a level (the share of the mean it swings): 0.1 at the bottom of the scale and 0.4 at the top,
        // the depth the wind itself has (WindNoise.Depth).
        internal static float GustAmplitude(float level)
        {
            float t = (level - 0.04f) / 0.96f;
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            return 0.1f + 0.3f * t;
        }

        // A value one step on towards a target by at most `rate` per second (absolute).
        internal static float Slew(float previous, float target, float rate, float dt)
        {
            float step = Math.Max(rate, 0f) * Math.Max(dt, 0f);
            float d = target - previous;
            if (d > step) return previous + step;
            if (d < -step) return previous - step;
            return target;
        }

        // An angle in degrees one step on towards a target the short way by at most `rateDegPerS`.
        internal static float SlewAngle(float previousDeg, float targetDeg, float rateDegPerS, float dt)
        {
            float delta = (float)(((targetDeg - previousDeg) % 360.0 + 540.0) % 360.0 - 180.0);
            float step = Math.Max(rateDegPerS, 0f) * Math.Max(dt, 0f);
            float move = delta < -step ? -step : delta > step ? step : delta;
            return previousDeg + move;
        }

        // The direction a gust turns the flakes to, in degrees off the wind, from a second noise value.
        internal static float GustDirectionTarget(float noise01)
        {
            return (noise01 * 2f - 1f) * GustDirectionMaxDeg;
        }

        // The veil alpha a gust adds: 0.03 per 0.1 of gust (the gust being the factor's excess over 1; a lull adds nothing).
        internal static float GustVeilAlpha(float gustFactor)
        {
            return Math.Max(gustFactor - 1f, 0f) * VeilAlphaPerGust;
        }

        // The fog density factor at a time: 1 + amplitude x sin(2 pi t / period), the amplitude at most 12 % and the period at least 3
        // seconds, however the caller asks.
        internal static float FogFactor(double seconds, float amplitude, float periodS)
        {
            float a = Math.Min(Math.Max(amplitude, 0f), FogAmplitudeMax);
            float p = Math.Max(periodS, FogPeriodMinS);
            return 1f + a * (float)Math.Sin(2.0 * Math.PI * (seconds % p) / p);
        }

        // The fastest the written fog density may change, as a share of its value per second: the slope of a 12 % swing at 3 Hz.
        internal static float FogSlewPerS(float hertz)
        {
            return 2f * (float)Math.PI * Math.Max(hertz, 0f) * FogAmplitudeMax;
        }

        // The fog density one step on towards a target by at most FogSlewPerS (relative to the larger of the two, so a start from
        // zero still moves).
        internal static float FogStep(float previous, float target, float hertz, float dt)
        {
            float reference = Math.Max(Math.Max(previous, target), 1e-6f);
            return Slew(previous, target, FogSlewPerS(hertz) * reference, dt);
        }
    }
}
