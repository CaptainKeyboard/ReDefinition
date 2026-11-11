using System;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // One ray down onto the ground collider, to put the track strips on the surface the wheels roll on. UnityEngine.Physics lives in
    // UnityEngine.PhysicsModule, which the project of the atmospheres does not reference (a request to add it is in the report), so the
    // call is made by reflection, found once (as FrostRoof does). The ground's layer is 15 (the PQS colliders). [VERIFY in the game]
    internal static class GroundRay
    {
        internal const int GroundMask = 1 << 15;

        private static bool prepared;
        private static MethodInfo raycast;
        private static Type hitType;
        private static PropertyInfo pointProperty;
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
                pointProperty = hitType.GetProperty("point");
                ignoreTriggers = Enum.ToObject(query, 1);
                if (pointProperty == null) raycast = null;
            }
            catch (Exception e)
            {
                AtmospheresLog.WarnOnce("tracks: the ground ray is not available (" + CompatibilityLog.Reason(e) + "); strips stand at the terrain height.");
                raycast = null;
            }
            return raycast != null;
        }

        // The point where a ray from `from` along `direction` meets the ground within `reach` metres; false where it meets nothing or the
        // ray is not available.
        internal static bool Hit(Vector3 from, Vector3 direction, float reach, out Vector3 point)
        {
            point = Vector3.zero;
            if (!Prepare()) return false;
            try
            {
                object[] args = { from, direction, Activator.CreateInstance(hitType), reach, GroundMask, ignoreTriggers };
                if (!(bool)raycast.Invoke(null, args)) return false;
                point = (Vector3)pointProperty.GetValue(args[2], null);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
