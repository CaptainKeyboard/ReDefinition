using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // A frame read back at the end of a frame: linear colours of the flight camera's own target (otherwise of the screen), row-major with
    // row 0 at the bottom, as Unity reads them.
    internal sealed class ReconFrame
    {
        internal int Width, Height;
        internal Color[] Pixels;

        // The largest channel value of the frame, linear: a region near 1 in an 8-bit target is saturated.
        internal float Peak;

        // The values as the target held them, before the gamma-to-linear step of Pixels (set only while ReconHost.KeepRaw is on; else null).
        internal Color[] Raw;

        // The pixels of a rectangle given as fractions of the frame (centre x, centre y, width, height; y up), as a list of luminances.
        internal List<float> Lumas(double cx, double cy, double w, double h)
        {
            int x0, y0, x1, y1;
            Bounds(cx, cy, w, h, out x0, out y0, out x1, out y1);
            var list = new List<float>((x1 - x0) * (y1 - y0));
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    Color c = Pixels[y * Width + x];
                    list.Add(ReconMath.Luma(c.r, c.g, c.b));
                }
            }
            return list;
        }

        // The mean colour of a rectangle.
        internal Color Mean(double cx, double cy, double w, double h)
        {
            int x0, y0, x1, y1;
            Bounds(cx, cy, w, h, out x0, out y0, out x1, out y1);
            double r = 0, g = 0, b = 0;
            int n = 0;
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    Color c = Pixels[y * Width + x];
                    r += c.r;
                    g += c.g;
                    b += c.b;
                    n++;
                }
            }
            return n == 0 ? Color.black : new Color((float)(r / n), (float)(g / n), (float)(b / n), 1f);
        }

        // The fraction of the frame's pixels that are not black (a frame that was never drawn is black).
        internal double LitFraction()
        {
            int lit = 0;
            for (int i = 0; i < Pixels.Length; i++)
            {
                Color c = Pixels[i];
                if (c.r + c.g + c.b > 0.003f) lit++;
            }
            return Pixels.Length == 0 ? 0 : lit / (double)Pixels.Length;
        }

        private void Bounds(double cx, double cy, double w, double h, out int x0, out int y0, out int x1, out int y1)
        {
            x0 = Math.Max(0, (int)((cx - w / 2) * Width));
            x1 = Math.Min(Width, (int)((cx + w / 2) * Width));
            y0 = Math.Max(0, (int)((cy - h / 2) * Height));
            y1 = Math.Min(Height, (int)((cy + h / 2) * Height));
            if (x1 <= x0) x1 = Math.Min(Width, x0 + 1);
            if (y1 <= y0) y1 = Math.Min(Height, y0 + 1);
        }
    }

    // The probe's hands inside the engine: the end-of-frame reads that need a coroutine, and a per-frame hook for the fixtures whose
    // meshes follow the floating origin. It exists only while a series probe runs.
    internal sealed class ReconHost : MonoBehaviour
    {
        private static ReconHost instance;

        // The latest frame read, and whether a read is under way.
        internal ReconFrame Frame;
        internal bool Reading;

        // Read the screen instead of the flight camera's target: run2 read the same frame (luma 0.4232, peak 1.49) in every state, with
        // the disc on and off, so the target did not follow the scene; the liveness step in AtmoRecon decides.
        internal bool UseScreen;

        // Keep a copy of the values as the target held them (ReconFrame.Raw) next to the converted Pixels: the numeric readouts of series K-T2d compare units.
        internal bool KeepRaw;

        // Called every frame before the cameras cull; fixtures rebuild their meshes here.
        internal readonly List<Action> PerFrame = new List<Action>();

        // Called before every physics step (FixedUpdate): the K-T2 hold sets the vessel's position and velocity here, since a per-frame tick leaves a step or more unheld.
        internal readonly List<Action> PerFixed = new List<Action>();

        // Called at the end of every frame (the depth probes read their targets here).
        internal readonly List<Action> EndOfFrame = new List<Action>();

        internal static ReconHost Get()
        {
            if (instance != null) return instance;
            var go = new GameObject("ReDefinition recon host");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<ReconHost>();
            return instance;
        }

        internal static void Release()
        {
            if (instance == null) return;
            Destroy(instance.gameObject);
            instance = null;
        }

        // Reads the flight camera's target (or the screen) at the end of this frame into Frame.
        internal void Read()
        {
            if (Reading) return;
            Reading = true;
            Frame = null;
            StartCoroutine(ReadRoutine());
        }

        private IEnumerator ReadRoutine()
        {
            yield return new WaitForEndOfFrame();
            try
            {
                Camera camera = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
                RenderTexture target = camera != null && !UseScreen ? camera.targetTexture : null;
                Color[] pixels;
                int width, height;
                if (target != null)
                {
                    RenderTexture active = RenderTexture.active;
                    RenderTexture.active = target;
                    var read = new Texture2D(target.width, target.height, TextureFormat.RGBAFloat, false);
                    read.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
                    RenderTexture.active = active;
                    pixels = read.GetPixels();
                    width = target.width;
                    height = target.height;
                    Destroy(read);
                }
                else
                {
                    var read = new Texture2D(Screen.width, Screen.height, TextureFormat.RGBAFloat, false);
                    read.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0, false);
                    pixels = read.GetPixels();
                    width = Screen.width;
                    height = Screen.height;
                    Destroy(read);
                }
                // Not clamped: a target in a float format can hold more than 1, and a clamp would hide a saturated region.
                float peak = 0f;
                Color[] raw = KeepRaw ? (Color[])pixels.Clone() : null;
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i].r = Mathf.GammaToLinearSpace(Mathf.Max(0f, pixels[i].r));
                    pixels[i].g = Mathf.GammaToLinearSpace(Mathf.Max(0f, pixels[i].g));
                    pixels[i].b = Mathf.GammaToLinearSpace(Mathf.Max(0f, pixels[i].b));
                    peak = Mathf.Max(peak, Mathf.Max(pixels[i].r, Mathf.Max(pixels[i].g, pixels[i].b)));
                }
                Frame = new ReconFrame { Width = width, Height = height, Pixels = pixels, Peak = peak, Raw = raw };
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("recon", "the frame could not be read", e);
                Frame = null;
            }
            Reading = false;
        }

        private void FixedUpdate()
        {
            for (int i = 0; i < PerFixed.Count; i++)
            {
                try
                {
                    PerFixed[i]();
                }
                catch (Exception e)
                {
                    AtmospheresLog.Failed("recon", "a per-step hook failed", e);
                }
            }
        }

        private void LateUpdate()
        {
            for (int i = 0; i < PerFrame.Count; i++)
            {
                try
                {
                    PerFrame[i]();
                }
                catch (Exception e)
                {
                    AtmospheresLog.Failed("recon", "a per-frame hook failed", e);
                }
            }
            if (EndOfFrame.Count > 0) StartCoroutine(EndRoutine());
        }

        private IEnumerator EndRoutine()
        {
            yield return new WaitForEndOfFrame();
            for (int i = 0; i < EndOfFrame.Count; i++)
            {
                try
                {
                    EndOfFrame[i]();
                }
                catch (Exception e)
                {
                    AtmospheresLog.Failed("recon", "an end-of-frame hook failed", e);
                }
            }
        }
    }

    // A tone for the audio path A: the engine calls OnAudioFilterRead on the audio thread for a component beside an AudioSource.
    // The component writes a sine; it needs no reference to the audio module.
    internal sealed class ReconTone : MonoBehaviour
    {
        internal volatile bool On;
        internal float Frequency = 440f;
        internal float Amplitude = 0.5f;
        private double phase;
        private int sampleRate = 48000;

        private void Awake()
        {
            Type settings = ReconDyn.Type("UnityEngine.AudioSettings");
            object rate = settings != null ? ReconDyn.GetStatic(settings, "outputSampleRate") : null;
            if (rate is int && (int)rate > 0) sampleRate = (int)rate;
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            double step = 2.0 * Math.PI * Frequency / sampleRate;
            for (int i = 0; i < data.Length; i += channels)
            {
                float v = On ? (float)(Math.Sin(phase) * Amplitude) : 0f;
                phase += step;
                if (phase > 2.0 * Math.PI) phase -= 2.0 * Math.PI;
                for (int c = 0; c < channels; c++) data[i + c] = v;
            }
        }
    }
}
