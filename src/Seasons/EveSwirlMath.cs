using System;

namespace ReDefinition.Seasons
{
    // The pure part of the cell's picture for EVE's 2D cloud layer: the swirl a storm cell is seen as from orbit and in the map view,
    // painted into an equirectangular RGBA picture. It is made of numbers only, so that it can be tested without the game.
    //
    // The 2D layer (EVE's Clouds2D, shader EVE/Cloud) is the scaled-space form of a cloud layer. It samples its _MainTex with the
    // formula of the coverage map (EveCoverageMath.Uv). Its alpha is the opacity (SphereCloud.shader: color = _Color * main * detail).
    // The picture is therefore white-grey with the alpha of the cloud.
    //
    // A texel is 2 pi R / Width wide at the equator and as high (Kerbin: 1.84 km). A cell is a disc on the sphere, so it is a wider
    // ellipse of texels toward the poles (RectAround holds the rectangle). A cell smaller than MinRadiusTexels texels is painted that
    // large: the picture is seen from far away, and a 6 km cell would be a single texel.
    internal static class EveSwirlMath
    {
        // The size of the picture, in texels, four bytes (red, green, blue, alpha) each.
        internal const int Width = 2048, Height = 1024;

        // The smallest radius a cell is painted with, in texels of the picture's height.
        internal const double MinRadiusTexels = 2.5;

        // The number of arms of the spiral and how far they turn from the centre to the rim, in turns.
        internal const int Arms = 2;
        internal const double TwistTurns = 0.8;

        // How much of the cloud is left between the arms at the rim (the core is closed).
        internal const double GapLevel = 0.45;

        // The colour of the cloud on the arms and between them, and of the picture where there is none (alpha 0; the colour is
        // what bilinear filtering blends in at the rim). The gaps are dark enough to show against EVE's white clouds.
        internal const byte ArmGrey = 240, GapGrey = 140, BackgroundGrey = 236;

        // A diagnostics switch for screenshots: the cells are painted as solid red discs, so that the layer can be told from the clouds.
        internal static bool Probe;

        // The size of a texel of the picture's height on a planet, in m.
        internal static double TexelMetres(double planetRadiusM)
        {
            return Math.PI * planetRadiusM / Height;
        }

        // The radius a cell is painted with, in m: its own, but not below MinRadiusTexels texels.
        internal static double DisplayRadiusM(double radiusM, double planetRadiusM)
        {
            return Math.Max(radiusM, MinRadiusTexels * TexelMetres(planetRadiusM));
        }

        // Fills the whole picture with the background: no cloud.
        internal static void Fill(byte[] rgba)
        {
            for (int i = 0; i < rgba.Length; i += 4)
            {
                rgba[i] = BackgroundGrey;
                rgba[i + 1] = BackgroundGrey;
                rgba[i + 2] = BackgroundGrey;
                rgba[i + 3] = 0;
            }
        }

        // Sets the texels of a rectangle back to the background.
        internal static void Clear(byte[] rgba, EveCoverageMath.Rect rect)
        {
            for (int r = 0; r < rect.Rows; r++)
            {
                int rowStart = (rect.Row0 + r) * Width;
                for (int k = 0; k < rect.Cols; k++)
                {
                    int i = 4 * (rowStart + (rect.Col0 + k) % Width);
                    rgba[i] = BackgroundGrey;
                    rgba[i + 1] = BackgroundGrey;
                    rgba[i + 2] = BackgroundGrey;
                    rgba[i + 3] = 0;
                }
            }
        }

        // The opacity and the grey of the swirl at a place of the cell. The place is the distance from the centre as a share of the radius.
        // Then the direction from the centre in radians. Then a noise from -1 to 1.
        internal static void Pattern(double rn, double bearing, double ragged, out double opacity, out double grey)
        {
            double phase = Arms * bearing - TwistTurns * 2 * Math.PI * rn;
            double wave = 0.5 + 0.5 * Math.Cos(phase);
            double arm = Mathx.SmoothStep(0.25, 0.85, wave);
            double open = Mathx.SmoothStep(0.1, 0.65, rn);
            double gap = 1 - open * (1 - GapLevel);
            opacity = (gap + (1 - gap) * arm) * (0.92 + 0.08 * ragged);
            grey = ArmGrey - (ArmGrey - GapGrey) * (1 - arm) * open;
        }

