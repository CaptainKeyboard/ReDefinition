using System;
using System.Collections;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Probe
{
    // -redefinitionProbeShotVariant <name>: the probe's flight with one thing changed,
    // from 5 s into it. Its shot (-redefinitionProbeShot) is compared with one
    // without the change, "none", at the same place and moment
    // (measured 2026-10). The change is only for that run.
    // The variant cascades1Low has one cascade at low resolution, the shadow distance kept.
    // The variant distanceHalf halves the shadow distance.
    // The variant shadowsMinimal has one cascade, 200 m, low resolution: where only the raytracing
    // shades the sun, and where the shadow maps still show.
    // The variant noDeferredAo takes Deferred's ambient occlusion command buffers off the cameras.
    // The variant noTufxAo switches TUFX's ambient occlusion off in its volumes' profiles.
    // The variant portraitLayers has the crew portraits' cameras draw only the layers their
    // interiors and kerbals are on, not every layer.
    // The variant portraitShadows sets the shadow distance to 10 m while a crew portrait renders.
    // The variant noSun sets KSP's sun lights at intensity 0.
    // The variant noLocalLights switches every point and spot light off, each logged.
    // The variant detailAsDiffuse draws KSP/Scenery/Diffuse Detail with KSP/Scenery/Diffuse.
    // The variant flagProbes sets the renderers named flag to blend the light probes.
    // The variant noFlags hides the renderers named flag.
    internal static class ShotVariant
    {
        // The variant the switch names, changed once in the flight, then two seconds to settle.
        internal static IEnumerator Apply()
        {
            string name = CommandLine.After("-redefinitionProbeShotVariant");
            if (name == null) yield break;
            string done;
            try
            {
                done = Change(name);
            }
            catch (Exception e)
            {
                done = "failed: " + e.Message;
            }
            Debug.Log(Log.Tag + " Probe: shot variant " + name + ": " + done + ".");
            // Two seconds for the temporal passes -- the upscaler, the denoiser, EVE's
            // clouds -- to settle on the change.
            yield return new WaitForSeconds(2f);
        }

        // Makes the named change and says what it did, for the log.
        private static string Change(string name)
        {
            switch (name)
            {
                case "cascades1Low":
                    QualitySettings.shadowCascades = 1;
                    QualitySettings.shadowResolution = ShadowResolution.Low;
                    foreach (Light light in UnityEngine.Object.FindObjectsOfType<Light>())
                        if (light.type == LightType.Directional && light.shadowCustomResolution > 0) light.shadowCustomResolution = 1024;
                    return "1 cascade, low resolution, the distance kept";
                case "distanceHalf":
                    QualitySettings.shadowDistance *= 0.5f;
                    return QualitySettings.shadowDistance.ToString("0") + " m";
                case "shadowsMinimal":
                    QualitySettings.shadowCascades = 1;
                    QualitySettings.shadowDistance = 200f;
                    QualitySettings.shadowResolution = ShadowResolution.Low;
                    return "1 cascade, 200 m, low resolution";
                case "noDeferredAo":
                    return RemoveBuffers("Ambient Occlusion") + " command buffers taken off";
                case "noTufxAo":
                    return TufxAmbientOcclusionOff() + " profiles' ambient occlusion off";
                case "portraitLayers":
                    return PortraitLayers();
                case "portraitShadows":
                    Camera.onPreCull += ShortShadows;
                    Camera.onPostRender += ShadowsBack;
                    return "10 m while a portrait renders";
                case "noSun":
                    Camera.onPreCull += SunOff;
                    return "the sun's light at intensity 0 before every camera";
                case "noLocalLights":
                    return LocalLightsOff();
                case "detailAsDiffuse":
                    return DetailAsDiffuse();
                case "flagProbes":
                    return FlagProbes();
                case "noFlags":
                    int hidden = 0;
                    foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
                        if (renderer.name.IndexOf("flag", StringComparison.OrdinalIgnoreCase) >= 0 && renderer.enabled)
                        {
                            renderer.enabled = false;
                            hidden++;
                        }
                    return hidden + " renderers named flag hidden";
                default:
                    return "unknown, nothing changed";
            }
        }

        // The layers the renderers under the vessel's internal spaces and the IVA kerbals
        // are on, and those cameras' masks set to them.
        private static string PortraitLayers()
        {
            int layers = 0;
            foreach (InternalModel model in UnityEngine.Object.FindObjectsOfType<InternalModel>())
                foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
                    layers |= 1 << r.gameObject.layer;
            foreach (Kerbal kerbal in UnityEngine.Object.FindObjectsOfType<Kerbal>())
                foreach (Renderer r in kerbal.GetComponentsInChildren<Renderer>(true))
                    layers |= 1 << r.gameObject.layer;
            // Each update copies the kerbal's own camera's mask to the seat's portrait
            // camera (Kerbal.kerbalSeatCamUpdate, decompiled): set there.
            int cameras = 0;
            foreach (Kerbal kerbal in UnityEngine.Object.FindObjectsOfType<Kerbal>())
            {
                Camera own = HarmonyLib.Traverse.Create(kerbal).Field("kerbalCam").GetValue<Camera>();
                if (own == null) continue;
                own.cullingMask = layers;
                cameras++;
            }
            var names = new System.Text.StringBuilder();
            for (int i = 0; i < 32; i++)
                if ((layers & (1 << i)) != 0) names.Append(i).Append(' ').Append(LayerMask.LayerToName(i)).Append(", ");
            return cameras + " portrait cameras to layers 0x" + layers.ToString("X8") + " (" + names + ")";
        }

        // The shadow distance held while a portrait renders with 10 m; -1 while none does.
        private static float heldDistance = -1f;

        // Whether a camera is one of the crew portraits' (pilotCamera...).
        private static bool IsPortrait(Camera camera)
        {
            return camera != null && camera.name.StartsWith("pilotCamera", StringComparison.Ordinal);
        }

        // KSP's sun lights (Sun.sunLight, the scaled space's and the IVA's), which KSP and
        // Parallax set again every frame: what of a surface's light is the sun's.
        private static void SunOff(Camera camera)
        {
            foreach (Light light in Light.GetLights(LightType.Directional, 0))
                if (light.name.IndexOf("Sun", StringComparison.OrdinalIgnoreCase) >= 0)
                    light.intensity = 0f;
        }

        // Every point and spot light switched off, each logged with its distance from the
        // camera: what of a surface's light is theirs.
        private static string LocalLightsOff()
        {
            Vector3 eye = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            int off = 0;
            foreach (Light light in UnityEngine.Object.FindObjectsOfType<Light>())
            {
                if (light.type != LightType.Point && light.type != LightType.Spot) continue;
                Debug.Log(Log.Tag + " Probe: local light " + light.name + " under "
                          + (light.transform.parent != null ? light.transform.parent.name : "nothing") + ", " + light.type + ", enabled "
                          + light.enabled + ", intensity " + light.intensity + ", range " + light.range + ", colour " + light.color
                          + ", mask " + light.cullingMask + ", render mode " + light.renderMode + ", "
                          + Vector3.Distance(eye, light.transform.position).ToString("0.0") + " m from the camera.");
                if (!light.enabled) continue;
                light.enabled = false;
                off++;
            }
            return off + " point and spot lights off";
        }

        // The materials on KSP/Scenery/Diffuse Detail, each logged with its shader's passes,
        // drawn with KSP/Scenery/Diffuse instead, their textures kept: what of their light
        // is that shader's.
        private static string DetailAsDiffuse()
        {
            Shader diffuse = Shader.Find("KSP/Scenery/Diffuse");
            if (diffuse == null) return "no KSP/Scenery/Diffuse";
            int swapped = 0;
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material == null || material.shader.name != "KSP/Scenery/Diffuse Detail") continue;
                    var passes = new System.Text.StringBuilder();
                    foreach (string pass in new[] { "FORWARD", "FORWARD_DELTA", "DEFERRED", "SHADOWCASTER", "META", "PREPASSBASE", "ALWAYS" })
                        passes.Append(' ').Append(pass).Append('=').Append(material.FindPass(pass));
                    Debug.Log(Log.Tag + " Probe: " + renderer.name + " under "
                              + (renderer.transform.parent != null ? renderer.transform.parent.name : "nothing") + ", " + material.name
                              + ": " + material.shader.passCount + " passes," + passes + ", RenderType " + material.GetTag("RenderType", false)
                              + ", textures " + string.Join(" ", material.GetTexturePropertyNames()) + ".");
                    material.shader = diffuse;
                    swapped++;
                }
            return swapped + " materials on KSP/Scenery/Diffuse";
        }

        // The renderers named Flag, each logged with how it takes its ambient light, then
        // set to blend the light probes as the scene around them does.
        private static string FlagProbes()
        {
            int changed = 0;
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (renderer.name.IndexOf("flag", StringComparison.OrdinalIgnoreCase) < 0) continue;
                Debug.Log(Log.Tag + " Probe: " + renderer.name + " under "
                          + (renderer.transform.parent != null ? renderer.transform.parent.name : "nothing") + ": light probes "
                          + renderer.lightProbeUsage + ", reflection probes " + renderer.reflectionProbeUsage + ", layer "
                          + renderer.gameObject.layer + ".");
                if (renderer.lightProbeUsage == LightProbeUsage.BlendProbes) continue;
                renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
                changed++;
            }
            return changed + " flags set to blend the light probes";
        }

        // Camera.onPreCull for portraitShadows: a portrait's camera renders with a shadow distance of 10 m.
        private static void ShortShadows(Camera camera)
        {
            if (!IsPortrait(camera) || heldDistance >= 0f) return;
            heldDistance = QualitySettings.shadowDistance;
            QualitySettings.shadowDistance = 10f;
        }

        // Camera.onPostRender for portraitShadows: the held shadow distance back after the portrait.
        private static void ShadowsBack(Camera camera)
        {
            if (!IsPortrait(camera) || heldDistance < 0f) return;
            QualitySettings.shadowDistance = heldDistance;
            heldDistance = -1f;
        }

        // Takes every command buffer whose name holds the keyword off every camera in a scene; how many.
        private static int RemoveBuffers(string keyword)
        {
            int removed = 0;
            foreach (Camera camera in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (camera == null || !camera.gameObject.scene.IsValid()) continue;
                foreach (CameraEvent at in (CameraEvent[])Enum.GetValues(typeof(CameraEvent)))
                    foreach (CommandBuffer buffer in camera.GetCommandBuffers(at))
                        if (buffer.name != null && buffer.name.Contains(keyword))
                        {
                            camera.RemoveCommandBuffer(at, buffer);
                            removed++;
                        }
            }
            return removed;
        }

        // Unity's post-processing stack, as TUFX loads it: each PostProcessVolume's
        // profile holds its effects' settings, AmbientOcclusion among them, each with an
        // "enabled" parameter whose value switches it.
        private static int TufxAmbientOcclusionOff()
        {
            int changed = 0;
            foreach (MonoBehaviour behaviour in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (behaviour == null || behaviour.GetType().Name != "PostProcessVolume") continue;
                foreach (string member in new[] { "sharedProfile", "profile" })
                {
                    object profile = Read(behaviour, member);
                    var settings = Read(profile, "settings") as IEnumerable;
                    if (settings == null) continue;
                    foreach (object effect in settings)
                    {
                        if (effect == null || effect.GetType().Name != "AmbientOcclusion") continue;
                        object enabled = Read(effect, "enabled");
                        FieldInfo value = enabled?.GetType().GetField("value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        FieldInfo overrides = enabled?.GetType().GetField("overrideState", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (value == null) continue;
                        value.SetValue(enabled, false);
                        if (overrides != null) overrides.SetValue(enabled, true);
                        changed++;
                    }
                }
            }
            return changed;
        }

        // A field's or unindexed property's value by name, public or not; null where there is none.
        private static object Read(object from, string member)
        {
            if (from == null) return null;
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo field = from.GetType().GetField(member, any);
            if (field != null) return field.GetValue(from);
            PropertyInfo property = from.GetType().GetProperty(member, any);
            return property != null && property.GetIndexParameters().Length == 0 ? property.GetValue(from, null) : null;
        }
    }
}
