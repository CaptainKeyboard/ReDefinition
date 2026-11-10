using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Seasons
{
    // Takes EVE's rain away where the ground is snow or ice, by reflection and guarded. A cloud layer
    // of EVE carries a rain particle field, droplets, wet surfaces, lightning and a looped rain sound, switched by the cloud type at
    // the camera. While the camera is in a cold region, each frame in Camera.onPreCull (after EVE's Update) this does two things.
    //
    // 1. The coverage of each rain particle field is read (what the rain would have been) and set to 0. The caller raises EVE's snow
    //    field and snow sound to that value (EveSnowLink).
    // 2. The cloud types of every rain layer get DropletsDensity, WetSurfacesIntensity, LightningFrequency and AmbientVolume set to
    //    0, those of a layer with droplets only their DropletsDensity. EVE reads them in its next Update. The first values are kept
    //    and put back when the camera leaves the region.
    //
    // Names from the decompile of the installed Atmosphere.dll: CloudsManager.GetObjectList(), CloudsObject.Body and
    // .LayerRaymarchedVolume, CloudsRaymarchedVolume.particleField, .droplets and .CloudTypes, ParticleField.particleFieldConfig,
    // .currentCoverage and .SetRendererEnabled(bool), and the four private floats of CloudType.
    internal static class EveRainLink
    {
        // The coverage of the ground from which the rain is replaced, and the one below which it comes back (so that it does not
        // flicker at the edge).
        internal const double ColdOn = 0.5, ColdOff = 0.45;

        private sealed class TypeRecord
        {
            internal object Type;
            internal float Droplets, Wet, Lightning, Ambient;
            // Whether the first values above are read and the type may be zeroed; a type that is not kept is never written back.
            internal bool Kept;
        }

        // The arguments of the calls made every frame, made once.
        private static readonly object ZeroValue = 0f;
        private static readonly object[] RendererOff = { false };

        private sealed class Entry
        {
            internal object Field;
            internal bool RainField;
            internal readonly List<TypeRecord> Types = new List<TypeRecord>();
            internal bool Zeroed;
        }

        private static bool looked;
        private static string summary = "not found (EVE is not installed)";
        private static MethodInfo getList;
        private static PropertyInfo bodyProperty;
        private static PropertyInfo volumeProperty;
        private static PropertyInfo typesProperty;
        private static FieldInfo particleFieldField;
        private static FieldInfo dropletsField;
        private static FieldInfo configField;
        private static FieldInfo coverageField;
        private static MethodInfo setRenderer;
        private static FieldInfo dropletsDensity;
        private static FieldInfo wetIntensity;
        private static FieldInfo lightningFrequency;
        private static FieldInfo ambientVolume;
        private static readonly List<Entry> entries = new List<Entry>();
        private static float nextScan;
        private static int lastFrame = -1;
        private static float lastCoverage;
        private static bool suppressed;
        private static int fields, dropletLayers, soundLayers, lightningLayers, wetLayers;

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
                return getList != null && bodyProperty != null && volumeProperty != null && typesProperty != null && particleFieldField != null
                       && dropletsField != null && configField != null && coverageField != null && setRenderer != null
                       && dropletsDensity != null && wetIntensity != null && lightningFrequency != null && ambientVolume != null;
            }
        }

        // Whether the rain is being replaced now.
        internal static bool Suppressed
        {
            get { return suppressed; }
        }

        // What the rain would have been at the camera in the last frame, 0 to 1: the largest coverage of the rain fields.
        internal static float Coverage
        {
            get { return lastCoverage; }
        }

        // The rain particle fields, the layers with droplets, with a rain sound, with lightning and with wet surfaces that the last scan
        // found.
        internal static string Describe()
        {
            return "fields " + fields + ", droplets " + dropletLayers + ", wet surfaces " + wetLayers + ", lightning " + lightningLayers + ", sound " + soundLayers;
        }

        // Whether the camera is in a cold region, with a margin so that it does not flicker: on from ColdOn, off below ColdOff.
        internal static bool Cold(bool wasCold, double snowCoverage)
        {
            if (double.IsNaN(snowCoverage)) return false;
            return wasCold ? snowCoverage >= ColdOff : snowCoverage >= ColdOn;
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
                Type cloudType = TypeLookup.Find("Atmosphere.CloudType");
                if (manager == null || layer == null || volume == null || field == null || cloudType == null) return;
                var link = new ReflectLink();
                getList = link.Method(manager, "CloudsManager", "GetObjectList", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy, null);
                bodyProperty = link.Property(layer, "CloudsObject", "Body", BindingFlags.Public | BindingFlags.Instance);
                volumeProperty = link.Property(layer, "CloudsObject", "LayerRaymarchedVolume", BindingFlags.Public | BindingFlags.Instance);
                typesProperty = link.Property(volume, "CloudsRaymarchedVolume", "CloudTypes", BindingFlags.Public | BindingFlags.Instance);
                particleFieldField = link.Field(volume, "CloudsRaymarchedVolume", "particleField", TypeLookup.Any);
                dropletsField = link.Field(volume, "CloudsRaymarchedVolume", "droplets", TypeLookup.Any);
                configField = link.Field(field, "ParticleField", "particleFieldConfig", TypeLookup.Any, typeof(string));
                coverageField = link.Field(field, "ParticleField", "currentCoverage", TypeLookup.Any, typeof(float));
                setRenderer = link.Method(field, "ParticleField", "SetRendererEnabled", BindingFlags.Public | BindingFlags.Instance, new[] { typeof(bool) });
                dropletsDensity = link.Field(cloudType, "CloudType", "dropletsDensity", TypeLookup.Any, typeof(float));
                wetIntensity = link.Field(cloudType, "CloudType", "wetSurfacesIntensity", TypeLookup.Any, typeof(float));
                lightningFrequency = link.Field(cloudType, "CloudType", "lightningFrequency", TypeLookup.Any, typeof(float));
                ambientVolume = link.Field(cloudType, "CloudType", "ambientVolume", TypeLookup.Any, typeof(float));
                summary = link.Complete
                    ? "found (CloudsObject -> CloudsRaymarchedVolume -> particleField, droplets, CloudType densities)"
                    : "not controllable (missing " + link.MissingText() + ")";
            }
            catch (Exception e)
            {
                getList = null;
                summary = "not controllable (" + CompatibilityLog.Reason(e) + ")";
            }
        }

        // Looks for the layers of a body that carry rain or droplets, at most every three seconds. Layers that have left the list are
        // dropped (their cloud types are put back first). In the installed configuration the rain comes from the layers
        // Kerbin-Weather-1 and Kerbin-weather-2 (cloud types Rain and Thunder), the base layers carry droplets-cloud-Kerbin. Known
        // gaps: water on the glass dries by EVE's dryingSpeed (about 12 s) after the droplets stop, wet ground stays until EVE dries
        // it, and the IVA rain sound is not replaced.
        internal static void Scan(string bodyName)
        {
            if (!Controllable) return;
            float now = Time.realtimeSinceStartup;
            if (now < nextScan) return;
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
                        if (volume == null) continue;
                        object field = particleFieldField.GetValue(volume);
                        string config = field != null ? configField.GetValue(field) as string : null;
                        bool rain = config != null && config.IndexOf("rain", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool droplets = dropletsField.GetValue(volume) != null;
                        if (!rain && !droplets) continue;
                        var entry = new Entry { Field = rain ? field : null, RainField = rain };
                        IEnumerable types = typesProperty.GetValue(volume, null) as IEnumerable;
                        if (types != null)
                        {
                            foreach (object type in types)
                            {
                                if (type != null) entry.Types.Add(new TypeRecord { Type = type });
                            }
                        }
                        found.Add(entry);
                    }
                }
                // Keep the records of layers that stay, so that the first values of zeroed cloud types are not lost.
                for (int i = 0; i < found.Count; i++)
                {
                    for (int j = 0; j < entries.Count; j++)
                    {
                        if (SameLayer(entries[j], found[i])) { found[i] = entries[j]; break; }
                    }
                }
                for (int j = 0; j < entries.Count; j++)
                {
                    if (!found.Contains(entries[j])) Restore(entries[j]);
                }
                entries.Clear();
                entries.AddRange(found);
                Count();
            }
            catch (Exception e)
            {
                entries.Clear();
                getList = null;
                summary = "not controllable (" + CompatibilityLog.Reason(e) + ")";
                SeasonsLog.Warn("seasons-eve-rain", "EVE rain: " + summary);
            }
        }

        private static bool SameLayer(Entry a, Entry b)
        {
            if (a.Types.Count == 0 || b.Types.Count == 0 || a.Types.Count != b.Types.Count) return false;
            return ReferenceEquals(a.Types[0].Type, b.Types[0].Type);
        }

        // The numbers of the log line, from the cloud types: which effects have a non-zero first value somewhere.
        private static void Count()
        {
            fields = 0;
            dropletLayers = 0;
            soundLayers = 0;
            lightningLayers = 0;
            wetLayers = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                if (entry.RainField) fields++;
                bool droplets = false, wet = false, lightning = false, sound = false;
                for (int j = 0; j < entry.Types.Count; j++)
                {
                    TypeRecord record = entry.Types[j];
                    droplets |= Original(record, dropletsDensity, record.Droplets) > 0f;
                    if (entry.RainField)
                    {
                        wet |= Original(record, wetIntensity, record.Wet) > 0f;
                        lightning |= Original(record, lightningFrequency, record.Lightning) > 0f;
                        sound |= Original(record, ambientVolume, record.Ambient) > 0f;
                    }
                }
                if (droplets) dropletLayers++;
                if (wet) wetLayers++;
                if (lightning) lightningLayers++;
                if (sound) soundLayers++;
            }
        }

        // The value a cloud type had before it was zeroed: the record if it is zeroed, the field if not.
        private static float Original(TypeRecord record, FieldInfo field, float kept)
        {
            return record.Kept ? kept : (float)field.GetValue(record.Type);
        }

        // Replaces the rain at the camera for this frame (once per frame) and returns what the rain would have been there, 0 to 1.
        // The caller raises the snow to that value.
        internal static float Suppress()
        {
            if (entries.Count == 0) return 0f;
            if (lastFrame == Time.frameCount) return lastCoverage;
            lastFrame = Time.frameCount;
            float rain = 0f;
            try
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    Entry entry = entries[i];
                    if (entry.RainField)
                    {
                        float current = (float)coverageField.GetValue(entry.Field);
                        if (current > rain) rain = current;
                        if (current != 0f)
                        {
                            coverageField.SetValue(entry.Field, ZeroValue);
                            setRenderer.Invoke(entry.Field, RendererOff);
                        }
                    }
                    if (!entry.Zeroed) Zero(entry);
                }
                suppressed = true;
            }
            catch (Exception e)
            {
                // What was zeroed goes back before the path is given up.
                for (int i = 0; i < entries.Count; i++) Restore(entries[i]);
                getList = null;
                summary = "not controllable (" + CompatibilityLog.Reason(e) + ")";
                SeasonsLog.Warn("seasons-eve-rain", "EVE rain: " + summary);
            }
            lastCoverage = Mathf.Clamp01(rain);
            return lastCoverage;
        }

        // Keeps the first values of a layer's cloud types and sets the rain effects to 0. Droplets only for a layer without rain.
        private static void Zero(Entry entry)
        {
            // Zeroed is set first: a failure half-way leaves the types that were kept to be put back, and only those.
            entry.Zeroed = true;
            for (int j = 0; j < entry.Types.Count; j++)
            {
                TypeRecord record = entry.Types[j];
                record.Droplets = (float)dropletsDensity.GetValue(record.Type);
                record.Wet = (float)wetIntensity.GetValue(record.Type);
                record.Lightning = (float)lightningFrequency.GetValue(record.Type);
                record.Ambient = (float)ambientVolume.GetValue(record.Type);
                record.Kept = true;
                dropletsDensity.SetValue(record.Type, ZeroValue);
                if (entry.RainField)
                {
                    wetIntensity.SetValue(record.Type, ZeroValue);
                    lightningFrequency.SetValue(record.Type, ZeroValue);
                    ambientVolume.SetValue(record.Type, ZeroValue);
                }
            }
        }

        // Puts every cloud type back to its first values and forgets the layers; the next scan finds them again. EVE sets its rain
        // fields again from its next update.
        internal static void Release()
        {
            for (int i = 0; i < entries.Count; i++) Restore(entries[i]);
            entries.Clear();
            nextScan = 0f;
            suppressed = false;
            lastCoverage = 0f;
        }

        private static void Restore(Entry entry)
        {
            if (!entry.Zeroed) return;
            entry.Zeroed = false;
            try
            {
                for (int j = 0; j < entry.Types.Count; j++)
                {
                    TypeRecord record = entry.Types[j];
                    if (!record.Kept) continue;
                    record.Kept = false;
                    dropletsDensity.SetValue(record.Type, record.Droplets);
                    if (entry.RainField)
                    {
                        wetIntensity.SetValue(record.Type, record.Wet);
                        lightningFrequency.SetValue(record.Type, record.Lightning);
                        ambientVolume.SetValue(record.Type, record.Ambient);
                    }
                }
            }
            catch (Exception)
            {
                // The layer is gone with its scene.
            }
        }
    }
}
