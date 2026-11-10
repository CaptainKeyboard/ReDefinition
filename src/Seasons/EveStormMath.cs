using System;

namespace ReDefinition.Seasons
{
    // The pure part of the storm cell inside EVE's own cloud layers (EveLayerStorm), as numbers that can be tested without the game.
    // The lobed, wind-stretched shape of a cell, how it is composited into a layer's maps, the spiral of the 2D layer and its vertices.
    //
    // A cell is a disc of radius R for the storm effect (fog, veil, flakes). Its cloud is several overlapping lobes, stretched along the
    // wind by 1.3 to 2 times, so it does not read as a circle. Each lobe is painted with EveCoverageMath's soft rim and ragged edge into
    // a mask of the cell's own rectangle.
    // The mask goes into the coverage map as a screen blend: the coverage rises to a plateau and keeps the layer's texture where the
    // mask is thin. It also moves the cloud-type map toward the type that carries the snow.
    //
    // The maps are equirectangular as EveCoverageMath describes, of any size: the stock layers' are 1024 x 1024 and 4096 x 4096.
    internal static class EveStormMath
    {
        // The most lobes a cell has, and the room of the arrays that take them.
        internal const int MaxLobes = 8;

        // The wind stretch of a cell: its lobes lie along the wind over this many times the radius.
        internal const double StretchMin = 1.3, StretchMax = 2.0;

        // The farthest a painted texel lies from the cell's centre, as a multiple of the cell's radius (the rim's reach included).
        internal const double MaxReachFactor = 2.0;

        // The mask value, 0 to 1, from which the cloud type starts to turn to the snow type and at which it has turned wholly.
        internal const double TypeFrom = 0.15, TypeTo = 0.55;

        // The smallest radius the swirl of the 2D layer is drawn with, in m: from 1000 km a cell is seen as a few pixels otherwise.
        internal const double MinSwirlRadiusM = 20000;

        // The most puffs of the swirl, and how many the arms have.
        internal const int MaxPuffs = 28;
        internal const int Arms = 2, PuffsPerArm = 6;

        // The turns of an arm from the centre to its end.
        internal const double SwirlTurns = 0.75;

        // The farthest a puff of the swirl reaches from the centre, as a multiple of the swirl's display radius.
        internal const double SwirlReachFactor = 2.1;

        // The share of its size at which a young cell starts to grow.
        internal const double GrowthStart = 0.4;

        // One lobe of a cell: its centre in metres east and north of the cell's centre, its radius and the share of the peak it has.
        internal struct Lobe
        {
            internal double EastM, NorthM, RadiusM, Weight;
        }

        // One puff of the 2D swirl: like a lobe, with the opacity and the grey (0 black, 1 white) it is drawn with.
        internal struct Puff
        {
            internal double EastM, NorthM, RadiusM, Alpha, Grey;
        }

        // A deterministic number from 0 to 1 of a seed and a salt.
        internal static double Unit(ulong seed, int salt)
        {
            return 0.5 + 0.5 * EveCoverageMath.Hash(salt, 7919, seed);
        }

        // The wind stretch of a cell, StretchMin to StretchMax, from its seed.
        internal static double Stretch(ulong seed)
        {
            return StretchMin + (StretchMax - StretchMin) * Unit(seed, 11);
        }

        // How much of its size a cell has at a strength (intensity now): it grows out of the existing clouds while its storm builds.
        internal static double GrowthScale(double strength)
        {
            return GrowthStart + (1 - GrowthStart) * Mathx.SmoothStep(0.0, 0.6, strength);
        }

