using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System;
using ReDefinition.Core;
using ReDefinition.Settings;
using UnityEngine.Rendering;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // TUFX's antialiasing (HostStack).
    internal static partial class HostStack
    {
        // report may be null -- when reasserting, nobody reads the text.
        private static void ApplyTufx(StringBuilder report)
        {
            Type profileType;
            object profile = TufxProfile(out profileType);
            if (profile == null)
            {
                if (report != null) report.Append("TUFX not installed.");
                return;
            }

            object mode;
            Type modeType;
            if (!TryGetAaMode(profile, profileType, "Antialiasing", out mode, out modeType))
            {
                if (report != null) report.Append("TUFX profile not readable.");
                return;
            }

            if (!tufxBackups.ContainsKey(profile))
            {
                object secondary;
                Type secondaryType;
                TryGetAaMode(profile, profileType, "SecondaryCameraAntialiasing",
                             out secondary, out secondaryType);
                tufxBackups[profile] = new TufxBackup { Primary = mode, Secondary = secondary };
            }

            // TUFX's antialiasing modes as PostProcessing v2 names them; without a
            // None there is nothing to set.
            if (!Enum.IsDefined(modeType, "None"))
            {
                if (report != null) report.Append("TUFX has no antialiasing mode None.");
                return;
            }
            object none = Enum.Parse(modeType, "None");
            tufxBackups[profile].Applied = none;
            TrySetAaMode(profile, profileType, "Antialiasing", none);
            TrySetAaMode(profile, profileType, "SecondaryCameraAntialiasing", none);

            // The profile alone is not enough: TUFX already copied it onto the
            // cameras' PostProcessLayers at scene start. Those have to be
            // touched as well, otherwise the change only takes effect after the
            // next scene change.
            int layers = SetLiveAntialiasingNone(none);
            if (report != null)
                report.Append("TUFX AA set to None (profile + ").Append(layers).Append(" layers).");
        }

        // TUFX's own pass over its cameras and the settings its profile implies
        // (TexturesUnlimitedFXLoader.RefreshCameras). Nothing where TUFX or the
        // method is not there: the layers have their own back already
        // (RestoreLiveAntialiasing).
        //
        // Not in a flight being torn down: TUFX takes its scene from
        // CameraManager.Instance there (Utils.GetCurrentScene,
        // GetTUFXSceneForCameraMode), which is gone by then. TUFX applies its
        // profile to the next scene's cameras as that scene loads
        // (TexturesUnlimitedFXLoader.onLevelLoaded).
        private static void RefreshTufxCameras()
        {
            if (HighLogic.LoadedScene == GameScenes.FLIGHT && CameraManager.Instance == null) return;
            Type loaderType = TypeLookup.Find(TufxLoaderTypeName);
            if (loaderType == null) return;
            object loader;
            if (!TryGet(null, loaderType, "INSTANCE", out loader) || loader == null) return;
            MethodInfo refresh = loaderType.GetMethod("RefreshCameras", BindingFlags.Instance | BindingFlags.Public
                                                                        | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (refresh == null) return;
            try
            {
                refresh.Invoke(loader, null);
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("tufx-refresh", "TUFX could not hand its cameras their antialiasing back ("
                                                      + CompatibilityLog.Reason(e) + ").");
            }
        }

        private static int SetLiveAntialiasingNone(object none)
        {
            if (none == null) return 0;
            // Layers of scenes since gone -- TUFX makes new ones with each -- let go.
            ForgetDestroyedLayers();

            int count = 0;
            Camera[] cameras = Camera.allCameras;
            for (int i = 0; i < cameras.Length; i++)
            {
                Component[] components = cameras[i].GetComponents<Component>();
                for (int j = 0; j < components.Length; j++)
                {
                    Component component = components[j];
                    // Missing scripts show up as null here.
                    if (component == null) continue;
                    if (component.GetType().Name != "PostProcessLayer") continue;
                    object before;
                    if (!liveAntialiasing.ContainsKey(component)
                        && TryGet(component, component.GetType(), "antialiasingMode", out before))
                        liveAntialiasing[component] = before;
                    if (TrySet(component, component.GetType(), "antialiasingMode", none)) count++;
                }
            }
            return count;
        }

        private static void ForgetDestroyedLayers()
        {
            foreach (Component layer in liveAntialiasing.Keys)
                if (layer == null) destroyedLayers.Add(layer);
            foreach (Component layer in destroyedLayers) liveAntialiasing.Remove(layer);
            destroyedLayers.Clear();
        }

        // What still has None, of the layers that are still there.
        private static void RestoreLiveAntialiasing()
        {
            foreach (KeyValuePair<Component, object> entry in liveAntialiasing)
            {
                Component layer = entry.Key;
                object now;
                if (layer == null || entry.Value == null
                    || !TryGet(layer, layer.GetType(), "antialiasingMode", out now)
                    || now == null || now.ToString() != "None")
                    continue;
                TrySet(layer, layer.GetType(), "antialiasingMode", entry.Value);
            }
            liveAntialiasing.Clear();
        }

        private static object TufxProfile(out Type profileType)
        {
            profileType = null;
            Type loaderType = TypeLookup.Find(TufxLoaderTypeName);
            if (loaderType == null) return null;

            object loader;
            if (!TryGet(null, loaderType, "INSTANCE", out loader) || loader == null) return null;

            object profile;
            if (!TryGet(loader, loaderType, "CurrentProfile", out profile) || profile == null) return null;

            profileType = profile.GetType();
            return profile;
        }

        // Antialiasing is a struct field on the profile. A write goes through the
        // box and back again; otherwise only a copy changes.
        private static bool TryGetAaMode(object profile, Type profileType, string fieldName,
                                         out object mode, out Type modeType)
        {
            mode = null;
            modeType = null;
            FieldInfo aaField = profileType.GetField(fieldName, Any);
            if (aaField == null) return false;
            object aa = aaField.GetValue(profile);
            if (aa == null) return false;
            FieldInfo modeField = aa.GetType().GetField("Mode", Any);
            if (modeField == null) return false;
            mode = modeField.GetValue(aa);
            modeType = modeField.FieldType;
            return true;
        }

        private static bool TrySetAaMode(object profile, Type profileType, string fieldName, object mode)
        {
            if (mode == null) return false;
            FieldInfo aaField = profileType.GetField(fieldName, Any);
            if (aaField == null) return false;
            object aa = aaField.GetValue(profile);
            if (aa == null) return false;
            FieldInfo modeField = aa.GetType().GetField("Mode", Any);
            if (modeField == null) return false;
            modeField.SetValue(aa, mode);
            aaField.SetValue(profile, aa);
            return true;
        }
    }
}
