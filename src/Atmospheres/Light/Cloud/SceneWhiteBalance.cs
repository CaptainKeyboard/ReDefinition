using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // The scene's white balance (owner 2026-11-19, audit A2): the whole upscaled picture is divided by the sun's tint, through the host's after-upscaling hook,
    // instead of the clouds alone (the layer pass's white point, _OutW) with a counter-correction on their haze (CloudHazePlan.ExemptFromWhitePoint).
    // The cloud module hands the tint over every frame and asks Takes() whether the scene balances this frame: only then does it drop the clouds' own white
    // point, so the picture is balanced once, never twice and never not at all. Without the host (the main mod absent) or while its hook stays silent the
    // clouds keep their own white point as before.
    internal sealed class SceneWhiteBalance
    {
        internal const string ShaderName = "ReDefinition/AtmospheresWhiteBalance";
        // The hook counts as alive while it was called within this many frames.
        private const int AliveFrames = 3;

        // Taken when the material is made: the module is built in the unit tests, where Unity's calls are not there.
        private int idScale, idCopy, idSource;

        private Material material;
        private PostUpscaleDraw draw;
        private bool registered, failed;
        private int lastCallFrame = -1000;
        // The tint of this frame and whether the clouds left their own white point out for it (set by the module before the host draws).
        private Color tint = Color.white;
        private bool scene;

        // The tint raised to the strength per channel (0: white, 1: the tint itself): a partial balance that keeps a share of the light's colour.
        internal static Color Partial(Color tint, float strength)
        {
            return new Color(Mathf.Pow(Mathf.Max(tint.r, 1e-3f), strength), Mathf.Pow(Mathf.Max(tint.g, 1e-3f), strength), Mathf.Pow(Mathf.Max(tint.b, 1e-3f), strength), 1f);
        }

        // Whether the scene balances this frame: wanted, registered and the hook called lately.
        internal bool Takes(bool wanted)
        {
            return wanted && registered && Time.frameCount - lastCallFrame <= AliveFrames;
        }

        // Registers or removes the draw as the switch says, and keeps this frame's tint; scene: the clouds dropped their own white point.
        internal void Update(bool wanted, Color frameTint, bool sceneThisFrame)
        {
            tint = frameTint;
            scene = sceneThisFrame;
            if (wanted && !registered && !failed)
            {
                if (material == null)
                {
                    string reason;
                    Shader shader = AtmosphereShaders.Get(ShaderName, out reason);
                    if (shader == null)
                    {
                        AtmospheresLog.WarnOnce("scene white balance: the shader " + ShaderName + " is not available (" + reason + "); the clouds keep their own white point");
                        failed = true;
                        return;
                    }
                    material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    idScale = Shader.PropertyToID("_WbScale");
                    idCopy = Shader.PropertyToID("_ReDefinitionWbCopy");
                    idSource = Shader.PropertyToID("_WbSource");
                }
                if (Services.PostUpscale is NullPostUpscaleCompositor) Services.PostUpscale = new ApiBridge();
                if (draw == null) draw = Draw;
                registered = Services.PostUpscale.Register("scene-white-balance", draw, false);
                if (!registered) failed = true;
                AtmospheresLog.Say("scene white balance: " + (registered ? "registered with the host" : "no host hook; the clouds keep their own white point"));
            }
            else if (!wanted && registered)
            {
                Services.PostUpscale.Unregister(draw);
                registered = false;
            }
        }

        // No cloud frame this frame (the module's pass is hidden): the picture is left as it is until the next frame hands a tint over.
        internal void Clear()
        {
            scene = false;
        }

        private void Draw(CommandBuffer cb, RenderTexture upscaled, RenderTexture depth, Camera camera)
        {
            // Only a linear HDR image can be scaled by the tint (the rig's pass-through hands its render-size target over, which may be LDR in gamma): elsewhere the
            // hook does not count as alive, so the clouds keep their own white point.
            if (upscaled == null || !IsHdr(upscaled.format)) return;
            lastCallFrame = Time.frameCount;
            if (!scene || material == null) return;
            material.SetVector(idScale, new Vector4(1f / Mathf.Max(tint.r, 1e-3f), 1f / Mathf.Max(tint.g, 1e-3f), 1f / Mathf.Max(tint.b, 1e-3f), 1f));
            RenderTextureDescriptor desc = upscaled.descriptor;
            desc.enableRandomWrite = false;
            desc.msaaSamples = 1;
            desc.depthBufferBits = 0;
            // Each pixel is scaled where it stands: an exact copy, then one triangle over the whole target that reads its own pixel back by index
            // (ReDefinitionAtmospheresWhiteBalance.shader), so no matrix and no filtering takes part. With the two Blits it replaces (the second through
            // vert_img's view and projection) the finished picture came out moved by this frame's upscaler jitter and resampled every frame: a shake of about
            // 1.5 px and a fifth of its sharpness lost (measured, K-T2 perfNpic2560dlssqdraws, 2026-11-21).
            if (ProbeVp) RecordVpProbe(cb, camera);
            cb.GetTemporaryRT(idCopy, desc);
            if (ProbeOldBlit)
            {
                // Test switch only: the two Blits of be261627 (the second through vert_img, pass 1), optionally after a plain view-projection recorded
                // first: identity view and the orthographic 0..1 projection Graphics.Blit loads for its quad (GL.LoadOrtho), without any jitter.
                if (ProbeIdentityVp) cb.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(0f, 1f, 0f, 1f, -1f, 100f));
                cb.Blit(upscaled, idCopy);
                cb.Blit(idCopy, upscaled, material, 1);
            }
            else
            {
                cb.CopyTexture(upscaled, idCopy);
                cb.SetGlobalTexture(idSource, idCopy);
                cb.SetRenderTarget(upscaled);
                cb.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3, 1);
            }
            cb.ReleaseTemporaryRT(idCopy);
            if (ApiBridge.ProbeLogFrames)
                AtmospheresLog.Say("white balance frame " + Time.frameCount + ": scale " + (1f / Mathf.Max(tint.r, 1e-3f)).ToString("R", Inv) + " "
                                   + (1f / Mathf.Max(tint.g, 1e-3f)).ToString("R", Inv) + " " + (1f / Mathf.Max(tint.b, 1e-3f)).ToString("R", Inv)
                                   + (ProbeOldBlit ? " old blit" : " in place") + (ProbeOldBlit && ProbeIdentityVp ? " after identity VP" : ""));
        }

        // Probe switches of the white balance test (2026-11-21, T09 -t09PinLimits p:wboldblit / p:wbident / p:wbvp): the old two-Blit path, an identity view and
        // projection recorded before it, and the view-projection probe. All false outside the probe.
        internal static bool ProbeOldBlit, ProbeIdentityVp, ProbeVp;
        private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
        private RenderTexture vpProbe;

        // The view-projection the GPU has current at this point of the host's buffer: pass 2 draws one triangle (placed from the vertex index, no matrix)
        // over a 4 x 1 float target whose pixel x holds row x of UNITY_MATRIX_VP; read back and logged beside the camera's own matrices (C#), so the
        // source can be named by its values. Logged only while ApiBridge.ProbeLogFrames.
        private void RecordVpProbe(CommandBuffer cb, Camera camera)
        {
            if (!ApiBridge.ProbeLogFrames || !SystemInfo.supportsAsyncGPUReadback) return;
            if (vpProbe == null)
            {
                vpProbe = new RenderTexture(4, 1, 0, RenderTextureFormat.ARGBFloat) { name = "ReDefinition_WbVpProbe", filterMode = FilterMode.Point };
                vpProbe.Create();
            }
            cb.SetRenderTarget(vpProbe);
            cb.DrawProcedural(Matrix4x4.identity, material, 2, MeshTopology.Triangles, 3, 1);
            int frame = Time.frameCount;
            string candidates = "";
            if (camera != null)
            {
                Matrix4x4 view = camera.worldToCameraMatrix;
                Matrix4x4 unjittered = GL.GetGPUProjectionMatrix(camera.nonJitteredProjectionMatrix, true) * view;
                Matrix4x4 current = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true) * view;
                candidates = " | camera " + camera.name + " unjitteredVP " + Rows(unjittered) + " | camera projection now VP " + Rows(current)
                             + " | camera projection [0,2] [1,2] now " + camera.projectionMatrix[0, 2].ToString("R", Inv) + " "
                             + camera.projectionMatrix[1, 2].ToString("R", Inv) + " | Camera.current " + (Camera.current != null ? Camera.current.name : "null")
                             + " | host jitter " + OwnCloudModule.HostJitterNow().ToString("R");
            }
            cb.RequestAsyncReadback(vpProbe, request =>
            {
                if (request.hasError)
                {
                    AtmospheresLog.Say("white balance VP probe frame " + frame + ": read-back failed");
                    return;
                }
                Unity.Collections.NativeArray<Vector4> rows = request.GetData<Vector4>();
                var text = new System.Text.StringBuilder();
                for (int r = 0; r < 2 && r < rows.Length; r++)
                    text.Append(r == 0 ? "" : " / ").Append(rows[r].x.ToString("R", Inv)).Append(' ').Append(rows[r].y.ToString("R", Inv)).Append(' ')
                        .Append(rows[r].z.ToString("R", Inv)).Append(' ').Append(rows[r].w.ToString("R", Inv));
                AtmospheresLog.Say("white balance VP probe frame " + frame + ": GPU VP rows 0/1 " + text + candidates);
            });
        }

        // Rows 0 and 1 of a matrix, for the probe's log line.
        private static string Rows(Matrix4x4 m)
        {
            return m[0, 0].ToString("R", Inv) + " " + m[0, 1].ToString("R", Inv) + " " + m[0, 2].ToString("R", Inv) + " " + m[0, 3].ToString("R", Inv) + " / "
                   + m[1, 0].ToString("R", Inv) + " " + m[1, 1].ToString("R", Inv) + " " + m[1, 2].ToString("R", Inv) + " " + m[1, 3].ToString("R", Inv);
        }

        private static bool IsHdr(RenderTextureFormat f)
        {
            return f == RenderTextureFormat.DefaultHDR || f == RenderTextureFormat.ARGBHalf || f == RenderTextureFormat.ARGBFloat
                   || f == RenderTextureFormat.RGB111110Float || f == RenderTextureFormat.RGBAUShort;
        }

        internal void Dispose()
        {
            if (registered) Services.PostUpscale.Unregister(draw);
            registered = false;
            if (material != null) Object.Destroy(material);
            material = null;
        }
    }
}