        // The lobes of a cell of that radius with the wind blowing toward heading (radians clockwise from north), at the time.
        // A function of the seed, the radius, the heading and the time only. There are 5 to 7: a wide one in the middle.
        // The others are spread along the wind over Stretch times the radius and breathe slowly. Returns their number.
        internal static int Lobes(ulong seed, double radiusM, double headingRad, double universalTime, Lobe[] output)
        {
            if (output == null || output.Length == 0 || !(radiusM > 0)) return 0;
            int n = Math.Min(output.Length, 5 + (int)(Unit(seed, 1) * 2.999));
            double stretch = Stretch(seed);
            double sinH = Math.Sin(headingRad), cosH = Math.Cos(headingRad);
            double time = double.IsNaN(universalTime) || double.IsInfinity(universalTime) ? 0 : universalTime;
            for (int k = 0; k < n; k++)
            {
                double along = 0, across = 0, radius = 0.8 * radiusM, weight = 1.0;
                if (k > 0)
                {
                    double s = -1.0 + 2.0 * (k - 0.5) / (n - 1);
                    double breath = 2 * Math.PI * (time / (4800.0 + 2400.0 * Unit(seed, 20 + k)) + Unit(seed, 40 + k));
                    along = stretch * 0.55 * radiusM * s + 0.05 * radiusM * Math.Sin(breath);
                    across = 0.35 * radiusM * (2 * Unit(seed, 60 + k) - 1) + 0.04 * radiusM * Math.Sin(breath * 1.3 + 1.0);
                    radius = radiusM * (0.42 + 0.2 * Unit(seed, 80 + k));
                    weight = 0.82 + 0.18 * Unit(seed, 100 + k);
                }
                output[k] = new Lobe
                {
                    EastM = along * sinH + across * cosH,
                    NorthM = along * cosH - across * sinH,
                    RadiusM = radius,
                    Weight = weight
                };
            }
            return n;
        }

        // The unit direction of the point that lies eastM and northM from a place (a unit direction; the pole is +y), on a sphere.
        internal static void Offset(double cx, double cy, double cz, double eastM, double northM, double planetRadiusM, out double x, out double y, out double z)
        {
            double length = Math.Sqrt(cx * cx + cy * cy + cz * cz);
            if (length <= 0 || planetRadiusM <= 0)
            {
                x = cx; y = cy; z = cz;
                return;
            }
            cx /= length; cy /= length; cz /= length;
            double distance = Math.Sqrt(eastM * eastM + northM * northM);
            if (distance <= 0)
            {
                x = cx; y = cy; z = cz;
                return;
            }
            // East is the pole axis crossed with the place; at a pole any direction will do.
            double ex = cz, ey = 0, ez = -cx;
            double el = Math.Sqrt(ex * ex + ez * ez);
            if (el < 1e-9) { ex = 1; ez = 0; el = 1; }
            ex /= el; ez /= el;
            double nx = cy * ez - cz * ey, ny = cz * ex - cx * ez, nz = cx * ey - cy * ex;
            double tx = ex * eastM + nx * northM, ty = ey * eastM + ny * northM, tz = ez * eastM + nz * northM;
            double tl = Math.Sqrt(tx * tx + ty * ty + tz * tz);
            tx /= tl; ty /= tl; tz /= tl;
            double angle = distance / planetRadiusM;
            double c = Math.Cos(angle), s = Math.Sin(angle);
            x = cx * c + tx * s;
            y = cy * c + ty * s;
            z = cz * c + tz * s;
        }

        // The rectangle of a map that holds every lobe of a cell (the most they reach is MaxReachFactor radii from the centre).
        internal static EveCoverageMath.Rect CellRect(double cx, double cy, double cz, double radiusM, double planetRadiusM, int width, int height)
        {
            return EveCoverageMath.RectAround(cx, cy, cz, Math.Min(Math.PI, MaxReachFactor * radiusM / planetRadiusM), width, height);
        }

        // The number of bytes a mask of a rectangle takes.
        internal static int MaskSize(EveCoverageMath.Rect rect)
        {
            return rect.IsEmpty ? 0 : rect.Cols * rect.Rows;
        }

