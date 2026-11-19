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
        private int idScale, idCopy;

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
            cb.GetTemporaryRT(idCopy, desc);
            cb.Blit(upscaled, idCopy);
            cb.Blit(idCopy, upscaled, material, 0);
            cb.ReleaseTemporaryRT(idCopy);
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