        // Paints one cell into the picture (the more opaque texel wins where cells overlap) and returns the rectangle it touched.
        // blob.Peak is the largest opacity; longitudeShift moves the cell along u as in EveCoverageMath.Paint.
        internal static EveCoverageMath.Rect Paint(byte[] rgba, EveCoverageMath.Blob blob, double planetRadiusM, double longitudeShift)
        {
            double cx = blob.X, cy = blob.Y, cz = blob.Z;
            double length = Math.Sqrt(cx * cx + cy * cy + cz * cz);
            if (length <= 0 || blob.RadiusM <= 0 || blob.Peak <= 0 || planetRadiusM <= 0) return default(EveCoverageMath.Rect);
            cx /= length; cy /= length; cz /= length;
            if (longitudeShift != 0)
            {
                double u0, v0;
                EveCoverageMath.Uv(cx, cy, cz, out u0, out v0);
                EveCoverageMath.Direction(u0 + longitudeShift, v0, out cx, out cy, out cz);
            }
            double radiusM = DisplayRadiusM(blob.RadiusM, planetRadiusM);
            double reach = radiusM * EveCoverageMath.ReachShare / planetRadiusM;
            EveCoverageMath.Rect rect = EveCoverageMath.RectAround(cx, cy, cz, reach, Width, Height);
            double cosReach = Math.Cos(Math.Min(reach, Math.PI));

            // The tangent plane at the centre: east is the pole axis crossed with the centre, north completes it.
            double ex = cz, ey = 0, ez = -cx;
            double el = Math.Sqrt(ex * ex + ez * ez);
            if (el < 1e-9) { ex = 1; ey = 0; ez = 0; el = 1; }
            ex /= el; ey /= el; ez /= el;
            double nx = cy * ez - cz * ey, ny = cz * ex - cx * ez, nz = cx * ey - cy * ex;

            for (int r = 0; r < rect.Rows; r++)
            {
                int row = rect.Row0 + r;
                double theta = (row + 0.5) / Height * Math.PI;
                double sinTheta = Math.Sin(theta), cosTheta = Math.Cos(theta);
                for (int k = 0; k < rect.Cols; k++)
                {
                    int col = (rect.Col0 + k) % Width;
                    double a = ((col + 0.5) / Width - 0.5) * 2 * Math.PI;
                    double dx = sinTheta * Math.Sin(a), dy = cosTheta, dz = sinTheta * Math.Cos(a);
                    double dot = cx * dx + cy * dy + cz * dz;
                    if (dot < cosReach) continue;
                    double angle = Math.Acos(dot > 1 ? 1 : dot);
                    double distanceM = angle * planetRadiusM;
                    double ragged = EveCoverageMath.Ragged(col, row, blob.Seed);
                    double edge = EveCoverageMath.Falloff(distanceM, radiusM, ragged);
                    if (edge <= 0) continue;
                    double bearing = Math.Atan2(dx * nx + dy * ny + dz * nz, dx * ex + dy * ey + dz * ez);
                    double opacity, grey;
                    Pattern(distanceM / radiusM, bearing, ragged, out opacity, out grey);
                    if (Probe) opacity = 1.0;
                    int alpha = (int)Math.Round(edge * opacity * blob.Peak * 255.0);
                    if (alpha > 255) alpha = 255;
                    int i = 4 * (row * Width + col);
                    if (alpha <= rgba[i + 3]) continue;
                    byte g = (byte)Math.Round(grey);
                    rgba[i] = Probe ? (byte)255 : g;
                    rgba[i + 1] = Probe ? (byte)30 : g;
                    rgba[i + 2] = Probe ? (byte)30 : g;
                    rgba[i + 3] = (byte)alpha;
                }
            }
            return rect;
        }
    }
}
