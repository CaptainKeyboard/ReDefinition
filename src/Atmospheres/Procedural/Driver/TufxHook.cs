using System;
using System.Collections;
using System.Reflection;

namespace ReDefinition.Atmospheres
{
    // Lowers the exposure compensation of TUFX's colour grading while precipitation is heavy, so that a storm keeps its darkness under
    // the eye adaptation of the post-processing: postExposure = base - ExposurePerLevel x level. TUFX is reached by reflection only
    // (TUFX.TexturesUnlimitedFXLoader.INSTANCE, CurrentProfile.Settings, the ColorGrading setting and its postExposure parameter; names
    // read in TUFX's source, GPL-3.0-or-later) and nothing is written when any step is missing: the hook is silent when TUFX is absent
    // or its members differ. postExposure acts in an HDR profile only [VERIFY in the game with TUFX present]. A storm colour LUT with a
    // 3 s crossfade is not done: TUFX's ColorGrading has externalLut (used only in GradingMode.External, which replaces the profile's
    // whole grading) and ldrLut with ldrLutContribution (used only in GradingMode.LowDefinitionRange), while the installed profile
    // (Blackrack_TUFX) is HDR. A fade would mean switching the grading mode of the player's profile at runtime and baking a LUT; open
    // item for E40 (needs an in-game look at a TUFX profile), owner package E40.
    internal sealed class TufxHook
    {
        // The exposure lowered at level 1, in EV (the look's constant, Tuning "tufx.exposurePerLevel").
        internal const float ExposurePerLevelDefault = 0.5f;

        private bool resolved;
        private bool available;
        private FieldInfo instanceField;
        private PropertyInfo currentProfile;
        private FieldInfo settingsField;
        private FieldInfo postExposureField;
        private FieldInfo valueField;
        private FieldInfo overrideField;

        private object grading;
        private object parameter;
        private float baseValue;
        private bool baseOverride;
        private float written;
        private bool writing;

        // Whether TUFX answered the last time.
        internal bool Available
        {
            get { return available; }
        }

        // Sets the exposure for a level (0 puts it back). Never throws.
        internal void Apply(float level01)
        {
            try
            {
                if (level01 <= 0.001f)
                {
                    Restore();
                    return;
                }
                if (!Resolve()) return;
                object loader = instanceField.GetValue(null);
                object profile = loader == null ? null : currentProfile.GetValue(loader, null);
                IList settings = profile == null ? null : settingsField.GetValue(profile) as IList;
                if (settings == null)
                {
                    Restore();
                    return;
                }
                object found = null;
                for (int i = 0; i < settings.Count; i++)
                {
                    object s = settings[i];
                    if (s != null && s.GetType().Name == "ColorGrading")
                    {
                        found = s;
                        break;
                    }
                }
                if (found == null)
                {
                    Restore();
                    return;
                }
                if (postExposureField == null) postExposureField = found.GetType().GetField("postExposure", BindingFlags.Public | BindingFlags.Instance);
                if (postExposureField == null) return;
                object p = postExposureField.GetValue(found);
                if (p == null) return;
                if (valueField == null)
                {
                    valueField = p.GetType().GetField("value", BindingFlags.Public | BindingFlags.Instance);
                    overrideField = p.GetType().GetField("overrideState", BindingFlags.Public | BindingFlags.Instance);
                }
                if (valueField == null || overrideField == null) return;
                float current = (float)valueField.GetValue(p);
                if (!ReferenceEquals(p, parameter) || !writing)
                {
                    if (writing) Restore();
                    parameter = p;
                    grading = found;
                    baseValue = current;
                    baseOverride = (bool)overrideField.GetValue(p);
                }
                else if (Math.Abs(current - written) > 1e-4f)
                {
                    // Someone else wrote it: that is the new base.
                    baseValue = current;
                }
                written = baseValue - Tuning.Get("tufx.exposurePerLevel", ExposurePerLevelDefault) * level01;
                valueField.SetValue(p, written);
                overrideField.SetValue(p, true);
                writing = true;
            }
            catch (Exception)
            {
                available = false;
                writing = false;
                parameter = null;
            }
        }

        // Puts the exposure back where it is still the value written.
        internal void Restore()
        {
            try
            {
                if (!writing || parameter == null) return;
                float current = (float)valueField.GetValue(parameter);
                if (Math.Abs(current - written) <= 1e-4f)
                {
                    valueField.SetValue(parameter, baseValue);
                    overrideField.SetValue(parameter, baseOverride);
                }
            }
            catch (Exception)
            {
                // TUFX changed under us; nothing to put back.
            }
            writing = false;
            parameter = null;
            grading = null;
        }

        private bool Resolve()
        {
            if (resolved) return available;
            resolved = true;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name != "TUFX") continue;
                Type loader = assembly.GetType("TUFX.TexturesUnlimitedFXLoader", false);
                if (loader == null) return false;
                instanceField = loader.GetField("INSTANCE", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                currentProfile = loader.GetProperty("CurrentProfile", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Type profile = assembly.GetType("TUFX.TUFXProfile", false);
                settingsField = profile == null ? null : profile.GetField("Settings", BindingFlags.Instance | BindingFlags.Public);
                available = instanceField != null && currentProfile != null && settingsField != null;
                return available;
            }
            return false;
        }
    }
}