        // Paints the lobes of a cell into mask, which holds the rectangle row by row (rect.Cols per row, the columns counted from
        // rect.Col0 and wrapping at the map's width). The mask is cleared first. A texel gets the largest value of the lobes there:
        // the lobe's weight times peak times EveCoverageMath's soft edge. The map is width x height texels; (cx, cy, cz) is the cell's
        // centre in the layer's frame.
        internal static void PaintMask(byte[] mask, EveCoverageMath.Rect rect, int width, int height, double cx, double cy, double cz,
            double planetRadiusM, Lobe[] lobes, int lobeCount, double peak, ulong seed)
        {
            int size = MaskSize(rect);
            if (mask == null || mask.Length < size) throw new ArgumentException("the mask is too small for the rectangle");
            Array.Clear(mask, 0, size);
            if (size == 0 || lobeCount <= 0 || !(peak > 0) || planetRadiusM <= 0) return;
            double nx = width / 4096.0, ny = height / 2048.0;
            for (int l = 0; l < lobeCount; l++)
            {
                double lx, ly, lz;
                Offset(cx, cy, cz, lobes[l].EastM, lobes[l].NorthM, planetRadiusM, out lx, out ly, out lz);
                double radiusM = lobes[l].RadiusM;
                if (!(radiusM > 0)) continue;
                double level = peak * lobes[l].Weight;
                double cosReach = Math.Cos(Math.Min(radiusM * EveCoverageMath.ReachShare / planetRadiusM, Math.PI));
                ulong lobeSeed = seed + (ulong)(l * 7919 + 1);
                for (int r = 0; r < rect.Rows; r++)
                {
                    int row = rect.Row0 + r;
                    double theta = (row + 0.5) / height * Math.PI;
                    double sinTheta = Math.Sin(theta), cosTheta = Math.Cos(theta);
                    double rowDot = ly * cosTheta;
                    int rowStart = r * rect.Cols;
                    for (int k = 0; k < rect.Cols; k++)
                    {
                        int col = (rect.Col0 + k) % width;
                        double a = ((col + 0.5) / width - 0.5) * 2 * Math.PI;
                        double dot = rowDot + sinTheta * (lx * Math.Sin(a) + lz * Math.Cos(a));
                        if (dot < cosReach) continue;
                        double angle = Math.Acos(dot > 1 ? 1 : dot);
                        double ragged = EveCoverageMath.RaggedAt(col / nx, row / ny, lobeSeed);
                        double swell = 0.86 + 0.14 * ragged;
                        double value = EveCoverageMath.Falloff(angle * planetRadiusM, radiusM, ragged) * level * swell;
                        int v = (int)Math.Round(value * 255.0);
                        if (v > 255) v = 255;
                        if (v > mask[rowStart + k]) mask[rowStart + k] = (byte)v;
                    }
                }
            }
        }

        // The screen blend of a layer's own coverage and a cell's mask: the plateau where the mask is strong, the layer's own texture
        // where it is thin, never less than either.
        internal static byte Screen(byte layer, byte mask)
        {
            int v = layer + mask - (layer * mask + 127) / 255;
            return (byte)(v > 255 ? 255 : v);
        }

        // The cloud type, 0 to 255, of a texel with a cell's mask on it: it turns from the layer's own toward the target type as
        // the mask goes from TypeFrom to TypeTo.
        internal static byte BlendType(byte layerType, byte mask, byte targetType)
        {
            double t = Mathx.SmoothStep(TypeFrom, TypeTo, mask / 255.0);
            return (byte)Math.Round(layerType + (targetType - layerType) * t);
        }

        // Puts a cell's mask into the layer's working maps: coverage always, the type where a type map is given. Both maps are
        // width wide; the rectangle is the mask's. orig is not read here (the caller restored the rectangle from it); work takes the result.
        internal static void Composite(byte[] coverage, byte[] type, byte targetType, byte[] mask, EveCoverageMath.Rect rect, int width)
        {
            for (int r = 0; r < rect.Rows; r++)
            {
                int mapRow = (rect.Row0 + r) * width;
                int maskRow = r * rect.Cols;
                for (int k = 0; k < rect.Cols; k++)
                {
                    byte m = mask[maskRow + k];
                    if (m == 0) continue;
                    int i = mapRow + (rect.Col0 + k) % width;
                    coverage[i] = Screen(coverage[i], m);
                    if (type != null) type[i] = BlendType(type[i], m, targetType);
                }
            }
        }

        // Puts a rectangle of the working map back to the layer's own values.
        internal static void Restore(byte[] work, byte[] original, EveCoverageMath.Rect rect, int width)
        {
            for (int r = 0; r < rect.Rows; r++)
            {
                int mapRow = (rect.Row0 + r) * width;
                for (int k = 0; k < rect.Cols; k++)
                {
                    int i = mapRow + (rect.Col0 + k) % width;
                    work[i] = original[i];
                }
            }
        }

