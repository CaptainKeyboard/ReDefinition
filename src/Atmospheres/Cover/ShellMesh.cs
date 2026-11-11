using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // The mesh of a part's cover shell: the triangles of the part's mesh whose face does not look down, inflated along the vertex normals
    // by a centimetre or two. The exact "up" test is on the GPU (the shader masks by the world normal), so the shell keeps every
    // triangle that can face up in the orientation the vessel has when the shell is built (face normal against the local up at least
    // minUpDot) and is built anew when the vessel has turned far from that orientation (Turned). Arrays instead of engine types, so
    // that the selection is tested outside the game. Package D34, brief item 4 (shell mesh, option c of the plan).
    internal static class ShellMesh
    {
        internal sealed class Result
        {
            internal float[] Positions;
            internal float[] Normals;
            internal int[] Indices;
            internal int VertexCount;
            internal int TriangleCount;
        }

        // The angle in degrees by which the vessel's up may turn in the part's frame before its shell is built anew.
        internal const double RebuildDegrees = 40.0;

        // The vertex budget of a shell: a part with more triangles than this gives a shell of its first ones only (a guard, not a look).
        internal const int MaxTriangles = 20000;

        // positions and normals hold x, y, z per vertex; indices three per triangle. up is the world up in the mesh's local frame
        // (unit). minUpDot is the lowest dot product of a face normal and up that is kept (a little under 0 keeps the vertical faces
        // too, which a tilted vessel turns up). offset is the inflation in the mesh's local units. Returns null where nothing is kept.
        internal static Result Select(float[] positions, float[] normals, int[] indices, float upX, float upY, float upZ, float minUpDot, float offset)
        {
            if (positions == null || normals == null || indices == null) return null;
            int vertexCount = positions.Length / 3;
            if (normals.Length / 3 != vertexCount || indices.Length < 3) return null;
            var map = new Dictionary<int, int>();
            var outPos = new List<float>();
            var outNor = new List<float>();
            var outIdx = new List<int>();
            int triangles = 0;
            for (int t = 0; t + 2 < indices.Length && triangles < MaxTriangles; t += 3)
            {
                int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                if (a < 0 || b < 0 || c < 0 || a >= vertexCount || b >= vertexCount || c >= vertexCount) continue;
                double fx, fy, fz;
                FaceNormal(positions, a, b, c, out fx, out fy, out fz);
                double len = Math.Sqrt(fx * fx + fy * fy + fz * fz);
                if (len < 1e-12) continue;
                double dot = (fx * upX + fy * upY + fz * upZ) / len;
                if (dot < minUpDot) continue;
                outIdx.Add(Take(a, positions, normals, offset, map, outPos, outNor));
                outIdx.Add(Take(b, positions, normals, offset, map, outPos, outNor));
                outIdx.Add(Take(c, positions, normals, offset, map, outPos, outNor));
                triangles++;
            }
            if (triangles == 0) return null;
            return new Result
            {
                Positions = outPos.ToArray(),
                Normals = outNor.ToArray(),
                Indices = outIdx.ToArray(),
                VertexCount = outPos.Count / 3,
                TriangleCount = triangles
            };
        }

        private static int Take(int source, float[] positions, float[] normals, float offset, Dictionary<int, int> map, List<float> outPos, List<float> outNor)
        {
            int at;
            if (map.TryGetValue(source, out at)) return at;
            at = outPos.Count / 3;
            map[source] = at;
            float nx = normals[source * 3], ny = normals[source * 3 + 1], nz = normals[source * 3 + 2];
            float len = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 1e-6f)
            {
                nx /= len; ny /= len; nz /= len;
            }
            else
            {
                nx = 0; ny = 1; nz = 0;
            }
            outPos.Add(positions[source * 3] + nx * offset);
            outPos.Add(positions[source * 3 + 1] + ny * offset);
            outPos.Add(positions[source * 3 + 2] + nz * offset);
            outNor.Add(nx);
            outNor.Add(ny);
            outNor.Add(nz);
            return at;
        }

        private static void FaceNormal(float[] p, int a, int b, int c, out double x, out double y, out double z)
        {
            double ux = p[b * 3] - p[a * 3], uy = p[b * 3 + 1] - p[a * 3 + 1], uz = p[b * 3 + 2] - p[a * 3 + 2];
            double vx = p[c * 3] - p[a * 3], vy = p[c * 3 + 1] - p[a * 3 + 1], vz = p[c * 3 + 2] - p[a * 3 + 2];
            // Unity's winding is clockwise seen from the front, so the front normal is (b - a) x (c - a) with the left-handed sign flipped
            // by the engine's convention: cross(u, v) points to the viewer for a clockwise triangle in a left-handed frame.
            x = uy * vz - uz * vy;
            y = uz * vx - ux * vz;
            z = ux * vy - uy * vx;
        }

        // Whether the vessel's up, in the part's frame, has turned far enough from the one the shell was built for to build it anew.
        internal static bool Turned(float builtX, float builtY, float builtZ, float nowX, float nowY, float nowZ)
        {
            double dot = builtX * nowX + builtY * nowY + builtZ * nowZ;
            if (dot > 1) dot = 1;
            if (dot < -1) dot = -1;
            return Math.Acos(dot) * 180.0 / Math.PI > RebuildDegrees;
        }

        // The key under which shells of the same mesh and nearly the same up are shared: the mesh's id and the up cut into 15 degree
        // cells (about 20 cells a sphere).
        internal static long CacheKey(int meshId, float upX, float upY, float upZ)
        {
            int qx = (int)Math.Round(upX * 4.0), qy = (int)Math.Round(upY * 4.0), qz = (int)Math.Round(upZ * 4.0);
            return ((long)meshId << 24) ^ (uint)((qx + 8) | ((qy + 8) << 5) | ((qz + 8) << 10));
        }
    }
}
