using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // What the storm asks of EVE's snow particle field (config `snow-Kerbin` in the installed particleFields.cfg), by reflection and
    // guarded. EVE's ParticleField keeps the cloud coverage at the camera in a private field, `currentCoverage`, computed in its Update
    // from the layer's coverage map. It draws the field only while that value is above 0. EVE has no setting or API for it. So the
    // storm raises the value to its own intensity for the frame, in Camera.onPreCull (after EVE's Update, before the field is drawn),
    // and puts it back when the storm ends.
    //
    // The path, each step a reflection that may fail with a line in the log.
    // Atmosphere.CloudsManager.GetObjectList() (static, inherited from GenericEVEManager) lists the CloudsObject layers.
    // CloudsObject.Body and .LayerRaymarchedVolume (public properties) name the body and give the CloudsRaymarchedVolume.
    // CloudsRaymarchedVolume.particleField (private field) is the ParticleField, or null for a layer without one.
    // ParticleField.particleFieldConfig (private string) names the field. currentCoverage (private float) is the value raised.
    // SetRendererEnabled(bool) (public) switches its renderer on, as EVE does itself when the coverage is above 0.
    internal static class EveSnowLink
    {
        private sealed class Entry
        {
            internal object Field;
            internal object Volume;
            internal string Config;
            internal float Original;
            internal float Written;
            internal bool Raised;
        }

        private static bool looked;
        private static string summary = "not found (EVE is not installed)";
        private static MethodInfo getList;
        private static PropertyInfo bodyProperty;
        private static PropertyInfo volumeProperty;
        private static FieldInfo particleFieldField;
        private static FieldInfo configField;
        private static FieldInfo coverageField;
        private static MethodInfo setRenderer;
        // The snow sound (optional): CloudsRaymarchedVolume.ambientSound -> AmbientSound.audioSource, and the volume EVE scales by.
        private static FieldInfo ambientSoundField;
        private static FieldInfo audioSourceField;
        private static PropertyInfo sourceVolume;
        private static MethodInfo sourceUnPause;
        private static PropertyInfo effectiveVolume;
        private static readonly List<Entry> entries = new List<Entry>();
        private static float nextScan;
        private static int lastRaiseFrame = -1;

        // What the lookup of the symbols found, for the log at the start.
        internal static string Summary
        {
            get
            {
                Look();
                return summary;
            }
        }

        // Whether every symbol of the path exists in the installed EVE.
        internal static bool Controllable
        {
            get
            {
                Look();
                return getList != null && bodyProperty != null && volumeProperty != null && particleFieldField != null
                       && configField != null && coverageField != null && setRenderer != null;
            }
        }

        // The number of snow fields of Kerbin found by the last scan.
        internal static int Fields
        {
            get { return entries.Count; }
        }

        // The config names of the fields found, for the log.
        internal static string Names()
        {
            if (entries.Count == 0) return "none";
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) text.Append(", ");
                text.Append(entries[i].Config);
            }
            return text.ToString();
        }

        private static void Look()
        {
            if (looked) return;
            looked = true;
            try
            {
                Type manager = TypeLookup.Find("Atmosphere.CloudsManager");
                Type layer = TypeLookup.Find("Atmosphere.CloudsObject");
                Type volume = TypeLookup.Find("Atmosphere.CloudsRaymarchedVolume");
                Type field = TypeLookup.Find("Atmosphere.ParticleField");
                if (manager == null || layer == null || volume == null || field == null) return;
                var link = new ReflectLink();
                getList = link.Method(manager, "CloudsManager", "GetObjectList", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy, null);
                bodyProperty = link.Property(layer, "CloudsObject", "Body", BindingFlags.Public | BindingFlags.Instance);
                volumeProperty = link.Property(layer, "CloudsObject", "LayerRaymarchedVolume", BindingFlags.Public | BindingFlags.Instance);
                particleFieldField = link.Field(volume, "CloudsRaymarchedVolume", "particleField", TypeLookup.Any);
                configField = link.Field(field, "ParticleField", "particleFieldConfig", TypeLookup.Any, typeof(string));
                coverageField = link.Field(field, "ParticleField", "currentCoverage", TypeLookup.Any, typeof(float));
                setRenderer = link.Method(field, "ParticleField", "SetRendererEnabled", BindingFlags.Public | BindingFlags.Instance, new[] { typeof(bool) });
                ambientSoundField = volume.GetField("ambientSound", TypeLookup.Any);
                Type sound = TypeLookup.Find("Atmosphere.AmbientSound");
                audioSourceField = sound != null ? sound.GetField("audioSource", TypeLookup.Any) : null;
                Type quality = TypeLookup.Find("Atmosphere.RaymarchedCloudsQualityManager");
                effectiveVolume = quality != null ? quality.GetProperty("EffectiveAmbientVolume", TypeLookup.Any) : null;
                // UnityEngine.AudioSource is reached by reflection too: this assembly does not reference the audio module.
                if (audioSourceField != null)
                {
                    sourceVolume = audioSourceField.FieldType.GetProperty("volume", BindingFlags.Public | BindingFlags.Instance);
                    sourceUnPause = audioSourceField.FieldType.GetMethod("UnPause", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (sourceVolume == null || sourceVolume.PropertyType != typeof(float) || sourceUnPause == null) audioSourceField = null;
                }
                summary = link.Complete
                    ? "found (CloudsManager -> CloudsObject -> CloudsRaymarchedVolume.particleField -> ParticleField.currentCoverage)"
                    : "not controllable (missing " + link.MissingText() + ")";
                if (link.Complete && !AtmospheresAssumptions.Holds(AssumptionsC23.GroundId))
                {
                    getList = null;
                    ForeignAssumption assumption = AtmospheresAssumptions.Find(AssumptionsC23.GroundId);
                    summary = "not used (" + (assumption != null ? assumption.State + ": " + assumption.Problem : "assumption unknown") + ")";
                }
            }
            catch (Exception e)
            {
                getList = null;
                summary = "not controllable (" + CompatibilityLog.Reason(e) + ")";
            }
        }

        // Looks for the snow fields of a body at most every three seconds; returns how many it holds. Fields that are not raised any
        // more and have left the list are dropped.
        internal static int Scan(string bodyName)
        {
            if (!Controllable) return 0;
            float now = Time.realtimeSinceStartup;
            if (now < nextScan) return entries.Count;
            nextScan = now + 3f;
            try
            {
                var found = new List<Entry>();
                IEnumerable list = getList.Invoke(null, null) as IEnumerable;
                if (list != null)
                {
                    foreach (object layer in list)
                    {
                        if (layer == null) continue;
                        if (!string.Equals(bodyProperty.GetValue(layer, null) as string, bodyName, StringComparison.Ordinal)) continue;
                        object volume = volumeProperty.GetValue(layer, null);
                        object field = volume != null ? particleFieldField.GetValue(volume) : null;
                        if (field == null) continue;
                        string config = configField.GetValue(field) as string;
                        if (config == null || config.IndexOf("snow", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        found.Add(new Entry { Field = field, Volume = volume, Config = config });
                    }
                }
                // Keep the records of fields that stay, so that the original value of a raised one is not lost.
                for (int i = 0; i < found.Count; i++)
                {
                    for (int j = 0; j < entries.Count; j++)
                    {
                        if (ReferenceEquals(entries[j].Field, found[i].Field)) { found[i] = entries[j]; break; }
                    }
                }
                for (int j = 0; j < entries.Count; j++)
                {
                    if (!found.Contains(entries[j]) && entries[j].Raised) Release(entries[j]);
                }
                entries.Clear();
                entries.AddRange(found);
            }
            catch (Exception e)
            {
                entries.Clear();
                getList = null;
                summary = "not controllable (" + CompatibilityLog.Reason(e) + ")";
                AtmospheresLog.Warn("atmospheres-eve-snow", "EVE snow field: " + summary);
            }
            return entries.Count;
        }

        // Raises the coverage of each field to the intensity for this frame (once per frame) and switches its renderer on.
        internal static void Raise(float intensity)
        {
            if (entries.Count == 0 || lastRaiseFrame == Time.frameCount) return;
            lastRaiseFrame = Time.frameCount;
            try
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    Entry entry = entries[i];
                    float current = (float)coverageField.GetValue(entry.Field);
                    if (!entry.Raised)
                    {
                        entry.Original = current;
                        entry.Raised = true;
                    }
                    else if (current != entry.Written)
                    {
                        // EVE computed a new value since: that is the one to put back.
                        entry.Original = current;
                    }
                    entry.Written = Math.Max(entry.Original, intensity);
                    coverageField.SetValue(entry.Field, entry.Written);
                    setRenderer.Invoke(entry.Field, new object[] { true });
                }
            }
            catch (Exception e)
            {
                getList = null;
                summary = "not controllable (" + CompatibilityLog.Reason(e) + ")";
                AtmospheresLog.Warn("atmospheres-eve-snow", "EVE snow field: " + summary);
            }
        }

        // Raises the snow sound of the layers with a snow field to the level (a rain that was replaced) times EVE's master volume,
        // smoothly, and unpauses it. EVE pauses it again from its next update once the level is gone. Returns the sounds raised.
        internal static int RaiseSound(float level)
        {
            if (entries.Count == 0 || ambientSoundField == null || audioSourceField == null || level < 0.05f) return 0;
            int raised = 0;
            try
            {
                float master = 1f;
                if (effectiveVolume != null)
                {
                    object value = effectiveVolume.GetValue(null, null);
                    if (value is float) master = (float)value;
                }
                for (int i = 0; i < entries.Count; i++)
                {
                    object sound = entries[i].Volume != null ? ambientSoundField.GetValue(entries[i].Volume) : null;
                    object source = sound != null ? audioSourceField.GetValue(sound) : null;
                    if (source == null) continue;
                    float target = level * master;
                    float now = (float)sourceVolume.GetValue(source, null);
                    if (now < target) sourceVolume.SetValue(source, Mathf.Lerp(now, target, 0.05f), null);
                    sourceUnPause.Invoke(source, null);
                    raised++;
                }
            }
            catch (Exception e)
            {
                ambientSoundField = null;
                AtmospheresLog.Failed("atmospheres-eve-snow-sound", "EVE snow sound: not controllable", e);
            }
            return raised;
        }

        // Puts the coverage of every raised field back where EVE has not written it since; EVE sets its renderer by the value on its next
        // update.
        internal static void Release()
        {
            for (int i = 0; i < entries.Count; i++) Release(entries[i]);
        }

        private static void Release(Entry entry)
        {
            if (!entry.Raised) return;
            entry.Raised = false;
            try
            {
                float current = (float)coverageField.GetValue(entry.Field);
                if (current == entry.Written) coverageField.SetValue(entry.Field, entry.Original);
                if (entry.Original <= 0f) setRenderer.Invoke(entry.Field, new object[] { false });
            }
            catch (Exception)
            {
                // The field is gone with its scene.
            }
        }
    }
}
