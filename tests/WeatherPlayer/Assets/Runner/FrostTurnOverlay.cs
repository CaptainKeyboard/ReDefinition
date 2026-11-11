using ReDefinition.Atmospheres;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.WeatherPlayer
{
    // Draws the camera frost into the camera-turn run of the weather player (-mode turn, see tools/weather_metrics/frost.py), so that the
    // ghost metrics of the upscalers can be read with the frost on and off at identical state (package D37, review R2).
    //
    //   -frost host   the host composite: FrostPass.RecordHost on the finished image after the upscaler (WeatherPass.PostComposite), and on
    //                 each reference copy, so that the frame and its reference carry the same frost over different content.
    //   -frost quad   the fallback: FrostPass.DrawQuad on the camera, a scene-side overlay the upscaler gets as part of the picture.
    //   -frost 0 or absent: nothing is attached.
    //
    // The frost is a fixed state (exposure 0.7, 20 flakes, 12 drops), screen-fixed like the mod's composite, without motion vectors.
    internal sealed class FrostTurnOverlay : MonoBehaviour
    {
        private string mode;
        private WeatherPass pass;
        private Camera cam;
        private FrostPass frost;
        private FrostDraw draw;
        private CommandBuffer commands;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Attach()
        {
            string value = WeatherPass.Argument("-frost");
            if (value != "host" && value != "quad") return;
            var go = new GameObject("FrostTurnOverlay");
            go.AddComponent<FrostTurnOverlay>().mode = value;
            DontDestroyOnLoad(go);
        }

        private void Update()
        {
            if (pass != null) return;
            pass = FindObjectOfType<WeatherPass>();
            if (pass == null) return;
            cam = pass.GetComponent<Camera>();
            Shader shader = Shader.Find("ReDefinition/AtmospheresFrost");
            if (shader == null)
            {
                Debug.Log("WEATHERPLAYER FAIL  the frost shader is not in the player");
                return;
            }
            frost = new FrostPass(shader, FrostAtlas.MakeTexture(FrostAtlas.Build()));
            FrostTuning tuning = FrostTuning.Defaults();
            tuning.FlipY = FrostPass.ResolveFlipY(-1f);
            draw = new FrostDraw
            {
                Exposure = 0.7f,
                Growth = FrostShape.Growth(0.7f, ref tuning),
                Aspect = 16f / 9f,
                Light = 0.8f,
                QuadColour = Color.white,
                Flakes = new Vector4[CameraFrost.MaxFlakes],
                Drops = new Vector4[CameraFrost.MaxDrops],
                FlakeCount = 20,
                DropCount = 12,
                Gravity = new Vector2(0f, -1f),
                Tuning = tuning,
                FlipY = tuning.FlipY
            };
            for (int i = 0; i < 20; i++)
                draw.Flakes[i] = new Vector4(Mathf.Repeat(0.137f * i + 0.05f, 1f), Mathf.Repeat(0.291f * i, 1f), 0.004f + 0.0004f * (i % 8), i % 16 + 0.3f);
            for (int i = 0; i < 12; i++)
                draw.Drops[i] = new Vector4(Mathf.Repeat(0.211f * i + 0.03f, 1f), Mathf.Repeat(0.173f * i + 0.1f, 1f), 0.007f + 0.0003f * (i % 6), 0.05f);
            if (mode == "host")
            {
                commands = new CommandBuffer { name = "frost turn host" };
                pass.PostComposite = Compose;
            }
            Debug.Log("WEATHERPLAYER frost overlay: " + mode + ", exposure 0.7, 20 flakes, 12 drops, flipY " + tuning.FlipY);
        }

        private void LateUpdate()
        {
            if (mode != "quad" || frost == null || cam == null) return;
            draw.Aspect = cam.aspect;
            frost.DrawQuad(cam, ref draw);
        }

        private void Compose(RenderTexture image)
        {
            if (frost == null || image == null) return;
            FrostDraw d = draw;
            d.Aspect = image.width / (float)image.height;
            commands.Clear();
            frost.RecordHost(commands, image, image.width, image.height, ref d);
            Graphics.ExecuteCommandBuffer(commands);
        }
    }
}
