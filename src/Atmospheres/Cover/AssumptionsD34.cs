using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // What the surface cover takes for granted about the game's code (Assembly-CSharp of KSP 1.12). The entries name the members the
    // tracks and the ground cover reach by name; a change of the game that moves one makes the entry Changed and, with
    // WhenChanged.Fallback, switches the feature off. The cover calls no method of the game's wheel code: it reads three fields and a
    // transform, so the entries are checked by name and type, and an IL fingerprint (which ForeignAssumption.Method records for a
    // method a feature calls) has nothing to attach to. The hooks of the ground quad are overridden by the mod, not called; the entry
    // checks that they exist with the signature the mod overrides. Deviation from brief item 8, which asks for fingerprints: none is
    // recorded because no foreign method body is relied on; a wheel contact from WheelCollider.GetGroundHit would add one and was not
    // taken (ModuleWheelBase.isGrounded and radius give the contact at the 10 Hz of the stamps). Parallax is reached only through
    // ParallaxLink (its own assumption entries live there).
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
