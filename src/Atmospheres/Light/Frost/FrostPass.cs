using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // Everything one draw of the frost needs, in the units the shader reads. Built by the renderer from the state machine and the tuning.
    internal struct FrostDraw
    {
        // Reach of the rim frost (share of the half-diagonal), exposure 0 to 1, aspect of the target (width / height).
        public float Growth, Exposure, Aspect;
        // How much light there is to scatter, 0 to 1, and the frost colour in scene units for the fallback quad.
        public float Light;
        public Color QuadColour;
        // The stuck flakes and the drops as the shader reads them (x, y, radius, variant + age / trail); the counts say how many.
        public Vector4[] Flakes;
        public Vector4[] Drops;
        public int FlakeCount, DropCount;
        // The direction down the picture in the picture plane (x right, y up, unit).
        public Vector2 Gravity;
        public FrostTuning Tuning;
        // The interior mask of the internal camera, or null.
        public RenderTexture InteriorMask;
        // Whether the target is upside down against the picture (Tuning frost.flipY).
        public bool FlipY;
    }

    // Records the draws of the camera frost: the host composite after the upscaler, the interior mask, and the quad fallback. Engine only
    // (no game type), so the weather player draws with the same class the mod does. Owns its materials and the quad mesh; Dispose frees
    // them. The atlas texture belongs to the caller.
    internal sealed class FrostPass : IDisposable
    {
        private static readonly int IdMain = Shader.PropertyToID("_MainTex");
        private static readonly int IdAtlas = Shader.PropertyToID("_FrostAtlas");
        private static readonly int IdCopy = Shader.PropertyToID("_FrostCopy");
        private static readonly int IdCopyTexel = Shader.PropertyToID("_FrostCopy_TexelSize");
        private static readonly int IdMask = Shader.PropertyToID("_FrostMask");
        private static readonly int IdA = Shader.PropertyToID("_FrostA");
        private static readonly int IdB = Shader.PropertyToID("_FrostB");
        private static readonly int IdC = Shader.PropertyToID("_FrostC");
        private static readonly int IdD = Shader.PropertyToID("_FrostD");
        private static readonly int IdE = Shader.PropertyToID("_FrostE");
        private static readonly int IdTint = Shader.PropertyToID("_FrostTint");
        private static readonly int IdGravity = Shader.PropertyToID("_FrostGravity");
        private static readonly int IdQuad = Shader.PropertyToID("_FrostQuad");
        private static readonly int IdFlakes = Shader.PropertyToID("_FrostFlakes");
        private static readonly int IdDrops = Shader.PropertyToID("_FrostDrops");
        private static readonly int IdTiles = Shader.PropertyToID("_FrostTiles");

        internal const int PassHost = 0, PassCopy = 1, PassQuad = 2, PassMask = 3;
        internal const int QuadQueue = 3500;

        private readonly Material host;
        private readonly Material quad;
        private readonly Mesh quadMesh;
        private readonly Texture atlas;
        private Vector4[] tiles;

        internal FrostPass(Shader shader, Texture atlas)
        {
            if (shader == null) throw new ArgumentNullException("shader");
            if (atlas == null) throw new ArgumentNullException("atlas");
            this.atlas = atlas;
            host = new Material(shader) { name = "ReDefinition Atmospheres frost (host)", hideFlags = HideFlags.HideAndDontSave };
            quad = new Material(shader) { name = "ReDefinition Atmospheres frost (quad)", hideFlags = HideFlags.HideAndDontSave, renderQueue = QuadQueue };
            quadMesh = MakeQuad();
        }

        // Whether the host draw has to turn its picture coordinates over: the pass draws a clip-space triangle into a render texture, and
        // where the platform's textures start at the top (Direct3D) Unity stores a render texture's picture upside down in memory, so
        // the triangle's uv runs against the picture. setting is the tuning value frost.flipY: below 0 decides by the platform, 0 and
        // above force it off and on.
        internal static bool ResolveFlipY(float setting)
        {
            return setting < 0f ? SystemInfo.graphicsUVStartsAtTop : setting != 0f;
        }

        private static Mesh MakeQuad()
        {
            var mesh = new Mesh { name = "ReDefinition Atmospheres frost quad", hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(-1, 1, 0), new Vector3(1, 1, 0) };
            mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            // The vertex shader ignores the position; the bounds only have to meet the camera's frustum wherever it is drawn.
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(1e7f, 1e7f, 1e7f));
            return mesh;
        }

        // The vectors the shader reads for a draw. Packed once, written to a command buffer or to the quad's material.
        private static void Pack(ref FrostDraw d, out Vector4 a, out Vector4 b, out Vector4 c, out Vector4 dd, out Vector4 e, out Vector4 tint, out Vector4 gravity)
        {
            FrostTuning t = d.Tuning;
            a = new Vector4(d.Growth, d.Exposure, d.Aspect, 0f);
            b = new Vector4(t.Jag, t.Centre, t.Opacity, t.AtlasScale);
            c = new Vector4(t.Refraction, t.Blur, t.Whiten, d.Light);
            dd = new Vector4(d.FlakeCount, d.DropCount, t.DropGain, d.FlipY ? 1f : 0f);
            e = new Vector4(d.InteriorMask != null ? 1f : 0f, t.QuadAlpha, 0f, 0f);
            tint = new Vector4(t.TintR, t.TintG, t.TintB, 1f);
            Vector2 g = d.Gravity;
            float len = Mathf.Sqrt(g.x * g.x + g.y * g.y);
            gravity = len > 1e-4f ? new Vector4(g.x / len, g.y / len, 0f, 0f) : new Vector4(0f, -1f, 0f, 0f);
        }

        // The tile lists of the draw's flakes and drops, or null where there are none.
        private Vector4[] TilesOf(ref FrostDraw d, Vector4 gravity)
        {
            if (d.FlakeCount == 0 && d.DropCount == 0) return null;
            if (tiles == null) tiles = new Vector4[FrostTiles.Count];
            FrostTiles.Build(tiles, d.Flakes, d.FlakeCount, d.Drops, d.DropCount, new Vector2(gravity.x, gravity.y), d.Aspect);
            return tiles;
        }

        private void SetOnBuffer(CommandBuffer cmd, ref FrostDraw d)
        {
            Vector4 a, b, c, dd, e, tint, gravity;
            Pack(ref d, out a, out b, out c, out dd, out e, out tint, out gravity);
            Vector4[] tileList = TilesOf(ref d, gravity);
            if (tileList != null) cmd.SetGlobalVectorArray(IdTiles, tileList);
            cmd.SetGlobalTexture(IdAtlas, atlas);
            cmd.SetGlobalVector(IdA, a);
            cmd.SetGlobalVector(IdB, b);
            cmd.SetGlobalVector(IdC, c);
            cmd.SetGlobalVector(IdD, dd);
            cmd.SetGlobalVector(IdE, e);
            cmd.SetGlobalVector(IdTint, tint);
            cmd.SetGlobalVector(IdGravity, gravity);
            if (d.FlakeCount > 0 && d.Flakes != null) cmd.SetGlobalVectorArray(IdFlakes, d.Flakes);
            if (d.DropCount > 0 && d.Drops != null) cmd.SetGlobalVectorArray(IdDrops, d.Drops);
            if (d.InteriorMask != null) cmd.SetGlobalTexture(IdMask, d.InteriorMask);
        }

        // Records the host composite onto the upscaled image: a half-size copy of the image, then the frost, the flakes and the drops
        // refracting and scattering that copy. Records nothing where the draw has nothing on the glass.
        internal void RecordHost(CommandBuffer cmd, RenderTargetIdentifier image, int width, int height, ref FrostDraw d)
        {
            if (d.Growth <= 0.0005f && d.FlakeCount == 0 && d.DropCount == 0) return;
            if (width < 2 || height < 2) return;
            int cw = Math.Max(1, width / 2), ch = Math.Max(1, height / 2);
            SetOnBuffer(cmd, ref d);
            cmd.GetTemporaryRT(IdCopy, cw, ch, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            cmd.SetGlobalVector(IdCopyTexel, new Vector4(1f / cw, 1f / ch, cw, ch));
            cmd.SetGlobalTexture(IdMain, image);
            cmd.SetRenderTarget(new RenderTargetIdentifier(IdCopy));
            cmd.DrawProcedural(Matrix4x4.identity, host, PassCopy, MeshTopology.Triangles, 3, 1);
            cmd.SetRenderTarget(image);
            cmd.DrawProcedural(Matrix4x4.identity, host, PassHost, MeshTopology.Triangles, 3, 1);
            cmd.ReleaseTemporaryRT(IdCopy);
        }

        // Records the interior mask: where the internal camera's depth buffer holds geometry, into mask (R8 or similar). The source is
        // the camera's depth (BuiltinRenderTextureType.Depth at an event after the opaque queue).
        internal void RecordMask(CommandBuffer cmd, RenderTargetIdentifier depth, RenderTexture mask)
        {
            cmd.SetGlobalTexture(IdMain, depth);
            cmd.SetRenderTarget(mask);
            cmd.DrawProcedural(Matrix4x4.identity, host, PassMask, MeshTopology.Triangles, 3, 1);
        }

        // Queues the fallback quad for a camera (Graphics.DrawMesh, queue 3500): a plain alpha overlay in the scene. Draws nothing where
        // the draw has nothing on the glass.
        internal void DrawQuad(Camera camera, ref FrostDraw d)
        {
            if (camera == null) return;
            if (d.Growth <= 0.0005f && d.FlakeCount == 0 && d.DropCount == 0) return;
            Vector4 a, b, c, dd, e, tint, gravity;
            Pack(ref d, out a, out b, out c, out dd, out e, out tint, out gravity);
            quad.SetTexture(IdAtlas, atlas);
            quad.SetVector(IdA, a);
            quad.SetVector(IdB, b);
            quad.SetVector(IdC, c);
            quad.SetVector(IdD, dd);
            quad.SetVector(IdE, new Vector4(0f, d.Tuning.QuadAlpha, 0f, 0f));
            quad.SetVector(IdTint, tint);
            quad.SetVector(IdGravity, gravity);
            quad.SetVector(IdQuad, new Vector4(d.QuadColour.r, d.QuadColour.g, d.QuadColour.b, 1f));
            if (d.FlakeCount > 0 && d.Flakes != null) quad.SetVectorArray(IdFlakes, d.Flakes);
            if (d.DropCount > 0 && d.Drops != null) quad.SetVectorArray(IdDrops, d.Drops);
            Vector4[] tileList = TilesOf(ref d, gravity);
            if (tileList != null) quad.SetVectorArray(IdTiles, tileList);
            Graphics.DrawMesh(quadMesh, Matrix4x4.TRS(camera.transform.position + camera.transform.forward, Quaternion.identity, Vector3.one), quad, 0, camera);
        }

        public void Dispose()
        {
            if (host != null) UnityEngine.Object.Destroy(host);
            if (quad != null) UnityEngine.Object.Destroy(quad);
            if (quadMesh != null) UnityEngine.Object.Destroy(quadMesh);
        }
    }
}
