using System.Text;
using System;
using UnityEngine.Rendering;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // The text and the statistics of "Write diagnostics to log" that need no state of the rig. They describe a
    // texture's native handle, a camera's projection, its jitter and its command buffers. They also say what the
    // pixels read back from the depth, motion and mask images show.
    internal static class RigReport
    {
        // One texture's line: size, format and native pointer, against the pointer taken at setup.
        internal static void AppendHandle(StringBuilder sb, string label, RenderTexture texture, IntPtr atSetup)
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

        // Whether the jitter reached the render.
        //
        // The shear sits in m02/m12 of the projection matrix. Other code writes
        // camera.projectionMatrix too -- Scatterer's TemporalAntiAliasing in its own
        // OnPreCull -- and where it overwrites the jitter, the upscaler gets an
        // image without subpixel offset while computing against a jitter that is
        // not in it.
        internal static string DescribeShear(CameraRedirect redirect)
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

        // The largest difference between two matrices, element by element; 0 where they are the same bit for bit.
        internal static float MaxElementDelta(Matrix4x4 a, Matrix4x4 b)
        {
            float worst = 0f;
            for (int i = 0; i < 16; i++)
            {
                float delta = Mathf.Abs(a[i] - b[i]);
                if (delta > worst) worst = delta;
            }
            return worst;
        }

        // A projection's jitter terms (m02, m12) as a pair with six decimals.
        internal static string Shear(float x, float y)
        {
            return "(" + x.ToString("0.000000") + ", " + y.ToString("0.000000") + ")";
        }

        // The command buffers on the camera's common events, as event:name, for the camera list in the log.
        internal static string DescribeHooks(Camera camera)
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
        // upscaler cannot detect disocclusion. Sorts the values.
        internal static string DescribeDepth(float near, float[] values)
        {
            System.Array.Sort(values);

            // With KSP's small near plane all values sit just above zero. In
            // metres they compare with what is on screen: distance ~ near plane /
            // depth, while the far plane is far away.
            float nearest = values[values.Length - 1];
            float median = values[values.Length / 2];

            return "nearest point " + Distance(near, nearest)
                   + "  median " + Distance(near, median)
                   + "  (raw max " + nearest.ToString("0.00000") + ")";
        }

        // Near and far plane from a projection matrix. Where they differ from the
        // camera's nearClipPlane/farClipPlane, KSP set a projection of its own.
        internal static string ClipRange(Matrix4x4 projection)
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

        // A reversed device depth as a distance, near plane / depth, in m or km; infinite at the far plane.
        internal static string Distance(float near, float depth)
        {
            if (depth <= 1e-7f) return "infinite";
            float metres = near / depth;
            return metres >= 1000f
                ? (metres / 1000f).ToString("0.0") + " km"
                : metres.ToString("0.0") + " m";
        }

        // How much of a mask marks anything (red above 0.01) and its mean, from the downscaled readback.
        internal static string Coverage(Color[] pixels)
        {
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

        // Statistics across the whole buffer: motion at the edge of the image --
        // the vehicles around the space centre, a rotating part -- leaves the
        // centre pixel at zero.
        internal static string DescribeMotion(Color[] pixels, Vector2Int renderSize)
        {
            const float movingThresholdPx = 0.25f;

            float[] magnitudes = new float[pixels.Length];
            int moving = 0;

            for (int i = 0; i < pixels.Length; i++)
            {
                float magnitude = RigPreview.MotionMagnitude(pixels[i], renderSize);
                magnitudes[i] = magnitude;
                if (magnitude > movingThresholdPx) moving++;
            }

            System.Array.Sort(magnitudes);

            return "moving pixels " + (100f * moving / magnitudes.Length).ToString("0.00") + "%"
                   + "  p50 " + magnitudes[magnitudes.Length / 2].ToString("0.000")
                   + "  p95 " + magnitudes[(int)(magnitudes.Length * 0.95f)].ToString("0.000")
                   + "  max " + magnitudes[magnitudes.Length - 1].ToString("0.000") + " px";
        }
    }
}
