using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // What one composite needs besides the frame: where the camera looks, which mode draws, the depth, the tuning and the veil colours.
    internal struct PrecipDraw
    {
        // True for the composite after the upscaler (render-size depth, the veil colour relative to the image); false for the Scene
        // composite (hardware depth test, the veil colour in scene units).
        public bool Host;
        // The weight of this composite in a cross-fade, 0 to 1.
        public float Weight;
        // The camera's view and (non-jittered) projection, used by the Host composite; the Scene composite takes the engine's.
        public Matrix4x4 View, Projection;
        // The world directions of the box frame: east, up, north.
        public Vector3 AxisE, AxisU, AxisN;
        // The size of the target the composite draws into, in pixels.
        public int TargetWidth, TargetHeight;
        // The render-size depth the host gave, or null.
        public RenderTexture Depth;
        public bool DepthFlipY;
        // The camera's place in the frame, in metres, accumulated (double), and the time in seconds mod 60.
        public double CamE, CamU, CamN;
        public float TimeMod60;
        // The distance from the camera down to the ground, for the splashes.
        public float GroundDistance;
        public int Seed;
        public Color VeilGroundColour, VeilSkyColour;
        public float SkyGreyStartDeg, SkyGreyFullDeg;
        // The share of the storm grade, the veil's luminance gain (fogBrightness), the dt for the luminance smoothing.
        public float Grade, LumaGain, Dt;
        public PrecipTuning Tuning;
    }

    // Records the draws of the precipitation into a command buffer: the veil, the three layers of billboards, the splashes and, in Host
    // mode, the capture of the image's luminance. Engine only (no game type), so the weather player draws with the same class the mod
    // does. Owns its material, its sheet and its small targets; Dispose frees them.
    internal sealed class PrecipPass : IDisposable
    {
        private static readonly int ZTest = Shader.PropertyToID("_ZTest");
        private static readonly int IdMain = Shader.PropertyToID("_MainTex");
        private static readonly int IdPrev = Shader.PropertyToID("_PrecipPrev");
        private static readonly int IdLuma = Shader.PropertyToID("_PrecipLuma");
        private static readonly int IdBlend = Shader.PropertyToID("_PrecipBlend");

        private readonly Material scene;
        private readonly Material host;
        private readonly Texture2D sheet;
        private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        private RenderTexture lumaDown, lumaA, lumaB;
        private bool lumaFlip;
        private bool lumaReady;

        internal PrecipPass(Shader shader)
        {
            if (shader == null) throw new ArgumentNullException("shader");
            scene = new Material(shader) { name = "ReDefinition Atmospheres precip (scene)", hideFlags = HideFlags.HideAndDontSave };
            host = new Material(shader) { name = "ReDefinition Atmospheres precip (host)", hideFlags = HideFlags.HideAndDontSave };
            scene.SetFloat(ZTest, (float)CompareFunction.LessEqual);
            host.SetFloat(ZTest, (float)CompareFunction.Always);
            sheet = PrecipSheets.Make();
        }

        // The smoothed luminance of the image after the last capture, or -1 before the first one is read. Diagnostics only: the read
        // waits for the GPU.
        internal float ReadLuma()
        {
            RenderTexture current = lumaFlip ? lumaB : lumaA;
            if (current == null) return -1f;
            RenderTexture previous = RenderTexture.active;
            var read = new Texture2D(1, 1, TextureFormat.RGBAHalf, false);
            RenderTexture.active = current;
            read.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false);
            RenderTexture.active = previous;
            float v = read.GetPixel(0, 0).r;
            UnityEngine.Object.Destroy(read);
            return v;
        }

        private void EnsureLuma()
        {
            if (lumaReady) return;
            lumaDown = Make(16, true);
            lumaA = Make(1, false);
            lumaB = Make(1, false);
            RenderTexture previous = RenderTexture.active;
            foreach (RenderTexture t in new[] { lumaA, lumaB })
            {
                RenderTexture.active = t;
                GL.Clear(false, true, new Color(0.5f, 0.5f, 0.5f, 1f));
            }
            RenderTexture.active = previous;
            lumaReady = true;
        }

        private static RenderTexture Make(int size, bool mips)
        {
            var t = new RenderTexture(size, size, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = "ReDefinition Atmospheres precip luma",
                useMipMap = mips,
                autoGenerateMips = false,
                filterMode = mips ? FilterMode.Bilinear : FilterMode.Point,
                hideFlags = HideFlags.HideAndDontSave
            };
            t.Create();
            return t;
        }

        // Records the capture of the image's luminance: the whole picture into 16 x 16 pixels, its smallest mip mixed into the previous
        // value. The next veil reads the result. The caller sets the render target again afterwards.
        internal void RecordLuma(CommandBuffer cmd, RenderTargetIdentifier source, float dt, float tauSeconds)
        {
            EnsureLuma();
            RenderTexture previous = lumaFlip ? lumaB : lumaA;
            RenderTexture next = lumaFlip ? lumaA : lumaB;
            float blend = 1f - (float)Math.Exp(-Math.Max(dt, 0f) / Math.Max(tauSeconds, 1e-3f));
            cmd.SetGlobalTexture(IdMain, source);
            cmd.SetRenderTarget(lumaDown);
            cmd.DrawProcedural(Matrix4x4.identity, host, 3, MeshTopology.Triangles, 3, 1);
            cmd.GenerateMips(lumaDown);
            cmd.SetGlobalTexture(IdMain, lumaDown);
            cmd.SetGlobalTexture(IdPrev, previous);
            cmd.SetGlobalFloat(IdBlend, blend);
            cmd.SetRenderTarget(next);
            cmd.DrawProcedural(Matrix4x4.identity, host, 4, MeshTopology.Triangles, 3, 1);
            cmd.SetGlobalTexture(IdLuma, next);
            lumaFlip = !lumaFlip;
        }

        // Records the veil, the layers and the splashes of a frame. The render target is the caller's (Scene: the camera's with its
        // depth; Host: the caller sets the upscaled image first).
        internal void Record(CommandBuffer cmd, ref PrecipFrame frame, ref PrecipDraw draw)
        {
            if (!frame.Draw) return;
            Material material = draw.Host ? host : scene;
            float weight = Mathf.Clamp01(draw.Weight);
            if (weight <= 0f) return;
            PrecipTuning tuning = draw.Tuning;

            Vector4 target = new Vector4(draw.TargetWidth, draw.TargetHeight, draw.DepthFlipY ? 1f : 0f, draw.Host && draw.Depth != null ? 1f : 0f);

            // The veil first, so that the flakes are not washed out by it.
            if (frame.VeilGround > 0.001f || frame.VeilSky > 0.001f)
            {
                block.Clear();
                Common(block, ref draw, target);
                block.SetVector("_PrecipVeilA", new Vector4(
                    PrecipMath.VeilShare(frame.VeilGround, weight), PrecipMath.VeilShare(Mathf.Max(frame.VeilSky, frame.VeilGround), weight),
                    Mathf.Sin(draw.SkyGreyStartDeg * Mathf.Deg2Rad), Mathf.Sin(Mathf.Max(draw.SkyGreyFullDeg, draw.SkyGreyStartDeg + 0.01f) * Mathf.Deg2Rad)));
                block.SetVector("_PrecipVeilGround", draw.VeilGroundColour);
                block.SetVector("_PrecipVeilSky", draw.VeilSkyColour);
                block.SetVector("_PrecipVeilGrade", new Vector4(draw.Grade, tuning.VeilLumaFloor, draw.Host ? 1f : 0f, draw.LumaGain * tuning.VeilLumaGain));
                block.SetVector("_PrecipColour", new Vector4(1f, 1f, 1f, 1f));
                cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3, 1, block);
            }

            RecordLayer(cmd, material, ref frame, ref draw, frame.Near, 0, target, weight);
            RecordLayer(cmd, material, ref frame, ref draw, frame.Mid, 1, target, weight);
            RecordLayer(cmd, material, ref frame, ref draw, frame.Far, 2, target, weight);

            if (frame.SplashCount >= 1f)
            {
                block.Clear();
                Common(block, ref draw, target);
                float field = 2f * tuning.SplashRangeM;
                block.SetVector("_PrecipSplash", new Vector4(draw.GroundDistance, tuning.SplashRangeM, 0.35f, 2f));
                block.SetVector("_PrecipSplashCam", new Vector4(Frac((float)(draw.CamE / field)), Frac((float)(draw.CamN / field)), draw.Seed & 0xFFFFFF, 0f));
                block.SetVector("_PrecipColour", new Vector4(frame.Colour0 * frame.Light, frame.Colour1 * frame.Light, frame.Colour2 * frame.Light, 0.8f * weight));
                cmd.DrawProcedural(Matrix4x4.identity, material, 2, MeshTopology.Triangles, 6, (int)frame.SplashCount, block);
            }
        }

        private void RecordLayer(CommandBuffer cmd, Material material, ref PrecipFrame frame, ref PrecipDraw draw, PrecipLayerFrame layer, int index,
            Vector4 target, float weight)
        {
            if (!layer.Active || layer.Count <= 0) return;
            block.Clear();
            Common(block, ref draw, target);
            PrecipTuning tuning = draw.Tuning;
            block.SetVector("_PrecipLayerBox", new Vector4(layer.BoxW, layer.BoxH, tuning.EdgeShare, draw.Seed & 0xFFFFFF));
            block.SetVector("_PrecipLayerSize", new Vector4(layer.SizeMin, layer.SizeMax, layer.Opacity, index * 100003f));
            block.SetVector("_PrecipDrift", new Vector4(layer.DriftX, layer.DriftY, layer.DriftZ, 0f));
            block.SetVector("_PrecipCamBox", new Vector4(
                Frac((float)(draw.CamE / layer.BoxW)), Frac((float)(draw.CamU / layer.BoxH)), Frac((float)(draw.CamN / layer.BoxW)), draw.TimeMod60));
            block.SetVector("_PrecipVel", new Vector4(frame.VelE, frame.VelU, frame.VelN, frame.Look.Sway));
            float gain = draw.Host ? tuning.HostFlakeGain : 1f;
            block.SetVector("_PrecipColour", new Vector4(frame.Colour0 * frame.Light * gain, frame.Colour1 * frame.Light * gain, frame.Colour2 * frame.Light * gain, weight));
            block.SetVector("_PrecipLook", new Vector4(frame.Look.Stretch, tuning.FloorPx, draw.Host ? tuning.HostMinContrast : tuning.SceneMinContrast, (float)frame.Look.Sheet));
            cmd.DrawProcedural(Matrix4x4.identity, material, 1, MeshTopology.Triangles, 6, layer.Count, block);
        }

        private void Common(MaterialPropertyBlock b, ref PrecipDraw draw, Vector4 target)
        {
            b.SetTexture("_PrecipSheet", sheet);
            b.SetVector("_PrecipTarget", target);
            if (draw.Depth != null)
            {
                b.SetTexture("_PrecipDepth", draw.Depth);
                b.SetVector("_PrecipDepthTexel", new Vector4(1f / draw.Depth.width, 1f / draw.Depth.height, 0f, 0f));
            }
            b.SetFloat("_PrecipUseMat", draw.Host ? 1f : 0f);
            if (draw.Host)
            {
                b.SetMatrix("_PrecipV", draw.View);
                b.SetMatrix("_PrecipP", draw.Projection);
            }
            b.SetVector("_PrecipAxisE", draw.AxisE);
            b.SetVector("_PrecipAxisU", draw.AxisU);
            b.SetVector("_PrecipAxisN", draw.AxisN);
        }

        private static float Frac(float v)
        {
            return v - Mathf.Floor(v);
        }

        public void Dispose()
        {
            Destroy(scene);
            Destroy(host);
            Destroy(sheet);
            Destroy(lumaDown);
            Destroy(lumaA);
            Destroy(lumaB);
            lumaReady = false;
        }

        private static void Destroy(UnityEngine.Object o)
        {
            if (o != null) UnityEngine.Object.Destroy(o);
        }
    }
}
