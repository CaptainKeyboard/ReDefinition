using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Seasons
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
                getList = manager.GetMethod("GetObjectList", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                bodyProperty = layer.GetProperty("Body", BindingFlags.Public | BindingFlags.Instance);
                volumeProperty = layer.GetProperty("LayerRaymarchedVolume", BindingFlags.Public | BindingFlags.Instance);
                particleFieldField = volume.GetField("particleField", TypeLookup.Any);
                configField = field.GetField("particleFieldConfig", TypeLookup.Any);
                coverageField = field.GetField("currentCoverage", TypeLookup.Any);
                setRenderer = field.GetMethod("SetRendererEnabled", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
                if (coverageField != null && coverageField.FieldType != typeof(float)) coverageField = null;
                if (configField != null && configField.FieldType != typeof(string)) configField = null;
                var missing = new List<string>();
                if (getList == null) missing.Add("CloudsManager.GetObjectList");
                if (bodyProperty == null) missing.Add("CloudsObject.Body");
                if (volumeProperty == null) missing.Add("CloudsObject.LayerRaymarchedVolume");
                if (particleFieldField == null) missing.Add("CloudsRaymarchedVolume.particleField");
                if (configField == null) missing.Add("ParticleField.particleFieldConfig");
                if (coverageField == null) missing.Add("ParticleField.currentCoverage");
                if (setRenderer == null) missing.Add("ParticleField.SetRendererEnabled");
                summary = missing.Count == 0
                    ? "found (CloudsManager -> CloudsObject -> CloudsRaymarchedVolume.particleField -> ParticleField.currentCoverage)"
                    : "not controllable (missing " + string.Join(", ", missing.ToArray()) + ")";
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
                        found.Add(new Entry { Field = field, Config = config });
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
                Debug.LogWarning(Log.Tag + " Seasons: EVE snow field: " + summary);
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
                Debug.LogWarning(Log.Tag + " Seasons: EVE snow field: " + summary);
            }
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