        // The value of a cloud type as the map holds it. EVE selects the type of a texel with the red value 0 to 1: the types are
        // the list's items, the value times (count - 1) is the index, and the two types around a fractional index are interpolated.
        internal static double CloudTypeValue(int index, int count)
        {
            if (count <= 1 || index <= 0) return 0.0;
            if (index >= count - 1) return 1.0;
            return (double)index / (count - 1);
        }

        // The type value as a byte.
        internal static byte CloudTypeByte(int index, int count)
        {
            return (byte)Math.Round(CloudTypeValue(index, count) * 255.0);
        }

        // The index of the first of the wanted names that the list has (case does not matter), or -1. The wanted names are in order of
        // preference; a name is matched whole.
        internal static int FindType(string[] typeNames, string[] wanted)
        {
            if (typeNames == null || wanted == null) return -1;
            for (int w = 0; w < wanted.Length; w++)
            {
                for (int i = 0; i < typeNames.Length; i++)
                {
                    if (typeNames[i] != null && string.Equals(typeNames[i], wanted[w], StringComparison.OrdinalIgnoreCase)) return i;
                }
            }
            return -1;
        }

        // The radius the swirl of a cell is drawn with: the cell's own (a bit larger, for the stretch), but not below the minimum
        // that is seen from far away.
        internal static double SwirlRadiusM(double radiusM)
        {
            return Math.Max(radiusM * 1.1, MinSwirlRadiusM);
        }

        // The puffs of a cell's swirl: a core, and two arms of puffs that wind outward from it, elongated along the wind.
        // The arms wind the other way in the southern hemisphere. strength, 0 to 1, is the cell's intensity now.
        // The layer's _Color is 512, so the picture's rgb is doubled: the lanes must be very dark to show, the arms mid grey.
        // The puffs are a function of the seed, the radius, the hemisphere and the heading. Returns their number.
        internal static int Swirl(ulong seed, double radiusM, double strength, bool northern, double headingRad, Puff[] output)
        {
            if (output == null || output.Length < 2 + 2 * Arms * PuffsPerArm || !(radiusM > 0)) return 0;
            double s = SwirlRadiusM(radiusM);
            double body = Mathx.SmoothStep(0.02, 0.45, strength);
            double elongation = 1.0 + (Stretch(seed) - 1.0) * 0.35;
            double sinH = Math.Sin(headingRad), cosH = Math.Cos(headingRad);
            double sign = northern ? 1.0 : -1.0;
            double phase = 2 * Math.PI * Unit(seed, 5);
            int n = 0;
            output[n++] = new Puff { EastM = 0, NorthM = 0, RadiusM = 0.55 * s, Alpha = 0.55 + 0.4 * body, Grey = 0.6 };
            // First the dark lanes between the arms, then the white arms over them, so that the spiral reads on white clouds as well.
            for (int pass = 0; pass < 2; pass++)
            {
                bool lane = pass == 0;
                for (int arm = 0; arm < Arms; arm++)
                {
                    for (int j = 0; j < PuffsPerArm; j++)
                    {
                        double t = (j + 1.0) / PuffsPerArm;
                        double r = s * (0.25 + 0.95 * t);
                        double theta = phase + (arm + (lane ? 0.5 : 0.0)) * 2 * Math.PI / Arms + sign * 2 * Math.PI * SwirlTurns * t;
                        double east = r * Math.Sin(theta), north = r * Math.Cos(theta);
                        // Along the wind and across it: the along part is stretched.
                        double along = east * sinH + north * cosH, across = east * cosH - north * sinH;
                        along *= elongation;
                        output[n++] = new Puff
                        {
                            EastM = along * sinH + across * cosH,
                            NorthM = along * cosH - across * sinH,
                            RadiusM = s * ((lane ? 0.30 : 0.32) - 0.14 * t),
                            Alpha = lane ? (0.55 - 0.2 * t) * (0.4 + 0.6 * body) : (0.85 - 0.35 * t) * (0.55 + 0.45 * body),
                            Grey = lane ? 0.1 : 0.6
                        };
                    }
                }
            }
            output[n++] = new Puff { EastM = 0, NorthM = 0, RadiusM = 0.28 * s, Alpha = 0.7 * body, Grey = 0.03 };
            return n;
        }

