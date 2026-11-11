using System;
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.WeatherPlayer
{
    // A frame's luma, read back from a render texture: Rec. 709 luma of the coded values, row 0 at the bottom as Unity reads it.
    public sealed class LumaFrame
    {
        public readonly int Width, Height;
        public readonly float[] Y;

        public LumaFrame(int width, int height, float[] y)
        {
            Width = width;
            Height = height;
            Y = y;
        }

        // Reads the texture back; call it after WaitForEndOfFrame so that the camera has drawn into it.
        public static LumaFrame Read(RenderTexture texture)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = texture;
            var copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBAHalf, false, true);
            copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            copy.Apply(false);
            RenderTexture.active = previous;
            Color[] pixels = copy.GetPixels();
            UnityEngine.Object.Destroy(copy);
            var y = new float[pixels.Length];
            for (int i = 0; i < y.Length; i++) y[i] = 0.2126f * pixels[i].r + 0.7152f * pixels[i].g + 0.0722f * pixels[i].b;
            return new LumaFrame(texture.width, texture.height, y);
        }
    }

    // The image statistics of the series, over a rectangle of a luma frame (x0, y0, x1, y1 exclusive, in pixels). Plain functions
    // of arrays, so that a scene and the self test share them.
    public static class ImageStats
    {
        public struct Rect2
        {
            public int X0, Y0, X1, Y1;

            public Rect2(int x0, int y0, int x1, int y1)
            {
                X0 = x0; Y0 = y0; X1 = x1; Y1 = y1;
            }

            public int Area { get { return Math.Max(0, X1 - X0) * Math.Max(0, Y1 - Y0); } }

            public static Rect2 Whole(LumaFrame f)
            {
                return new Rect2(0, 0, f.Width, f.Height);
            }
        }

        public static double MeanLuma(LumaFrame f, Rect2 r)
        {
            double sum = 0;
            for (int y = r.Y0; y < r.Y1; y++)
                for (int x = r.X0; x < r.X1; x++) sum += f.Y[y * f.Width + x];
            return r.Area == 0 ? 0 : sum / r.Area;
        }

        // The mean of a box blur of radius `radius`, for every pixel, by an integral image.
        public static float[] BoxBlur(LumaFrame f, int radius)
        {
            int w = f.Width, h = f.Height;
            var integral = new double[(w + 1) * (h + 1)];
            for (int y = 0; y < h; y++)
            {
                double row = 0;
                for (int x = 0; x < w; x++)
                {
                    row += f.Y[y * w + x];
                    integral[(y + 1) * (w + 1) + x + 1] = integral[y * (w + 1) + x + 1] + row;
                }
            }
            var blurred = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int x0 = Math.Max(0, x - radius), x1 = Math.Min(w, x + radius + 1), y0 = Math.Max(0, y - radius), y1 = Math.Min(h, y + radius + 1);
                    double sum = integral[y1 * (w + 1) + x1] - integral[y0 * (w + 1) + x1] - integral[y1 * (w + 1) + x0] + integral[y0 * (w + 1) + x0];
                    blurred[y * w + x] = (float)(sum / ((x1 - x0) * (y1 - y0)));
                }
            return blurred;
        }

        // Local contrast: the RMS of the frame minus its blur over the mean, in the rectangle.
        public static double LocalContrast(LumaFrame f, Rect2 r, int radius)
        {
            float[] blurred = BoxBlur(f, radius);
            double sum = 0, mean = MeanLuma(f, r);
            for (int y = r.Y0; y < r.Y1; y++)
                for (int x = r.X0; x < r.X1; x++)
                {
                    double d = f.Y[y * f.Width + x] - blurred[y * f.Width + x];
                    sum += d * d;
                }
            return r.Area == 0 || mean < 1e-6 ? 0 : Math.Sqrt(sum / r.Area) / mean;
        }

        // Opacity of a level: 1 minus the mean luma of the frame at that level over the mean luma of the clear frame.
        public static double Opacity(double meanWithLevel, double meanClear)
        {
            return meanClear < 1e-6 ? 0 : 1.0 - meanWithLevel / meanClear;
        }

        // Whether the opacities rise with the level (by at least `slack` each step, which may be negative to allow noise).
        public static bool Monotonic(IList<double> opacities, double slack)
        {
            for (int i = 1; i < opacities.Count; i++)
                if (opacities[i] - opacities[i - 1] < slack) return false;
            return true;
        }

        // The contrast of a marker: the mean luma inside its rectangle against the mean of the ring around it (the rectangle grown by
        // its own size), as |inside - ring| over the ring.
        public static double MarkerContrast(LumaFrame f, Rect2 marker)
        {
            int w = marker.X1 - marker.X0, h = marker.Y1 - marker.Y0;
            var outer = new Rect2(Math.Max(0, marker.X0 - w), Math.Max(0, marker.Y0 - h), Math.Min(f.Width, marker.X1 + w), Math.Min(f.Height, marker.Y1 + h));
            double inside = MeanLuma(f, marker);
            double ringSum = MeanLuma(f, outer) * outer.Area - inside * marker.Area;
            int ringArea = outer.Area - marker.Area;
            if (ringArea <= 0) return 0;
            double ring = ringSum / ringArea;
            return ring < 1e-6 ? 0 : Math.Abs(inside - ring) / ring;
        }

        // The rectangle on the screen of a world sphere (centre, radius), in pixels, or an empty one where it is behind the camera.
        public static Rect2 ScreenRect(Camera cam, Vector3 centre, float radius, int width, int height)
        {
            Vector3 p = cam.WorldToScreenPoint(centre);
            if (p.z <= 0f) return new Rect2(0, 0, 0, 0);
            float pixels = radius / (p.z * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad)) * (height * 0.5f);
            int half = Math.Max(1, (int)Math.Round(pixels));
            int cx = (int)Math.Round(p.x), cy = (int)Math.Round(p.y);
            return new Rect2(Math.Max(0, cx - half), Math.Max(0, cy - half), Math.Min(width, cx + half), Math.Min(height, cy + half));
        }

        // Particles: the connected groups of at least `minPixels` pixels that differ by more than `threshold` from the local mean (a box
        // blur of `radius`), by a flood fill with 4-neighbours.
        public static int CountParticles(LumaFrame f, float threshold, int radius, int minPixels)
        {
            float[] blurred = BoxBlur(f, radius);
            int w = f.Width, h = f.Height;
            var on = new bool[w * h];
            for (int i = 0; i < on.Length; i++) on[i] = Math.Abs(f.Y[i] - blurred[i]) > threshold;
            var stack = new Stack<int>();
            int count = 0;
            for (int start = 0; start < on.Length; start++)
            {
                if (!on[start]) continue;
                int size = 0;
                stack.Push(start);
                on[start] = false;
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    size++;
                    int x = i % w, y = i / w;
                    if (x > 0 && on[i - 1]) { on[i - 1] = false; stack.Push(i - 1); }
                    if (x < w - 1 && on[i + 1]) { on[i + 1] = false; stack.Push(i + 1); }
                    if (y > 0 && on[i - w]) { on[i - w] = false; stack.Push(i - w); }
                    if (y < h - 1 && on[i + w]) { on[i + w] = false; stack.Push(i + w); }
                }
                if (size >= minPixels) count++;
            }
            return count;
        }
    }
}
