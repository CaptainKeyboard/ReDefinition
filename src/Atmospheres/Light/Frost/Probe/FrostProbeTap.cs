using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // The probe's view of what the host composite leaves: a second handler on the host's after-upscaling hook, registered after the
    // frost's own, so that it runs after it in the same command buffer. When armed it copies the upscaled image (frost included) into
    // a small render texture for the camera the frost draws on, once; the probe reads it back and compares pictures with the frost on and
    // off. The quad fallback is drawn in the scene before the upscaler, so its frost is in the same picture.
    internal sealed class FrostProbeTap : IDisposable
    {
        internal const int Width = 640, Height = 360;

        private RenderTexture target;
        private bool armed;
        private bool registered;
        private PostUpscaleDraw draw;
        private int capturedFrame = -1;

        // The name of the camera the last capture was taken for.
        internal string CapturedCamera { get; private set; }

        internal bool Captured
        {
            get { return capturedFrame >= 0; }
        }

        internal bool Register()
        {
            if (registered) return true;
            draw = Draw;
            registered = Services.PostUpscale.Register("camera-frost-probe", draw, false);
            return registered;
        }

        // The next call of the hook for the flight camera copies the image. The handler is registered again first so that it runs
        // after the frost's own, whichever of the two registered last before.
        internal void Arm()
        {
            if (registered && draw != null) Services.PostUpscale.Unregister(draw);
            registered = false;
            Register();
            capturedFrame = -1;
            armed = true;
        }

        private void Draw(CommandBuffer commands, RenderTexture upscaled, RenderTexture depth, Camera camera)
        {
            if (!armed || upscaled == null) return;
            Camera flight = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            // The camera the hook is called for in the IVA is itself a premise under test (the flight camera or the internal one).
            if (camera != flight && (camera == null || camera.name != "InternalCamera")) return;
            CapturedCamera = camera.name;
            if (target == null)
            {
                target = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                {
                    name = "ReDefinition Atmospheres frost probe tap",
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave
                };
                target.Create();
            }
            commands.Blit(upscaled, target);
            armed = false;
            capturedFrame = Time.frameCount;
        }

        // The captured picture's luminance, row 0 at the bottom; null before a capture.
        internal float[] ReadLuma()
        {
            if (target == null || capturedFrame < 0) return null;
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            var copy = new Texture2D(Width, Height, TextureFormat.RGBAHalf, false, true);
            copy.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            RenderTexture.active = previous;
            Color[] pixels = copy.GetPixels();
            UnityEngine.Object.Destroy(copy);
            var luma = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                float v = FrostAbMetrics.Luma(pixels[i].r, pixels[i].g, pixels[i].b);
                luma[i] = float.IsNaN(v) || float.IsInfinity(v) ? 0f : Math.Min(v, 64f);
            }
            return luma;
        }

        public void Dispose()
        {
            if (registered && draw != null) Services.PostUpscale.Unregister(draw);
            registered = false;
            if (target != null)
            {
                target.Release();
                UnityEngine.Object.Destroy(target);
                target = null;
            }
        }
    }
}
