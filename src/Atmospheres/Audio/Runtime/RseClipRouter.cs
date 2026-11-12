using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace ReDefinition.Atmospheres.Audio
{
    // Routes the footstep sources through Rocket Sound Enhancement's mixer where its settings list the clip.
    //
    // RSE (KSPModStewards, GPL-3.0) fills the public static dictionary RocketSoundEnhancement.Settings.CustomAudioClips (clip name -> MixerGroup)
    // in Settings.Load() from the file GameData/RocketSoundEnhancement/Settings.cfg, read with ConfigNode.Load (decompiled RSE 0.9.13). That is
    // the file on disk, not the game database: a ModuleManager patch of RSE_SETTINGS (RocketSoundEnhancementPatch.cfg) never reaches the
    // dictionary, so RSE's table does not list our step clips (series row D33.rse_state -2 with the table patched and a router that only read
    // the table). RSE also applies the table only in ApplySettings(), to the audio sources that exist at that moment, and the step sources
    // set their clip at every step.
    // This router therefore knows the clips from two places: RSE's table (an entry the player or another mod put in Settings.cfg wins) and the
    // clips our patch declares (name -> group name, read from the patched node of the game database). For a clip it gives the source the
    // group RocketSoundEnhancement.AudioUtility.GetMixerGroup returns for the group value. Nothing is written into RSE's table or file. RSE
    // members are reached by reflection, as RSE is a soft dependency.
    internal sealed class RseClipRouter
    {
        private readonly IDictionary clipGroups;
        private readonly Func<object, object> mixerGroupOf;
        private readonly Func<string, object> parseGroup;
        private readonly Dictionary<string, string> declared = new Dictionary<string, string>(StringComparer.Ordinal);

        // clipGroups: clip name -> the MixerGroup value (an enum, boxed). mixerGroupOf: that value -> the AudioMixerGroup, or null.
        // parseGroup: a group name from the patch ("Exterior") -> the MixerGroup value, or null where it is not a group.
        internal RseClipRouter(IDictionary clipGroups, Func<object, object> mixerGroupOf, Func<string, object> parseGroup = null)
        {
            this.clipGroups = clipGroups;
            this.mixerGroupOf = mixerGroupOf;
            this.parseGroup = parseGroup;
        }

        // Entries RSE's table holds, and clips declared by our patch; for the log line of the first step.
        internal int TableCount { get { return clipGroups.Count; } }
        internal int DeclaredCount { get { return declared.Count; } }

        // A clip our patch routes (name as the patch node spells it, group name as its value).
        internal void Declare(string clipName, string groupName)
        {
            if (!string.IsNullOrEmpty(clipName) && !string.IsNullOrEmpty(groupName)) declared[clipName] = groupName;
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
            return new RseClipRouter(dictionary, value => method.Invoke(null, new[] { value }), name =>
            {
                try { return Enum.Parse(groupType, name, true); }
                catch (ArgumentException) { return null; }
            });
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
                object value = null;
                if (clipGroups.Contains(clipName)) value = clipGroups[clipName];
                else
                {
                    string groupName;
                    if (parseGroup != null && declared.TryGetValue(clipName, out groupName)) value = parseGroup(groupName);
                }
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
