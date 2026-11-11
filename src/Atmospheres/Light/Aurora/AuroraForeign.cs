using System;
using System.Collections;
using System.Reflection;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // Package D38. Finds out whether another mod already draws an aurora over a body: an EVE cloud layer of that body whose name has
    // "aurora" in it (SPVE, EVEVolumetricsAurorae, JOVE Lite). Ours is not an EVE layer, so any such layer is foreign. EVE is reached by
    // reflection only (Atmosphere.CloudsManager.GetObjectList, CloudsObject.Name and .Body, as in AssumptionsC23); where a member is
    // missing the answer is "none found" and nothing is logged. The result is cached per body for a few seconds: EVE rebuilds its layers
    // on a reload, and a scan walks every layer.
    internal static class AuroraForeign
    {
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static bool looked;
        private static MethodInfo getList;
        private static PropertyInfo nameProperty, bodyProperty;

        // The name of the first foreign aurora layer of the body, or null where there is none (or EVE cannot be read).
        internal static string Find(string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName)) return null;
            try
            {
                if (!Look()) return null;
                IEnumerable list = getList.Invoke(null, null) as IEnumerable;
                if (list == null) return null;
                foreach (object layer in list)
                {
                    if (layer == null) continue;
                    string name = nameProperty.GetValue(layer, null) as string;
                    string body = bodyProperty.GetValue(layer, null) as string;
                    if (name != null && string.Equals(body, bodyName, StringComparison.Ordinal) && IsAuroraName(name)) return name;
                }
            }
            catch (Exception e)
            {
                // A reflection failure means "cannot tell": the shell is drawn (said once, so that a double aurora can be traced to it).
                AtmospheresLog.WarnOnce("aurora: the EVE layers could not be read (" + e.GetType().Name + "); the shell is drawn whatever EVE shows");
            }
            return null;
        }

        // Whether a layer name says it is an aurora ("aurora" in it, in any case).
        internal static bool IsAuroraName(string name)
        {
            return name != null && name.IndexOf("aurora", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool Look()
        {
            if (looked) return getList != null && nameProperty != null && bodyProperty != null;
            looked = true;
            Type manager = TypeLookup.Find("Atmosphere.CloudsManager");
            Type layer = TypeLookup.Find("Atmosphere.CloudsObject");
            if (manager == null || layer == null) return false;
            getList = manager.GetMethod("GetObjectList", Static);
            nameProperty = layer.GetProperty("Name", Instance);
            bodyProperty = layer.GetProperty("Body", Instance);
            bool complete = getList != null && nameProperty != null && bodyProperty != null;
            if (!complete) AtmospheresLog.WarnOnce("aurora: EVE is installed but its layer list has another shape; foreign aurora layers are not detected");
            return complete;
        }
    }
}
