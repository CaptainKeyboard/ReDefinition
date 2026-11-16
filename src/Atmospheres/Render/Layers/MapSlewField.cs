using System;

namespace ReDefinition.Atmospheres
{
    // The slew limit of the layer maps in continuous time, pure (the CPU twin of the layer shaders' map reads). A map is held as two byte maps, from (the value the map showed when its
    // newest goal arrived) and to (that goal), and the value shown t seconds after the goal arrived is, texel by texel,
    //   from + clamp(to - from, -rate * t, +rate * t)          (in shares of full scale)
    // which is the rule of MapSlew.StepMap with the step size taken from the time since the goal instead of the time since the last frame. No texel moves faster than rate, however the
    // frames fall, and the CPU never steps a texel: the shader evaluates this at the texels it reads, and a new goal's "from" is this function at the moment the goal takes over
    // (Handover), made once a goal by the maps' worker. The one difference to MapSlew.StepByte is the 8-bit snapping of the old path (a step under one level moved a texel by one level
    // per frame, whatever the frame time); here the value is continuous, so the shown value differs from the old path's by less than one level wherever the old path's step was whole levels.
    internal static class MapSlewField
    {
        // The share of full scale a texel may have moved when `ageS` seconds have passed since its goal arrived, at most 1.
        internal static float Reach01(float rate, double ageS)
        {
            if (!(rate > 0f) || !(ageS > 0.0)) return 0f;
            double r = rate * ageS;
            return r >= 1.0 ? 1f : (float)r;
        }

        // The shown value (0 to 1) of a texel: the shader's formula.
        internal static float Value(byte from, byte to, float reach01)
        {
            float f = from * (1f / 255f), d = (to - from) * (1f / 255f);
            return f + Math.Max(-reach01, Math.Min(reach01, d));
        }

        // The same as a byte (rounded to the nearest level).
        internal static byte ValueByte(byte from, byte to, float reach01)
        {
            float levels = reach01 * 255f;
            int d = to - from;
            if (d <= levels && d >= -levels) return to;
            return (byte)Math.Max(0, Math.Min(255, (int)Math.Round(from + (d > 0 ? levels : -levels))));
        }

        // into[i] = the value shown at the reach (the "from" of the next goal). The arrays have the same length.
        internal static void Handover(byte[] from, byte[] to, float reach01, byte[] into)
        {
            float levels = reach01 * 255f;
            int step = (int)Math.Round(levels);
            int n = into.Length;
            for (int i = 0; i < n; i++)
            {
                int f = from[i], t = to[i], d = t - f;
                into[i] = d <= levels && d >= -levels ? (byte)t : (byte)(d > 0 ? f + step : f - step);
            }
        }

        // The texel pairs the GPU array holds: pairs[2 i] = from, pairs[2 i + 1] = to (TextureFormat.RG16).
        internal static void Interleave(byte[] from, byte[] to, byte[] pairs)
        {
            int n = from.Length;
            for (int i = 0, j = 0; i < n; i++, j += 2)
            {
                pairs[j] = from[i];
                pairs[j + 1] = to[i];
            }
        }

        // The pairs of a map that is not slewing (from = to = bytes).
        internal static void InterleaveSame(byte[] bytes, byte[] pairs)
        {
            int n = bytes.Length;
            for (int i = 0, j = 0; i < n; i++, j += 2)
            {
                byte b = bytes[i];
                pairs[j] = b;
                pairs[j + 1] = b;
            }
        }
    }
}
