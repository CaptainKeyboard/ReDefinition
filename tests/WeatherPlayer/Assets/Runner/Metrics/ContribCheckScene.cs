using System.Collections;
using UnityEngine;

namespace ReDefinition.WeatherPlayer
{
    // Checks the contribution mask (unity/Assets/ReDefinitionAtmospheres/Include/AtmoContribution.cginc) against planted pixels: three
    // flat quads cover exactly 200 x 100, 30 x 30 and 40 x 40 pixels of the frame; the third contributes 0. With the mask on the
    // pixels contributed must be 20900; with the mask off all three draw their normal colour (22500). The same count read from the
    // saved frame by tools/atmo_ab.py contrib gives the same number.
    //
    //   -mode scene -scene contrib-check
    [WeatherScene("contrib-check")]
    public sealed class ContribCheckScene : WeatherSceneBase
    {
        private const float Distance = 100f;

        private void Quad(int centreX, int widthPx, int heightPx, float contribution, float featureId, Color colour)
        {
            // Pixel size at the distance; a centre on a pixel boundary and even sizes put every edge on a pixel boundary.
            float pixel = 2f * Distance * Mathf.Tan(Cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Height;
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Object.Destroy(quad.GetComponent<Collider>());
            quad.layer = 31;
            quad.transform.position = Cam.transform.position + Vector3.forward * Distance + Vector3.right * ((centreX - Width / 2) * pixel);
            quad.transform.localScale = new Vector3(widthPx * pixel, heightPx * pixel, 1f);
            var material = new Material(Shader.Find("Hidden/ReDefinition/ContribCheck"));
            material.SetColor("_Colour", colour);
            material.SetFloat("_Contribution", contribution);
            material.SetFloat("_FeatureId", featureId);
            quad.GetComponent<Renderer>().sharedMaterial = material;
        }

        protected override void BuildContent()
        {
            Cam.transform.position = new Vector3(0f, 5000f, 0f);
            Cam.transform.rotation = Quaternion.identity;
            Cam.clearFlags = CameraClearFlags.SolidColor;
            Cam.backgroundColor = Color.black;
            Cam.cullingMask = 1 << 31;
            Quad(Width / 2, 200, 100, 1f, 15f, new Color(0.5f, 0.5f, 0.5f));
            Quad(Width / 2 + 300, 30, 30, 1f, 1f, new Color(0.5f, 0.5f, 0.5f));
            Quad(Width / 2 - 300, 40, 40, 0f, 2f, new Color(0.5f, 0.5f, 0.5f));
        }

        private static int Count(LumaFrame f)
        {
            int n = 0;
            foreach (float v in f.Y)
                if (v > 0.01f) n++;
            return n;
        }

        public override IEnumerator Run()
        {
            Shader.SetGlobalFloat("_AtmoDebug", 1f);
            yield return Frames(3);
            yield return new WaitForEndOfFrame();
            LumaFrame mask = LumaFrame.Read(Target);
            Sink.Reference("contrib_pixels_mask_on", Count(mask), 200 * 100 + 30 * 30, 0.0, 0.5);
            Shader.SetGlobalFloat("_AtmoDebug", 0f);
            yield return Frames(3);
            yield return new WaitForEndOfFrame();
            LumaFrame normal = LumaFrame.Read(Target);
            Sink.Reference("contrib_pixels_mask_off", Count(normal), 200 * 100 + 30 * 30 + 40 * 40, 0.0, 0.5);
            Sink.Flag("contrib_zero_quad_absent", Count(mask) < Count(normal));
        }
    }
}
