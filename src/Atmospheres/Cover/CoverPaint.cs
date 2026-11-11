using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // How cover is painted into a vertex colour. The ground shaders multiply the vertex colour into the terrain, and Parallax reads its
    // alpha for nothing the atmospheres know of, so the colour's red, green and blue move toward the layer's colour and the alpha stays.
    // The colour comes from the layer's definition (CoverDef), not from a constant: snow is a cool white, dust and sand are the cfg's.
    internal static class CoverPaint
    {
        // The colour with that part of it turned to the layer's colour; alpha as it was.
        internal static Color Blend(Color original, CoverDef def, float amount)
        {
            if (!(amount > 0)) return original;
            if (amount > 1) amount = 1;
            return new Color(original.r + (def.Red - original.r) * amount,
                             original.g + (def.Green - original.g) * amount,
                             original.b + (def.Blue - original.b) * amount,
                             original.a);
        }

        // The same on bytes, for the colours a mesh keeps.
        internal static Color32 Blend(Color32 original, CoverDef def, float amount)
        {
            if (!(amount > 0)) return original;
            if (amount > 1) amount = 1;
            return new Color32(Mix(original.r, def.Red, amount), Mix(original.g, def.Green, amount), Mix(original.b, def.Blue, amount), original.a);
        }

        private static byte Mix(byte original, float layer, float amount)
        {
            float value = original + (layer * 255f - original) * amount;
            return (byte)(value < 0 ? 0 : value > 255 ? 255 : value + 0.5f);
        }
    }
}
