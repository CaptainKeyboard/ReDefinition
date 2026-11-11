using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // The flat rings of the near-ground ice sheet, pure geometry (no engine type): square rings of cells around the foot of the camera,
    // each ring's cells twice the size of the ring inside it, up to a largest cell that keeps the chord of a cell within a few centimetres
    // of the sea sphere (sagitta = cell^2 / (8 radius)); beyond the last doubled ring one ring of that largest cell reaches the far
    // distance. The vertex shader moves every vertex (x east, z north, metres from the foot of the camera) onto the sphere, so the sheet
    // follows the curvature; the cells are small near the camera, where the eye sees the sheet's edge against the water, and large far away.
    // The fragment shader decides what is ice per pixel, so the sheet's cells carry no ice pattern and need no snapping to a lattice.
    internal static class IceClipmap
    {
        // The cells along one side of a ring and the smallest cell, in metres.
        internal const int RingCells = 64;
        internal const float SmallestCellM = 4f;
        // The least and the most sagitta a cell may have, in metres, and the cell never goes below this.
        internal const float MaxSagittaM = 0.06f;
        internal const float LeastLargestCellM = 64f;

        internal struct Ring
        {
            // Half extents in metres: the ring covers the square of OuterHalf minus the square of InnerHalf (0 for the first ring).
            internal float InnerHalf;
            internal float OuterHalf;
            internal float Cell;
        }

        // The largest cell for a body radius: the sagitta of a cell stays under MaxSagittaM, in a power-of-two multiple of the smallest.
        internal static float LargestCell(float radiusM)
        {
            double limit = Math.Sqrt(8.0 * Math.Max(1.0, radiusM) * MaxSagittaM);
            float cell = SmallestCellM;
            while (cell * 2f <= limit) cell *= 2f;
            return Math.Max(LeastLargestCellM, cell);
        }

        // The rings for a radius and a far distance (metres from the foot of the camera to the edge of the square).
        internal static List<Ring> Rings(float radiusM, float farM)
        {
            var rings = new List<Ring>();
            float largest = LargestCell(radiusM);
            float cell = SmallestCellM;
            float inner = 0f;
            while (true)
            {
                float outer = RingCells * 0.5f * cell;
                if (outer >= farM) outer = Round(farM, cell);
                rings.Add(new Ring { InnerHalf = inner, OuterHalf = outer, Cell = cell });
                inner = outer;
                if (outer >= farM) return rings;
                if (cell >= largest) break;
                cell *= 2f;
            }
            // The ring of the largest cell out to the far distance.
            float lastOuter = Round(farM, largest);
            if (lastOuter > inner) rings.Add(new Ring { InnerHalf = inner, OuterHalf = lastOuter, Cell = largest });
            return rings;
        }

        private static float Round(float value, float cell)
        {
            return (float)Math.Ceiling(value / cell) * cell;
        }

        // The vertices (x, z pairs) and the triangle indices of the rings. Cells inside the ring's InnerHalf square are left out.
        internal static void Build(List<Ring> rings, out float[] xz, out int[] indices)
        {
            var points = new List<float>();
            var tris = new List<int>();
            foreach (Ring ring in rings)
            {
                int n = (int)Math.Round(2.0 * ring.OuterHalf / ring.Cell);
                var index = new Dictionary<long, int>();
                for (int j = 0; j < n; j++)
                    for (int i = 0; i < n; i++)
                    {
                        float x0 = -ring.OuterHalf + i * ring.Cell, z0 = -ring.OuterHalf + j * ring.Cell;
                        float x1 = x0 + ring.Cell, z1 = z0 + ring.Cell;
                        if (ring.InnerHalf > 0f && Math.Abs(x0 + ring.Cell * 0.5f) < ring.InnerHalf && Math.Abs(z0 + ring.Cell * 0.5f) < ring.InnerHalf) continue;
                        int a = Vertex(points, index, i, j, x0, z0, n), b = Vertex(points, index, i + 1, j, x1, z0, n);
                        int c = Vertex(points, index, i + 1, j + 1, x1, z1, n), d = Vertex(points, index, i, j + 1, x0, z1, n);
                        tris.Add(a); tris.Add(d); tris.Add(b);
                        tris.Add(b); tris.Add(d); tris.Add(c);
                    }
            }
            xz = points.ToArray();
            indices = tris.ToArray();
        }

        private static int Vertex(List<float> points, Dictionary<long, int> index, int i, int j, float x, float z, int n)
        {
            long key = (long)j * (n + 1) + i;
            int at;
            if (index.TryGetValue(key, out at)) return at;
            at = points.Count / 2;
            points.Add(x);
            points.Add(z);
            index[key] = at;
            return at;
        }

        // The number of cells of the rings (for the log and the tests).
        internal static long CellCount(List<Ring> rings)
        {
            long total = 0;
            foreach (Ring ring in rings)
            {
                long n = (long)Math.Round(2.0 * ring.OuterHalf / ring.Cell);
                long hole = ring.InnerHalf > 0f ? (long)Math.Round(2.0 * ring.InnerHalf / ring.Cell) : 0;
                total += n * n - hole * hole;
            }
            return total;
        }
    }
}
