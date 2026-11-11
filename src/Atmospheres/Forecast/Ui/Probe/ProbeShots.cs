using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ReDefinition.Atmospheres.Forecast
{
    // A part of the screen a picture is about, in screen pixels (origin at the lower left), and whether it is the defect.
    internal struct ShotMark
    {
        internal Rect R;
        internal bool Bad;
    }

    // One item's pictures: the natural view (the screen cropped to the item, nothing drawn on it) and the aid view (the whole screen with
    // the item's parts framed, green where the item holds, red where it does not).
    internal sealed class ShotRequest
    {
        internal string Name;
        internal bool Ok;
        internal readonly List<ShotMark> Marks = new List<ShotMark>();
        // The part of the screen the natural view shows.
        internal Rect Crop;
        // The number of the pictures that were written for the item (2 when both views exist), and their files.
        internal int Written;
        internal int Number;
        internal readonly List<string> Files = new List<string>();
    }

    // Takes the screen at the end of a frame (ReadPixels is allowed nowhere else) for the probe: the item pictures and the two screens
    // of the overlay's on/off comparison. One object, made when first asked, on the probe's side of the game only.
    internal sealed class ProbeShots : MonoBehaviour
    {
        private static ProbeShots instance;
        private int pending;
        private int counter;

        internal static ProbeShots Ensure()
        {
            if (instance != null) return instance;
            var go = new GameObject("ReDefinitionD30Shots");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<ProbeShots>();
            return instance;
        }

        internal bool Idle
        {
            get { return pending == 0; }
        }

        internal static string Folder
        {
            get
            {
                string folder = Path.Combine(KSPUtil.ApplicationRootPath, "Screenshots");
                Directory.CreateDirectory(folder);
                return folder;
            }
        }

        // The pictures of the items, in the order given; each gets a number from the counter.
        internal void Item(string prefix, ShotRequest request)
        {
            counter++;
            request.Number = counter;
            pending++;
            StartCoroutine(TakeItem(prefix, request));
        }

        // The screen as RGB bytes (three a pixel, row 0 at the bottom) once the frame is drawn; done is called with the size.
        internal void Capture(Action<byte[], int, int> done)
        {
            pending++;
            StartCoroutine(TakeScreen(done));
        }

        private IEnumerator TakeScreen(Action<byte[], int, int> done)
        {
            yield return new WaitForEndOfFrame();
            try
            {
                int w = UnityEngine.Screen.width, h = UnityEngine.Screen.height;
                Texture2D texture = Read(w, h);
                byte[] rgb = ToRgb(texture.GetPixels32());
                Destroy(texture);
                done(rgb, w, h);
            }
            finally
            {
                pending--;
            }
        }

        private IEnumerator TakeItem(string prefix, ShotRequest request)
        {
            yield return new WaitForEndOfFrame();
            try
            {
                int w = UnityEngine.Screen.width, h = UnityEngine.Screen.height;
                Texture2D screen = Read(w, h);
                Color32[] pixels = screen.GetPixels32();
                Destroy(screen);
                string stem = prefix + request.Number.ToString("00") + "-" + request.Name;
                request.Files.Add(WriteCrop(stem + "-natural.png", pixels, w, h, request.Crop));
                var framed = (Color32[])pixels.Clone();
                var all = new Rect(0, 0, w, h);
                foreach (ShotMark mark in request.Marks) Frame(framed, w, h, mark.R, mark.Bad ? new Color32(255, 40, 40, 255) : new Color32(40, 220, 80, 255));
                request.Files.Add(WriteCrop(stem + "-aid.png", framed, w, h, all));
                request.Written = request.Files.FindAll(f => f != null).Count;
            }
            finally
            {
                pending--;
            }
        }

        // A picture of the mask (white where two screens differ) beside the item pictures: the overlay's aid view.
        internal string WriteMask(string name, byte[] rgb, int w, int h)
        {
            var pixels = new Color32[w * h];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2], 255);
            return WriteCrop(name, pixels, w, h, new Rect(0, 0, w, h));
        }

        // A picture of the screen as it is (the natural view of the overlay).
        internal string WriteScreen(string name, byte[] rgb, int w, int h)
        {
            return WriteMask(name, rgb, w, h);
        }

        private static Texture2D Read(int w, int h)
        {
            var texture = new Texture2D(w, h, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            texture.Apply();
            return texture;
        }

        private static byte[] ToRgb(Color32[] pixels)
        {
            var rgb = new byte[pixels.Length * 3];
            for (int i = 0; i < pixels.Length; i++)
            {
                rgb[i * 3] = pixels[i].r;
                rgb[i * 3 + 1] = pixels[i].g;
                rgb[i * 3 + 2] = pixels[i].b;
            }
            return rgb;
        }

        // The pixels inside the rectangle (clipped to the screen) as a PNG file; the path, or null when nothing of it is on the screen.
        private static string WriteCrop(string file, Color32[] pixels, int w, int h, Rect crop)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(crop.xMin), 0, w - 1), x1 = Mathf.Clamp(Mathf.CeilToInt(crop.xMax), 1, w);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(crop.yMin), 0, h - 1), y1 = Mathf.Clamp(Mathf.CeilToInt(crop.yMax), 1, h);
            int cw = x1 - x0, ch = y1 - y0;
            if (cw < 2 || ch < 2) return null;
            var part = new Color32[cw * ch];
            for (int y = 0; y < ch; y++) Array.Copy(pixels, (y0 + y) * w + x0, part, y * cw, cw);
            var texture = new Texture2D(cw, ch, TextureFormat.RGB24, false);
            texture.SetPixels32(part);
            texture.Apply();
            string path = Path.Combine(Folder, file);
            File.WriteAllBytes(path, texture.EncodeToPNG());
            Destroy(texture);
            return path;
        }

        // A two-pixel outline of the rectangle, clipped to the screen.
        private static void Frame(Color32[] pixels, int w, int h, Rect r, Color32 c)
        {
            int x0 = Mathf.FloorToInt(r.xMin), x1 = Mathf.CeilToInt(r.xMax), y0 = Mathf.FloorToInt(r.yMin), y1 = Mathf.CeilToInt(r.yMax);
            for (int t = 0; t < 2; t++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    Put(pixels, w, h, x, y0 + t, c);
                    Put(pixels, w, h, x, y1 - t, c);
                }
                for (int y = y0; y <= y1; y++)
                {
                    Put(pixels, w, h, x0 + t, y, c);
                    Put(pixels, w, h, x1 - t, y, c);
                }
            }
        }

        private static void Put(Color32[] pixels, int w, int h, int x, int y, Color32 c)
        {
            if (x >= 0 && y >= 0 && x < w && y < h) pixels[y * w + x] = c;
        }

        // The screen rectangle of a UI element, in pixels from the lower left.
        internal static Rect ScreenRect(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            Canvas canvas = rect.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            Vector2 a = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            Vector2 b = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }
    }
}
