using System;

namespace ReDefinition.Seasons
{
    // The snow of the orbit and map view as pixels: the same SnowMask.Coverage as the ground, evaluated for every texel of an
    // equirectangular map in the planet's own texture convention. Pure numbers (no engine type), so that it runs on a worker thread and
    // is tested outside the game.
    //
    // The convention is PQS's own (PQS.BuildVertexMapCoords): u = longitude / 2 pi, with the longitude measured from the +z axis
    // toward +x, and v = latitude / pi + 0.5. The direction of a texel is (cos lat sin lon, sin lat, cos lat cos lon). The ground's noise
    // reads the PQS direction. So the same direction gives the same noise here, and the line is one line on the ground and in orbit.
    // The mask is 1024 x 512 texels. The heights are a 256 x 128 grid read from the PQS once (OrbitHeights); until it is read the mask
    // has no height bonus. The noise table of 0.5 MB keeps 1/127 steps. So the ground and the map can differ by a texel at the line.
    internal static class OrbitMask
    {
        // The mask texture and the table of heights it is built from.
        internal const int MaskWidth = 1024;
        internal const int MaskHeight = 512;
        internal const int HeightWidth = 256;
        internal const int HeightHeight = 128;

        // How the snow trails the sky: the least phase change and the least real time between two compositions, and the larger change
        // that may come sooner.
        internal const double SmallPhaseStep = 0.01;
        internal const double LargePhaseStep = 0.05;
        internal const double SmallStepSeconds = 30.0;
        internal const double LargeStepSeconds = 2.0;

        // The unit direction of the texel at texture coordinates u, v (0 to 1), as the PQS has it.
        internal static void Direction(double u, double v, out double x, out double y, out double z)
        {
            Geo.DirectionOf((v - 0.5) * Math.PI, 2 * Math.PI * u, out x, out y, out z);
        }

        // The latitude in degrees of the row at texture coordinate v.
        internal static double LatitudeDegrees(double v)
        {
            return (v - 0.5) * 180.0;
        }

        // The noise of every texel as a signed byte (value * 127), row by row from the south.
        internal static sbyte[] BuildNoise(int width, int height)
        {
            var table = new sbyte[width * height];
            for (int row = 0; row < height; row++)
            {
                double v = (row + 0.5) / height;
                for (int column = 0; column < width; column++)
                {
                    double x, y, z;
                    Direction((column + 0.5) / width, v, out x, out y, out z);
                    table[row * width + column] = (sbyte)Math.Round(SnowNoise.Value(x, y, z) * 127.0);
                }
            }
            return table;
        }

        // The height in metres of the grid at texture coordinates u (wraps) and v (clamped), bilinear. Cells that are not numbers count 0.
        internal static float Height(float[] grid, int width, int height, double u, double v)
        {
            if (grid == null || grid.Length < width * height) return 0f;
            double fx = u * width - 0.5;
            double fy = v * height - 0.5;
            if (fy < 0) fy = 0;
            if (fy > height - 1) fy = height - 1;
            int x0 = (int)Math.Floor(fx);
            int y0 = (int)Math.Floor(fy);
            double tx = fx - x0, ty = fy - y0;
            int x1 = Wrap(x0 + 1, width);
            x0 = Wrap(x0, width);
            int y1 = Math.Min(y0 + 1, height - 1);
            double a = Cell(grid, width, x0, y0), b = Cell(grid, width, x1, y0), c = Cell(grid, width, x0, y1), d = Cell(grid, width, x1, y1);
            return (float)((a + (b - a) * tx) * (1 - ty) + (c + (d - c) * tx) * ty);
        }

        private static double Cell(float[] grid, int width, int x, int y)
        {
            float value = grid[y * width + x];
            return float.IsNaN(value) || float.IsInfinity(value) ? 0.0 : value;
        }

        private static int Wrap(int value, int size)
        {
            int r = value % size;
            return r < 0 ? r + size : r;
        }

        // The snow colour with an alpha of the snow amount for every texel: rgba holds width * height * 4 bytes, row by row from the
        // south. The amount is the same as on the ground: SnowPaint.Amount(SnowMask.Coverage(...), strength). The heights are bilinear
        // from a coarse grid, or all 0 where there is none yet.
        internal static void Fill(byte[] rgba, int width, int height, sbyte[] noise, float[] heights, int heightWidth, int heightHeight,
            double phase, float strength, double winterLine)
        {
            byte red = (byte)Math.Round(SnowPaint.SnowRed * 255f);
            byte green = (byte)Math.Round(SnowPaint.SnowGreen * 255f);
            byte blue = (byte)Math.Round(SnowPaint.SnowBlue * 255f);
            for (int row = 0; row < height; row++)
            {
                double v = (row + 0.5) / height;
                double latitude = LatitudeDegrees(v);
                for (int column = 0; column < width; column++)
                {
                    double u = (column + 0.5) / width;
                    double h = Math.Max(0.0, Height(heights, heightWidth, heightHeight, u, v));
                    double n = noise != null && noise.Length >= width * height ? noise[row * width + column] / 127.0 : 0.0;
                    float amount = SnowPaint.Amount(SnowMask.Coverage(latitude, h, phase, n, winterLine), strength);
                    int i = (row * width + column) * 4;
                    rgba[i] = red;
                    rgba[i + 1] = green;
                    rgba[i + 2] = blue;
                    rgba[i + 3] = (byte)Math.Round(amount * 255f);
                }
            }
        }

        // Whether the map is to be composed again: never composed (NaN phase), or the settings or the heights changed, or the phase moved
        // far enough for the time passed since the last composition.
        internal static bool Due(double lastPhase, double phase, double secondsSinceLast, bool inputsChanged)
        {
            if (double.IsNaN(lastPhase)) return true;
            if (inputsChanged) return secondsSinceLast >= LargeStepSeconds;
            double moved = SeasonClock.PhaseDistance(lastPhase, phase);
            if (moved >= LargePhaseStep) return secondsSinceLast >= LargeStepSeconds;
            if (moved >= SmallPhaseStep) return secondsSinceLast >= SmallStepSeconds;
            return false;
        }

        // The width and height of the working maps: the original's, cut down to the wanted width, at least 64 wide.
        internal static void WorkingSize(int originalWidth, int originalHeight, int wantedWidth, out int width, out int height)
        {
            width = Math.Max(64, Math.Min(originalWidth, wantedWidth));
            height = originalWidth > 0 ? Math.Max(32, (int)Math.Round((double)width * originalHeight / originalWidth)) : width / 2;
        }

        // The memory of the base copy (no mips) and the target (with mips) in bytes at 4 bytes a texel.
        internal static long Bytes(int width, int height)
        {
            long baseCopy = (long)width * height * 4;
            long target = baseCopy * 4 / 3;
            return baseCopy + target;
        }
    }
}
