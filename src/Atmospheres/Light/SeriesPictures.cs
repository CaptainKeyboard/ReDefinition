using System;
using System.IO;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The pictures a series saves where an owner taste item or a reviewer judges a look (lamp beam T1, aurora T3, camera frost T4).
    // Every picture is a sheet with TWO views side by side: the natural view on the left (what the player sees) and an aid view on the
    // right (the A/B difference as a heat map: red brighter, blue darker, over the dimmed natural picture, scaled to the largest
    // difference of the picture, which is named in the log line). The runner copies the folder to .local/work/series-pictures/<series>
    // of the main repository. The functions on arrays are pure (the unit tests run them); only Save touches the game.
    internal static class SeriesPictures
    {
        internal const string FolderRelative = "GameData/ReDefinition/Atmospheres/PluginData/series-pictures";

        // Sheets are scaled down by whole factors until one view is at most this wide.
        internal const int MaxViewWidth = 800;

        internal static string Folder
        {
            get { return Path.Combine(KSPUtil.ApplicationRootPath, FolderRelative.Replace('/', Path.DirectorySeparatorChar)); }
        }

        // The heat map of (on - off) over the dimmed natural picture; the scale is the largest absolute difference, at least floor.
        // Returns the scale it used.
        internal static Color[] HeatMap(float[] on, float[] off, int width, int height, float floor, out float scale)
        {
            scale = floor;
            for (int i = 0; i < on.Length; i++) scale = Math.Max(scale, Math.Abs(on[i] - off[i]));
            var map = new Color[on.Length];
            for (int i = 0; i < map.Length; i++)
            {
                float grey = Mathf.Clamp01(on[i]) * 0.4f;
                float d = (on[i] - off[i]) / scale;
                if (d >= 0f)
                {
                    float t = Mathf.Clamp01(d);
                    map[i] = new Color(Mathf.Lerp(grey, 1f, t), Mathf.Lerp(grey, 0.1f, t), Mathf.Lerp(grey, 0.1f, t), 1f);
                }
                else
                {
                    float t = Mathf.Clamp01(-d);
                    map[i] = new Color(Mathf.Lerp(grey, 0.1f, t), Mathf.Lerp(grey, 0.25f, t), Mathf.Lerp(grey, 1f, t), 1f);
                }
            }
            return map;
        }

        // The luminance of a picture.
        internal static float[] Luma(Color[] pixels)
        {
            var luma = new float[pixels.Length];
            for (int i = 0; i < luma.Length; i++) luma[i] = 0.2126f * pixels[i].r + 0.7152f * pixels[i].g + 0.0722f * pixels[i].b;
            return luma;
        }

        // The factor by which a view of that width is scaled down for a sheet.
        internal static int Factor(int width)
        {
            return Math.Max(1, (width + MaxViewWidth - 1) / MaxViewWidth);
        }

        // A picture scaled down by a whole factor (box mean).
        internal static Color[] Downscale(Color[] pixels, int width, int height, int factor, out int outWidth, out int outHeight)
        {
            outWidth = Math.Max(1, width / factor);
            outHeight = Math.Max(1, height / factor);
            if (factor == 1)
            {
                outWidth = width;
                outHeight = height;
                return pixels;
            }
            var result = new Color[outWidth * outHeight];
            for (int y = 0; y < outHeight; y++)
            {
                for (int x = 0; x < outWidth; x++)
                {
                    float r = 0, g = 0, b = 0;
                    for (int dy = 0; dy < factor; dy++)
                    {
                        for (int dx = 0; dx < factor; dx++)
                        {
                            Color c = pixels[(y * factor + dy) * width + x * factor + dx];
                            r += c.r;
                            g += c.g;
                            b += c.b;
                        }
                    }
                    float n = factor * factor;
                    result[y * outWidth + x] = new Color(r / n, g / n, b / n, 1f);
                }
            }
            return result;
        }

        // Two pictures of the same size side by side, the first on the left; the sheet is 2 * width wide.
        internal static Color[] SideBySide(Color[] left, Color[] right, int width, int height)
        {
            var sheet = new Color[2 * width * height];
            for (int y = 0; y < height; y++)
            {
                Array.Copy(left, y * width, sheet, y * 2 * width, width);
                Array.Copy(right, y * width, sheet, y * 2 * width + width, width);
            }
            return sheet;
        }

        // The sheet of one judged picture: natural on the left, the heat map of its difference to the A/B partner on the right. The
        // partner is the same view with the effect off (one luminance image of the same size). Returns the sheet and its width.
        internal static Color[] Sheet(Color[] natural, float[] partnerLuma, int width, int height, float floor, out int sheetWidth, out int sheetHeight, out float scale)
        {
            float[] on = Luma(natural);
            Color[] aid = HeatMap(on, partnerLuma, width, height, floor, out scale);
            int factor = Factor(width);
            int w, h;
            Color[] a = Downscale(natural, width, height, factor, out w, out h);
            Color[] b = Downscale(aid, width, height, factor, out w, out h);
            sheetWidth = 2 * w;
            sheetHeight = h;
            return SideBySide(a, b, w, h);
        }

        // Writes the sheet of a natural picture and its off partner as <folder>/<name>.png. Linear input is encoded to display values.
        // False where it could not be written (the probe goes on; the log says why).
        internal static bool SaveSheet(string name, Color[] natural, float[] partnerLuma, int width, int height, float floor = 0.01f)
        {
            if (natural == null || partnerLuma == null || natural.Length != width * height || partnerLuma.Length != natural.Length) return false;
            try
            {
                int sw, sh;
                float scale;
                Color[] sheet = Sheet(natural, partnerLuma, width, height, floor, out sw, out sh, out scale);
                var texture = new Texture2D(sw, sh, TextureFormat.RGBA32, false);
                for (int i = 0; i < sheet.Length; i++)
                {
                    Color c = sheet[i];
                    sheet[i] = new Color(Encode(c.r), Encode(c.g), Encode(c.b), 1f);
                }
                texture.SetPixels(sheet);
                texture.Apply(false);
                byte[] png = texture.EncodeToPNG();
                UnityEngine.Object.Destroy(texture);
                Directory.CreateDirectory(Folder);
                string path = Path.Combine(Folder, name + ".png");
                File.WriteAllBytes(path, png);
                AtmospheresLog.Say("picture sheet " + name + ".png: natural view left, difference heat map right (red brighter, blue darker, full scale "
                                   + scale.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture) + " luma)");
                return true;
            }
            catch (Exception e)
            {
                AtmospheresLog.Say("picture sheet " + name + " could not be saved (" + e.GetType().Name + " " + e.Message + ")");
                return false;
            }
        }

        // Writes one linear picture as <folder>/<name>.png, scaled down by a whole factor (1: full size). Returns the size of the file. Unlike
        // SaveSheet it throws where it cannot write (a full disk, a folder that is not writable): the caller names the fault.
        internal static long SaveImage(string name, Color[] linear, int width, int height, int factor)
        {
            if (linear == null || linear.Length != width * height) throw new ArgumentException("the picture has no " + width + "x" + height + " pixels");
            int w, h;
            Color[] scaled = Downscale(linear, width, height, Math.Max(1, factor), out w, out h);
            var encoded = new Color[scaled.Length];
            for (int i = 0; i < encoded.Length; i++)
            {
                Color c = scaled[i];
                encoded[i] = new Color(Encode(c.r), Encode(c.g), Encode(c.b), 1f);
            }
            var texture = new Texture2D(w, h, TextureFormat.RGBA32, false);
            byte[] png;
            try
            {
                texture.SetPixels(encoded);
                texture.Apply(false);
                png = texture.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.Destroy(texture);
            }
            Directory.CreateDirectory(Folder);
            File.WriteAllBytes(Path.Combine(Folder, name + ".png"), png);
            return png.Length;
        }

        private static float Encode(float linear)
        {
            if (float.IsNaN(linear) || float.IsInfinity(linear)) return 0f;
            return Mathf.Clamp01(Mathf.LinearToGammaSpace(Mathf.Max(0f, linear)));
        }
    }
}
