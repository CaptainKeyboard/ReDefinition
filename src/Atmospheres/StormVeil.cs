using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // A translucent grid just in front of the flight camera: the whiteout. It lowers the contrast of everything the camera shows, near
    // and far, by its alpha. Its vertices carry colour and alpha, set from the direction each one looks along. Toward the horizon and
    // the ground it is the light fog colour. Toward the zenith it turns into a dark storm grey with a heavier alpha, so that the sky goes
    // grey instead of lighter (StormLook.SkyGreyShare, SkyVeilAlpha). It is a normal alpha-blended object, made once.
    // Numbers: a grid of 9 x 9 vertices at queue 3000. The ground alpha is stormVeil times intensity^1.3. The sky colour is
    // (0.34, 0.36, 0.40) scaled by the sun, with alpha 1.1 times stormVeil times intensity^1.1, at most 0.97. The grey share rises
    // from the horizon to 24 degrees of elevation and is full at intensity 0.125, before the veil is opaque. stormSkyDarkening
    // scales it: full grey from 0.67, none at 0.
    internal sealed class StormVeil
    {
        // Vertices per side of the grid: the sky gradient is evaluated at each.
        private const int Side = 9;

        // True while the legacy storm effect shows this veil. The precipitation renderer (Render/Precip) does not draw while it is true,
        // so that no veil is drawn twice; the last caller package (C25) removes the legacy storm effect and with it this class.
        internal static bool LegacyDrawing { get; private set; }

        private GameObject go;
        private MeshRenderer renderer;
        private Material material;
        private Texture2D white;
        private Mesh mesh;
        private readonly Color32[] colours = new Color32[Side * Side];
        private readonly float[] gridX = new float[Side * Side];
        private readonly float[] gridY = new float[Side * Side];
        private bool failed;

        // Makes the grid for that camera and returns the shader it uses, or says why it cannot.
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
            // The vertex colours carry colour and alpha, so the material itself is white and opaque.
            StormShaders.Tint(material, Color.white, 1f);
            var vertices = new Vector3[Side * Side];
            var uv = new Vector2[Side * Side];
            var triangles = new int[(Side - 1) * (Side - 1) * 6];
            for (int y = 0; y < Side; y++)
            {
                for (int x = 0; x < Side; x++)
                {
                    int i = y * Side + x;
                    gridX[i] = x / (float)(Side - 1) - 0.5f;
                    gridY[i] = y / (float)(Side - 1) - 0.5f;
                    vertices[i] = new Vector3(gridX[i], gridY[i], 0f);
                    uv[i] = new Vector2(0.5f, 0.5f);
                }
            }
            int t = 0;
            for (int y = 0; y < Side - 1; y++)
            {
                for (int x = 0; x < Side - 1; x++)
                {
                    int a = y * Side + x, b = a + 1, c = a + Side, d = c + 1;
                    triangles[t++] = a;
                    triangles[t++] = d;
                    triangles[t++] = b;
                    triangles[t++] = a;
                    triangles[t++] = c;
                    triangles[t++] = d;
                }
            }
            mesh = new Mesh { name = "ReDefinition Atmospheres veil" };
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.triangles = triangles;
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(1e5f, 1e5f, 1e5f));
            go = new GameObject("ReDefinition Atmospheres storm veil");
            go.layer = StormShaders.LayerOf(cam);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            go.SetActive(false);
            return material.shader.name;
        }

        // Shows the veil. Toward the horizon and the ground it has the fog colour and the ground alpha. Toward the zenith it moves to
        // the sky grey and the sky alpha, by StormLook.SkyGreyShare.
        internal void Show(Camera cam, Vector3 up, float groundAlpha, Color fog, float skyAlpha, Color skyGrey, float intensity, float darkening)
        {
            if (go == null) Prepare(cam);
            if (go == null) return;
            if (groundAlpha <= 0.001f && skyAlpha <= 0.001f)
            {
                Hide();
                return;
            }
            LegacyDrawing = true;
            if (!go.activeSelf) go.SetActive(true);
            go.layer = StormShaders.LayerOf(cam);
            float distance = cam.nearClipPlane * 2f + 0.02f;
            Transform t = go.transform;
            t.position = cam.transform.position + cam.transform.forward * distance;
            t.rotation = cam.transform.rotation;
            float halfTan = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float height = 2f * distance * halfTan * 1.1f;
            t.localScale = new Vector3(height * cam.aspect, height, 1f);

            // The direction each vertex looks along, in the world, against the vessel's up.
            Quaternion rotation = cam.transform.rotation;
            float aspect = cam.aspect;
            for (int i = 0; i < colours.Length; i++)
            {
                Vector3 local = new Vector3(gridX[i] * 1.1f * 2f * halfTan * aspect, gridY[i] * 1.1f * 2f * halfTan, 1f).normalized;
                float sinElevation = Vector3.Dot(rotation * local, up);
                float share = (float)StormLook.SkyGreyShare(sinElevation, intensity, darkening);
                float alpha = Mathf.Lerp(groundAlpha, skyAlpha, (float)StormLook.SkyGreyShare(sinElevation, 1.0, darkening));
                alpha = Mathf.Max(alpha, (float)StormLook.HorizonAlpha(groundAlpha, sinElevation));
                Color c = Color.Lerp(fog, skyGrey, share);
                colours[i] = new Color32((byte)(c.r * 255f + 0.5f), (byte)(c.g * 255f + 0.5f), (byte)(c.b * 255f + 0.5f), (byte)(alpha * 255f + 0.5f));
            }
            mesh.colors32 = colours;
        }

        internal void Hide()
        {
            LegacyDrawing = false;
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
