using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // The look constants of the aurora shell, every one read through Tuning.Get (AuroraRenderer.ReadLook). Keys, defaults and the
    // ranges proposed for E40 are listed there.
    internal struct AuroraLook
    {
        public float Gain, ExposureScale, FoldPhase, RedStart, RayTileM, RayContrast, StepM, MaxSteps, HorizonStartDeg, HorizonFullDeg;
        public float DriftPerS, PathCap, BaseShare;

        internal static AuroraLook Defaults()
        {
            return new AuroraLook
            {
                Gain = 0.9f, ExposureScale = 1f, FoldPhase = 0.55f, RedStart = 0.55f, RayTileM = 400000f, RayContrast = 0.5f, StepM = 6000f,
                MaxSteps = 14f, HorizonStartDeg = AuroraModel.HorizonFadeStartDeg, HorizonFullDeg = AuroraModel.HorizonFadeFullDeg,
                DriftPerS = 0.004f, PathCap = 3f, BaseShare = 0.786f
            };
        }
    }

    // What one draw of the shell needs: where the body is, its pole, the frame of the model, the colours and the way to read the depth.
    internal struct AuroraDraw
    {
        // The camera's position in the world (the dome is centred on it) and the body's centre relative to the camera.
        public Vector3 CameraPosition, PlanetCentre;
        public float PlanetRadius;
        // The body's pole axis and a direction perpendicular to it (longitude zero), both unit vectors.
        public Vector3 Pole, RefA;
        // The altitude of the layer's base and top in metres.
        public float BaseAltitude, TopAltitude;
        public AuroraFrame Frame;
        public AuroraLook Look;
        // The body's aurora colour (lower part); the upper part is a red from the pass.
        public Color Colour;
        // The time in seconds, for the drift.
        public double Seconds;
        // The vertical field of view in radians and the target's size in pixels (the footprint of a pixel, the depth copy).
        public float FovYRad;
        public int WidthPx, HeightPx;
        // Where the scene's depth can be copied from at the event; null skips the depth test (the dome then covers the terrain).
        public RenderTargetIdentifier? DepthSource;
        // True where the depth is the engine's global _CameraDepthTexture (the shader samples it by name; the weather player), false
        // where DepthSource is a render target to bind (BuiltinRenderTextureType.Depth, the game).
        public bool DepthIsGlobalTexture;
    }

    // Records the aurora shell: a depth copy and one draw of the dome. Engine only (no game type), so the weather player draws with the
    // same class the mod does. Owns its material, mesh and noise texture; Dispose frees them.
    internal sealed class AuroraPass : IDisposable
    {
        private static readonly int IdDepth = Shader.PropertyToID("_AuroraDepth");
        private static readonly int IdDepthSource = Shader.PropertyToID("_AuroraDepthSource");
        private static readonly int IdSourceGlobal = Shader.PropertyToID("_AuroraSourceGlobal");
        private static readonly Color Red = new Color(1.0f, 0.18f, 0.22f, 1f);

        private readonly Material material;
        private readonly Mesh dome;
        private readonly Texture2D noise;
        private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();

        internal AuroraPass(Shader shader)
        {
            if (shader == null) throw new ArgumentNullException("shader");
            material = new Material(shader) { name = "ReDefinition Atmospheres aurora", hideFlags = HideFlags.HideAndDontSave };
            dome = BuildDome(24, 48);
            noise = BuildNoise();
        }

        private static Texture2D BuildNoise()
        {
            int size = AuroraNoise.DefaultSize;
            var t = new Texture2D(size, size, TextureFormat.RGBA32, true, true)
            {
                name = "ReDefinition Atmospheres aurora noise",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                hideFlags = HideFlags.HideAndDontSave
            };
            byte[] bytes = AuroraNoise.Generate(size, 20260711u);
            var pixels = new Color32[size * size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(bytes[i * 4], bytes[i * 4 + 1], bytes[i * 4 + 2], bytes[i * 4 + 3]);
            // Set pixel by pixel: the raw data of a texture with mips would need every level.
            t.SetPixels32(pixels);
            t.Apply(true, true);
            return t;
        }

        // A unit sphere of rings and segments around the origin; only its directions matter.
        internal static Mesh BuildDome(int rings, int segments)
        {
            var vertices = new Vector3[(rings + 1) * (segments + 1)];
            for (int r = 0; r <= rings; r++)
            {
                float theta = Mathf.PI * r / rings;
                for (int s = 0; s <= segments; s++)
                {
                    float phi = 2f * Mathf.PI * s / segments;
                    vertices[r * (segments + 1) + s] = new Vector3(Mathf.Sin(theta) * Mathf.Cos(phi), Mathf.Cos(theta), Mathf.Sin(theta) * Mathf.Sin(phi));
                }
            }
            var triangles = new int[rings * segments * 6];
            int k = 0;
            for (int r = 0; r < rings; r++)
                for (int s = 0; s < segments; s++)
                {
                    int a = r * (segments + 1) + s, b = a + 1, c = a + segments + 1, d = c + 1;
                    triangles[k++] = a; triangles[k++] = c; triangles[k++] = b;
                    triangles[k++] = b; triangles[k++] = c; triangles[k++] = d;
                }
            var mesh = new Mesh { name = "ReDefinition Atmospheres aurora dome", hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(4f, 4f, 4f));
            return mesh;
        }

        // Records the depth copy and the dome into the buffer. The render target is the camera's own (set here).
        internal void Record(CommandBuffer cmd, ref AuroraDraw draw)
        {
            if (!draw.Frame.Draw) return;
            AuroraLook look = draw.Look;
            bool reversed = SystemInfo.usesReversedZBuffer;
            bool useDepth = draw.DepthSource.HasValue;
            float intensity = draw.Frame.Intensity * look.Gain * look.ExposureScale;

            if (useDepth)
            {
                // The copy is global (_AuroraDepth); the property block below does not name it. Made with a draw (shader pass 1), so
                // that the camera's matrices are not touched the way a Blit would.
                cmd.GetTemporaryRT(IdDepth, new RenderTextureDescriptor(Mathf.Max(1, draw.WidthPx), Mathf.Max(1, draw.HeightPx), RenderTextureFormat.RFloat, 0),
                                   FilterMode.Point);
                cmd.SetGlobalFloat(IdSourceGlobal, draw.DepthIsGlobalTexture ? 1f : 0f);
                if (!draw.DepthIsGlobalTexture) cmd.SetGlobalTexture(IdDepthSource, draw.DepthSource.Value);
                cmd.SetRenderTarget(new RenderTargetIdentifier(IdDepth));
                cmd.DrawProcedural(Matrix4x4.identity, material, 1, MeshTopology.Triangles, 3, 1);
            }
            cmd.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);

            float halfWidthRad = draw.Frame.WidthDeg * 0.5f * Mathf.Deg2Rad;
            float drift = (float)((draw.Seconds * look.DriftPerS) % 64.0);
            float pixelAngle = 2f * Mathf.Tan(draw.FovYRad * 0.5f) / Mathf.Max(draw.HeightPx, 1);
            block.Clear();
            block.SetTexture("_AuroraNoise", noise);
            block.SetVector("_AuroraPlanet", new Vector4(draw.PlanetCentre.x, draw.PlanetCentre.y, draw.PlanetCentre.z, draw.PlanetRadius));
            block.SetVector("_AuroraPole", new Vector4(draw.Pole.x, draw.Pole.y, draw.Pole.z, 0f));
            block.SetVector("_AuroraRefA", new Vector4(draw.RefA.x, draw.RefA.y, draw.RefA.z, drift));
            block.SetVector("_AuroraRing", new Vector4(draw.Frame.RingLatDeg * Mathf.Deg2Rad, halfWidthRad, intensity, look.PathCap));
            block.SetVector("_AuroraLayer", new Vector4(draw.BaseAltitude, draw.TopAltitude,
                Mathf.Sin(look.HorizonStartDeg * Mathf.Deg2Rad), Mathf.Sin(look.HorizonFullDeg * Mathf.Deg2Rad)));
            block.SetVector("_AuroraLook", new Vector4(look.StepM, look.MaxSteps, look.RayTileM, look.RayContrast));
            block.SetVector("_AuroraLook2", new Vector4(look.FoldPhase, look.RedStart, pixelAngle, reversed ? 1f : 0f));
            block.SetVector("_AuroraColourA", new Vector4(draw.Colour.r, draw.Colour.g, draw.Colour.b, 1f));
            block.SetVector("_AuroraColourB", new Vector4(Red.r, Red.g, Red.b, 1f));
            block.SetVector("_AuroraScreen", new Vector4(useDepth ? 1f : 0f, 0f, 0f, 0f));
            cmd.DrawMesh(dome, Matrix4x4.TRS(draw.CameraPosition, Quaternion.identity, new Vector3(1000f, 1000f, 1000f)), material, 0, 0, block);
            if (useDepth) cmd.ReleaseTemporaryRT(IdDepth);
        }

        public void Dispose()
        {
            Destroy(material);
            Destroy(dome);
            Destroy(noise);
        }

        private static void Destroy(UnityEngine.Object o)
        {
            if (o != null) UnityEngine.Object.Destroy(o);
        }
    }
}
