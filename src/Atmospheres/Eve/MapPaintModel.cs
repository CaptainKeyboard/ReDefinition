using System;

namespace ReDefinition.Atmospheres
{
    // The CPU twin of one painted map, pure: a layer-frame byte map that follows a field's map by the painter pass's rule. The GPU
    // painter (MapPainter and the shader ReDefinition/Atmospheres/MapPaint) and the CPU mirror (CpuMirror, 512 x 256) run this
    // rule, so they differ only in size and in the last 8 bit level:
    //   target(i, j) = lookup(bilinear(field, EveMapLayout.FieldU(u, shift, mirrored), v) x scale)
    //   current      = current moved toward target by at most rate x dt (MapSlew)
    // The lookup is the identity for a coverage map and the level-to-type table (CloudTypeEncoding.BuildLut) for a type map.
    internal sealed class MapPaintModel
    {
        private readonly byte[] target;
        private readonly byte[] lookup;

        internal MapPaintModel(int width, int height, byte[] lookup = null)
        {
            Width = width;
            Height = height;
            Current = new byte[width * height];
            target = new byte[width * height];
            this.lookup = lookup;
        }

        internal int Width { get; }
        internal int Height { get; }

        // The map as it is now, row by row, row 0 at the north pole.
        internal byte[] Current { get; }

        // The map the field asks for (before the limit), as of the last Update.
        internal byte[] Target
        {
            get { return target; }
        }

        // Sets the map to the field's at once (no limit): the first paint of a body and a cut.
        internal void Snap(float[] field, int fieldWidth, int fieldHeight, double shiftDeg, bool mirrored)
        {
            Resolve(field, fieldWidth, fieldHeight, shiftDeg, mirrored);
            Array.Copy(target, Current, Current.Length);
        }

        // Moves the map toward the field's by dt seconds of limited change; returns the number of texels still away from the target.
        internal int Update(float[] field, int fieldWidth, int fieldHeight, double shiftDeg, bool mirrored, float dt, float rate)
        {
            Resolve(field, fieldWidth, fieldHeight, shiftDeg, mirrored);
            return MapSlew.StepMap(Current, target, rate, dt);
        }

        // Works on the rows row0 to row0 + rows - 1 only: the CPU mirror takes a band of rows a frame, each with the dt of its whole
        // cycle. Returns the number of texels of the band still away from the target.
        internal int UpdateRows(float[] field, int fieldWidth, int fieldHeight, double shiftDeg, bool mirrored, int row0, int rows,
            float dt, float rate)
        {
            int end = Math.Min(Height, row0 + rows);
            if (row0 < 0 || row0 >= end) return 0;
            EveMapLayout.ResampleRows(field, fieldWidth, fieldHeight, target, Width, Height, shiftDeg, mirrored, row0, end - row0);
            ApplyLookup(row0 * Width, end * Width);
            float maxDelta = MapSlew.MaxDelta(rate, dt);
            int open = 0;
            for (int i = row0 * Width; i < end * Width; i++)
            {
                Current[i] = MapSlew.StepByte(Current[i], target[i], maxDelta);
                if (Current[i] != target[i]) open++;
            }
            return open;
        }

        private void Resolve(float[] field, int fieldWidth, int fieldHeight, double shiftDeg, bool mirrored)
        {
            EveMapLayout.Resample(field, fieldWidth, fieldHeight, target, Width, Height, shiftDeg, mirrored);
            ApplyLookup(0, target.Length);
        }

        private void ApplyLookup(int from, int to)
        {
            if (lookup == null) return;
            for (int i = from; i < to; i++) target[i] = lookup[target[i]];
        }

        // The value (0 to 1) of the current map at a layer coordinate, bilinear: what EVE's CPU sampling of the mirror returns.
        internal float Sample(double u, double v)
        {
            double x = u * Width - 0.5;
            double y = v * Height - 0.5;
            if (y < 0) y = 0;
            if (y > Height - 1) y = Height - 1;
            double x0 = Math.Floor(x);
            double fx = x - x0;
            int ix0 = Wrap((int)x0, Width), ix1 = Wrap((int)x0 + 1, Width);
            int iy0 = (int)Math.Floor(y), iy1 = Math.Min(iy0 + 1, Height - 1);
            double fy = y - iy0;
            double top = Current[iy0 * Width + ix0] * (1 - fx) + Current[iy0 * Width + ix1] * fx;
            double bottom = Current[iy1 * Width + ix0] * (1 - fx) + Current[iy1 * Width + ix1] * fx;
            return (float)((top * (1 - fy) + bottom * fy) / 255.0);
        }

        private static int Wrap(int i, int n)
        {
            int r = i % n;
            return r < 0 ? r + n : r;
        }
    }
}
