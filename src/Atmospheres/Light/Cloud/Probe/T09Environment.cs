using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Atmospheres
{
    // What series K-T2d logs about the light and the target the own cloud pass draws into, at the first drawn frame and every 30 s (the questions of
    // K-T2-black-clouds-diagnosis.md, "One game start would settle the rest"): the sun light and the other directional lights, the frame's sun colour and direction,
    // the ambient light, Scatterer, the camera's HDR flag and target format, the colour space, the camera's distance from the planet's centre against the vessel's height,
    // and the composition the pass uses (event, buffer, blend). Engine code: only the game calls it; the walk-through test uses the fake world's own lines.
    internal static class T09Environment
    {
        private static string F(float v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }
        private static string F(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
        private static string C(Color c) { return "(" + F(c.r) + ", " + F(c.g) + ", " + F(c.b) + ") luminance " + F(Luminance(c)); }
        private static float Luminance(Color c) { return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b; }

        internal static List<KeyValuePair<string, string>> Read()
        {
            var lines = new List<KeyValuePair<string, string>>();
            OwnCloudModule own = OwnCloudModule.Current;
            LayerFrame f = own != null ? own.Frame : null;
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            CelestialBody body = FlightGlobals.currentMainBody;
            Vessel vessel = FlightGlobals.ActiveVessel;

            Light sun = Sun.Instance != null ? Sun.Instance.sunLight : null;
            lines.Add(Line("sunLight", sun == null ? "none (Sun.Instance.sunLight is null)" : Describe(sun)));
            var others = new StringBuilder();
            int count = 0;
            foreach (Light l in UnityEngine.Object.FindObjectsOfType<Light>())
            {
                if (l == null || l.type != LightType.Directional || !l.enabled || !l.gameObject.activeInHierarchy || ReferenceEquals(l, sun)) continue;
                if (count++ > 0) others.Append(" | ");
                others.Append(Describe(l));
            }
            lines.Add(Line("otherDirectionalLights", count == 0 ? "none" : count + ": " + others));

            if (f != null)
            {
                Vector3 up = cam != null ? (cam.transform.position - f.PlanetCenter).normalized : Vector3.up;
                float elevation = Mathf.Asin(Mathf.Clamp(Vector3.Dot(f.TowardsSun.normalized, up), -1f, 1f)) * Mathf.Rad2Deg;
                lines.Add(Line("frameSun", "SunColour " + C(f.SunColour) + ", TowardsSun " + f.TowardsSun.ToString("0.000") + ", elevation over the local up " + F(elevation)
                               + " degrees, Light.SunAirHeightM " + F(f.Light.SunAirHeightM)));
                lines.Add(Line("frameAmbient", "AmbientTop " + C(f.AmbientTop) + ", AmbientBottom " + C(f.AmbientBottom)));
            }
            lines.Add(Line("renderSettingsAmbient", "mode " + RenderSettings.ambientMode + ", ambientLight " + C(RenderSettings.ambientLight) + ", sky " + C(RenderSettings.ambientSkyColor)
                           + ", equator " + C(RenderSettings.ambientEquatorColor) + ", ground " + C(RenderSettings.ambientGroundColor) + ", intensity " + F(RenderSettings.ambientIntensity)));
            lines.Add(Line("scatterer", "loaded " + EveRenderCompat.ScattererLoaded + ", cloud.sunAirWithScatterer " + Tuning.Flag(OwnCloudModule.TuneSunAirWithScatterer, false)));
            lines.Add(Line("colourSpace", "active " + QualitySettings.activeColorSpace + ", desired " + QualitySettings.desiredColorSpace + ", HDR in the graphics tier "
                           + SafeHdr()));

            if (cam != null)
            {
                RenderTexture target = cam.targetTexture;
                lines.Add(Line("camera", cam.name + ": allowHDR " + cam.allowHDR + ", allowMSAA " + cam.allowMSAA + ", rendering path " + cam.actualRenderingPath + ", pixels " + cam.pixelWidth + "x" + cam.pixelHeight
                               + ", depthTextureMode " + cam.depthTextureMode + ", field of view " + F(cam.fieldOfView) + ", clear " + cam.clearFlags));
                lines.Add(Line("targetFormat", target == null ? "the camera draws into the back buffer (no target texture)"
                               : target.name + ": " + target.format + ", read-write " + (target.sRGB ? "sRGB" : "linear/none") + ", " + target.width + "x" + target.height + ", antiAliasing " + target.antiAliasing
                                 + ", useMipMap " + target.useMipMap));
                lines.Add(Line("cameraBuffers", Buffers(cam)));
                lines.Add(Line("cameraComponents", Components(cam)));
                if (body != null)
                {
                    Vector3d camPos = cam.transform.position;
                    double fromCentre = (camPos - body.position).magnitude;
                    string passCentre = f != null ? F((double)((Vector3d)cam.transform.position - (Vector3d)f.PlanetCenter).magnitude - body.Radius) : "n/a";
                    lines.Add(Line("geometry", "camera minus planet centre minus radius " + F(fromCentre - body.Radius) + " m (the pass's centre: " + passCentre + " m), radius " + F(body.Radius)
                                   + " m, vessel altitude " + (vessel != null ? F(vessel.altitude) : "n/a") + " m"));
                }
            }
            if (own != null)
            {
                lines.Add(Line("composition", "event " + own.AttachedEvent + " on " + (own.AttachedCamera != null ? own.AttachedCamera.name : "no camera") + "; the march (resolution "
                               + (f != null ? F(f.Resolution) : "n/a") + " of the camera) is composited by pass 1 of the layer shader into BuiltinRenderTextureType.CameraTarget (the camera's target"
                               + (cam != null && cam.targetTexture != null ? " texture" : ", the back buffer") + ") with Blend One OneMinusSrcAlpha: rgb + dst * (1 - alpha), the march's radiance raw, "
                               + "no exposure, no gamma step; mode " + own.Stats.Mode + ", drawn frames " + own.Stats.FramesDrawn));
            }
            return lines;
        }

        private static KeyValuePair<string, string> Line(string key, string value)
        {
            return new KeyValuePair<string, string>(key, value);
        }

        private static string SafeHdr()
        {
            try { return Graphics.activeTier + " tier, " + (SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) ? "ARGBHalf supported" : "ARGBHalf not supported"); }
            catch (Exception e) { return "unknown (" + e.GetType().Name + ")"; }
        }

        private static string Describe(Light l)
        {
            return l.name + ": enabled " + l.enabled + ", type " + l.type + ", intensity " + F(l.intensity) + ", colour " + C(l.color) + ", shadows " + l.shadows + " (strength " + F(l.shadowStrength)
                   + "), cullingMask " + l.cullingMask + ", forward direction " + l.transform.forward.ToString("0.000");
        }

        private static string Buffers(Camera cam)
        {
            var sb = new StringBuilder();
            foreach (CameraEvent e in new[] { CameraEvent.BeforeDepthTexture, CameraEvent.AfterDepthTexture, CameraEvent.BeforeForwardOpaque, CameraEvent.AfterForwardOpaque, CameraEvent.BeforeForwardAlpha,
                                              CameraEvent.AfterForwardAlpha, CameraEvent.BeforeImageEffectsOpaque, CameraEvent.BeforeImageEffects, CameraEvent.AfterImageEffects, CameraEvent.AfterEverything })
            {
                CommandBuffer[] buffers = cam.GetCommandBuffers(e);
                if (buffers.Length == 0) continue;
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(e).Append(": ");
                for (int i = 0; i < buffers.Length; i++) sb.Append(i > 0 ? ", " : "").Append(buffers[i].name);
            }
            return sb.Length == 0 ? "none" : sb.ToString();
        }

        private static string Components(Camera cam)
        {
            var sb = new StringBuilder();
            foreach (Behaviour b in cam.GetComponents<Behaviour>())
            {
                if (b == null || !b.enabled) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(b.GetType().Name);
            }
            return sb.ToString();
        }
    }
}
