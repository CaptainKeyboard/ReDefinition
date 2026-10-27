using System;
using System.Collections.Generic;
using ReDefinition.Core;
using ReDefinition.Proxy;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Raytracing
{
    // The part of RtCamera that handles the sun: the mask flag on its light, the shadow cascades held while the rays make
    // its shadows, and the read-back of how Unity fades its shadow out.
    internal sealed partial class RtCamera
    {
        // The mask flag on the sun's light, moved when the sun is another light now.
        // While the raytraced visibility makes the sun's shadows, they take all of its light,
        // as a shadow does. The traced sky light fills them (KSP's sun keeps a quarter of its
        // light in its shadows otherwise, shadow strength 0.75). It is set back when the mask moves
        // or the raytracing goes.
        private void MaskSun(Light light)
        {
            if (light == maskedLight)
            {
                if (light != null && maskedStrength >= 0f)
                {
                    float wanted = Settings.Shadows ? 1f : maskedStrength;
                    if (light.shadowStrength != wanted) light.shadowStrength = wanted;
                }
                KeepCascades(light != null && Settings.Shadows);
                return;
            }
            UnmaskSun();
            maskedLight = light;
            Shader.SetGlobalFloat(RtSunMask, 0f);
            if (light != null && maskOn != null)
            {
                light.AddCommandBuffer(LightEvent.BeforeScreenspaceMask, maskOn);
                light.AddCommandBuffer(LightEvent.AfterScreenspaceMask, maskOff);
                if (fadeGrab != null) light.AddCommandBuffer(LightEvent.AfterScreenspaceMask, fadeGrab);
                maskedStrength = light.shadowStrength;
                if (Settings.Shadows) light.shadowStrength = 1f;
            }
        }

        // The masked light's own shadow strength, set back by UnmaskSun; -1 for none held.
        private float maskedStrength = -1f;

        // While the rays make the sun's shadows, the shadow maps are used only where the
        // raytraced frame did not see a point (RtShadow, ReDefinitionScreenSpaceShadows).
        // Scatterer's godrays and caustics, EVE's cloud shadows and the vessel's
        // shadow for frame generation use them too. Two cascades in place of four took 1.4 ms of the
        // GPU's frame off at the pad, with the 3D scene unchanged against two shots
        // without a change (measured 2026-10-19). The distance and
        // the resolution stay Scatterer's.
        private const int RaytracedCascades = 2;
        // The count the cascades had before they were held at RaytracedCascades; -1 for none held. They are set back
        // when the rays stop, where the value is still the one set here. A count set meanwhile, the player's shadow
        // quality in KSP's settings (KspBehaviour.ApplyQuality), is what comes back then: more
        // than two is held again, one stays as it is (two cannot be told from the count
        // set here).
        private int heldCascades = -1;

        // Holds the shadow cascades at RaytracedCascades while the rays make the sun's shadows, and sets them back after.
        private void KeepCascades(bool raytraced)
        {
            if (raytraced)
            {
                int now = QualitySettings.shadowCascades;
                if (now == RaytracedCascades && heldCascades >= 0) return;
                if (now <= RaytracedCascades)
                {
                    heldCascades = -1;
                    return;
                }
                heldCascades = now;
                QualitySettings.shadowCascades = RaytracedCascades;
                return;
            }
            if (heldCascades < 0) return;
            if (QualitySettings.shadowCascades == RaytracedCascades) QualitySettings.shadowCascades = heldCascades;
            heldCascades = -1;
        }

        // Reads back Unity's shadow fade of the frame before, from the start of this one,
        // where the GPU has its texels after that frame. This frame's is written in forward
        // rendering, where Unity fades the screen-space shadows in the surfaces' own passes.
        // Unity's deferred lighting fades them itself, and the forward light there takes
        // the surface from the G-buffer.
        private void GrabShadowFade()
        {
            if (fadeGrab == null) return;
            if (fadeGrabbed && !fadeReading && SystemInfo.supportsAsyncGPUReadback)
            {
                fadeReading = true;
                fadeReadView = fadeGrabbedView;
                AsyncGPUReadback.Request(fadeTexture, 0, fadeDone);
            }
            fadeGrab.Clear();
            fadeGrabbed = maskedLight != null && cam.actualRenderingPath != RenderingPath.DeferredShading;
            if (!fadeGrabbed)
            {
                fadeScale = Vector4.zero;
                return;
            }
            fadeGrab.Blit((Texture)null, fadeTexture, capture, ShadowFadePass);
            fadeGrabbedView = cam.worldToCameraMatrix;
        }

        // _LightShadowData's scale and bias, and unity_ShadowFadeCenterAndType's centre in
        // the camera's frame as it was then (UnityShadowLibrary.cginc).
        private void ShadowFadeRead(AsyncGPUReadbackRequest request)
        {
            fadeReading = false;
            if (request.hasError || fadeGrab == null) return;
            Unity.Collections.NativeArray<Vector4> texels = request.GetData<Vector4>();
            if (texels.Length < 2) return;
            Vector4 data = texels[0], centre = texels[1];
            Vector3 local = fadeReadView.MultiplyPoint3x4(new Vector3(centre.x, centre.y, centre.z));
            fadeCentre = new Vector4(local.x, local.y, local.z, centre.w);
            fadeScale = new Vector4(data.z, data.w, 0f, 0f);
        }

        // Takes the mask's flags off the sun's light and gives it back its shadow strength and cascades.
        private void UnmaskSun()
        {
            KeepCascades(false);
            if (maskedLight != null)
            {
                maskedLight.RemoveCommandBuffer(LightEvent.BeforeScreenspaceMask, maskOn);
                maskedLight.RemoveCommandBuffer(LightEvent.AfterScreenspaceMask, maskOff);
                if (fadeGrab != null) maskedLight.RemoveCommandBuffer(LightEvent.AfterScreenspaceMask, fadeGrab);
                if (maskedStrength >= 0f) maskedLight.shadowStrength = maskedStrength;
            }
            maskedLight = null;
            maskedStrength = -1f;
        }
    }
}
