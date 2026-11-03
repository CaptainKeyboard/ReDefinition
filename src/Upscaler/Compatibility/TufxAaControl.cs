using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // TUFX's antialiasing: its profile's modes and the PostProcessLayers they were copied onto, set to None while
    // the upscaler runs (HostStack).
    internal sealed class TufxAaControl : IHostFilter
    {
        // A TUFX profile's antialiasing modes before Apply set them to None.
        private sealed class TufxBackup
        {
            // The modes of the profile's primary camera and of its other cameras, as TUFX names them.
            public object Primary;
            public object Secondary;

            // The value written here. Restore compares against it: a profile that
            // holds another value has been edited since, and that choice stands.
            public object Applied;
        }

        private const string LoaderTypeName = "TUFX.TexturesUnlimitedFXLoader";

        // Keyed by profile object rather than by name: TUFX keeps its profiles
        // alive in a dictionary, so the reference stays valid. And a different
        // profile can be active per scene.
        private readonly Dictionary<object, TufxBackup> backups = new Dictionary<object, TufxBackup>();

        // What each PostProcessLayer's antialiasing was before Apply first set
        // None on it, for the layers TUFX does not hand theirs out to again.
        private readonly Dictionary<Component, object> liveAntialiasing = new Dictionary<Component, object>();
        private readonly List<Component> destroyedLayers = new List<Component>();

        // One camera's components, kept between passes so that each makes no list.
        private readonly List<Component> layerScratch = new List<Component>();

        // TUFX's own save (TUFXProfile.SaveToDisk -- its window's Save Selected and
        // Save All) writes the profile's antialiasing into its file (SaveToNode). It
        // writes the player's modes: the profile holds them for the call, and None
        // again after. A profile changed in TUFX's window since it was set here holds
        // the player's mode already and is saved as it is.
        private bool saveHookTried;

        // The name in the notes of a step that failed.
        public string Label
        {
            get { return "TUFX"; }
        }

        // Sets TUFX's profile and the PostProcessLayers of the cameras to antialiasing None, and remembers the modes
        // for Restore. The report may be null -- when reasserting, nobody reads the text.
        public void Apply(StringBuilder report)
        {
            Type profileType;
            object profile = Profile(out profileType);
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

            HookSave(profileType);
            object secondary;
            Type secondaryType;
            TryGetAaMode(profile, profileType, "SecondaryCameraAntialiasing", out secondary, out secondaryType);
            TufxBackup backup;
            if (!backups.TryGetValue(profile, out backup))
            {
                backups[profile] = new TufxBackup { Primary = mode, Secondary = secondary };
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
            backups[profile].Applied = none;
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

        // TUFX creates new PostProcessLayers with each scene, so the setting is made again; nobody reads the text.
        public void Reassert()
        {
            Apply(null);
        }

        // Puts back each TUFX profile's antialiasing modes where they still hold None, then its layers' and cameras'.
        public void Restore(StringBuilder report)
        {
            // Every profile changed here, not just the active one: across a scene
            // change there may have been several.
            int profiles = 0;
            int changedElsewhere = 0;
            foreach (KeyValuePair<object, TufxBackup> entry in backups)
            {
                object profile = entry.Key;
                if (profile == null) continue;
                Type profileType = profile.GetType();

                object current;
                Type currentType;
                if (!TryGetAaMode(profile, profileType, "Antialiasing", out current, out currentType))
                    continue;
                if (!Equals(current, entry.Value.Applied))
                {
                    changedElsewhere++;
                    continue;
                }

                TrySetAaMode(profile, profileType, "Antialiasing", entry.Value.Primary);
                TrySetAaMode(profile, profileType, "SecondaryCameraAntialiasing", entry.Value.Secondary);
                profiles++;
            }
            // Every layer set to None its own antialiasing again, where it still has
            // None; then TUFX's cameras theirs from the profile just restored -- the
            // primary camera the profile's, the others its
            // SecondaryCameraAntialiasing -- as TUFX itself hands them out
            // (TexturesUnlimitedFXLoader.RefreshCameras, ApplyProfileToCamera).
            RestoreLiveAntialiasing();
            if (profiles > 0)
            {
                RefreshCameras();
                report.Append(profiles).Append(" TUFX profile(s) restored.");
            }
            if (changedElsewhere > 0)
                report.Append(' ').Append(changedElsewhere)
                      .Append(" TUFX profile(s) left alone, changed since they were set here.");
            backups.Clear();
        }

        // The rows for TUFX's current profile and its two antialiasing modes, or one row where TUFX is not installed.
        public void AddLines(List<HostStack.Line> lines)
        {
            Type profileType;
            object profile = Profile(out profileType);
            if (profile == null)
            {
                lines.Add(HostStack.Line.Missing("TUFX"));
            }
            else
            {
                object name;
                ForeignReflection.TryGet(profile, profileType, "ProfileName", out name);
                lines.Add(HostStack.Line.Info("TUFX profile", name == null ? "?" : name.ToString()));
                lines.Add(AaLine(profile, profileType, "Antialiasing", "TUFX AA primary"));
                lines.Add(AaLine(profile, profileType, "SecondaryCameraAntialiasing", "TUFX AA secondary"));
            }
        }

        // The row for one of a TUFX profile's antialiasing modes: suitable only when it is None.
        private static HostStack.Line AaLine(object profile, Type profileType, string field, string label)
        {
            object mode;
            Type modeType;
            if (!TryGetAaMode(profile, profileType, field, out mode, out modeType))
                return HostStack.Line.Info(label, "not readable");

            string text = mode == null ? "?" : mode.ToString();
            return new HostStack.Line { Label = label, Value = text, Ok = text == "None", Present = true };
        }

        // Installs the save hook on TUFX's profile type, once; without it a save while the upscaler runs writes None.
        private void HookSave(Type profileType)
        {
            if (saveHookTried || profileType == null) return;
            saveHookTried = true;
            MethodInfo save = profileType.GetMethod("SaveToDisk", BindingFlags.Instance | BindingFlags.Public,
                                                    null, Type.EmptyTypes, null);
            try
            {
                HarmonyHooks.Install("ReDefinition.TufxSave", typeof(TufxAaControl), new[]
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
            if (__instance == null || !HostStack.Tufx.backups.TryGetValue(__instance, out backup) || backup.Applied == null) return;
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
            if (!HostStack.Tufx.backups.TryGetValue(__instance, out backup) || backup.Applied == null) return __exception;
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
        internal static bool SwapAaMode(object profile, Type profileType, string field, object from, object to)
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
        private static void RefreshCameras()
        {
            if (HighLogic.LoadedScene == GameScenes.FLIGHT && CameraManager.Instance == null) return;
            Type loaderType = TypeLookup.Find(LoaderTypeName);
            if (loaderType == null) return;
            object loader;
            if (!ForeignReflection.TryGet(null, loaderType, "INSTANCE", out loader) || loader == null) return;
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
        private int SetLiveAntialiasingNone(object none)
        {
            if (none == null) return 0;
            // Layers of scenes since gone -- TUFX makes new ones with each -- let go.
            ForgetDestroyedLayers();

            int count = 0;
            int cameraCount = HostCameras.EnabledCameras();
            for (int i = 0; i < cameraCount; i++)
            {
                HostCameras.CameraAt(i).GetComponents(layerScratch);
                for (int j = 0; j < layerScratch.Count; j++)
                {
                    Component component = layerScratch[j];
                    // Missing scripts show up as null here.
                    if (component == null) continue;
                    Type componentType = component.GetType();
                    if (componentType.Name != "PostProcessLayer") continue;
                    // Kept anew where the layer holds another mode than None: TUFX has
                    // handed it out since (RefreshCameras, after a change in its window).
                    object before;
                    if (ForeignReflection.TryGet(component, componentType, "antialiasingMode", out before)
                        && (!liveAntialiasing.ContainsKey(component) || (before != null && !Equals(before, none))))
                        liveAntialiasing[component] = before;
                    if (ForeignReflection.TrySet(component, componentType, "antialiasingMode", none)) count++;
                }
            }
            layerScratch.Clear();
            return count;
        }

        // Drops the layers destroyed since from liveAntialiasing.
        private void ForgetDestroyedLayers()
        {
            foreach (Component layer in liveAntialiasing.Keys)
                if (layer == null) destroyedLayers.Add(layer);
            foreach (Component layer in destroyedLayers) liveAntialiasing.Remove(layer);
            destroyedLayers.Clear();
        }

        // What still has None, of the layers that are still there.
        private void RestoreLiveAntialiasing()
        {
            foreach (KeyValuePair<Component, object> entry in liveAntialiasing)
            {
                Component layer = entry.Key;
                object now;
                if (layer == null || entry.Value == null
                    || !ForeignReflection.TryGet(layer, layer.GetType(), "antialiasingMode", out now)
                    || now == null || now.ToString() != "None")
                    continue;
                ForeignReflection.TrySet(layer, layer.GetType(), "antialiasingMode", entry.Value);
            }
            liveAntialiasing.Clear();
        }

        // TUFX's current profile and its type, through TexturesUnlimitedFXLoader.INSTANCE; null without TUFX.
        private static object Profile(out Type profileType)
        {
            profileType = null;
            Type loaderType = TypeLookup.Find(LoaderTypeName);
            if (loaderType == null) return null;

            object loader;
            if (!ForeignReflection.TryGet(null, loaderType, "INSTANCE", out loader) || loader == null) return null;

            object profile;
            if (!ForeignReflection.TryGet(loader, loaderType, "CurrentProfile", out profile) || profile == null) return null;

            profileType = profile.GetType();
            return profile;
        }

        // Antialiasing is a struct field on the profile. A write goes through the
        // box and back again; otherwise only a copy changes.
        internal static bool TryGetAaMode(object profile, Type profileType, string fieldName,
                                          out object mode, out Type modeType)
        {
            mode = null;
            modeType = null;
            FieldInfo aaField = ForeignReflection.FieldOf(profileType, fieldName, TypeLookup.Any);
            if (aaField == null) return false;
            object aa = aaField.GetValue(profile);
            if (aa == null) return false;
            FieldInfo modeField = ForeignReflection.FieldOf(aa.GetType(), "Mode", TypeLookup.Any);
            if (modeField == null) return false;
            mode = modeField.GetValue(aa);
            modeType = modeField.FieldType;
            return true;
        }

        // Writes a mode into one of a profile's antialiasing struct fields (see TryGetAaMode).
        internal static bool TrySetAaMode(object profile, Type profileType, string fieldName, object mode)
        {
            if (mode == null) return false;
            FieldInfo aaField = ForeignReflection.FieldOf(profileType, fieldName, TypeLookup.Any);
            if (aaField == null) return false;
            object aa = aaField.GetValue(profile);
            if (aa == null) return false;
            FieldInfo modeField = ForeignReflection.FieldOf(aa.GetType(), "Mode", TypeLookup.Any);
            if (modeField == null) return false;
            modeField.SetValue(aa, mode);
            aaField.SetValue(profile, aa);
            return true;
        }
    }
}