        // The vertices of a soft disc on the sphere, for drawing: a centre and rings of segments points, in texture coordinates.
        // The disc has the angular radius and a ragged rim.
        // u is unwrapped around the centre's u, so it may be below 0 or above 1: the caller draws the disc again shifted by one.
        // Returns the number of vertices, 1 + rings * segments.
        internal static int FanVertices(double cx, double cy, double cz, double angularRadius, ulong seed, int segments, int rings,
            double[] u, double[] v, double[] alpha)
        {
            int count = 1 + rings * segments;
            if (u == null || v == null || alpha == null || u.Length < count || v.Length < count || alpha.Length < count || segments < 3 || rings < 1) return 0;
            double length = Math.Sqrt(cx * cx + cy * cy + cz * cz);
            if (length <= 0) return 0;
            cx /= length; cy /= length; cz /= length;
            double u0, v0;
            EveCoverageMath.Uv(cx, cy, cz, out u0, out v0);
            u[0] = u0;
            v[0] = v0;
            alpha[0] = 1.0;
            double ex = cz, ez = -cx;
            double el = Math.Sqrt(ex * ex + ez * ez);
            if (el < 1e-9) { ex = 1; ez = 0; el = 1; }
            ex /= el; ez /= el;
            double nx = cy * ez, ny = cz * ex - cx * ez, nz = -cy * ex;
            int i = 1;
            for (int ring = 1; ring <= rings; ring++)
            {
                double share = (double)ring / rings;
                for (int seg = 0; seg < segments; seg++)
                {
                    double bearing = 2 * Math.PI * seg / segments;
                    double ragged = 0.5 * EveCoverageMath.Hash(seg, ring, seed) + 0.5 * EveCoverageMath.Hash(seg * 3, 17, seed ^ 0x5DEECE66DUL);
                    double rim = 1.0 + EveCoverageMath.Raggedness * ragged * share;
                    double angle = angularRadius * share * rim;
                    double s = Math.Sin(angle), c = Math.Cos(angle);
                    double bx = Math.Sin(bearing), by = Math.Cos(bearing);
                    double tx = ex * bx + nx * by, ty = ny * by, tz = ez * bx + nz * by;
                    double x = cx * c + tx * s, y = cy * c + ty * s, z = cz * c + tz * s;
                    double uu, vv;
                    EveCoverageMath.Uv(x, y, z, out uu, out vv);
                    while (uu - u0 > 0.5) uu -= 1.0;
                    while (uu - u0 < -0.5) uu += 1.0;
                    u[i] = uu;
                    v[i] = vv;
                    // The alpha falls from 1 at the centre to 0 at the rim: EveCoverageMath's soft edge, the same core.
                    alpha[i] = EveCoverageMath.Falloff(share, 1.0, 0.0);
                    i++;
                }
            }
            return count;
        }

        // The triangle indices of a fan: the centre with the first ring, then the rings against each other. Returns the number of
        // indices (3 per triangle): segments + (rings - 1) * segments * 2 triangles.
        internal static int FanIndices(int segments, int rings, int[] indices)
        {
            int triangles = segments + (rings - 1) * segments * 2;
            if (indices == null || indices.Length < triangles * 3) return 0;
            int n = 0;
            for (int s = 0; s < segments; s++)
            {
                indices[n++] = 0;
                indices[n++] = 1 + s;
                indices[n++] = 1 + (s + 1) % segments;
            }
            for (int ring = 1; ring < rings; ring++)
            {
                int inner = 1 + (ring - 1) * segments, outer = 1 + ring * segments;
                for (int s = 0; s < segments; s++)
                {
                    int s1 = (s + 1) % segments;
                    indices[n++] = inner + s;
                    indices[n++] = outer + s;
                    indices[n++] = outer + s1;
                    indices[n++] = inner + s;
                    indices[n++] = outer + s1;
                    indices[n++] = inner + s1;
                }
            }
            return n;
        }
    }
}
