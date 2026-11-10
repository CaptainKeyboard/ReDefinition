using UnityEngine;

namespace ReDefinition.Seasons
{
    // How snow is painted into a vertex colour. The ground shaders multiply the vertex colour into the terrain, and Parallax reads
    // its alpha for nothing the seasons know of, so the colour's red, green and blue move toward the snow colour and the alpha stays.
    internal static class SnowPaint
    {
        // The snow colour, in one place: slightly cool white. A brighter colour would not fit into a vertex colour.
        internal const float SnowRed = 0.93f;
        internal const float SnowGreen = 0.95f;
        internal const float SnowBlue = 0.98f;

        // The part of the colour that becomes snow: the mask's cover times the strength setting, from 0 to 1.
        internal static float Amount(double coverage, float strength)
        {
            if (double.IsNaN(coverage) || float.IsNaN(strength)) return 0;
            double amount = coverage * strength;
            return amount < 0 ? 0 : amount > 1 ? 1 : (float)amount;
        }

        // The colour with that part of it turned to snow; alpha as it was.
        internal static Color Blend(Color original, float amount)
        {
            if (!(amount > 0)) return original;
            if (amount > 1) amount = 1;
            return new Color(original.r + (SnowRed - original.r) * amount,
                             original.g + (SnowGreen - original.g) * amount,
                             original.b + (SnowBlue - original.b) * amount,
                             original.a);
        }

        // The same on bytes, for the colours a mesh keeps.
        internal static Color32 Blend(Color32 original, float amount)
        {
            if (!(amount > 0)) return original;
            if (amount > 1) amount = 1;
            return new Color32(Mix(original.r, SnowRed, amount), Mix(original.g, SnowGreen, amount), Mix(original.b, SnowBlue, amount), original.a);
        }

        private static byte Mix(byte original, float snow, float amount)
        {
            float value = original + (snow * 255f - original) * amount;
            return (byte)(value < 0 ? 0 : value > 255 ? 255 : value + 0.5f);
        }
    }
}
