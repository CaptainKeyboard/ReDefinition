using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The mesh of a bolt (package T07): every strand as a strip of quads, two vertices for each polyline point, one on each side. The
    // position is the point itself; the shader moves the vertices across the view direction. normal = the strand's direction at the point,
    // uv0 = (side -1 or +1, position along the strand 0 to 1), uv1 = (core width in m, brightness).
    internal sealed class BoltMesh
    {
        private readonly List<Vector3> vertices = new List<Vector3>(), tangents = new List<Vector3>();
        private readonly List<Vector2> sides = new List<Vector2>(), shapes = new List<Vector2>();
        private readonly List<int> indices = new List<int>();

        internal int VertexCount { get { return vertices.Count; } }
        internal int TriangleCount { get { return indices.Count / 3; } }

        // Fills the mesh from the strands, rotated by rotation and moved by offset, with the brightness scaled.
        internal void Build(Mesh mesh, List<BoltStrand> strands, Quaternion rotation, Vector3 offset, float brightness)
        {
            vertices.Clear();
            tangents.Clear();
            sides.Clear();
            shapes.Clear();
            indices.Clear();
            for (int s = 0; s < strands.Count; s++)
            {
                BoltStrand strand = strands[s];
                int n = strand.Points.Count;
                if (n < 2) continue;
                int first = vertices.Count;
                for (int i = 0; i < n; i++)
                {
                    Vector3 p = strand.Points[i];
                    Vector3 a = strand.Points[Mathf.Max(i - 1, 0)], b = strand.Points[Mathf.Min(i + 1, n - 1)];
                    Vector3 tangent = rotation * (b - a).normalized;
                    Vector3 world = rotation * p + offset;
                    float along = (float)i / (n - 1);
                    for (int side = -1; side <= 1; side += 2)
                    {
                        vertices.Add(world);
                        tangents.Add(tangent);
                        sides.Add(new Vector2(side, along));
                        shapes.Add(new Vector2(strand.WidthM, strand.Brightness * brightness));
                    }
                }
                for (int i = 0; i < n - 1; i++)
                {
                    int v = first + i * 2;
                    indices.Add(v); indices.Add(v + 1); indices.Add(v + 2);
                    indices.Add(v + 2); indices.Add(v + 1); indices.Add(v + 3);
                }
            }
            mesh.Clear();
            mesh.SetVertices(vertices);
            mesh.SetNormals(tangents);
            mesh.SetUVs(0, sides);
            mesh.SetUVs(1, shapes);
            mesh.SetTriangles(indices, 0, false);
            // The vertices lie within a few tens of km of the object; the bounds are set by hand so that the renderer is never culled while the bolt is in view.
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(80000f, 80000f, 80000f));
        }
    }
}
