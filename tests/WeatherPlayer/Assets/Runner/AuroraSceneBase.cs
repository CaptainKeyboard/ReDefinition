using System;
using System.Collections;
using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // The shared part of the aurora scenes (package D38): the fake world made dark by the sun's elevation, the fake planet under it (the
    // shell's geometry needs a sphere: the camera stands on the pole-ward side of a body of the radius of Kerbin at a chosen latitude),
    // and the shell's draw through the same AuroraPass the mod uses, recorded at BeforeForwardAlpha as the mod does. The state of the
    // aurora is forced (the field generator does not exist yet): an activity, a sun elevation, a latitude.
    public abstract class AuroraSceneBase : WeatherSceneBase
    {
        // The planet and its atmosphere in the player. Constants of the test world only (the mod reads them from the body).
        protected const float PlanetRadius = 600000f, AtmosphereDepth = 70000f;
        internal AuroraPass Pass;
        private CommandBuffer shell;
        private Material sky;
        private Color skyHorizon, skyZenith, ambient;
        private float sunIntensity;

        protected override void BuildContent()
        {
            Shader shader = Shader.Find("ReDefinition/AtmospheresAurora");
            if (shader == null) throw new InvalidOperationException("the aurora shader is not in the player");
            Pass = new AuroraPass(shader);
            Cam.depthTextureMode |= DepthTextureMode.Depth;
            sky = RenderSettings.skybox;
            skyHorizon = sky.GetColor("_Horizon");
            skyZenith = sky.GetColor("_Zenith");
            ambient = RenderSettings.ambientLight;
            sunIntensity = Sun.intensity;
        }

        // The world as the sun at that elevation leaves it: the sky, the ambient and the sun scaled down by the night gate (a deep night
        // keeps 2 % of the day's light).
        protected void SetSun(float sunElevationDeg)
        {
            float night = AuroraModel.NightGate(sunElevationDeg);
            float k = Mathf.Lerp(1f, 0.02f, night);
            sky.SetColor("_Horizon", skyHorizon * k);
            sky.SetColor("_Zenith", skyZenith * k);
            RenderSettings.ambientLight = ambient * k;
            Sun.intensity = sunIntensity * k;
        }

        // The state of the aurora for a forced activity and sun elevation (the model's own functions).
        internal static AuroraFrame Forced(float activity, float sunElevationDeg, float strongEvent = 0f)
        {
            var f = new AuroraFrame();
            f.Activity = activity;
            f.Event = strongEvent;
            f.Night = AuroraModel.NightGate(sunElevationDeg);
            f.RingLatDeg = AuroraModel.RingLatitudeDeg(activity, strongEvent, 60f);
            f.WidthDeg = AuroraModel.WidthDeg(activity);
            f.Intensity = f.Night * AuroraModel.ActivityRamp(activity);
            f.Draw = f.Intensity > 0.002f;
            return f;
        }

        // The yaw that faces the oval: north (0) while the oval is poleward of the camera, south (180) while it is equatorward.
        internal static float YawFor(AuroraFrame frame, float cameraLatDeg)
        {
            return AuroraModel.PoseYawDeg(frame.RingLatDeg, cameraLatDeg);
        }

        // The camera at the scene's start place, looking along the yaw (0 is north, towards +z) and up by the elevation in degrees.
        protected void Look(float yawDeg, float elevationDeg)
        {
            Cam.transform.position = new Vector3(2000f, 120f, -800f);
            Cam.transform.rotation = Quaternion.Euler(-elevationDeg, yawDeg, 0f);
        }

        // Puts the shell on the camera for the frame (or takes it off with draw false). The camera stands at latitude latDeg of the fake
        // planet: its pole axis leans from the local up by the colatitude towards +z.
        internal void Shell(bool on, AuroraFrame frame, float latDeg, bool depthTest, AuroraLook look)
        {
            if (shell != null)
            {
                Cam.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, shell);
                shell.Release();
                shell = null;
            }
            if (!on) return;
            float lat = latDeg * Mathf.Deg2Rad;
            var draw = new AuroraDraw
            {
                CameraPosition = Cam.transform.position,
                PlanetCentre = new Vector3(0f, -(PlanetRadius + Cam.transform.position.y), 0f),
                PlanetRadius = PlanetRadius,
                Pole = new Vector3(0f, Mathf.Sin(lat), Mathf.Cos(lat)),
                RefA = Vector3.right,
                BaseAltitude = AtmosphereDepth * look.BaseShare,
                TopAltitude = AtmosphereDepth,
                Frame = frame,
                Look = look,
                Colour = new Color(0.2f, 1f, 0.5f, 1f),
                Seconds = 1234.5,
                FovYRad = Cam.fieldOfView * Mathf.Deg2Rad,
                WidthPx = Width,
                HeightPx = Height,
                // The player's depth is the engine's global texture (the forward path fills it); the game's sky test is the series' business.
                SkyTest = depthTest ? AuroraSkyTest.CopyGlobal : AuroraSkyTest.None
            };
            shell = new CommandBuffer { name = "aurora shell" };
            Pass.Record(shell, ref draw);
            Cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, shell);
        }

        // Writes the target as a PNG into the run's output folder (call after WaitForEndOfFrame): the coded values times gain, clamped.
        internal void SavePicture(string name, float gain)
        {
            if (string.IsNullOrEmpty(Output)) return;
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = Target;
            var read = new Texture2D(Target.width, Target.height, TextureFormat.RGBAHalf, false, true);
            read.ReadPixels(new Rect(0, 0, Target.width, Target.height), 0, 0);
            RenderTexture.active = previous;
            Color[] pixels = read.GetPixels();
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color(Mathf.Clamp01(pixels[i].r * gain), Mathf.Clamp01(pixels[i].g * gain), Mathf.Clamp01(pixels[i].b * gain), 1f);
            var png = new Texture2D(Target.width, Target.height, TextureFormat.RGB24, false, false);
            png.SetPixels(pixels);
            System.IO.Directory.CreateDirectory(Output);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(Output, name + ".png"), png.EncodeToPNG());
            UnityEngine.Object.Destroy(read);
            UnityEngine.Object.Destroy(png);
        }

        protected IEnumerator Grab(Action<LumaFrame> done)
        {
            yield return new WaitForEndOfFrame();
            done(LumaFrame.Read(Target));
        }
    }
}
