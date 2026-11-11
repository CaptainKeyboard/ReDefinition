using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres.Audio
{
    // EVE's own snow-storm loop, reached by reflection (EVE's AmbientSound.cs: one 2D looping AudioSource per cloud layer, parented to the
    // flight camera, volume = coverage x EVE's ambient volume, smoothed in its Update, paused while the coverage is below 0.05). The path:
    // Atmosphere.CloudsManager.GetObjectList() -> CloudsObject.Body / LayerRaymarchedVolume -> CloudsRaymarchedVolume.ambientSound ->
    // AmbientSound.audioSource. Reading is always allowed; writing only under the policy Ours.
    internal static class EveSoundAccess
    {
        private static bool looked;
        private static MethodInfo getList;
        private static PropertyInfo bodyProperty;
        private static PropertyInfo volumeProperty;
        private static FieldInfo ambientSoundField;
        private static FieldInfo audioSourceField;
        private static readonly List<AudioSource> sources = new List<AudioSource>();
        private static float nextScan;
        private static string summary = "not looked for";

        internal static string Summary { get { Look(); return summary; } }

        // The number of EVE loops found on the body at the last scan.
        internal static int Count { get { return sources.Count; } }

        private static void Look()
        {
            if (looked) return;
            looked = true;
            try
            {
                Type manager = TypeLookup.Find("Atmosphere.CloudsManager");
                Type layer = TypeLookup.Find("Atmosphere.CloudsObject");
                Type volume = TypeLookup.Find("Atmosphere.CloudsRaymarchedVolume");
                Type sound = TypeLookup.Find("Atmosphere.AmbientSound");
                if (manager == null || layer == null || volume == null || sound == null)
                {
                    summary = "EVE sound not found";
                    return;
                }
                getList = manager.GetMethod("GetObjectList", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                bodyProperty = layer.GetProperty("Body", BindingFlags.Public | BindingFlags.Instance);
                volumeProperty = layer.GetProperty("LayerRaymarchedVolume", BindingFlags.Public | BindingFlags.Instance);
                ambientSoundField = volume.GetField("ambientSound", TypeLookup.Any);
                audioSourceField = sound.GetField("audioSource", TypeLookup.Any);
                bool complete = getList != null && bodyProperty != null && volumeProperty != null && ambientSoundField != null
                                && audioSourceField != null && audioSourceField.FieldType == typeof(AudioSource);
                if (!complete) getList = null;
                summary = complete ? "found (CloudsRaymarchedVolume.ambientSound.audioSource)" : "not readable (a member is missing)";
            }
            catch (Exception e)
            {
                getList = null;
                summary = "not readable (" + e.GetType().Name + ")";
            }
        }

        // Looks for the loops of a body at most every three seconds.
        internal static void Scan(string bodyName)
        {
            Look();
            if (getList == null) { sources.Clear(); return; }
            float now = Time.realtimeSinceStartup;
            if (now < nextScan) return;
            nextScan = now + 3f;
            try
            {
                sources.Clear();
                IEnumerable list = getList.Invoke(null, null) as IEnumerable;
                if (list == null) return;
                foreach (object layer in list)
                {
                    if (layer == null) continue;
                    if (!string.Equals(bodyProperty.GetValue(layer, null) as string, bodyName, StringComparison.Ordinal)) continue;
                    object volume = volumeProperty.GetValue(layer, null);
                    object sound = volume != null ? ambientSoundField.GetValue(volume) : null;
                    AudioSource source = sound != null ? audioSourceField.GetValue(sound) as AudioSource : null;
                    if (source != null) sources.Add(source);
                }
            }
            catch (Exception e)
            {
                sources.Clear();
                getList = null;
                summary = "not readable (" + e.GetType().Name + ")";
                AtmospheresLog.Warn("atmospheres-audio-eve", "weather sound: " + summary);
            }
        }

        // The loudest volume among the loops that exist and play (0 where there is none).
        internal static float AudibleVolume()
        {
            float best = 0f;
            for (int i = 0; i < sources.Count; i++)
            {
                AudioSource s = sources[i];
                if (s == null || s.clip == null || !s.isPlaying) continue;
                if (s.volume > best) best = s.volume;
            }
            return best;
        }

        // Writes the volume of every loop to zero (policy Ours; called in LateUpdate, after EVE's update of the frame).
        internal static void Silence()
        {
            for (int i = 0; i < sources.Count; i++)
            {
                AudioSource s = sources[i];
                if (s != null && s.volume != 0f) s.volume = 0f;
            }
        }

        // The loudest volume after a write, for the series row that measures the ordering.
        internal static float VolumeNow()
        {
            float best = 0f;
            for (int i = 0; i < sources.Count; i++)
            {
                if (sources[i] != null && sources[i].volume > best) best = sources[i].volume;
            }
            return best;
        }
    }
}
