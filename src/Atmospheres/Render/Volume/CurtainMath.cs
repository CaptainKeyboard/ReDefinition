using System;

namespace ReDefinition.Atmospheres
{
    // The numbers the precipitation curtain (CurtainPass, shader ReDefinition/AtmospheresStormVolume) is fed with and the bounding mesh it
    // is drawn on, as pure functions so that they can be tested without the game. The tiles must match the constants of the shader.
    internal static class CurtainMath
    {
        // The noise texture's side in voxels, and the lattice period and octaves of its noise (StormNoise.Volume).
        internal const int NoiseSize = 64, NoisePeriod = 4, NoiseOctaves = 4, NoiseSeed = 5;

        // The period the horizontal offsets wrap at, and the vertical tile of the snow's streaks, in m (the shader's). The period is a
        // whole number of the shader's horizontal tiles (1500 and 500 m).
        internal const double CloudTileM = 6000, StreakTileM = 14000;

        // How far above the cloud base the volume reaches, in m: the dark band of the cloud's underside (the shader's DarkBandM, 380 m) and a margin.
        internal const float SnowOnlyHeadroomM = 450f;

        // How far the pattern moves a second sideways and down, in m.
        internal const double WindSpeed = 9, FallSpeed = 30;

        // The shear of the pattern with height, in m of sideways shift per m of height.
        internal const double Shear = 0.25;

        // The segments of the bounding cylinder, and how much wider than the cell it is made so that the polygon holds the circle.
        internal const int Segments = 48;
        internal const float Widening = 1.02f;

        // The longest a ray is marched while the camera is inside a storm, in m: 4 times the visibility, at least 1.5 times the cloud
        // base (the veil reaches that high), and at most the cell's range.
        internal const double InsideVisibilityFactor = 4, InsideBaseFactor = 1.5;

        internal static double InsideRayM(double visibilityM, double baseAglM, double rangeM)
        {
            return Math.Min(rangeM, Math.Max(InsideVisibilityFactor * visibilityM, InsideBaseFactor * baseAglM));
        }

        // value modulo period, 0 up to (not including) period.
        internal static float Wrap(double value, double period)
        {
            double r = value - Math.Floor(value / period) * period;
            return (float)(r >= period ? 0 : r);
        }

        // The planet's curvature as the shader takes it: the ground falls away by r^2 times this at the distance r.
        internal static float Curvature(double planetRadius)
        {
            return (float)(1.0 / (2.0 * Math.Max(1.0, planetRadius)));
        }

        // How far below the cell's centre the volume reaches, m: the ground's fall at the rim of the cell and a margin.
        internal static float BelowM(double radiusM, double planetRadius)
        {
            return (float)(radiusM * radiusM / (2.0 * Math.Max(1.0, planetRadius)) + 150.0);
        }

        // How far above the cell's centre the volume reaches, m: just over the cloud base.
        internal static float TopM(double baseAglM)
        {
            return (float)baseAglM + SnowOnlyHeadroomM;
        }

        // The wind of a cell at a time. The offsets are wrapped by the noise tiles so that they stay small and the pattern does not jump;
        // the shear is along the same direction. yawDegrees picks the direction.
        internal static void Wind(double yawDegrees, double seconds, out float offsetX, out float offsetZ, out float shearX, out float shearZ, out float fall)
        {
            double a = Geo.ToRad(yawDegrees);
            double dx = Math.Cos(a), dz = Math.Sin(a);
            offsetX = Wrap(dx * WindSpeed * seconds, CloudTileM);
            offsetZ = Wrap(dz * WindSpeed * seconds, CloudTileM);
            shearX = (float)(dx * Shear);
            shearZ = (float)(dz * Shear);
            fall = Wrap(FallSpeed * seconds, StreakTileM);
        }

        // The corners of a cylinder of radius 1 around the y axis from y = 0 to y = 1: the bottom ring, the top ring, then the bottom and
        // the top centre; x, y, z for each.
        internal static float[] CylinderVertices(int segments)
        {
            var v = new float[((segments * 2) + 2) * 3];
            for (int s = 0; s < segments; s++)
            {
                double a = s / (double)segments * 2 * Math.PI;
                float x = (float)Math.Cos(a), z = (float)Math.Sin(a);
                int bottom = s * 3, top = (segments + s) * 3;
                v[bottom] = x; v[bottom + 1] = 0f; v[bottom + 2] = z;
                v[top] = x; v[top + 1] = 1f; v[top + 2] = z;
            }
            int centres = segments * 2 * 3;
            v[centres + 1] = 0f;
            v[centres + 4] = 1f;
            return v;
        }

        // The triangles of that cylinder with every face turned outward in Unity's winding: the side, the bottom cap, the top cap.
        internal static int[] CylinderTriangles(int segments)
        {
            var t = new int[segments * 12];
            int i = 0, bottomCentre = segments * 2, topCentre = segments * 2 + 1;
            for (int s = 0; s < segments; s++)
            {
                int n = (s + 1) % segments;
                int a = s, b = n, c = segments + s, d = segments + n;
                t[i++] = a; t[i++] = c; t[i++] = b;
                t[i++] = b; t[i++] = c; t[i++] = d;
                t[i++] = bottomCentre; t[i++] = a; t[i++] = b;
                t[i++] = topCentre; t[i++] = d; t[i++] = c;
            }
            return t;
        }
    }
}
