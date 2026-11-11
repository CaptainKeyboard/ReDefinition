using System;
using System.Collections;
using System.Collections.Generic;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // The base of the vehicle-cover scenes (package D34, the L2 shell spike): stand-in parts that get a cover shell with the mod's own
    // classes (ShellMesh builds the mesh, the shader ReDefinition/AtmospheresShell draws it, CoverScreenPass and its shader draw variant
    // (f)), and the reads of the frame, the motion vectors and the depth. The stand-in parts are dark boxes (luma about 0.1) so that
    // the white cover (luma about 0.95) is told from the part by a threshold.
    internal sealed class MotionTap : MonoBehaviour
    {
        // Armed in the Update phase of a frame: that frame's camera motion vectors are copied into Copy while the camera's image effects
        // run (the global texture is alive there, as in WeatherPass), pass 4 of the capture shader.
        internal bool Armed, Fresh;
        internal RenderTexture Copy;
        private Material capture;

        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            if (Armed)
            {
                Armed = false;
                if (capture == null) capture = new Material(Shader.Find("Hidden/ReDefinition/RtCapture"));
                if (Copy == null || Copy.width != source.width || Copy.height != source.height)
                {
                    if (Copy != null) Copy.Release();
                    Copy = new RenderTexture(source.width, source.height, 0, RenderTextureFormat.RGHalf, RenderTextureReadWrite.Linear);
                }
                Graphics.Blit(source, Copy, capture, 4);
                Fresh = true;
            }
            Graphics.Blit(source, destination);
        }
    }

    internal abstract class ShellSceneBase : WeatherSceneBase
    {
        // The stand-in floats in the air against terrain and sky, away from the base scene's vehicle.
        protected static readonly Vector3 Stand = new Vector3(2000f, 140f, -700f);
        protected const float PartLuma = 0.12f;
        protected static readonly Color CoverColor = new Color(0.93f, 0.95f, 0.98f, 1f);
        // A pixel whose luma rose by more than this over the part's own is cover.
        protected const float CoverThreshold = 0.25f;

        protected Material ShellMaterial;
        protected Material PartMaterial;
        protected readonly List<GameObject> Parts = new List<GameObject>();
        protected readonly List<GameObject> Shells = new List<GameObject>();
        protected CoverScreenPass Screen;
        protected CommandBuffer ScreenBuffer;
        protected MotionTap Tap;
        private MaterialPropertyBlock block;

        protected void MakeMaterials()
        {
            Vehicle.SetActive(false);
            Tap = Cam.gameObject.AddComponent<MotionTap>();
            Shader shell = Shader.Find("ReDefinition/AtmospheresShell");
            if (shell == null) throw new InvalidOperationException("the shell shader is not in the player");
            ShellMaterial = new Material(shell);
            Material template = Resources.Load<Material>("WeatherStandard");
            PartMaterial = new Material(template) { color = new Color(PartLuma, PartLuma, PartLuma + 0.02f) };
            PartMaterial.SetFloat("_Glossiness", 0.05f);
            Shader screen = Shader.Find("ReDefinition/AtmospheresShellScreen");
            if (screen == null) throw new InvalidOperationException("the screen-space cover shader is not in the player");
            Screen = new CoverScreenPass(screen);
        }

        // A box mesh (24 vertices, flat normals) with the given size.
        protected static Mesh BoxMesh(Vector3 size)
        {
            Vector3 h = size * 0.5f;
            var v = new List<Vector3>();
            var n = new List<Vector3>();
            var t = new List<int>();
            Vector3[] axes = { Vector3.right, Vector3.up, Vector3.forward };
            for (int a = 0; a < 3; a++)
            {
                for (int s = -1; s <= 1; s += 2)
                {
                    Vector3 normal = axes[a] * s;
                    Vector3 u = axes[(a + 1) % 3], w = axes[(a + 2) % 3];
                    Vector3 centre = Vector3.Scale(normal, h);
                    int b = v.Count;
                    Vector3 du = Vector3.Scale(u, h), dw = Vector3.Scale(w, h);
                    v.Add(centre - du - dw); v.Add(centre + du - dw); v.Add(centre + du + dw); v.Add(centre - du + dw);
                    for (int k = 0; k < 4; k++) n.Add(normal);
                    // Unity's winding convention is the one ShellMesh.FaceNormal uses: (b - a) x (c - a) points to the viewer.
                    // The cycle (centre -u -w, +u -w, +u +w) has the face normal u x w; it is the front where that is the outward normal.
                    bool flip = Vector3.Dot(Vector3.Cross(u, w), normal) < 0f;
                    if (flip) { t.Add(b); t.Add(b + 2); t.Add(b + 1); t.Add(b); t.Add(b + 3); t.Add(b + 2); }
                    else { t.Add(b); t.Add(b + 1); t.Add(b + 2); t.Add(b); t.Add(b + 2); t.Add(b + 3); }
                }
            }
            var mesh = new Mesh { name = "stand-in part" };
            mesh.SetVertices(v);
            mesh.SetNormals(n);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // A part: a box at a place, with a shell child built by the mod's ShellMesh for the up in its frame. Motion vectors of both are
        // the object's own.
        protected GameObject AddPart(Mesh mesh, Vector3 at, Quaternion rotation, Transform parent, bool withShell)
        {
            var go = new GameObject("Part");
            go.transform.SetParent(parent, false);
            go.transform.position = at;
            go.transform.rotation = rotation;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = PartMaterial;
            mr.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
            Parts.Add(go);
            if (withShell) Shells.Add(AddShell(go, mesh));
            return go;
        }

        protected GameObject AddShell(GameObject part, Mesh mesh)
        {
            Vector3 localUp = part.transform.InverseTransformDirection(Vector3.up).normalized;
            Vector3[] vertices = mesh.vertices, normals = mesh.normals;
            var positions = new float[vertices.Length * 3];
            var norm = new float[vertices.Length * 3];
            for (int i = 0; i < vertices.Length; i++)
            {
                positions[i * 3] = vertices[i].x; positions[i * 3 + 1] = vertices[i].y; positions[i * 3 + 2] = vertices[i].z;
                norm[i * 3] = normals[i].x; norm[i * 3 + 1] = normals[i].y; norm[i * 3 + 2] = normals[i].z;
            }
            ShellMesh.Result r = ShellMesh.Select(positions, norm, mesh.triangles, localUp.x, localUp.y, localUp.z, -0.25f, 0.02f);
            if (r == null) throw new InvalidOperationException("the shell of a stand-in part is empty");
            // The top face must be among the kept triangles (a mesh wound the wrong way round keeps the bottom face instead).
            bool top = false;
            for (int i = 0; i < r.VertexCount; i++) if (r.Normals[i * 3 + 1] > 0.9f) top = true;
            if (!top) throw new InvalidOperationException("the shell of a stand-in part has no up-facing face: the box mesh is wound the wrong way");
            var shellMesh = new Mesh { name = "stand-in shell" };
            var v = new Vector3[r.VertexCount];
            var n = new Vector3[r.VertexCount];
            for (int i = 0; i < r.VertexCount; i++)
            {
                v[i] = new Vector3(r.Positions[i * 3], r.Positions[i * 3 + 1], r.Positions[i * 3 + 2]);
                n[i] = new Vector3(r.Normals[i * 3], r.Normals[i * 3 + 1], r.Normals[i * 3 + 2]);
            }
            shellMesh.vertices = v;
            shellMesh.normals = n;
            shellMesh.triangles = r.Indices;
            shellMesh.RecalculateBounds();
            var go = new GameObject("ReDefinitionAtmospheresShell");
            go.transform.SetParent(part.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = shellMesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = ShellMaterial;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = true;
            mr.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            ApplyShell(mr, 1f);
            return go;
        }

        protected void ApplyShell(MeshRenderer mr, float amount)
        {
            if (block == null) block = new MaterialPropertyBlock();
            block.Clear();
            block.SetFloat("_Amount", amount);
            block.SetColor("_CoverColor", CoverColor);
            block.SetVector("_UpWorld", new Vector4(0f, 1f, 0f, 0f));
            block.SetFloat("_EdgeLow", 0.35f);
            block.SetFloat("_EdgeHigh", 0.75f);
            mr.SetPropertyBlock(block);
        }

        protected void ShellsOn(bool on)
        {
            foreach (GameObject s in Shells) s.GetComponent<MeshRenderer>().enabled = on;
        }

        // The screen-space variant on the camera for one region (the stand-in's bounds), or removed.
        protected void ScreenOn(bool on, Vector3 centre, Vector3 halfSize, int debug = 0)
        {
            Screen.Debug = debug;
            Screen.DebugOrigin = Stand;
            if (ScreenBuffer != null)
            {
                Cam.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, ScreenBuffer);
                ScreenBuffer.Release();
                ScreenBuffer = null;
            }
            if (!on) return;
            var region = CoverScreenMath.RegionOf(centre.x, centre.y, centre.z, halfSize.x, halfSize.y, halfSize.z, 0f, 1f, 0f, 1f);
            var draw = new CoverScreenDraw
            {
                Regions = new[] { region },
                Count = 1,
                Up = Vector3.up,
                Colour = CoverColor,
                TowardsSun = -Sun.transform.forward,
                SunColour = Sun.color * Sun.intensity,
                Ambient = RenderSettings.ambientLight,
                EdgeLow = 0.35f,
                EdgeHigh = 0.75f,
                Grain = 40f
            };
            ScreenBuffer = new CommandBuffer { name = "shell screen" };
            Screen.Record(ScreenBuffer, ref draw);
            Cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, ScreenBuffer);
        }

        protected IEnumerator GrabRgb(Action<RgbFrame> done)
        {
            yield return new WaitForEndOfFrame();
            done(RgbFrame.Read(Target));
        }

        // The frame and the camera's motion vectors of the frame that renders now. Call it in the Update phase of the frame whose pose was
        // just set: the tap is armed, the camera renders, and at the end of the same frame the colour and the vectors are read (x and y
        // in screen units, row 0 at the bottom; null where the camera made none).
        protected IEnumerator GrabFrameAndMotion(Action<RgbFrame, float[], float[]> done)
        {
            Tap.Armed = true;
            Tap.Fresh = false;
            yield return new WaitForEndOfFrame();
            RgbFrame frame = RgbFrame.Read(Target);
            RenderTexture copy = Tap.Fresh ? Tap.Copy : null;
            if (copy == null)
            {
                done(frame, null, null);
                yield break;
            }
            // A two-channel half texture cannot be read with GetPixels: the vectors go through a four-channel half one (red and green).
            var wide = new RenderTexture(copy.width, copy.height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            Graphics.Blit(copy, wide);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = wide;
            var tex = new Texture2D(wide.width, wide.height, TextureFormat.RGBAHalf, false, true);
            tex.ReadPixels(new Rect(0, 0, wide.width, wide.height), 0, 0);
            tex.Apply(false);
            RenderTexture.active = previous;
            Color[] pixels = tex.GetPixels();
            UnityEngine.Object.Destroy(tex);
            wide.Release();
            UnityEngine.Object.Destroy(wide);
            var x = new float[pixels.Length];
            var y = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                x[i] = pixels[i].r;
                y[i] = pixels[i].g;
            }
            done(frame, x, y);
        }

        // The largest difference of any channel of any pixel between two frames.
        protected static double MaxAbsDifference(RgbFrame a, RgbFrame b)
        {
            double m = 0;
            for (int i = 0; i < a.R.Length; i++)
                m = Math.Max(m, Math.Max(Math.Abs(a.R[i] - b.R[i]), Math.Max(Math.Abs(a.G[i] - b.G[i]), Math.Abs(a.B[i] - b.B[i]))));
            return m;
        }

        protected static float Luma(RgbFrame f, int i)
        {
            return 0.2126f * f.R[i] + 0.7152f * f.G[i] + 0.0722f * f.B[i];
        }

        // The screen rectangle of a world-space box's projected corners, shrunk by a share on every side.
        protected ImageStats.Rect2 ScreenRect(Vector3 centre, Vector3 halfSize, float shrink)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = centre + new Vector3((i & 1) == 0 ? -halfSize.x : halfSize.x, (i & 2) == 0 ? -halfSize.y : halfSize.y, (i & 4) == 0 ? -halfSize.z : halfSize.z);
                Vector3 p = Cam.WorldToScreenPoint(corner);
                x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x); y0 = Mathf.Min(y0, p.y); y1 = Mathf.Max(y1, p.y);
            }
            float w = x1 - x0, h = y1 - y0;
            x0 += w * shrink; x1 -= w * shrink; y0 += h * shrink; y1 -= h * shrink;
            return new ImageStats.Rect2(Mathf.Clamp((int)x0, 0, Width - 1), Mathf.Clamp((int)y0, 0, Height - 1), Mathf.Clamp((int)x1, 1, Width), Mathf.Clamp((int)y1, 1, Height));
        }
    }
}
