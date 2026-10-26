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

            HookTufxSave(profileType);
            object secondary;
            Type secondaryType;
            TryGetAaMode(profile, profileType, "SecondaryCameraAntialiasing", out secondary, out secondaryType);
            TufxBackup backup;
            if (!tufxBackups.TryGetValue(profile, out backup))
            {
                tufxBackups[profile] = new TufxBackup { Primary = mode, Secondary = secondary };
            }
            else if (backup.Applied != null)
            {
                // A mode chosen in TUFX's own window since, or read again from its file
                // (Reload Selected): the player's now, the one handed back.
                if (mode != null && !Equals(mode, backup.Applied)) backup.Primary = mode;
                if (secondary != null && !Equals(secondary, backup.Applied)) backup.Secondary = secondary;
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

        // TUFX's own save (TUFXProfile.SaveToDisk -- its window's Save Selected and
        // Save All) writes the profile's antialiasing into its file (SaveToNode). It
        // writes the player's modes: the profile holds them for the call, and None
        // again after. A profile changed in TUFX's window since it was set here holds
        // the player's mode already and is saved as it is.
        private static bool tufxSaveHookTried;

        // Installs the save hook on TUFX's profile type, once; without it a save while the upscaler runs writes None.
        private static void HookTufxSave(Type profileType)
        {
            if (tufxSaveHookTried || profileType == null) return;
            tufxSaveHookTried = true;
            MethodInfo save = profileType.GetMethod("SaveToDisk", BindingFlags.Instance | BindingFlags.Public,
                                                    null, Type.EmptyTypes, null);
            try
            {
                HarmonyHooks.Install("ReDefinition.TufxSave", typeof(HostStack), new[]
                {
                    new HarmonyHook(save, nameof(TufxSaveBefore), null, nameof(TufxSaveAfter)),
                });
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("tufx-save", "TUFX's save could not be hooked (" + CompatibilityLog.Reason(e)
                                                   + "): a profile saved in its window while the upscaler runs is"
                                                   + " saved with antialiasing None.");
            }
        }

        // Which of the two modes were swapped for the save: 1 the primary, 2 the
        // secondary.
        private static void TufxSaveBefore(object __instance, out int __state)
        {
            __state = 0;
            TufxBackup backup;
            if (__instance == null || !tufxBackups.TryGetValue(__instance, out backup) || backup.Applied == null) return;
            int swapped = 0;
            HarmonyHooks.Safely("tufx-save", "TUFX's profile could not be given its antialiasing back for its save", () =>
            {
                Type profileType = __instance.GetType();
                if (SwapAaMode(__instance, profileType, "Antialiasing", backup.Applied, backup.Primary)) swapped |= 1;
                if (SwapAaMode(__instance, profileType, "SecondaryCameraAntialiasing", backup.Applied, backup.Secondary))
                    swapped |= 2;
            });
            __state = swapped;
        }

        // After the save: None again on the modes TufxSaveBefore swapped, where the profile still holds the player's.
        private static Exception TufxSaveAfter(object __instance, int __state, Exception __exception)
        {
            if (__state == 0 || __instance == null) return __exception;
            TufxBackup backup;
            if (!tufxBackups.TryGetValue(__instance, out backup) || backup.Applied == null) return __exception;
            HarmonyHooks.Safely("tufx-save", "TUFX's profile could not be set to antialiasing None again after its save", () =>
            {
                Type profileType = __instance.GetType();
                if ((__state & 1) != 0) SwapAaMode(__instance, profileType, "Antialiasing", backup.Primary, backup.Applied);
                if ((__state & 2) != 0)
                    SwapAaMode(__instance, profileType, "SecondaryCameraAntialiasing", backup.Secondary, backup.Applied);
            });
            return __exception;
        }

        // `to` where the profile still holds `from`.
        private static bool SwapAaMode(object profile, Type profileType, string field, object from, object to)
        {
            object now;
            Type modeType;
            if (to == null || Equals(from, to) || !TryGetAaMode(profile, profileType, field, out now, out modeType)
                || !Equals(now, from))
                return false;
            return TrySetAaMode(profile, profileType, field, to);
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

        // Sets None on every PostProcessLayer of the enabled cameras and keeps what each held before, for
        // RestoreLiveAntialiasing. Returns how many layers were set.
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
                    // Kept anew where the layer holds another mode than None: TUFX has
                    // handed it out since (RefreshCameras, after a change in its window).
                    object before;
                    if (TryGet(component, component.GetType(), "antialiasingMode", out before)
                        && (!liveAntialiasing.ContainsKey(component) || (before != null && !Equals(before, none))))
                        liveAntialiasing[component] = before;
                    if (TrySet(component, component.GetType(), "antialiasingMode", none)) count++;
                }
            }
            return count;
        }

        // Drops the layers destroyed since from liveAntialiasing.
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

        // TUFX's current profile and its type, through TexturesUnlimitedFXLoader.INSTANCE; null without TUFX.
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

        // Writes a mode into one of a profile's antialiasing struct fields (see TryGetAaMode).
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
