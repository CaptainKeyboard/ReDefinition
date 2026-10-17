using System;
using System.Collections;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReDefinition.Probe
{
    // -redefinitionProbeShotVariant <name>: the probe's flight with one thing changed,
    // from 5 s into it, and its shot (-redefinitionProbeShot) to compare with one
    // without the change, "none", at the same place and moment
    // (docs/development/performance-findings.md). Only for that run.
    //   cascades2      two shadow cascades instead of the quality level's
    //   distanceHalf   the shadow distance halved
    //   shadowsMinimal one cascade, 200 m, low resolution: where only the raytracing
    //                  shades the sun, and where the shadow maps still show
    //   noDeferredAo   Deferred's ambient occlusion command buffers taken off the cameras
    //   noTufxAo       TUFX's ambient occlusion switched off in its volumes' profiles
    //   portraitLayers the crew portraits' cameras drawing only the layers their
    //                  interiors and kerbals are on, in place of every layer
    //   portraitShadows the shadow distance 10 m while a crew portrait renders
    internal static class ShotVariant
    {
        internal static IEnumerator Apply()
        {
            string name = SceneProbe.ArgumentAfter("-redefinitionProbeShotVariant");
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

        private static string Change(string name)
        {
            switch (name)
            {
                case "cascades2":
                    QualitySettings.shadowCascades = 2;
                    return "2 cascades";
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

        private static float heldDistance = -1f;

        private static bool IsPortrait(Camera camera)
        {
            return camera != null && camera.name.StartsWith("pilotCamera", StringComparison.Ordinal);
        }

        private static void ShortShadows(Camera camera)
        {
            if (!IsPortrait(camera) || heldDistance >= 0f) return;
            heldDistance = QualitySettings.shadowDistance;
            QualitySettings.shadowDistance = 10f;
        }

        private static void ShadowsBack(Camera camera)
        {
            if (!IsPortrait(camera) || heldDistance < 0f) return;
            QualitySettings.shadowDistance = heldDistance;
            heldDistance = -1f;
        }

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
