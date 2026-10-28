using System;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Raytracing
{
    // Which of EVE's cloud layers are weather and which shine by themselves. A body can have many layers: Volumetric Clouds
    // brings a base layer, a cumulonimbus layer and weather layers, and SPVE (SpacePotato's Volumetric Enhancements) adds
    // cirrus, hurricanes, rain, fog, dust, ambience and the auroras. The weather layers are lit by the sun and the moon. An
    // aurora is not: its config gives it a negative skylightTintMultiplier (-10000 on Kerbin) so that it glows at night, and
    // its colour would change with the light handed to it, so it is left alone. It takes no part in the clouds' moonlight,
    // in the rays' cloud map or in the layers' height.
    //
    // The tint decides. The name ("aurora" in it) is a second sign where it can be read. Only EVE's cloud objects and Scatterer's
    // layers give one; the volumes of a renderer's list carry none, so the tint alone tells them. Every class that tells the kinds
    // asks this one (ForeignAssumptions.EveCloudKinds names what it reads).
    internal static class CloudLayerKinds
    {
        // The material property that tells; looked up on first use, so that the name rule below needs no Unity.
        private static int tintId = -1;

        private static int SkylightTintId
        {
            get
            {
                if (tintId < 0) tintId = Shader.PropertyToID("skylightTintMultiplier");
                return tintId;
            }
        }

        // Whether a layer's name says it shines by itself: "aurora" in it, in any case.
        internal static bool ShinesByName(string name)
        {
            return name != null && name.IndexOf("aurora", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Whether the layer named `name` with the raymarched material `raymarched` shines by itself. Either may be null.
        internal static bool IsEmissive(string name, Material raymarched)
        {
            if (ShinesByName(name)) return true;
            if (raymarched == null) return false;
            if (!raymarched.HasProperty(SkylightTintId))
            {
                WarnOnce("a cloud material has no skylightTintMultiplier");
                return false;
            }
            return raymarched.GetFloat(SkylightTintId) < 0f;
        }

        // The name of the EVE cloud object a Scatterer cloud layer (Scatterer.EVECloudLayer) was made from; null where it cannot be
        // read.
        internal static string NameOfLayer(object layer)
        {
            if (layer == null) return null;
            try
            {
                if (!layerLooked)
                {
                    layerLooked = true;
                    cloudObjectField = layer.GetType().GetField("CloudObject", TypeLookup.Any);
                }
                object cloudObject = cloudObjectField != null ? cloudObjectField.GetValue(layer) : null;
                return cloudObject != null ? NameOfObject(cloudObject) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // The name of an EVE cloud object (Atmosphere.CloudsObject.Name); null where it cannot be read. Then every layer counts as
        // weather unless its tint says otherwise.
        internal static string NameOfObject(object cloudsObject)
        {
            if (cloudsObject == null) return null;
            try
            {
                if (!objectLooked)
                {
                    objectLooked = true;
                    objectName = cloudsObject.GetType().GetProperty("Name", TypeLookup.Any);
                    if (objectName == null) WarnOnce("EVE's cloud objects give no name");
                }
                return objectName != null ? objectName.GetValue(cloudsObject, null) as string : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // The members looked up once, and whether each was looked for.
        private static FieldInfo cloudObjectField;
        private static PropertyInfo objectName;
        private static bool layerLooked, objectLooked, warned;

        // A missing member is told once: the layers are then told apart by what is left.
        private static void WarnOnce(string what)
        {
            if (warned) return;
            warned = true;
            CompatibilityLog.Warn("eve-cloud-kinds", "The auroras are told from the clouds by the tint alone or by the name alone: " + what + ".");
        }
    }
}
