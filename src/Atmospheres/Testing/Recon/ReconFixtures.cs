using System;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The meshes of the reconnaissance probe. They are test fixtures with no relation to the product's own rendering.
    // A disc lies on the sea level sphere of a body (so that it follows the planet's curve over kilometres); a ball is a sphere at a
    // place in the air. Both follow the floating origin: their positions are recomputed from the body every frame.
    internal static class ReconMesh
    {
        // A UV sphere of the given radius around the origin, with normals.
        internal static Mesh Sphere(float radius, int segments, int rings)
        {
            var vertices = new Vector3[(segments + 1) * (rings + 1)];
            var normals = new Vector3[vertices.Length];
            for (int r = 0; r <= rings; r++)
            {
                float theta = Mathf.PI * r / rings;
                for (int s = 0; s <= segments; s++)
                {
                    float phi = 2f * Mathf.PI * s / segments;
                    var n = new Vector3(Mathf.Sin(theta) * Mathf.Cos(phi), Mathf.Cos(theta), Mathf.Sin(theta) * Mathf.Sin(phi));
                    vertices[r * (segments + 1) + s] = n * radius;
                    normals[r * (segments + 1) + s] = n;
                }
            }
            var triangles = new int[segments * rings * 6];
            int t = 0;
            for (int r = 0; r < rings; r++)
            {
                for (int s = 0; s < segments; s++)
                {
                    int a = r * (segments + 1) + s, b = a + segments + 1;
                    triangles[t++] = a; triangles[t++] = b; triangles[t++] = a + 1;
                    triangles[t++] = a + 1; triangles[t++] = b; triangles[t++] = b + 1;
                }
            }
            var mesh = new Mesh { name = "recon ball" };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.triangles = triangles;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * radius * 2.2f);
            return mesh;
        }

        // The triangles of a fan of rings: the centre vertex first, then rings of the given number of segments.
        internal static int[] DiscTriangles(int segments, int rings)
        {
            var triangles = new int[segments * 3 + (rings - 1) * segments * 6];
            int t = 0;
            for (int s = 0; s < segments; s++)
            {
                triangles[t++] = 0;
                triangles[t++] = 1 + (s + 1) % segments;
                triangles[t++] = 1 + s;
            }
            for (int r = 1; r < rings; r++)
            {
                int inner = 1 + (r - 1) * segments, outer = 1 + r * segments;
                for (int s = 0; s < segments; s++)
                {
                    int s1 = (s + 1) % segments;
                    triangles[t++] = inner + s; triangles[t++] = inner + s1; triangles[t++] = outer + s;
                    triangles[t++] = inner + s1; triangles[t++] = outer + s1; triangles[t++] = outer + s;
                }
            }
            return triangles;
        }

        // The material of a flat colour that writes depth, from the shader every player has. The render queue is the test's variable.
        internal static Material Flat(Color color, int queue)
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) return null;
            var material = new Material(shader);
            material.SetColor("_Color", color);
            material.SetInt("_SrcBlend", 1);
            material.SetInt("_DstBlend", 0);
            material.SetInt("_Cull", 0);
            material.SetInt("_ZTest", 4);
            material.SetInt("_ZWrite", 1);
            material.renderQueue = queue;
            return material;
        }
    }

    // A disc on the sea level of a body, centred on a point that follows a vessel at a fixed offset, drawn as an opaque flat colour at a
    // render queue. The test fixture of the ice overlay's premise (P10, P11).
    internal sealed class ReconDisc
    {
        private const int Segments = 32, Rings = 8;

        private readonly CelestialBody body;
        private readonly Vessel vessel;
        private readonly Vector3d offset;
        private readonly double radiusMetres, liftMetres;
        private readonly GameObject go;
        private readonly Mesh mesh;
        private readonly Vector3[] vertices = new Vector3[1 + Segments * Rings];

        internal ReconDisc(CelestialBody body, Vessel vessel, Vector3d offset, double radiusMetres, double liftMetres, int queue, int layer)
        {
            this.body = body;
            this.vessel = vessel;
            this.offset = offset;
            this.radiusMetres = radiusMetres;
            this.liftMetres = liftMetres;
            go = new GameObject("ReDefinition recon disc");
            go.layer = layer;
            mesh = new Mesh { name = "recon disc" };
            mesh.vertices = vertices;
            mesh.triangles = ReconMesh.DiscTriangles(Segments, Rings);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = ReconMesh.Flat(Color.white, queue);
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            go.SetActive(false);
        }

        internal bool Active
        {
            get { return go.activeSelf; }
            set
            {
                go.SetActive(value);
                if (value) Update();
            }
        }

        // The point of the sea level under the disc's centre, in world coordinates.
        internal Vector3d Centre()
        {
            Vector3d at = vessel.CoMD + offset;
            Vector3d up = (at - body.position).normalized;
            return body.position + up * (body.Radius + liftMetres);
        }

        // Rebuilds the fan on the sphere around the centre; called every frame while the disc is on.
        internal void Update()
        {
            if (!go.activeSelf) return;
            Vector3d up = (vessel.CoMD + offset - body.position).normalized;
            Vector3d centre = body.position + up * (body.Radius + liftMetres);
            go.transform.position = (Vector3)centre;
            Vector3d t1 = Vector3d.Cross(up, Math.Abs(up.y) < 0.9 ? Vector3d.up : Vector3d.right).normalized;
            Vector3d t2 = Vector3d.Cross(up, t1);
            double radius = body.Radius + liftMetres;
            double span = radiusMetres / body.Radius;
            vertices[0] = Vector3.zero;
            for (int r = 1; r <= Rings; r++)
            {
                double alpha = span * r / Rings;
                double sinA = Math.Sin(alpha), cosA = Math.Cos(alpha);
                for (int s = 0; s < Segments; s++)
                {
                    double phi = 2.0 * Math.PI * s / Segments;
                    Vector3d dir = up * cosA + (t1 * Math.Cos(phi) + t2 * Math.Sin(phi)) * sinA;
                    vertices[1 + (r - 1) * Segments + s] = (Vector3)((dir - up) * radius);
                }
            }
            mesh.vertices = vertices;
            mesh.RecalculateBounds();
        }

        internal void Destroy()
        {
            UnityEngine.Object.Destroy(go);
            UnityEngine.Object.Destroy(mesh);
        }
    }

    // A lit ball in the air at a fixed offset from a vessel (world axes), for the fog and shader tests.
    internal sealed class ReconBall
    {
        private readonly Vessel vessel;
        private readonly Vector3d offset;
        private readonly GameObject go;

        internal ReconBall(Vessel vessel, Vector3d offset, float radius, Material material, int layer)
        {
            this.vessel = vessel;
            this.offset = offset;
            go = new GameObject("ReDefinition recon ball");
            go.layer = layer;
            go.AddComponent<MeshFilter>().sharedMesh = ReconMesh.Sphere(radius, 32, 20);
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            Update();
        }

        internal Vector3 Position
        {
            get { return go.transform.position; }
        }

        internal Material Material
        {
            get { return go.GetComponent<MeshRenderer>().sharedMaterial; }
            set { go.GetComponent<MeshRenderer>().sharedMaterial = value; }
        }

        internal void Update()
        {
            go.transform.position = (Vector3)(vessel.CoMD + offset);
        }

        internal void Destroy()
        {
            UnityEngine.Object.Destroy(go);
        }
    }
}
