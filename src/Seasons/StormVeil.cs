using UnityEngine;

namespace ReDefinition.Seasons
{
    // A translucent quad just in front of the flight camera: the whiteout. It lowers the contrast of everything the camera shows, near
    // and far, by its alpha. It is a normal alpha-blended object, not an image effect, and it is made once.
    internal sealed class StormVeil
    {
        private GameObject go;
        private MeshRenderer renderer;
        private Material material;
        private Texture2D white;
        private Mesh mesh;
        private bool failed;

        // Makes the quad for that camera and returns the shader it uses, or says why it cannot.
        internal string Prepare(Camera cam)
        {
            if (go != null) return material.shader.name;
            if (failed) return "none found (veil off)";
            white = StormShaders.White();
            material = StormShaders.Blended(white);
            if (material == null)
            {
                // Tried once: the texture is not made again at every frame.
                failed = true;
                UnityEngine.Object.Destroy(white);
                white = null;
                return "none found (veil off)";
            }
            mesh = new Mesh { name = "ReDefinition Seasons veil" };
            mesh.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
            mesh.uv = new[] { new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f) };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(1e5f, 1e5f, 1e5f));
            go = new GameObject("ReDefinition Seasons storm veil");
            go.layer = StormShaders.LayerOf(cam);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            go.SetActive(false);
            return material.shader.name;
        }

        internal void Show(Camera cam, float alpha, Color colour)
        {
            if (go == null) Prepare(cam);
            if (go == null) return;
            if (alpha <= 0.001f)
            {
                Hide();
                return;
            }
            if (!go.activeSelf) go.SetActive(true);
            go.layer = StormShaders.LayerOf(cam);
            float distance = cam.nearClipPlane * 2f + 0.02f;
            Transform t = go.transform;
            t.position = cam.transform.position + cam.transform.forward * distance;
            t.rotation = cam.transform.rotation;
            float height = 2f * distance * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.1f;
            t.localScale = new Vector3(height * cam.aspect, height, 1f);
            StormShaders.Tint(material, colour, alpha);
        }

        internal void Hide()
        {
            if (go != null && go.activeSelf) go.SetActive(false);
        }

        internal void Dispose()
        {
            if (go != null) Object.Destroy(go);
            if (material != null) Object.Destroy(material);
            if (white != null) Object.Destroy(white);
            if (mesh != null) Object.Destroy(mesh);
            go = null;
        }
    }
}
