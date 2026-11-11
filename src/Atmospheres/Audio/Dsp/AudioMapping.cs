using System;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres.Audio
{
    // The pure map from the weather scalars of the field to AudioParams: the sound level column of the precipitation table, the
    // gust depth of WindNoise, and the type of the fall (hiss for snow and sleet, grit for dust, sand and ash; rain belongs to other
    // mods and is not ours). The rest of AudioParams (interior, snow cover, cell, volumes) is the runtime's to set. No Unity objects.
    public static class AudioMapping
    {
        // Key points of the sound level column: precipitation level against sound level (plan 09, 3.3).
        private static readonly float[] LevelKeys = { 0f, 0.1f, 0.25f, 0.4f, 0.6f, 0.8f, 1f };
        private static readonly float[] SoundKeys = { 0f, 0.05f, 0.15f, 0.3f, 0.55f, 0.8f, 1f };

        // The sound level for a precipitation level from 0 to 1: linear between the key points, monotone.
        public static float SoundLevel(float level01)
        {
            if (!(level01 > 0f)) return 0f;
            if (level01 >= 1f) return 1f;
            for (int i = 1; i < LevelKeys.Length; i++)
            {
                if (level01 <= LevelKeys[i])
                {
                    float t = (level01 - LevelKeys[i - 1]) / (LevelKeys[i] - LevelKeys[i - 1]);
                    return SoundKeys[i - 1] + (SoundKeys[i] - SoundKeys[i - 1]) * t;
                }
            }
            return 1f;
        }

        // Whether this mod makes the sound of a fall of this type.
        public static bool IsHeard(PrecipType type)
        {
            switch (type)
            {
                case PrecipType.Snow:
                case PrecipType.Sleet:
                case PrecipType.Dust:
                case PrecipType.Sand:
                case PrecipType.Ash:
                    return true;
                default:
                    return false;
            }
        }

        // 1 where the fall grates (dust, sand, ash), 0 where it hisses.
        public static float GritFor(PrecipType type)
        {
            return type == PrecipType.Dust || type == PrecipType.Sand || type == PrecipType.Ash ? 1f : 0f;
        }

        // Sets the wind, gust, intensity and grit fields from the weather at the listener. gustWave is WindNoise.Wave at the
        // listener's place, from -1 to 1.
        public static void Fill(ref AudioParams p, PrecipType type, float level01, float windSpeedMs, float gustWave)
        {
            p.windSpeed = windSpeedMs;
            p.gust = gustWave;
            p.gustDepth = WindNoise.Depth(level01);
            p.intensity = IsHeard(type) ? SoundLevel(level01) : 0f;
            p.grit = GritFor(type);
        }
    }
}
