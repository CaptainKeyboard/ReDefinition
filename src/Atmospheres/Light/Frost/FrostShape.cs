using System;

namespace ReDefinition.Atmospheres
{
    // The geometry of the rim frost, in one place for the shader (ReDefinitionAtmospheresFrost.shader, function FrostAlpha) and for the
    // tests, which run the same arithmetic on the CPU against the atlas bytes. Pure, no engine type.
    //
    // The picture is described by r, the distance from its centre in units of the half-diagonal (0 at the centre, 1 in a corner). The
    // frost covers the picture outside the radius 1 - growth; the atlas mask makes that edge ragged (crystals reach in, gaps reach
    // out), and a hard clear disc around the centre keeps the middle of the view readable whatever the numbers say.
    //
    // The design table (the reference the player's coverage is judged against) is GeometricCoverage: the share of the picture outside
    // the radius 1 - growth. At 16:9 and the default growth curve: exposure 0 gives 0 %, 0.4 about 7 %, 1.0 about 38 % (the exact figures
    // are printed by the unit test FrostShapeTests.DesignTable).
    internal static class FrostShape
    {
        // The most the frost reaches in from the corners, as a share of the half-diagonal. No tuning value goes above it.
        internal const float MaxGrowth = 0.35f;

        // GLSL/HLSL smoothstep.
        internal static float Smooth(float a, float b, float x)
        {
            float t = (x - a) / (b - a);
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            return t * t * (3f - 2f * t);
        }

        internal static float Clamp01(float x)
        {
            return x < 0f ? 0f : x > 1f ? 1f : x;
        }

        // The half-diagonal of a picture one unit high and aspect wide, in picture heights.
        internal static float HalfDiagonal(float aspect)
        {
            return 0.5f * (float)Math.Sqrt(aspect * aspect + 1f);
        }

        // The distance of a point of the picture (u and v from 0 to 1) from the centre, in half-diagonals.
        internal static float Radius(float u, float v, float aspect)
        {
            float x = (u - 0.5f) * aspect, y = v - 0.5f;
            return (float)Math.Sqrt(x * x + y * y) / HalfDiagonal(aspect);
        }

        // How far the frost reaches in at an exposure: share of the half-diagonal, never above MaxGrowth.
        internal static float Growth(float exposure, ref FrostTuning t)
        {
            float g = Math.Min(Math.Max(t.MaxGrowth, 0f), MaxGrowth) * (float)Math.Pow(Clamp01(exposure), t.GrowthPow);
            return g > MaxGrowth ? MaxGrowth : g;
        }

        // Where the atlas is read for a point of the picture, in atlas units (wrapping). The picture is measured in heights, so the tiles
        // are square on screen at any aspect.
        internal static void AtlasUv(float u, float v, float aspect, float scale, out float au, out float av)
        {
            au = (u - 0.5f) * aspect * scale + 0.5f;
            av = (v - 0.5f) * scale + 0.5f;
        }

        // The opacity of the rim frost at a point. r is its distance from the centre; mask, nx and ny are the atlas channels R, B and A
        // (0 to 1) at that point. The same function is FrostAlpha in the shader.
        internal static float Alpha(float r, float growth, float exposure, ref FrostTuning t, float mask, float nx, float ny)
        {
            if (growth <= 0.0005f) return 0f;
            float jag = t.Jag * (0.4f + growth);
            float thr = (r - (1f - growth)) + jag * (mask - 0.5f) * 2f;
            float geo = Smooth(0f, 0.05f, thr);
            geo *= Smooth(t.Centre, t.Centre + 0.12f, r);
            float bx = nx * 2f - 1f, by = ny * 2f - 1f;
            float relief = Clamp01((float)Math.Sqrt(bx * bx + by * by) * 1.6f);
            float thick = 0.62f + 0.38f * Clamp01(exposure);
            return geo * t.Opacity * thick * (0.6f + 0.4f * relief);
        }

        // The design table: the share of a picture of that aspect outside the radius 1 - growth (integrated by columns).
        internal static float GeometricCoverage(float growth, float aspect)
        {
            if (growth <= 0f) return 0f;
            double radius = (1.0 - growth) * HalfDiagonal(aspect);
            const int Columns = 2000;
            double half = aspect * 0.5, inside = 0.0, dx = aspect / Columns;
            for (int i = 0; i < Columns; i++)
            {
                double x = -half + (i + 0.5) * dx;
                double chord = radius * radius - x * x;
                double h = chord <= 0.0 ? 0.0 : Math.Min(0.5, Math.Sqrt(chord));
                inside += 2.0 * h * dx;
            }
            return (float)(1.0 - inside / aspect);
        }

        // The coverage the shader's arithmetic gives over the atlas bytes: the share of a gridW x gridH grid of picture points whose
        // frost opacity is at least threshold. atlas is the RGBA32 of FrostAtlas.Build.
        internal static float CoverageOf(byte[] atlas, float exposure, float strength, float aspect, ref FrostTuning t, float threshold, int gridW, int gridH)
        {
            float growth = Growth(exposure, ref t) * Math.Min(Math.Max(strength, 0f), 1f);
            int hit = 0;
            for (int j = 0; j < gridH; j++)
            {
                float v = (j + 0.5f) / gridH;
                for (int i = 0; i < gridW; i++)
                {
                    float u = (i + 0.5f) / gridW;
                    float au, av;
                    AtlasUv(u, v, aspect, t.AtlasScale, out au, out av);
                    float m, g, b, a;
                    FrostAtlas.Sample(atlas, au, av, out m, out g, out b, out a);
                    float alpha = Alpha(Radius(u, v, aspect), growth, exposure, ref t, m, b, a);
                    if (alpha >= threshold) hit++;
                }
            }
            return hit / (float)(gridW * gridH);
        }
    }
}
