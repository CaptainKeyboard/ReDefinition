using System.Text;
using ReDefinition.Proxy;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // The rig's part for "Write diagnostics to log": what the cameras, the
    // inputs, the masks and the proxy's textures hold, read back where numbers
    // are needed. The text and statistics that need no rig state are in RigReport.
    public partial class UpscalerRig
    {
        // The CPU textures the readbacks land in.
        private readonly RigReadback readback = new RigReadback();

        // Whether the proxy can address the rig's textures, and whether the
        // address holds: a pointer that changed since setup would have to be asked
        // for again every frame; zero means the texture has no D3D resource yet.
        private string DescribeNativeHandles()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("captured on frame ").Append(textures.HandleFrame)
              .Append(", now frame ").Append(Time.frameCount);

            RigReport.AppendHandle(sb, "upscaled", textures.Upscaled, textures.UpscaledHandle);
            if (nativeSharpener != null)
                RigReport.AppendHandle(sb, UpscalerBackends.Name(Backend) + " output (RCAS input)", nativeSharpener.Input,
                    nativeSharpenInputHandle);
            RigReport.AppendHandle(sb, "depthCopy", textures.DepthCopy, textures.DepthHandle);
            RigReport.AppendHandle(sb, "motionVectors", textures.MotionVectors, textures.MotionHandle);

            return sb.ToString();
        }

        // What a black or wrong image does not show: whether the cameras render
        // into the rig's texture, whether the redirect holds, and which other
        // CommandBuffers sit on the cameras.
        public void LogDiagnostics()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Log.Tag).Append(" Diagnostics").AppendLine();
            sb.Append("  screen ").Append(Screen.width).Append("x").Append(Screen.height)
              .Append(", render ").Append(renderSize.x).Append("x").Append(renderSize.y)
              .Append(", bypass ").Append(Bypass).AppendLine();

            sb.Append("  lowRes: ");
            if (textures.LowRes == null) sb.Append("not present");
            else sb.Append(textures.LowRes.width).Append("x").Append(textures.LowRes.height)
                   .Append(" created=").Append(textures.LowRes.IsCreated())
                   .Append(" format=").Append(textures.LowRes.format)
                   .Append(" centre pixel=").Append(readback.SamplePixel(textures.LowRes));
            sb.AppendLine();

            sb.Append("  upscaled: ");
            if (textures.Upscaled == null) sb.Append("not present");
            else sb.Append(textures.Upscaled.width).Append("x").Append(textures.Upscaled.height)
                   .Append(" created=").Append(textures.Upscaled.IsCreated())
                   .Append(" centre pixel=").Append(readback.SamplePixel(textures.Upscaled));
            sb.AppendLine();

            sb.Append("  sharpness ").Append(Sharpness.ToString("0.00"))
              .Append(Sharpening ? " -> RCAS step " + Mathf.RoundToInt(Mathf.Clamp01(Sharpness) * 20) + " of 20" : " -> RCAS off")
              .Append(", FSR auto exposure ").Append(AutoExposure ? "on" : "off")
              .Append(", mipmap bias ")
              .Append(biasApplied ? mipmapBias.TextureCount + " textures" : "off").AppendLine();

            sb.Append("  motion vectors: ");
            if (textures.MotionVectors == null) sb.Append("not present");
            else sb.Append(textures.MotionVectors.width).Append("x").Append(textures.MotionVectors.height)
                   .Append("  ").Append(RigReport.DescribeMotion(readback.ReadBack(textures.MotionVectors), renderSize));
            sb.AppendLine();
            sb.Append("  EVE's cloud motion vectors: ")
              .Append(inputs.CloudMotion.State == null ? "blended in" : "left out -- " + inputs.CloudMotion.State).AppendLine();

            sb.Append("  upscaler: ").Append(PassThrough ? "off, frame generation's inputs only" : UpscalerBackends.Name(Backend));
            if (Backend == UpscalerBackend.Dlss) sb.Append(", preset ").Append(DlssPreset);
            if (nativeLink != null) sb.Append(" -- ").Append(nativeLink.Describe());
            sb.AppendLine();
            sb.Append("  skinned renderers: ").Append(skinned.Describe(VisibleLayerMask())).AppendLine();
            sb.Append("  frame generation: ").Append(FrameGenerationBridge.Describe()).AppendLine();
            sb.Append("  HUD-less snapshot after: ").Append(hudLess.Describe()).AppendLine();
            sb.Append("  TUFX effects after FSR: ").Append(tufx.Describe()).AppendLine();
            sb.Append("  masks: ").Append(DescribeInputMasks()).AppendLine();

            sb.Append("  native texture handles: ").Append(DescribeNativeHandles()).AppendLine();

            sb.Append("  depth: ");
            if (textures.DepthCopy == null) sb.Append("not present");
            else sb.Append(textures.DepthCopy.width).Append("x").Append(textures.DepthCopy.height)
                   .Append("  ").Append(RigReport.DescribeDepth(cam != null ? cam.nearClipPlane : 0.2f,
                                                                readback.ReadLuminance(textures.DepthCopy)));
            sb.AppendLine();

            // Every switch position, so a diagnostics block can be attributed.
            sb.Append("  settings: ").Append(QualityMode)
              .Append(", sharpness ").Append(Sharpness.ToString("0.00"))
              .Append(", bypass ").Append(Bypass)
              .Append(", jitter ").Append(EnableJitter)
              .Append(", HDR effective ").Append(EffectiveHdr)
              .Append(", mipmap bias ").Append(EnableMipmapBias)
              .Append(", ").Append(quality.Describe())
              .Append(", depth source ").Append(Depth)
              .AppendLine();

            sb.Append("  colour space ").Append(QualitySettings.activeColorSpace)
              .Append(", buffer ").Append(textures.LowRes != null ? textures.LowRes.format.ToString() : "-")
              .Append(" sRGB=").Append(textures.LowRes != null && textures.LowRes.sRGB)
              .Append(", camera allowHDR=").Append(cam != null && cam.allowHDR)
              .Append(", path ").Append(cam != null ? cam.actualRenderingPath.ToString() : "-")
              .AppendLine();

            sb.Append("  shadow distance ").Append(QualitySettings.shadowDistance.ToString("0"))
              .Append(" m, cascades ").Append(QualitySettings.shadowCascades).AppendLine();

            sb.Append("  projection per redirected camera (n/f from the matrix against n/f of the camera):")
              .AppendLine();
            foreach (CameraRedirect redirect in cameras.Redirects)
            {
                if (redirect == null || redirect.Camera == null) continue;
                Camera other = redirect.Camera;

                sb.Append("    ").Append(other.name.PadRight(20))
                  .Append(" camera n=").Append(other.nearClipPlane.ToString("0.###"))
                  .Append(" f=").Append(other.farClipPlane.ToString("0"))
                  .Append(" fov=").Append(other.fieldOfView.ToString("0.#"));

                if (redirect.DidJitter)
                {
                    sb.Append(" | found ").Append(RigReport.ClipRange(redirect.CapturedProjection))
                      .Append(" | set ").Append(RigReport.ClipRange(redirect.AppliedProjection));
                    sb.AppendLine();
                    sb.Append("      ").Append(RigReport.DescribeShear(redirect));
                }
                else
                {
                    sb.Append(" | no jitter, matrix untouched: ")
                      .Append(RigReport.ClipRange(other.projectionMatrix));
                }
                sb.AppendLine();
            }

            sb.Append("  cameras in the scene:").AppendLine();
            foreach (Camera other in Camera.allCameras)
            {
                bool redirected = false;
                foreach (CameraRedirect redirect in cameras.Redirects)
                    if (redirect != null && redirect.Camera == other) { redirected = true; break; }

                sb.Append("    ").Append(other.name.PadRight(20))
                  .Append(" depth ").Append(other.depth.ToString("0.##").PadLeft(6))
                  .Append(" target ").Append(other.targetTexture == null ? "frame buffer" : other.targetTexture.name)
                  .Append(redirected ? "  [redirected]" : "")
                  .Append(" rect ").Append(other.rect)
                  .Append(" clear ").Append(other.clearFlags);

                // CommandBuffers of other mods -- EVE, Scatterer and TUFX hook in
                // this way -- name the mod involved.
                string hooks = RigReport.DescribeHooks(other);
                if (hooks.Length > 0) sb.Append(" | hooks: ").Append(hooks);
                sb.AppendLine();
            }

            Debug.Log(sb.ToString());
        }

        // The masks the rig sends: the transparency mask's coverage, the reactive source and its coverage, and for
        // the renderers' mask how many draws it made of how many transparent renderers, with a problem it reported.
        private string DescribeInputMasks()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("transparency ").Append(TransparencyMask ? Coverage(inputs.Masks.TransparencyTexture) : "off");
            sb.Append(", reactive ").Append(ReactiveMask);
            if (ReactiveMask != UpscalerMasks.ReactiveSource.Off) sb.Append(' ').Append(Coverage(ReactiveTexture()));
            if (ReactiveMask == UpscalerMasks.ReactiveSource.Renderers)
                sb.Append(", ").Append(inputs.Masks.Drawn).Append(" draw(s) from ").Append(inputs.Masks.Candidates).Append(" transparent renderer(s)");
            if (inputs.Masks.Problem != null) sb.Append(" -- ").Append(inputs.Masks.Problem);
            return sb.ToString();
        }

        // How much of a mask marks anything, read back; "not present" without the texture.
        private string Coverage(RenderTexture texture)
        {
            return texture == null ? "not present" : RigReport.Coverage(readback.ReadBack(texture));
        }
    }
}
