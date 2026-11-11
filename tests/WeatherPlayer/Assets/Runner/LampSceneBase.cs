using System;
using System.Collections;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // A frame's colour channels, read back from a render texture (half float, row 0 at the bottom as Unity reads it).
    public sealed class RgbFrame
    {
        public readonly int Width, Height;
        public readonly float[] R, G, B;

        private RgbFrame(int width, int height)
        {
            Width = width;
            Height = height;
            R = new float[width * height];
            G = new float[width * height];
            B = new float[width * height];
        }

        public static RgbFrame Read(RenderTexture texture)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = texture;
            var copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBAHalf, false, true);
            copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            copy.Apply(false);
            RenderTexture.active = previous;
            Color[] pixels = copy.GetPixels();
            UnityEngine.Object.Destroy(copy);
            var frame = new RgbFrame(texture.width, texture.height);
            for (int i = 0; i < pixels.Length; i++)
            {
                frame.R[i] = pixels[i].r;
                frame.G[i] = pixels[i].g;
                frame.B[i] = pixels[i].b;
            }
            return frame;
        }

        // The frame a minus the frame b, channel by channel: the light a pass added.
        public static RgbFrame Difference(RgbFrame a, RgbFrame b)
        {
            var d = new RgbFrame(a.Width, a.Height);
            for (int i = 0; i < d.R.Length; i++)
            {
                d.R[i] = a.R[i] - b.R[i];
                d.G[i] = a.G[i] - b.G[i];
                d.B[i] = a.B[i] - b.B[i];
            }
            return d;
        }

        // The correlation coefficient of the luma of two frames over a rectangle.
        public static double Correlation(RgbFrame a, RgbFrame b, ImageStats.Rect2 r)
        {
            double ma = a.Mean(r, 3), mb = b.Mean(r, 3), sab = 0, saa = 0, sbb = 0;
            for (int y = r.Y0; y < r.Y1; y++)
            {
                for (int x = r.X0; x < r.X1; x++)
                {
                    int i = y * a.Width + x;
                    double da = a.Value(i, 3) - ma, db = b.Value(i, 3) - mb;
                    sab += da * db;
                    saa += da * da;
                    sbb += db * db;
                }
            }
            return saa <= 0 || sbb <= 0 ? 0 : sab / Math.Sqrt(saa * sbb);
        }

        // Mean of one channel (0 = r, 1 = g, 2 = b, 3 = Rec. 709 luma) over a rectangle.
        public double Mean(ImageStats.Rect2 r, int channel)
        {
            double sum = 0;
            for (int y = r.Y0; y < r.Y1; y++)
                for (int x = r.X0; x < r.X1; x++) sum += Value(y * Width + x, channel);
            return r.Area == 0 ? 0 : sum / r.Area;
        }

        // Standard deviation of the luma over a rectangle.
        public double LumaStdDev(ImageStats.Rect2 r)
        {
            double mean = Mean(r, 3), sum = 0;
            for (int y = r.Y0; y < r.Y1; y++)
            {
                for (int x = r.X0; x < r.X1; x++)
                {
                    double d = Value(y * Width + x, 3) - mean;
                    sum += d * d;
                }
            }
            return r.Area == 0 ? 0 : Math.Sqrt(sum / r.Area);
        }

        // The root mean square of the luma minus its mean over the (2 radius + 1)^2 box around each pixel of the rectangle, by an integral
        // image of the rectangle grown by the radius. A smooth gradient is not counted; fine structure is.
        public double HighFrequency(ImageStats.Rect2 r, int radius)
        {
            int x0 = Math.Max(0, r.X0 - radius), y0 = Math.Max(0, r.Y0 - radius), x1 = Math.Min(Width, r.X1 + radius), y1 = Math.Min(Height, r.Y1 + radius);
            int w = x1 - x0, h = y1 - y0;
            var integral = new double[(w + 1) * (h + 1)];
            for (int y = 0; y < h; y++)
            {
                double row = 0;
                for (int x = 0; x < w; x++)
                {
                    row += Value((y0 + y) * Width + x0 + x, 3);
                    integral[(y + 1) * (w + 1) + x + 1] = integral[y * (w + 1) + x + 1] + row;
                }
            }
            double sum = 0;
            int n = 0;
            for (int y = r.Y0; y < r.Y1; y++)
            {
                for (int x = r.X0; x < r.X1; x++)
                {
                    int ax = Math.Max(x0, x - radius) - x0, bx = Math.Min(x1, x + radius + 1) - x0;
                    int ay = Math.Max(y0, y - radius) - y0, by = Math.Min(y1, y + radius + 1) - y0;
                    double total = integral[by * (w + 1) + bx] - integral[ay * (w + 1) + bx] - integral[by * (w + 1) + ax] + integral[ay * (w + 1) + ax];
                    double mean = total / ((bx - ax) * (by - ay));
                    double d = Value(y * Width + x, 3) - mean;
                    sum += d * d;
                    n++;
                }
            }
            return n == 0 ? 0 : Math.Sqrt(sum / n);
        }

        private double Value(int i, int channel)
        {
            switch (channel)
            {
                case 0: return R[i];
                case 1: return G[i];
                case 2: return B[i];
                default: return 0.2126 * R[i] + 0.7152 * G[i] + 0.0722 * B[i];
            }
        }
    }

    // The base of the lamp scenes: the lamp pass of the mod (LampPass and its shader, the classes the mod uses), the two command
    // buffers it puts on a camera (the depth copy at BeforeForwardAlpha, the draws at AfterForwardAlpha) and the reads of the frame.
    internal abstract class LampSceneBase : WeatherSceneBase
    {
        // The lamp of the checks: at the origin of a local frame, shining along +z with a 20 degree half angle and 120 m of reach.
        protected const float HalfAngleDeg = 20f;
        protected const float LampRange = 120f;

        protected LampPass Pass;
        protected LampTuning Tuning = LampTuning.Defaults();
        protected CommandBuffer DepthBuffer, DrawBuffer;
        protected Vector3 LampOrigin = new Vector3(2000f, 118f, -780f);

        protected void MakePass()
        {
            Shader shader = Shader.Find("ReDefinition/AtmospheresLamp");
            if (shader == null) throw new InvalidOperationException("the lamp shader is not in the player");
            Pass = new LampPass(shader);
        }

        protected LampDraw Lamp(Vector3 at, Vector3 axis, float sigma, float sigmaCamera, float glint, float scale)
        {
            float half = HalfAngleDeg * Mathf.Deg2Rad;
            return new LampDraw
            {
                Position = at, Axis = axis, Range = LampRange, CosOuter = Mathf.Cos(half), HalfAngleRad = half,
                Colour = new Color(1.0f * scale, 0.82f * scale, 0.55f * scale, 1f), Sigma = sigma, SigmaCamera = sigmaCamera, GlintAmp = glint
            };
        }

        protected LampFrame MakeFrame(float weight)
        {
            return new LampFrame
            {
                CameraPosition = Cam.transform.position, CameraForward = Cam.transform.forward, Near = Cam.nearClipPlane, Far = Cam.farClipPlane,
                UseDepth = true, DepthFlipY = false, ReversedZ = SystemInfo.usesReversedZBuffer, JitterOffset = 0f, Drift = Vector3.zero,
                Weight = weight, Tuning = Tuning
            };
        }

        // Puts the lamps on the camera as the module does: the depth copy before the transparent queue, the draws after it. Nothing is
        // attached for no lamps.
        protected void Attach(LampDraw[] draws, int count, float weight)
        {
            Detach();
            if (count <= 0) return;
            Pass.EnsureDepth(Cam.targetTexture.width, Cam.targetTexture.height);
            DepthBuffer = new CommandBuffer { name = "lamp depth" };
            DrawBuffer = new CommandBuffer { name = "lamp draws" };
            Pass.RecordDepthCopy(DepthBuffer);
            LampFrame frame = MakeFrame(weight);
            Pass.Record(DrawBuffer, draws, count, ref frame, Pass.DepthCopy);
            Cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, DepthBuffer);
            Cam.AddCommandBuffer(CameraEvent.AfterForwardAlpha, DrawBuffer);
        }

        protected void Detach()
        {
            if (DepthBuffer == null) return;
            Cam.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, DepthBuffer);
            Cam.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, DrawBuffer);
            DepthBuffer.Release();
            DrawBuffer.Release();
            DepthBuffer = null;
            DrawBuffer = null;
        }

        // The red channel of a float render texture at a pixel (for the diagnostics of the depth copy).
        protected static float ReadRed(RenderTexture texture, int x, int y)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = texture;
            var one = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            one.ReadPixels(new Rect(x, y, 1, 1), 0, 0);
            one.Apply(false);
            RenderTexture.active = previous;
            float v = one.GetPixel(0, 0).r;
            UnityEngine.Object.Destroy(one);
            return v;
        }

        protected IEnumerator GrabRgb(Action<RgbFrame> done)
        {
            yield return new WaitForEndOfFrame();
            done(RgbFrame.Read(Target));
        }
    }
}
