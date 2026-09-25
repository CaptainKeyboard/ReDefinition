using System.Collections;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.MotionVectorCheck
{
    // Hidden/ReDefinition/VesselShadow as VesselShadowLayer in the plugin draws it, over a
    // red block casting its shadow onto a white ground under a directional light with
    // shadows. The matrices and passes follow VesselShadowLayer.Record and Compose.
    //
    // chase: the block and the camera move together, as a camera following a vessel; the
    // shadow stands on the screen while the ground runs, so the weight must choose the
    // shadow's own place. parked: the block stands, the camera moves; the shadow moves with
    // the ground, so the weight must leave it to the motion vectors. One line each: the
    // ground pixels in the block's shadow (Unity's mask), those the pass found in it, those
    // it found outside it, the mean choice over the shadow, and the shadowed ground's
    // brightness before and after the composition against the same frame rendered without
    // the block's shadow.
    public partial class MotionVectorProbe
    {
        private const int LightMapSize = 1024;

        private IEnumerator ShadowCases()
        {
            sphere.SetActive(false);
            foreach (Light other in FindObjectsOfType<Light>()) other.enabled = false;
            QualitySettings.shadows = ShadowQuality.All;
            QualitySettings.shadowDistance = 100f;

            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.transform.localScale = new Vector3(20f, 1f, 20f);
            ground.GetComponent<Renderer>().sharedMaterial = new Material(surface) { color = Color.white };
            GameObject block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.transform.position = new Vector3(0f, 1.5f, 0f);
            block.transform.localScale = new Vector3(2f, 0.4f, 3f);
            block.GetComponent<Renderer>().sharedMaterial = new Material(surface) { color = Color.red };
            Light sun = new GameObject("shadow light").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.8f;
            sun.transform.rotation = Quaternion.Euler(55f, 30f, 0f);

            RenderTexture mask = new RenderTexture(Width, Height, 0, RenderTextureFormat.R8);
            CommandBuffer maskCopy = new CommandBuffer { name = "MotionVectorCheck shadow mask" };
            maskCopy.Blit(BuiltinRenderTextureType.CurrentActive, mask);
            sun.AddCommandBuffer(LightEvent.AfterScreenspaceMask, maskCopy);

            cam.transform.position = block.transform.position + new Vector3(0f, 4f, -7f);
            cam.transform.LookAt(block.transform.position + new Vector3(0f, -1f, 0f));
            for (int i = 0; i < StillFrames; i++) yield return null;

            Material material = new Material(vesselShadow);
            // chase: 0.4 m a frame for block and camera; parked: the camera alone.
            yield return ShadowCase("shadowChase", material, sun, mask, block, true);
            for (int i = 0; i < StillFrames; i++) yield return null;
            yield return ShadowCase("shadowParked", material, sun, mask, block, false);

            sun.RemoveCommandBuffer(LightEvent.AfterScreenspaceMask, maskCopy);
            Destroy(ground);
            Destroy(block);
        }

        private IEnumerator ShadowCase(string label, Material material, Light sun, RenderTexture mask,
                                       GameObject block, bool withBlock)
        {
            Matrix4x4 previousViewProjection = ViewProjection();
            Renderer blockRenderer = block.GetComponent<Renderer>();
            Matrix4x4 previousModel = blockRenderer.localToWorldMatrix;
            Vector3 step = new Vector3(0.4f, 0f, 0f);
            if (withBlock) block.transform.position += step;
            cam.transform.position += step;
            yield return new WaitForEndOfFrame();

            // As VesselShadowLayer.Record: the light map over the caster's bounds.
            Vector3 towardsLight = -sun.transform.forward;
            Bounds bounds = blockRenderer.bounds;
            float radius = bounds.extents.magnitude + 1f;
            Vector3 eye = bounds.center + towardsLight * (radius + 2f);
            Matrix4x4 lightView = Matrix4x4.Scale(new Vector3(1f, 1f, -1f))
                                  * Matrix4x4.TRS(eye, Quaternion.LookRotation(-towardsLight), Vector3.one).inverse;
            Matrix4x4 lightProjection = Matrix4x4.Ortho(-radius, radius, -radius, radius, 0.05f, 2f * radius + 4f);
            RenderTexture lightMap = new RenderTexture(LightMapSize, LightMapSize, 24, RenderTextureFormat.ARGBFloat)
            {
                filterMode = FilterMode.Point,
            };
            RenderTexture weight = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGBHalf)
            {
                filterMode = FilterMode.Bilinear,
            };
            Matrix4x4 viewProjection = ViewProjection();

            CommandBuffer draw = new CommandBuffer { name = "MotionVectorCheck vessel shadow" };
            draw.SetRenderTarget(lightMap);
            draw.ClearRenderTarget(true, true, new Color(0f, 0f, 0f, 1e9f));
            draw.SetGlobalMatrix("_VesselShadowLightRaster", GL.GetGPUProjectionMatrix(lightProjection, true) * lightView);
            draw.SetGlobalVector("_VesselShadowLight",
                new Vector4(towardsLight.x, towardsLight.y, towardsLight.z, Vector3.Dot(eye, towardsLight)));
            draw.SetGlobalMatrix("_VesselShadowPreviousModel", previousModel);
            draw.DrawRenderer(blockRenderer, material, 0, 0);
            draw.SetGlobalTexture("_VesselShadowDepth", sceneDepth);
            draw.SetGlobalTexture("_VesselShadowMotion", unityMotion);
            draw.SetGlobalTexture("_VesselShadowMask", mask);
            draw.SetGlobalTexture("_VesselShadowLightMap", lightMap);
            draw.SetGlobalMatrix("_VesselShadowRasterInverse",
                (GL.GetGPUProjectionMatrix(cam.projectionMatrix, false) * cam.worldToCameraMatrix).inverse);
            draw.SetGlobalMatrix("_VesselShadowViewProjection", viewProjection);
            draw.SetGlobalMatrix("_VesselShadowPreviousViewProjection", previousViewProjection);
            draw.SetGlobalMatrix("_VesselShadowLightViewProjection",
                GL.GetGPUProjectionMatrix(lightProjection, false) * lightView);
            draw.SetGlobalVector("_VesselShadowTexel", new Vector4(1f / Width, 1f / Height, Width, Height));
            draw.SetGlobalVector("_VesselShadowParams", new Vector4(sun.shadowStrength, 1f / LightMapSize, 0.15f, 3f * LightMapSize / (2f * radius)));
            draw.SetGlobalVector("_VesselShadowReach", new Vector4(0.5f * LightMapSize / (2f * radius), 0f, 0f, 0f));
            draw.Blit(sceneDepth, weight, material, 1);

            // As VesselShadowLayer.Compose, the display size the render size here.
            RenderTexture shadowed = Mipped(), lit = Mipped();
            RenderTexture composed = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGBHalf);
            draw.SetGlobalTexture("_VesselShadowWeight", weight);
            draw.Blit(color, shadowed, material, 2);
            draw.Blit(color, lit, material, 3);
            draw.GenerateMips(shadowed);
            draw.GenerateMips(lit);
            draw.SetGlobalTexture("_VesselShadowShadowed", shadowed);
            draw.SetGlobalTexture("_VesselShadowLit", lit);
            draw.SetGlobalVector("_VesselShadowMips", new Vector4(3f, 6f, 0f, 0f));
            draw.Blit(color, composed, material, 4);
            Graphics.ExecuteCommandBuffer(draw);

            Texture2D image = Read(color, TextureFormat.RGBAHalf);
            Texture2D shadowMask = Read(mask, TextureFormat.R8);
            Texture2D weights = Read(weight, TextureFormat.RGBAHalf);
            Texture2D after = Read(composed, TextureFormat.RGBAHalf);
            // The truth to compare with: the same frame, nothing moved, without the block's
            // shadow.
            blockRenderer.shadowCastingMode = ShadowCastingMode.Off;
            yield return new WaitForEndOfFrame();
            Texture2D unshadowed = Read(color, TextureFormat.RGBAHalf);
            blockRenderer.shadowCastingMode = ShadowCastingMode.On;
            int inShadow = 0, found = 0, outside = 0, litCount = 0;
            double choice = 0, shadowBefore = 0, shadowAfter = 0, shadowTruth = 0, litChange = 0;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    Color c = image.GetPixel(x, y);
                    // The ground: white, lit or shadowed; the block is red.
                    bool groundPixel = c.g > 0.02f && c.r < c.g * 1.5f;
                    if (!groundPixel) continue;
                    float m = shadowMask.GetPixel(x, y).r;
                    Color w = weights.GetPixel(x, y);
                    bool shadow = m < 0.5f;
                    if (shadow)
                    {
                        inShadow++;
                        if (w.g > 0.5f) found++;
                        choice += w.a;
                        shadowBefore += c.g;
                        shadowAfter += after.GetPixel(x, y).g;
                        shadowTruth += unshadowed.GetPixel(x, y).g;
                    }
                    else if (m > 0.98f)
                    {
                        if (w.g > 0.1f) outside++;
                        litCount++;
                        litChange += Mathf.Abs(after.GetPixel(x, y).g - c.g);
                    }
                }
            }
            double n = Mathf.Max(inShadow, 1), l = Mathf.Max(litCount, 1);
            result.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3} {4} {5} {6} {7}",
                label, inShadow, found, outside, choice / n, shadowBefore / Mathf.Max((float)shadowTruth, 1e-6f),
                shadowAfter / Mathf.Max((float)shadowTruth, 1e-6f), litChange / l));
            lightMap.Release();
            weight.Release();
            shadowed.Release();
            lit.Release();
            composed.Release();
        }

        private static RenderTexture Mipped()
        {
            RenderTexture texture = new RenderTexture(Width / 2, Height / 2, 0, RenderTextureFormat.ARGBHalf)
            {
                useMipMap = true,
                autoGenerateMips = false,
                filterMode = FilterMode.Bilinear,
            };
            texture.Create();
            return texture;
        }
    }
}
