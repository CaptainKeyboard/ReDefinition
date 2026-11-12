using System;
using System.Collections.Generic;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // What the orbit and map view show of the weather field, pure (plan 09 section 3.7): the picture is the field's coverage as grey
    // clouds. Flurries and light snow are faint grey without a swirl; the swirl of EveSwirlMath is drawn only where the precipitation
    // reaches Moderate. The swirl cells are the local maxima of the precipitation level map, found here from the field's map (no
    // cell list exists any more). The picture is the one EVE's 2D layer takes as its _MainTex and the one the own shell draws.
    internal static class OrbitPicture
    {
        // The level from which a swirl is drawn.
        internal const float SwirlFromLevel = PrecipScale.ModerateFrom;

        // The most swirl cells at a time.
        internal const int MaxCells = 24;

        // The opacity of the picture where the coverage is full, by the precipitation level: faint for flurries and light snow, full from
        // Moderate on. Continuous in the level.
        internal static float OpacityAt(float level01)
        {
            if (!(level01 > 0f)) return FaintOpacity;
            if (level01 >= SwirlFromLevel) return 1f;
            float t = level01 / SwirlFromLevel;
            t = t * t * (3f - 2f * t);
            return FaintOpacity + (1f - FaintOpacity) * t;
        }

        // EVE's 2D shader multiplies the picture's colour by the layer's _Color (2) and by its own lighting (up to 2 under a high sun):
        // a grey of 0.35 is full white on the lit side (the workaround of the old swirl, EveStormMath.WhiteGrey). The picture handed
        // to EVE's 2D layer is scaled by this; the own shell draws it as it is (scale 1).
        internal const float EveGreyScale = 0.38f;

        // The opacity of the picture where the field says there is no precipitation: the plain cloud cover, a little less than full.
        internal const float FaintOpacity = 0.35f;

        // The alpha of a texel of the picture where the swirl is not drawn: the coverage times the opacity of the level (the ORBIT pass of
        // ReDefinition/AtmospheresMapPaint, in float). The map view's check against the field's coverage map (package T06) reads this.
        internal static float PictureAlpha(float coverage01, float level01)
        {
            return (coverage01 <= 0f ? 0f : coverage01 >= 1f ? 1f : coverage01) * OpacityAt(level01);
        }

        // The grey of the picture's clouds: light for a thin deck, darker with the level.
        internal static float GreyAt(float level01)
        {
            float t = level01 <= 0f ? 0f : level01 >= 1f ? 1f : level01;
            return 0.93f - 0.25f * t;
        }

        // Finds the swirl cells: local maxima (8 neighbours, the longitude wraps) of the level map at or above SwirlFromLevel, strongest
        // first, at most max of them, none closer than MinSeparationTexels to a stronger one. The radius is the half-maximum width of the
        // maximum along the row, in metres of the body of that radius, never below MinRadiusM. The blob's centre is a unit vector in
        // the layer's frame: shiftDeg and mirrored are the map layout (EveMapLayout).
        internal static int FindCells(float[] levels, float[] coverage, int width, int height, double bodyRadiusM, double shiftDeg,
            bool mirrored, List<EveCoverageMath.Blob> result, int max = MaxCells)
        {
            result.Clear();
            if (levels == null || width < 3 || height < 3 || levels.Length < width * height || bodyRadiusM <= 0) return 0;
            var candidates = new List<int>();
            for (int j = 1; j < height - 1; j++)
            {
                for (int i = 0; i < width; i++)
                {
                    float v = levels[j * width + i];
                    if (v < SwirlFromLevel) continue;
                    bool top = true;
                    for (int dj = -1; dj <= 1 && top; dj++)
                    {
                        for (int di = -1; di <= 1; di++)
                        {
                            if (di == 0 && dj == 0) continue;
                            int ii = (i + di + width) % width;
                            float w = levels[(j + dj) * width + ii];
                            // A plateau counts once: the first texel in reading order wins.
                            if (w > v || (w == v && (dj < 0 || (dj == 0 && di < 0)))) { top = false; break; }
                        }
                    }
                    if (top) candidates.Add(j * width + i);
                }
            }
            candidates.Sort((a, b) => levels[b].CompareTo(levels[a]));
            var kept = new List<int>();
            foreach (int index in candidates)
            {
                if (kept.Count >= max) break;
                int ci = index % width, cj = index / width;
                bool near = false;
                foreach (int other in kept)
                {
                    int oi = other % width, oj = other / width;
                    int di = Math.Abs(ci - oi);
                    if (di > width / 2) di = width - di;
                    if (di * di + (cj - oj) * (cj - oj) < MinSeparationTexels * MinSeparationTexels) { near = true; break; }
                }
                if (near) continue;
                kept.Add(index);
                double half = levels[index] * 0.5;
                int left = 0, right = 0;
                while (left < width / 2 && levels[cj * width + (ci - left - 1 + width) % width] >= half) left++;
                while (right < width / 2 && levels[cj * width + (ci + right + 1) % width] >= half) right++;
                double latDeg = 90.0 - (cj + 0.5) / height * 180.0;
                double lonDeg = (ci + 0.5) / width * 360.0 - 180.0;
                double cosLat = Math.Max(Math.Cos(latDeg * Math.PI / 180.0), 0.05);
                double radiusM = Math.Max((left + right + 1) * 0.5 * (2.0 * Math.PI * bodyRadiusM * cosLat / width), MinRadiusM);
                double u = EveMapLayout.LayerU(lonDeg, shiftDeg, mirrored);
                double v2 = (cj + 0.5) / height;
                double x, y, z;
                EveCoverageMath.Direction(u, v2, out x, out y, out z);
                float peak = coverage != null && coverage.Length >= width * height ? coverage[index] : levels[index];
                result.Add(new EveCoverageMath.Blob
                {
                    X = x, Y = y, Z = z, RadiusM = radiusM, Peak = Math.Max(peak, 0.4f),
                    Seed = (ulong)(ci * 73856093L ^ cj * 19349663L) + 0x9E3779B97F4A7C15UL
                });
            }
            return result.Count;
        }

        // Cells closer together than this, in texels of the level map, are one cell.
        internal const int MinSeparationTexels = 6;

        // The smallest radius of a cell, in m.
        internal const double MinRadiusM = 20000;
    }
}
