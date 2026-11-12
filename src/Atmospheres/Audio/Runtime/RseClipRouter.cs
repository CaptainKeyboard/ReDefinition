using System;
using System.Collections;
using System.Reflection;

namespace ReDefinition.Atmospheres.Audio
{
    // Routes the footstep sources through Rocket Sound Enhancement's mixer where its settings list the clip.
    //
    // RSE (KSPModStewards, GPL-3.0) reads RSE_SETTINGS/CustomMixerRouting/AudioClips into the public static dictionary
    // RocketSoundEnhancement.Settings.CustomAudioClips (clip name -> MixerGroup) and applies it in ApplySettings(), which walks the audio sources
    // that exist at that moment (once when the flight scene starts, again from its settings panel). A source made later, or a clip put on a
    // source later, is never looked at: the step sources of the weather sound set their clip at every step, so RSE leaves them without a mixer
    // group (series row D33.rse_state -2). This router does for one source what ApplySettings does for the sources that existed: it looks the
    // clip's name up in RSE's dictionary and asks RocketSoundEnhancement.AudioUtility.GetMixerGroup for the group. Both are public members of
    // RSE; they are reached by reflection, as RSE is a soft dependency.
    internal sealed class RseClipRouter
    {
        private readonly IDictionary clipGroups;
        private readonly Func<object, object> mixerGroupOf;

        // clipGroups: clip name -> the MixerGroup value (an enum, boxed). mixerGroupOf: that value -> the AudioMixerGroup, or null.
        internal RseClipRouter(IDictionary clipGroups, Func<object, object> mixerGroupOf)
        {
            this.clipGroups = clipGroups;
            this.mixerGroupOf = mixerGroupOf;
        }

        // The router of an RSE install, from its Settings and AudioUtility types; null where a member is not as expected (another RSE version).
        internal static RseClipRouter FromTypes(Type settings, Type audioUtility)
        {
            if (settings == null || audioUtility == null) return null;
            FieldInfo field = settings.GetField("CustomAudioClips", BindingFlags.Public | BindingFlags.Static);
            IDictionary dictionary = field != null ? field.GetValue(null) as IDictionary : null;
            if (dictionary == null || !dictionary.GetType().IsGenericType) return null;
            Type groupType = dictionary.GetType().GetGenericArguments()[1];
            MethodInfo method = audioUtility.GetMethod("GetMixerGroup", BindingFlags.Public | BindingFlags.Static, null, new[] { groupType }, null);
            if (method == null) return null;
            return new RseClipRouter(dictionary, value => method.Invoke(null, new[] { value }));
        }

        // The router of the loaded RSE assembly; null without RSE or with members that are not as expected.
        internal static RseClipRouter FromAssembly(Assembly rse)
        {
            if (rse == null) return null;
            return FromTypes(rse.GetType("RocketSoundEnhancement.Settings", false), rse.GetType("RocketSoundEnhancement.AudioUtility", false));
        }

        // The mixer group RSE's settings give a clip of this name, or null where the clip is not listed, is listed as ignored (the enum's zero), RSE's
        // audio effects are off, or the lookup fails. Never throws.
        internal object GroupFor(string clipName)
        {
            if (string.IsNullOrEmpty(clipName)) return null;
            try
            {
                if (!clipGroups.Contains(clipName)) return null;
                object value = clipGroups[clipName];
                if (value == null || Convert.ToInt32(value) == 0) return null;
                return mixerGroupOf(value);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
