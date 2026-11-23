using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // What the solo mode asks for (E40 SOLO): one layer and one type of it drawn alone over a clear sky, at a stated strength, as a uniform field or as ONE cell.
    internal struct SoloSelection
    {
        // The layer's index in the stack and the type's index in its table; Layer below 0 is no solo (every layer, the field's own weather).
        internal int Layer, Type;
        // The forced map value: the cover share of a uniform field (the cover meaning: a map value is the share of the sky), or the peak of the cell.
        internal float Strength;
        // One cell instead of a uniform field: its e-folding radius in km (the tower is about 2 radii wide) and its centre in degrees as the MAP holds it (the pass shifts a layer's map by
        // its drift: the caller subtracts the layer's MapShiftDeg from the world longitude it wants).
        internal bool Cell;
        internal double CellKm, CellLatDeg, CellLonDeg;

        internal bool On { get { return Layer >= 0; } }
    }

    // The solo mode as data: the tuning keys that draw one layer and one type alone, from the stack's layers. The cloud viewer's "Solo layer / type" control and the solo probe (series K-S1)
    // use this one mechanism: the overrides of the keys the pass already reads (cloud.mapOverride.<layer>, cloud.typeOverride.<layer>, cloud.lowStratiformRule.<layer>, cloud.coverCombine) and
    // two new ones (cloud.soloLayer: the layer drawn, cloud.cellKm / cellLat / cellLon: the forced map as one cell), so that A/B and the presets of the viewer carry the state.
    // No layer name is fixed: the layers and their types are read from the stack, so another body's stack works the same way.
    internal static class CloudSoloPlan
    {
        // The default cell: the e-folding radius of the player's cumulonimbus rows (tools/weather_metrics/e40ty.py CB_CELL_KM).
        internal const double DefaultCellKm = 12.0;

        // The type value (0 to 1, the table's axis: type i of n stands at i / (n - 1)) of a type.
        internal static float TypeValue(CloudLayerDef layer, int type)
        {
            int n = layer.Types.Count;
            return n > 1 ? Math.Max(0, Math.Min(n - 1, type)) / (float)(n - 1) : 0f;
        }

        // The type whose value a type override has (the nearest), or -1 for none (a negative value).
        internal static int TypeAt(CloudLayerDef layer, float typeValue)
        {
            if (float.IsNaN(typeValue) || typeValue < 0f) return -1;
            int n = layer.Types.Count;
            return n > 1 ? (int)Math.Round(Math.Min(1f, typeValue) * (n - 1)) : 0;
        }

        // The name the control and the probe show: "<layer> / <type>".
        internal static string Label(CloudLayerDef layer, int type)
        {
            return layer.Name + " / " + layer.Types[type].Name;
        }

        // The text of a type's button: its name without the genus its layer's other types share ("Cumulus humilis" -> "Humilis"), first letter up.
        internal static string ShortLabel(CloudLayerDef layer, int type)
        {
            string name = layer.Types[type].Name ?? "";
            foreach (string genus in new[] { "Cumulonimbus ", "Cumulus " })
                if (name.StartsWith(genus, StringComparison.OrdinalIgnoreCase) && name.Length > genus.Length) name = name.Substring(genus.Length);
            return name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name.Substring(1);
        }

        // A type whose tower feeds an anvil (the cfg key outflow): drawn as a cell, the solo mode's one non-uniform case.
        internal static bool IsCellType(CloudLayerDef layer, int type)
        {
            return layer.Types[type].Outflow > 0f;
        }

        // The strength a type is shown at by default: the share of the sky a typical field of it covers (the cover meaning), a low stratiform layer closed, a cell at its strongest.
        // Chosen by the names, as WeatherPresets chooses the roles (no layer is fixed); a type of another name gets DefaultOther.
        internal const float DefaultOther = 0.6f;

        internal static float DefaultStrength(CloudLayerDef layer, int type)
        {
            if (string.Equals(layer.Group, CloudCoverShift.RainGroup, StringComparison.Ordinal)) return 1f;
            string t = layer.Types[type].Name ?? "";
            if (IsCellType(layer, type) || Has(t, "cumulonimbus")) return 1f;
            if (Has(t, "stratocumulus")) return 0.8f;
            if (Has(t, "altocumulus")) return 0.6f;
            if (Has(t, "humilis") || Has(t, "mediocris")) return 0.5f;
            if (Has(t, "congestus")) return 0.4f;
            if (Has(t, "cirro") || Has(t, "cirrus")) return 0.5f;
            if (Has(t, "stratus") || Has(t, "fog") || Has(t, "nimbo")) return 1f;
            return DefaultOther;
        }

        private static bool Has(string text, string part)
        {
            return text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // The overrides that make the selection: key and value, a value of NaN meaning the key's override is removed. With no solo (Layer below 0) every key is removed.
        // cloud.coverCombine is 0 so that the forced maps stay what they say (the pass scales the layer maps of a round to the field's cover otherwise); every layer's low stratiform rule is off
        // (a constant map then draws everywhere, not only where it rains); the other layers' maps are 0 (every reader of the maps, not only the shader's mask, sees them empty).
        internal static List<KeyValuePair<string, float>> Overrides(IList<CloudLayerDef> layers, SoloSelection s)
        {
            var list = new List<KeyValuePair<string, float>>();
            bool on = s.On && layers != null && s.Layer < layers.Count && s.Type >= 0 && s.Type < layers[s.Layer].Types.Count;
            list.Add(new KeyValuePair<string, float>(CloudLookTuning.TuneSoloLayer, on ? s.Layer + 1 : float.NaN));
            list.Add(new KeyValuePair<string, float>(CloudLookTuning.TuneCoverCombine, on ? 0f : float.NaN));
            bool cell = on && s.Cell && s.CellKm > 0.0;
            list.Add(new KeyValuePair<string, float>(CloudLookTuning.TuneCellKm, cell ? (float)s.CellKm : float.NaN));
            list.Add(new KeyValuePair<string, float>(CloudLookTuning.TuneCellLat, cell ? (float)s.CellLatDeg : float.NaN));
            list.Add(new KeyValuePair<string, float>(CloudLookTuning.TuneCellLon, cell ? (float)s.CellLonDeg : float.NaN));
            if (layers == null) return list;
            for (int k = 0; k < layers.Count; k++)
            {
                string name = layers[k].Name;
                if (string.IsNullOrEmpty(name)) continue;
                bool solo = on && k == s.Layer;
                list.Add(new KeyValuePair<string, float>(CloudLookTuning.TuneMapOverride + name, on ? (solo ? Math.Max(0f, Math.Min(1f, s.Strength)) : 0f) : float.NaN));
                list.Add(new KeyValuePair<string, float>(CloudLookTuning.TuneTypeOverride + name, solo ? TypeValue(layers[k], s.Type) : float.NaN));
                list.Add(new KeyValuePair<string, float>(CloudLookTuning.TuneLowStratiformRule + name, on ? 0f : float.NaN));
            }
            return list;
        }

        // The selection the overrides in force describe, read back through a reader of the effective values (a key without an override reads as its default: the solo layer 0, the maps
        // and types -1). Layer below 0 where no solo layer is set or its map or type is not forced.
        internal static SoloSelection Read(IList<CloudLayerDef> layers, Func<string, float, float> effective)
        {
            var none = new SoloSelection { Layer = -1, Type = -1 };
            if (layers == null) return none;
            float key = effective(CloudLookTuning.TuneSoloLayer, 0f);
            int layer = float.IsNaN(key) ? -1 : (int)Math.Round(key) - 1;
            if (layer < 0 || layer >= layers.Count) return none;
            float map = effective(CloudLookTuning.TuneMapOverride + layers[layer].Name, -1f);
            int type = TypeAt(layers[layer], effective(CloudLookTuning.TuneTypeOverride + layers[layer].Name, -1f));
            if (map < 0f || type < 0) return none;
            var s = new SoloSelection { Layer = layer, Type = type, Strength = map };
            float km = effective(CloudLookTuning.TuneCellKm, 0f);
            if (km > 0f)
            {
                s.Cell = true;
                s.CellKm = km;
                s.CellLatDeg = effective(CloudLookTuning.TuneCellLat, 0f);
                s.CellLonDeg = effective(CloudLookTuning.TuneCellLon, 0f);
            }
            return s;
        }

        // The keys the plan writes, for the restore (without the per-layer ones, which Overrides lists for the layers it is given).
        internal static IEnumerable<string> GlobalKeys()
        {
            yield return CloudLookTuning.TuneSoloLayer;
            yield return CloudLookTuning.TuneCoverCombine;
            yield return CloudLookTuning.TuneCellKm;
            yield return CloudLookTuning.TuneCellLat;
            yield return CloudLookTuning.TuneCellLon;
        }
    }
}
