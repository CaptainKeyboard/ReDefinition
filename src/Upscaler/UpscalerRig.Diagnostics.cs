using System.Text;
using System;
using ReDefinition.Bridges;
using ReDefinition.Core;
using UnityEngine.Rendering;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // The rig's part for "Write diagnostics to log": what the cameras, the
    // inputs, the masks and the proxy's textures hold, read back where numbers
    // are needed.
    public partial class UpscalerRig
    {
        private Texture2D probePixel;
        private Texture2D readback;

        // Whether the proxy can address the rig's textures, and whether the
        // address holds: a pointer that changed since setup would have to be asked
        // for again every frame; zero means the texture has no D3D resource yet.
        private string DescribeNativeHandles()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("captured on frame ").Append(handleFrame)
              .Append(", now frame ").Append(Time.frameCount);

            AppendHandle(sb, "upscaled", upscaled, upscaledHandle);
            if (dlssSharpener != null)
                AppendHandle(sb, "DLSS output (RCAS input)", dlssSharpener.Input, dlssSharpenInputHandle);
            AppendHandle(sb, "depthCopy", depthCopy, depthHandle);
            AppendHandle(sb, "motionVectors", motionVectors, motionHandle);

            return sb.ToString();
        }

        private static void AppendHandle(StringBuilder sb, string label, RenderTexture texture, IntPtr atSetup)
        {
            sb.AppendLine().Append("      ").Append(label.PadRight(28));

            if (texture == null)
            {
                sb.Append("texture is null");
                return;
            }

            IntPtr now = texture.GetNativeTexturePtr();

            sb.Append(texture.width).Append('x').Append(texture.height)
              .Append(' ').Append(texture.format)
              .Append("  ptr 0x").Append(now.ToInt64().ToString("X"));

            if (now == IntPtr.Zero)
                sb.Append("  -- NULL, no D3D resource behind it");
            else if (now == atSetup)
                sb.Append("  -- unchanged since setup");
            else
                sb.Append("  -- CHANGED, was 0x").Append(atSetup.ToInt64().ToString("X"));
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
            if (lowRes == null) sb.Append("not present");
            else sb.Append(lowRes.width).Append("x").Append(lowRes.height)
                   .Append(" created=").Append(lowRes.IsCreated())
                   .Append(" format=").Append(lowRes.format)
                   .Append(" centre pixel=").Append(SamplePixel(lowRes));
            sb.AppendLine();

            sb.Append("  upscaled: ");
            if (upscaled == null) sb.Append("not present");
            else sb.Append(upscaled.width).Append("x").Append(upscaled.height)
                   .Append(" created=").Append(upscaled.IsCreated())
                   .Append(" centre pixel=").Append(SamplePixel(upscaled));
            sb.AppendLine();

            sb.Append("  sharpness ").Append(Sharpness.ToString("0.00"))
              .Append(Sharpening ? " -> RCAS step " + Mathf.RoundToInt(Mathf.Clamp01(Sharpness) * 20) + " of 20" : " -> RCAS off")
              .Append(", FSR auto exposure ").Append(AutoExposure ? "on" : "off")
              .Append(", mipmap bias ")
              .Append(biasApplied ? mipmapBias.TextureCount + " textures" : "off").AppendLine();

            sb.Append("  motion vectors: ");
            if (motionVectors == null) sb.Append("not present");
            else sb.Append(motionVectors.width).Append("x").Append(motionVectors.height)
                   .Append("  ").Append(DescribeMotion(motionVectors));
            sb.AppendLine();
            sb.Append("  EVE's cloud motion vectors: ")
              .Append(cloudMotion.State == null ? "blended in" : "left out -- " + cloudMotion.State).AppendLine();

            sb.Append("  upscaler: ").Append(PassThrough ? "off, frame generation's inputs only" : UpscalerBackends.Name(Backend));
            if (Backend == UpscalerBackend.Dlss) sb.Append(", preset ").Append(DlssPreset);
            if (nativeLink != null) sb.Append(" -- ").Append(nativeLink.Describe());
            sb.AppendLine();
            sb.Append("  skinned renderers: ").Append(skinned.Describe(VisibleLayerMask())).AppendLine();
            sb.Append("  frame generation: ").Append(FrameGenerationBridge.Describe()).AppendLine();
            sb.Append("  HUD-less snapshot after: ").Append(DescribeHudLessCarriers()).AppendLine();
            sb.Append("  TUFX effects after FSR: ").Append(tufx.Describe()).AppendLine();
            sb.Append("  masks: ").Append(DescribeInputMasks()).AppendLine();

            sb.Append("  native texture handles: ").Append(DescribeNativeHandles()).AppendLine();

            sb.Append("  depth: ");
            if (depthCopy == null) sb.Append("not present");
            else sb.Append(depthCopy.width).Append("x").Append(depthCopy.height)
                   .Append("  ").Append(DescribeDepth(depthCopy));
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
              .Append(", buffer ").Append(lowRes != null ? lowRes.format.ToString() : "-")
              .Append(" sRGB=").Append(lowRes != null && lowRes.sRGB)
              .Append(", camera allowHDR=").Append(cam != null && cam.allowHDR)
              .Append(", path ").Append(cam != null ? cam.actualRenderingPath.ToString() : "-")
              .AppendLine();

            sb.Append("  shadow distance ").Append(QualitySettings.shadowDistance.ToString("0"))
              .Append(" m, cascades ").Append(QualitySettings.shadowCascades).AppendLine();

            sb.Append("  projection per redirected camera (n/f from the matrix against n/f of the camera):")
              .AppendLine();
            foreach (CameraRedirect redirect in redirects)
            {
                if (redirect == null || redirect.Camera == null) continue;
                Camera other = redirect.Camera;

                sb.Append("    ").Append(other.name.PadRight(20))
                  .Append(" camera n=").Append(other.nearClipPlane.ToString("0.###"))
                  .Append(" f=").Append(other.farClipPlane.ToString("0"))
                  .Append(" fov=").Append(other.fieldOfView.ToString("0.#"));

                if (redirect.DidJitter)
                {
                    sb.Append(" | found ").Append(ClipRange(redirect.CapturedProjection))
                      .Append(" | set ").Append(ClipRange(redirect.AppliedProjection));
                    sb.AppendLine();
                    sb.Append("      ").Append(DescribeShear(redirect));
                }
                else
                {
                    sb.Append(" | no jitter, matrix untouched: ")
                      .Append(ClipRange(other.projectionMatrix));
                }
                sb.AppendLine();
            }

            sb.Append("  cameras in the scene:").AppendLine();
            foreach (Camera other in Camera.allCameras)
            {
                bool redirected = false;
                foreach (CameraRedirect redirect in redirects)
                    if (redirect != null && redirect.Camera == other) { redirected = true; break; }

                sb.Append("    ").Append(other.name.PadRight(20))
                  .Append(" depth ").Append(other.depth.ToString("0.##").PadLeft(6))
                  .Append(" target ").Append(other.targetTexture == null ? "frame buffer" : other.targetTexture.name)
                  .Append(redirected ? "  [redirected]" : "")
                  .Append(" rect ").Append(other.rect)
                  .Append(" clear ").Append(other.clearFlags);

                // CommandBuffers of other mods -- EVE, Scatterer and TUFX hook in
                // this way -- name the mod involved.
                string hooks = DescribeHooks(other);
                if (hooks.Length > 0) sb.Append(" | hooks: ").Append(hooks);
                sb.AppendLine();
            }

            Debug.Log(sb.ToString());
        }

        // Whether the jitter reached the render.
        //
        // The shear sits in m02/m12 of the projection matrix. Other code writes
        // camera.projectionMatrix too -- Scatterer's TemporalAntiAliasing in its own
        // OnPreCull -- and where it overwrites the jitter, the upscaler gets an
        // image without subpixel offset while computing against a jitter that is
        // not in it.
        private static string DescribeShear(CameraRedirect redirect)
        {
            float wantedX = redirect.AppliedProjection.m02 - redirect.CapturedProjection.m02;
            float wantedY = redirect.AppliedProjection.m12 - redirect.CapturedProjection.m12;

            if (!redirect.CapturedRendered)
                return "shear requested " + Shear(wantedX, wantedY) + ", rendered value not captured";

            float actualX = redirect.RenderedProjection.m02 - redirect.CapturedProjection.m02;
            float actualY = redirect.RenderedProjection.m12 - redirect.CapturedProjection.m12;

            // Relative comparison: the absolute values are around 1e-4, an
            // absolute threshold would say nothing.
            float wanted = Mathf.Sqrt(wantedX * wantedX + wantedY * wantedY);
            float actual = Mathf.Sqrt(actualX * actualX + actualY * actualY);
            string verdict;
            if (wanted <= 1e-12f) verdict = "no shear requested";
            else if (actual > 1.5f * wanted) verdict = "AMPLIFIED -- another jitter on top of the rig's";
            else if (actual >= 0.5f * wanted) verdict = "survived";
            else
            {
                // The shear is gone. An element-wise comparison tells the two
                // causes apart: a rendered matrix bit for bit the one found means
                // KSP's matrix was written back over the jittered one and the jitter
                // was lost; a different one was recomputed -- Scatterer's
                // OnPostRender calls ResetProjectionMatrix -- after the jitter was
                // in the image, and only the reading point lies too late.
                verdict = MaxElementDelta(redirect.RenderedProjection, redirect.CapturedProjection) == 0f
                    ? "LOST -- KSP's matrix was written back over the jittered one"
                    : "gone at read time, but matrix was recomputed (Scatterer reset) -- jitter probably did render";
            }

            // The clip range of the rendered matrix shows the same: the range
            // "found" for KSP's matrix written back, Unity's own for a matrix
            // recomputed by ResetProjectionMatrix. KSP's own matrix encodes a far
            // plane of roughly 880804 on Camera 00, Unity's recomputed one
            // farClipPlane, 750000.
            return "shear requested " + Shear(wantedX, wantedY)
                   + ", rendered " + Shear(actualX, actualY) + "  -> " + verdict
                   + "  [rendered range " + ClipRange(redirect.RenderedProjection)
                   + ", apply/release per frame " + redirect.ApplyCount + "/" + redirect.ReleaseCount
                   + ", redirects on this camera "
                   + redirect.GetComponents<CameraRedirect>().Length + "]";
        }

        private static float MaxElementDelta(Matrix4x4 a, Matrix4x4 b)
        {
            float worst = 0f;
            for (int i = 0; i < 16; i++)
            {
                float delta = Mathf.Abs(a[i] - b[i]);
                if (delta > worst) worst = delta;
            }
            return worst;
        }

        private static string Shear(float x, float y)
        {
            return "(" + x.ToString("0.000000") + ", " + y.ToString("0.000000") + ")";
        }

        private static string DescribeHooks(Camera camera)
        {
            CameraEvent[] events =
            {
                CameraEvent.BeforeForwardOpaque, CameraEvent.AfterForwardOpaque,
                CameraEvent.BeforeImageEffects, CameraEvent.AfterImageEffects,
                CameraEvent.BeforeForwardAlpha, CameraEvent.AfterForwardAlpha,
                CameraEvent.AfterEverything, CameraEvent.BeforeGBuffer,
                CameraEvent.AfterGBuffer, CameraEvent.AfterLighting,
            };

            StringBuilder sb = new StringBuilder();
            foreach (CameraEvent evt in events)
            {
                CommandBuffer[] buffers = camera.GetCommandBuffers(evt);
                if (buffers == null || buffers.Length == 0) continue;
                foreach (CommandBuffer buffer in buffers)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(evt).Append(':').Append(buffer.name);
                }
            }
            return sb.ToString();
        }

        // Depth across the whole buffer: in KSP a centre pixel mostly hits the sky
        // and reports the far plane.
        //
        // The values are device depth with a reversed Z axis: 1 is near, 0 is
        // far. Everything at 0 means no depth arrives, and without depth the
        // upscaler cannot detect disocclusion.
        private string DescribeDepth(RenderTexture source)
        {
            float[] values = ReadLuminance(source);
            System.Array.Sort(values);

            // With KSP's small near plane all values sit just above zero. In
            // metres they compare with what is on screen: distance ~ near plane /
            // depth, while the far plane is far away.
            float near = cam != null ? cam.nearClipPlane : 0.2f;
            float nearest = values[values.Length - 1];
            float median = values[values.Length / 2];

            return "nearest point " + Distance(near, nearest)
                   + "  median " + Distance(near, median)
                   + "  (raw max " + nearest.ToString("0.00000") + ")";
        }

        // Near and far plane from a projection matrix. Where they differ from the
        // camera's nearClipPlane/farClipPlane, KSP set a projection of its own.
        private static string ClipRange(Matrix4x4 projection)
        {
            float m22 = projection.m22;
            float m23 = projection.m23;
            float denominatorNear = m22 - 1f;
            float denominatorFar = m22 + 1f;

            if (Mathf.Abs(denominatorNear) < 1e-9f || Mathf.Abs(denominatorFar) < 1e-9f)
                return "n/f not determinable";

            // With KSP's wide-range cameras the denominator of the far recovery
            // cancels almost completely; the value is then flagged unreliable.
            float far = m23 / denominatorFar;
            bool farIsNoise = Mathf.Abs(denominatorFar) < 1e-5f;

            return "n=" + (m23 / denominatorNear).ToString("0.###")
                   + " f=" + (farIsNoise ? "~" + far.ToString("0") + " (unreliable)" : far.ToString("0"));
        }

        private static string Distance(float near, float depth)
        {
            if (depth <= 1e-7f) return "infinite";
            float metres = near / depth;
            return metres >= 1000f
                ? (metres / 1000f).ToString("0.0") + " km"
                : metres.ToString("0.0") + " m";
        }

        private string DescribeInputMasks()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("transparency ").Append(TransparencyMask ? Coverage(masks.TransparencyTexture) : "off");
            sb.Append(", reactive ").Append(ReactiveMask);
            if (ReactiveMask != UpscalerMasks.ReactiveSource.Off) sb.Append(' ').Append(Coverage(ReactiveTexture()));
            if (ReactiveMask == UpscalerMasks.ReactiveSource.Renderers)
                sb.Append(", ").Append(masks.Drawn).Append(" draw(s) from ").Append(masks.Candidates).Append(" transparent renderer(s)");
            if (masks.Problem != null) sb.Append(" -- ").Append(masks.Problem);
            return sb.ToString();
        }

        private string Coverage(RenderTexture texture)
        {
            if (texture == null) return "not present";
            Color[] pixels = ReadBack(texture);
            int marked = 0;
            float sum = 0f;
            foreach (Color pixel in pixels)
            {
                if (pixel.r > 0.01f) marked++;
                sum += pixel.r;
            }
            return "marks " + (100f * marked / pixels.Length).ToString("0.00") + "% (mean "
                   + (sum / pixels.Length).ToString("0.000") + ")";
        }

        // Read back downscaled: 320x180 is enough for a statistic.
        private Color[] ReadBack(RenderTexture source)
        {
            const int width = PreviewWidth;
            const int height = PreviewHeight;

            RenderTexture small = RenderTexture.GetTemporary(
                width, height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            small.filterMode = FilterMode.Point;
            Graphics.Blit(source, small);

            if (readback == null)
                readback = new Texture2D(width, height, TextureFormat.RGBAHalf, false, true);

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = small;
            readback.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
            readback.Apply(false);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(small);

            return readback.GetPixels();
        }

        private float[] ReadLuminance(RenderTexture source)
        {
            Color[] pixels = ReadBack(source);
            float[] values = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++) values[i] = pixels[i].r;
            return values;
        }

        // Statistics across the whole buffer: motion at the edge of the image --
        // the vehicles around the space centre, a rotating part -- leaves the
        // centre pixel at zero.
        private string DescribeMotion(RenderTexture source)
        {
            const float movingThresholdPx = 0.25f;

            Color[] pixels = ReadBack(source);
            float[] magnitudes = new float[pixels.Length];
            int moving = 0;

            // The vectors are in UV units; the render resolution turns them into
            // pixels.
            for (int i = 0; i < pixels.Length; i++)
            {
                float dx = pixels[i].r * renderSize.x;
                float dy = pixels[i].g * renderSize.y;
                float magnitude = Mathf.Sqrt(dx * dx + dy * dy);
                magnitudes[i] = magnitude;
                if (magnitude > movingThresholdPx) moving++;
            }

            System.Array.Sort(magnitudes);

            return "moving pixels " + (100f * moving / magnitudes.Length).ToString("0.00") + "%"
                   + "  p50 " + magnitudes[magnitudes.Length / 2].ToString("0.000")
                   + "  p95 " + magnitudes[(int)(magnitudes.Length * 0.95f)].ToString("0.000")
                   + "  max " + magnitudes[magnitudes.Length - 1].ToString("0.000") + " px";
        }

        // A single pixel tells an image from a black one.
        private string SamplePixel(RenderTexture texture)
        {
            if (probePixel == null) probePixel = new Texture2D(1, 1, TextureFormat.RGBAFloat, false);

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = texture;
            probePixel.ReadPixels(new Rect(texture.width / 2f, texture.height / 2f, 1, 1), 0, 0, false);
            probePixel.Apply(false);
            RenderTexture.active = previous;

            Color c = probePixel.GetPixel(0, 0);
            return "(" + c.r.ToString("0.000") + ", " + c.g.ToString("0.000")
                   + ", " + c.b.ToString("0.000") + ")";
        }
    }
}
