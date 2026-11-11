using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // One lamp as the pass draws it: where it is, how far it reaches, the medium around it and its colour. Positions are world
    // positions of the frame.
    internal struct LampDraw
    {
        internal Vector3 Position, Axis;
        internal float Range;
        // The cosine of the outer half angle; 0 or less for a point light.
        internal float CosOuter;
        // The half angle of a spot in radians (the proxy cone's); ignored for a point light.
        internal float HalfAngleRad;
        // The lamp's colour times its intensity, the gain, the strength and the fade/lag factor, in scene units.
        internal Color Colour;
        // Sigma at the lamp and at the camera, in 1/m.
        internal float Sigma, SigmaCamera;
        // The depth of the glints (0 for none).
        internal float GlintAmp;
    }

    // What one record needs besides the lamps: the camera, the depth and the tuning.
    internal struct LampFrame
    {
        internal Vector3 CameraPosition, CameraForward;
        // The camera's near and far plane, for the depth set-up.
        internal float Near, Far;
        // True where the depth texture is bound (the copy or the host's depth); false: the beam is not clipped.
        internal bool UseDepth;
        // The depth lookup is flipped in y (Tuning lamp.depthFlipY).
        internal bool DepthFlipY;
        // Reversed z (SystemInfo.usesReversedZBuffer).
        internal bool ReversedZ;
        // The temporal jitter offset (0: the pattern is the same every frame), the drift of the flakes since the start, in metres.
        internal float JitterOffset;
        internal Vector3 Drift;
        // The share of the draw in a cross-fade of the two ways of composing (0 to 1) times any gain of the way; multiplies the colour.
        internal float Weight;
        internal LampTuning Tuning;
    }

    // The pure part of the depth set-up, apart from the engine: Unity's _ZBufferParams for a camera.
    internal static class LampDepth
    {
        // x, y, z, w of _ZBufferParams. LinearEyeDepth(raw) = 1 / (z raw + w).
        internal static void ZParams(float near, float far, bool reversed, out float x, out float y, out float z, out float w)
        {
            if (reversed)
            {
                x = -1f + far / near;
                y = 1f;
            }
            else
            {
                x = 1f - far / near;
                y = far / near;
            }
            z = x / far;
            w = y / far;
        }
    }

    // Records the lamp scatter into a command buffer: the copy of the depth, and one draw of a proxy mesh per lamp. Engine only (no game
    // type), so the weather player draws with the same class the mod does. Owns its material, its meshes and the depth copy; Dispose
    // frees them.
    internal sealed class LampPass : IDisposable
    {
        private static readonly int IdPos = Shader.PropertyToID("_LampPos");
        private static readonly int IdDir = Shader.PropertyToID("_LampDir");
        private static readonly int IdShape = Shader.PropertyToID("_LampShape");
        private static readonly int IdMedium = Shader.PropertyToID("_LampMedium");
        private static readonly int IdColour = Shader.PropertyToID("_LampColour");
        private static readonly int IdGlint = Shader.PropertyToID("_LampGlint");
        private static readonly int IdDrift = Shader.PropertyToID("_LampDrift");
        private static readonly int IdCamPos = Shader.PropertyToID("_LampCamPos");
        private static readonly int IdCamFwd = Shader.PropertyToID("_LampCamFwd");
        private static readonly int IdDepthMode = Shader.PropertyToID("_LampDepthMode");
        private static readonly int IdZ = Shader.PropertyToID("_LampZ");
        private static readonly int IdDepth = Shader.PropertyToID("_ReDefLampDepth");

        private readonly Material material;
        private readonly Mesh sphere, cone;
        private readonly Texture2D noise;
        private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        private RenderTexture depthCopy;

        internal LampPass(Shader shader)
        {
            if (shader == null) throw new ArgumentNullException("shader");
            material = new Material(shader) { name = "ReDefinition Atmospheres lamp", hideFlags = HideFlags.HideAndDontSave };
            sphere = MakeSphere(12, 8);
            cone = MakeCone(16);
            noise = MakeNoise();
            material.SetTexture("_LampNoise", noise);
        }

        // The table of the glints' value noise: red is the lattice value at a texel, green the value at the texel (37, 17) on, which is
        // the lattice point one step up in z (the shader reads three dimensions out of one bilinear lookup).
        private static Texture2D MakeNoise()
        {
            var pixels = new Color32[256 * 256];
            for (int y = 0; y < 256; y++)
            {
                for (int x = 0; x < 256; x++)
                {
                    byte r = (byte)(LampMath.NoiseValue(x, y) * 255f + 0.5f);
                    byte g = (byte)(LampMath.NoiseValue(x + 37, y + 17) * 255f + 0.5f);
                    pixels[y * 256 + x] = new Color32(r, g, 0, 255);
                }
            }
            var texture = new Texture2D(256, 256, TextureFormat.RGBA32, false, true)
            {
                name = "ReDefinition Atmospheres lamp noise",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Repeat,
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        // The depth copy's render texture (null before the first EnsureDepth).
        internal RenderTexture DepthCopy
        {
            get { return depthCopy; }
        }

        // Makes the depth copy at that size (a float texture, point filtered) when it is not there yet or has another size.
        internal void EnsureDepth(int width, int height)
        {
            if (depthCopy != null && depthCopy.width == width && depthCopy.height == height) return;
            ReleaseDepth();
            depthCopy = new RenderTexture(width, height, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
            {
                name = "ReDefinition Atmospheres lamp depth",
                filterMode = FilterMode.Point,
                hideFlags = HideFlags.HideAndDontSave
            };
            depthCopy.Create();
        }

        internal void ReleaseDepth()
        {
            if (depthCopy == null) return;
            depthCopy.Release();
            UnityEngine.Object.Destroy(depthCopy);
            depthCopy = null;
        }

        // Records the copy of the global depth texture into the depth copy. Run it at BeforeForwardAlpha. RECON P6 measured copies made
        // by command-buffer taps of the camera's depth buffer and of the global texture there (valid with and without Scatterer, the
        // global overwritten at AfterForwardAlpha); it did not measure a material that reads the global, which is what pass 1 of the
        // shader does [VERIFY in K-W3b: D32.depth_copy_varies], and the weather player's forward camera read the global as a blit source
        // wrongly, which is why the copy is a shader pass. That the blit leaves the camera's render target as it found it for the draw
        // at AfterForwardAlpha is Unity's behaviour for a blit [VERIFY in the game].
        internal void RecordDepthCopy(CommandBuffer cmd)
        {
            if (depthCopy == null) return;
            cmd.Blit(Texture2D.blackTexture, new RenderTargetIdentifier(depthCopy), material, 1);
        }

        // The spread (largest minus smallest) of the depth copy over nine places, read back from the GPU: a copy that holds one value
        // everywhere is not a copy of a scene's depth. For the probe only (it waits for the GPU); -1 without a copy.
        internal float DepthSpread()
        {
            if (depthCopy == null) return -1f;
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = depthCopy;
            var one = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            float min = float.MaxValue, max = float.MinValue;
            for (int iy = 0; iy < 3; iy++)
            {
                for (int ix = 0; ix < 3; ix++)
                {
                    int x = Mathf.Clamp((int)((ix + 0.5f) / 3f * depthCopy.width), 0, depthCopy.width - 1);
                    int y = Mathf.Clamp((int)((iy + 0.5f) / 3f * depthCopy.height), 0, depthCopy.height - 1);
                    one.ReadPixels(new Rect(x, y, 1, 1), 0, 0);
                    one.Apply(false);
                    float v = one.GetPixel(0, 0).r;
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }
            RenderTexture.active = previous;
            UnityEngine.Object.Destroy(one);
            return max - min;
        }

        // Records one draw per lamp. The render target is the caller's. depth is the texture to clip against (the copy, or the host's);
        // null draws without the clip.
        internal void Record(CommandBuffer cmd, LampDraw[] draws, int count, ref LampFrame frame, Texture depth)
        {
            if (count <= 0) return;
            float zx, zy, zz, zw;
            LampDepth.ZParams(frame.Near, frame.Far, frame.ReversedZ, out zx, out zy, out zz, out zw);
            bool useDepth = frame.UseDepth && depth != null;
            LampTuning t = frame.Tuning;
            for (int i = 0; i < count; i++)
            {
                LampDraw d = draws[i];
                if (d.Range <= 0f || d.Sigma <= 0f) continue;
                bool spot = d.CosOuter > 0f;
                block.Clear();
                block.SetVector(IdPos, new Vector4(d.Position.x, d.Position.y, d.Position.z, d.Range));
                block.SetVector(IdDir, new Vector4(d.Axis.x, d.Axis.y, d.Axis.z, spot ? d.CosOuter : 0f));
                float cosInner = spot ? Mathf.Cos(Mathf.Acos(Mathf.Min(d.CosOuter, 1f)) * t.SpotInner) : 1f;
                block.SetVector(IdShape, new Vector4(cosInner, t.NearFadeM, t.PhaseG, t.Albedo));
                block.SetVector(IdMedium, new Vector4(d.Sigma, d.SigmaCamera, t.AttenScale, Mathf.Clamp(Mathf.Round(t.Steps), 4f, 16f)));
                block.SetVector(IdColour, new Vector4(d.Colour.r * frame.Weight, d.Colour.g * frame.Weight, d.Colour.b * frame.Weight, 0f));
                block.SetVector(IdGlint, new Vector4(d.GlintAmp, t.GlintSizeM, frame.JitterOffset, useDepth ? 1f : 0f));
                block.SetVector(IdDrift, frame.Drift);
                block.SetVector(IdCamPos, frame.CameraPosition);
                block.SetVector(IdCamFwd, frame.CameraForward);
                block.SetVector(IdDepthMode, new Vector4(frame.DepthFlipY ? 1f : 0f, frame.ReversedZ ? 1f : 0f, 0f, 0f));
                block.SetVector(IdZ, new Vector4(zx, zy, zz, zw));
                if (useDepth) block.SetTexture(IdDepth, depth);
                // The proxy: the cone around a spot's reach (widened by 5 % so that the edge is inside), else the sphere of the range.
                Matrix4x4 matrix;
                Mesh mesh;
                if (spot && d.HalfAngleRad < 1.3f)
                {
                    float radius = d.Range * Mathf.Tan(d.HalfAngleRad) * 1.05f;
                    matrix = Matrix4x4.TRS(d.Position, Quaternion.LookRotation(d.Axis, Mathf.Abs(d.Axis.y) > 0.99f ? Vector3.right : Vector3.up),
                                           new Vector3(radius, radius, d.Range * 1.001f));
                    mesh = cone;
                }
                else
                {
                    matrix = Matrix4x4.TRS(d.Position, Quaternion.identity, Vector3.one * d.Range * 1.001f);
                    mesh = sphere;
                }
                cmd.DrawMesh(mesh, matrix, material, 0, 0, block);
            }
        }

        public void Dispose()
        {
            ReleaseDepth();
            Destroy(material);
            Destroy(sphere);
            Destroy(cone);
            Destroy(noise);
        }

        private static void Destroy(UnityEngine.Object o)
        {
            if (o != null) UnityEngine.Object.Destroy(o);
        }

        // A unit sphere: rings x segments, outward winding.
        private static Mesh MakeSphere(int segments, int rings)
        {
            var vertices = new Vector3[(rings + 1) * (segments + 1)];
            for (int r = 0; r <= rings; r++)
            {
                float phi = Mathf.PI * r / rings;
                for (int s = 0; s <= segments; s++)
                {
                    float theta = 2f * Mathf.PI * s / segments;
                    vertices[r * (segments + 1) + s] = new Vector3(Mathf.Sin(phi) * Mathf.Cos(theta), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(theta));
                }
            }
            var triangles = new int[rings * segments * 6];
            int k = 0;
            for (int r = 0; r < rings; r++)
            {
                for (int s = 0; s < segments; s++)
                {
                    int a = r * (segments + 1) + s, b = a + segments + 1;
                    triangles[k++] = a; triangles[k++] = a + 1; triangles[k++] = b;
                    triangles[k++] = a + 1; triangles[k++] = b + 1; triangles[k++] = b;
                }
            }
            var mesh = new Mesh { name = "lamp sphere", hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 2.2f);
            return mesh;
        }

        // A unit cone: the apex at the origin, the base circle of radius 1 at z = 1, closed with a cap. A cull of front faces then
        // keeps the inner side, so that the camera inside the cone sees it too.
        private static Mesh MakeCone(int segments)
        {
            var vertices = new Vector3[segments + 3];
            vertices[0] = Vector3.zero;
            for (int s = 0; s < segments; s++)
            {
                float a = 2f * Mathf.PI * s / segments;
                vertices[1 + s] = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 1f);
            }
            vertices[segments + 1] = new Vector3(0f, 0f, 1f);
            var triangles = new int[segments * 6];
            int k = 0;
            for (int s = 0; s < segments; s++)
            {
                int a = 1 + s, b = 1 + (s + 1) % segments;
                // The side: apex, next, this (outward as seen from outside).
                triangles[k++] = 0; triangles[k++] = b; triangles[k++] = a;
                // The cap: centre, this, next.
                triangles[k++] = segments + 1; triangles[k++] = a; triangles[k++] = b;
            }
            var mesh = new Mesh { name = "lamp cone", hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.bounds = new Bounds(new Vector3(0f, 0f, 0.5f), new Vector3(2.1f, 2.1f, 1.1f));
            return mesh;
        }
    }
}
