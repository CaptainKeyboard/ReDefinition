using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // What the surface cover takes for granted about the game's code (Assembly-CSharp of KSP 1.12). The entries name the members the
    // tracks and the ground cover reach by name; a change of the game that moves one makes the entry Changed and, with
    // WhenChanged.Fallback, switches the feature off. No method body is relied on, so there is no method fingerprint to record: the
    // lookups are by name and type. Parallax is reached only through ParallaxLink (its own assumption entries live there).
    [AtmospheresAssumptions]
    internal static class AssumptionsD34
    {
        internal const string WheelId = "atmo-d34-wheel-contact";
        internal const string QuadId = "atmo-d34-pqs-quad";

        // ModuleWheelBase's contact state, read for the stamps: isGrounded is true while the wheel rests on something, wheelTransform is
        // the wheel's, radius its radius in metres. The contact point is the wheel's centre one radius along the local down. The wheel
        // collider and its hit stay unread (WheelCollider.GetGroundHit would need the physics module).
        public static readonly ForeignAssumption WheelContact =
            new ForeignAssumption(WheelId, "KSP", "the wheels of a vessel mark the cover (ModuleWheelBase.isGrounded, wheelTransform, radius)")
                .Type("ModuleWheelBase")
                .Field("ModuleWheelBase", "isGrounded", "System.Boolean")
                .Field("ModuleWheelBase", "radius", "System.Single")
                .Field("ModuleWheelBase", "wheelTransform", "UnityEngine.Transform")
                .OnChange(WhenChanged.Fallback, "wheels leave no tracks; kerbals on foot still do");

        // The ground quad: the mod's hooks and the fields read for the slope of a vertex.
        public static readonly ForeignAssumption GroundQuad =
            new ForeignAssumption(QuadId, "KSP", "the cover reads the slope of a ground quad (PQ.vertNormals, verts, quadTransform) and its hooks")
                .Type("PQ")
                .Field("PQ", "vertNormals", "UnityEngine.Vector3[]")
                .Field("PQ", "verts", "UnityEngine.Vector3[]")
                .Field("PQ", "quadTransform", "UnityEngine.Transform")
                .Calls("PQSMod", "OnQuadUpdateNormals", "PQ")
                .Calls("PQSMod", "OnQuadBuilt", "PQ")
                .OnChange(WhenChanged.Fallback, "the cover ignores the slope of the ground");
    }
}
