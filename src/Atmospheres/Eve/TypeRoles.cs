using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // Which cloud type of a layer plays which role, from the layer's type names and the body's role lists (CloudRules: snowTypeNames,
    // stratusTypeNames, dustTypeNames). Separate from TypeSlots so that the maths of the encoding stands without the body schema.
    internal static class TypeRoles
    {
        // The slots of a layer from its cloud type names (a name matches a list entry ignoring case). A type whose name has "storm",
        // "thunder" or "cumulonimbus" in it is the storm type.
        internal static TypeSlots FromNames(IList<string> typeNames, CloudRules rules)
        {
            var slots = new TypeSlots(typeNames == null ? 0 : typeNames.Count);
            if (typeNames == null) return slots;
            for (int i = 0; i < typeNames.Count; i++)
            {
                string name = typeNames[i] ?? "";
                if (rules != null && Contains(rules.StratusTypeNames, name)) slots.Assign(CloudRole.SnowStratus, i);
                else if (rules != null && Contains(rules.SnowTypeNames, name)) slots.Assign(CloudRole.SnowLight, i);
                else if (rules != null && Contains(rules.DustTypeNames, name)) slots.Assign(CloudRole.Dust, i);
                else if (name.IndexOf("storm", StringComparison.OrdinalIgnoreCase) >= 0
                         || name.IndexOf("thunder", StringComparison.OrdinalIgnoreCase) >= 0
                         || name.IndexOf("cumulonimbus", StringComparison.OrdinalIgnoreCase) >= 0) slots.Assign(CloudRole.Storm, i);
            }
            return slots;
        }

        private static bool Contains(IReadOnlyList<string> list, string name)
        {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
