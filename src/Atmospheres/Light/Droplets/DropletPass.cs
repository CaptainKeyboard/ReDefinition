using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // What one draw of the droplets needs. Engine types only, so that the weather player draws with the class the mod uses.
    internal struct DropletDraw
    {
        internal DropletTuning Tuning;
        internal float Wet01;
        internal float ClockS;
        // The sliding velocity in picture heights per second (x right, y up).
        internal Vector2 Slide;
        internal float Aspect;
        internal bool FlipY;
        // The interior mask (1 where the cabin's own geometry is) or null.
        internal Texture InteriorMask;
        // 0..1: the light on the drops' highlights.
        internal float Light;
        internal Color QuadColour;
    }

    // Records the droplets (shader ReDefinition/AtmospheresDroplet): the host composite onto the upscaled image, the fallback quad and
    // the aid map. Owns its materials and the quad mesh; Dispose frees them.
    internal sealed class DropletPass : IDisposable
    {
        internal const string ShaderName = "ReDefinition/AtmospheresDroplet";
        internal const int QuadQueue = 3500;
        private const int PassHost = 0;
        private const int PassCopy = 1;
        private const int PassQuad = 2;
        private const int PassMap = 3;

        private static readonly int IdA = Shader.PropertyToID("_DrpA");
        private static readonly int IdB = Shader.PropertyToID("_DrpB");
        private static readonly int IdC = Shader.PropertyToID("_DrpC");
        private static readonly int IdD = Shader.PropertyToID("_DrpD");
        private static readonly int IdQuad = Shader.PropertyToID("_DrpQuad");
        private static readonly int IdCopy = Shader.PropertyToID("_DrpCopy");
        private static readonly int IdMask = Shader.PropertyToID("_DrpMask");
        private static readonly int IdMain = Shader.PropertyToID("_MainTex");

        private readonly Material host;
        private readonly Material quad;
        private readonly Mesh quadMesh;

        internal DropletPass(Shader shader)
        {
            if (shader == null) throw new ArgumentNullException("shader");
            host = new Material(shader) { name = "ReDefinition Atmospheres droplets (host)", hideFlags = HideFlags.HideAndDontSave };
            quad = new Material(shader) { name = "ReDefinition Atmospheres droplets (quad)", hideFlags = HideFlags.HideAndDontSave, renderQueue = QuadQueue };
            quadMesh = new Mesh { name = "ReDefinition Atmospheres droplets quad", hideFlags = HideFlags.HideAndDontSave };
            quadMesh.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(-1, 1, 0), new Vector3(1, 1, 0) };
            quadMesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            quadMesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            quadMesh.bounds = new Bounds(Vector3.zero, new Vector3(1e7f, 1e7f, 1e7f));
        }

        internal static bool NeedsDraw(ref DropletDraw d)
        {
            return d.Wet01 > d.Tuning.SkipBelow;
        }

        // The radius of a large drop in picture heights from its pixel size at 1080 lines.
        internal static float RadiusOf(ref DropletTuning t)
        {
            return t.SizePx / 1080f;
        }

        private void Set(Material m, ref DropletDraw d, bool onBuffer, CommandBuffer cmd)
        {
            Vector4 a = new Vector4(d.Wet01, d.ClockS, d.Aspect > 0.1f ? d.Aspect : 16f / 9f, d.FlipY ? 1f : 0f);
            Vector4 b = new Vector4(d.Slide.x, d.Slide.y, d.Tuning.Refraction, RadiusOf(ref d.Tuning));
            Vector4 c = new Vector4(d.InteriorMask != null ? 1f : 0f, d.Tuning.QuadAlpha, d.Light, d.Tuning.CoverMax);
            Vector4 dd = new Vector4(d.Tuning.Rim, d.Tuning.TrailAlpha, 0f, 0f);
            if (onBuffer)
            {
                cmd.SetGlobalVector(IdD, dd);
                cmd.SetGlobalVector(IdA, a);
                cmd.SetGlobalVector(IdB, b);
                cmd.SetGlobalVector(IdC, c);
                if (d.InteriorMask != null) cmd.SetGlobalTexture(IdMask, d.InteriorMask);
            }
            else
            {
                m.SetVector(IdD, dd);
                m.SetVector(IdA, a);
                m.SetVector(IdB, b);
                m.SetVector(IdC, c);
                m.SetVector(IdQuad, new Vector4(d.QuadColour.r, d.QuadColour.g, d.QuadColour.b, 1f));
            }
        }

        // Records the host composite onto the upscaled image: a half-size copy of the image, then the drops as lenses over it.
        internal void RecordHost(CommandBuffer cmd, RenderTargetIdentifier image, int width, int height, ref DropletDraw d)
        {
            if (!NeedsDraw(ref d) || width < 2 || height < 2) return;
            int cw = Math.Max(1, width / 2), ch = Math.Max(1, height / 2);
            Set(host, ref d, true, cmd);
            cmd.GetTemporaryRT(IdCopy, cw, ch, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            cmd.SetGlobalTexture(IdMain, image);
            cmd.SetRenderTarget(new RenderTargetIdentifier(IdCopy));
            cmd.DrawProcedural(Matrix4x4.identity, host, PassCopy, MeshTopology.Triangles, 3, 1);
            cmd.SetRenderTarget(image);
            cmd.DrawProcedural(Matrix4x4.identity, host, PassHost, MeshTopology.Triangles, 3, 1);
            cmd.ReleaseTemporaryRT(IdCopy);
        }

        // Records the aid map (R drop discs, G trails, B beads) into the target the caller has set.
        internal void RecordMap(CommandBuffer cmd, ref DropletDraw d)
        {
            Set(host, ref d, true, cmd);
            cmd.DrawProcedural(Matrix4x4.identity, host, PassMap, MeshTopology.Triangles, 3, 1);
        }

        // Queues the fallback quad for a camera. Draws nothing where the draw has nothing on the glass.
        internal void DrawQuad(Camera camera, ref DropletDraw d)
        {
            if (camera == null || !NeedsDraw(ref d)) return;
            Set(quad, ref d, false, null);
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
