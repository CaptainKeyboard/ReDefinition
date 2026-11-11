using System;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The procedural sheet the billboards are drawn with: a 2 x 2 atlas of 32-pixel cells (flake, streak, grain, droplet), white with the
    // shape in the alpha channel. Painted in C#, nothing is shipped. The shape functions are separate and pure, so that a test checks
    // them without the engine.
    internal static class PrecipSheets
    {
        internal const int CellSize = 32;

        // The alpha of a shape at a point of its cell, u and v from -1 to 1 (v up along the streak).
        internal static float Alpha(PrecipSheetKind kind, float u, float v)
        {
            switch (kind)
            {
                case PrecipSheetKind.Flake:
                {
                    float r = (float)Math.Sqrt(u * u + v * v);
                    float angle = (float)Math.Atan2(v, u);
                    // A round soft disc whose edge is faintly six-lobed.
                    float edge = 0.78f + 0.12f * (float)Math.Cos(6f * angle);
                    return Smooth(1f - r / edge) * 0.95f;
                }
                case PrecipSheetKind.Streak:
                {
                    // Thin across, bright at the head (the leading end, v > 0, the way the drop moves) and fading to a tail behind.
                    float across = (float)Math.Exp(-u * u * 18f);
                    float along = Smooth(1f - Math.Abs(v)) * (0.45f + 0.55f * Clamp01(0.5f + v * 0.5f));
                    return across * along;
                }
                case PrecipSheetKind.Grain:
                {
                    float r = (float)Math.Sqrt(u * u + v * v);
                    return Smooth((0.8f - r) / 0.25f);
                }
                case PrecipSheetKind.Droplet:
                {
                    // A round head at the leading end (v > 0) and a short tail behind it.
                    float head = (float)Math.Sqrt(u * u + (v - 0.35f) * (v - 0.35f));
                    float a = Smooth((0.45f - head) / 0.2f);
                    float tail = (float)Math.Exp(-u * u * 40f) * Clamp01((0.1f - v) * 1.4f) * Clamp01(1f + v) * 0.5f;
                    return Math.Min(1f, a + tail);
                }
                default:
                    return 0f;
            }
        }

        // The sheet as pixels, 2 x CellSize wide and high; cell k is at column k % 2, row k / 2.
        internal static byte[] Pixels()
        {
            int size = CellSize * 2;
            var bytes = new byte[size * size * 4];
            for (int cell = 0; cell < 4; cell++)
            {
                int cx = (cell % 2) * CellSize, cy = (cell / 2) * CellSize;
                for (int y = 0; y < CellSize; y++)
                {
                    for (int x = 0; x < CellSize; x++)
                    {
                        float u = ((x + 0.5f) / CellSize) * 2f - 1f;
                        float v = ((y + 0.5f) / CellSize) * 2f - 1f;
                        float a = Clamp01(Alpha((PrecipSheetKind)cell, u, v));
                        int i = ((cy + y) * size + cx + x) * 4;
                        bytes[i] = 255;
                        bytes[i + 1] = 255;
                        bytes[i + 2] = 255;
                        bytes[i + 3] = (byte)Math.Round(a * 255f);
                    }
                }
            }
            return bytes;
        }

        // The sheet as a texture the caller owns.
        internal static Texture2D Make()
        {
            int size = CellSize * 2;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "ReDefinition Atmospheres precip sheet",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            texture.LoadRawTextureData(Pixels());
            texture.Apply(false, false);
            return texture;
        }

        private static float Smooth(float t)
        {
            t = Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        private static float Clamp01(float v)
        {
            return v < 0f ? 0f : v > 1f ? 1f : v;
        }
    }
}
