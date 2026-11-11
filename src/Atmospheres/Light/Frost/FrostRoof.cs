using System;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The roof test of the EVA camera: whether something solid lies above it within reach, so that weather does not reach the glass.
    // UnityEngine.Physics lives in UnityEngine.PhysicsModule, which the project of the atmospheres does not reference (the project file
    // belongs to the integration; a request to add the reference is in the report), so the one call is made by reflection, found once.
    // Any failure to find it counts as no roof.
    internal static class FrostRoof
    {
        private static bool prepared;
        private static MethodInfo raycast;
        private static Type hitType;
        private static PropertyInfo colliderProperty;
        private static object ignoreTriggers;

        private static bool Prepare()
        {
            if (prepared) return raycast != null;
            prepared = true;
            try
            {
                Type physics = Type.GetType("UnityEngine.Physics, UnityEngine.PhysicsModule");
                hitType = Type.GetType("UnityEngine.RaycastHit, UnityEngine.PhysicsModule");
                Type query = Type.GetType("UnityEngine.QueryTriggerInteraction, UnityEngine.PhysicsModule");
                if (physics == null || hitType == null || query == null) return false;
                raycast = physics.GetMethod("Raycast", new[] { typeof(Vector3), typeof(Vector3), hitType.MakeByRefType(), typeof(float), typeof(int), query });
                colliderProperty = hitType.GetProperty("collider");
                ignoreTriggers = Enum.ToObject(query, 1);
                if (colliderProperty == null) raycast = null;
            }
            catch (Exception e)
            {
                AtmospheresLog.WarnOnce("camera frost: the roof test is not available (" + CompatibilityLog.Reason(e) + ")");
                raycast = null;
            }
            return raycast != null;
        }

        // A ray from the camera (lifted by 0.6 m, past the helmet) straight up. The vessel's own parts do not count as a roof.
        // [VERIFY the layers 0 (parts) and 15 (scenery and terrain) in the game.]
        internal static bool Roofed(Vector3 camera, Vector3 up, float reach, int layerMask, Vessel vessel)
        {
            if (!Prepare()) return false;
            try
            {
                object[] args = { camera + up * 0.6f, up, Activator.CreateInstance(hitType), reach, layerMask, ignoreTriggers };
                if (!(bool)raycast.Invoke(null, args)) return false;
                Component collider = colliderProperty.GetValue(args[2], null) as Component;
                Part part = collider != null ? collider.GetComponentInParent<Part>() : null;
                return part == null || part.vessel != vessel;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
