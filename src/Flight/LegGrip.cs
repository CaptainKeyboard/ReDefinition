using UnityEngine;

namespace ReDefinition.Flight
{
    // A landing leg's grip chosen as a grip pad's is (Breaking Ground's
    // ModulePhysicMaterial, docs/player/leg-grip.md). A pad sets a PhysicMaterial
    // on its collider; a leg stands on its wheel's collider, which does not take
    // one, and grips by its tyre friction (ModuleWheelBase: frictionAdherent,
    // frictionPeak and frictionLimit, each times frictionMultiplier, set every
    // physics frame while grounded; decompiled). KSP keeps the multiplier of the
    // stock legs at 1 (autoFrictionAvailable = False). Here the chosen grip sets
    // it to the grip's static friction over the default material's, as the
    // physics materials give them (SquadExpansion/Serenity/Resources/
    // PhysicMaterials.cfg: Default 0.4, Medium 0.5, High 0.75, Extreme 1.1);
    // Default leaves KSP's 1. Added to every leg whose friction KSP holds at 1 by
    // Patches/LegGrip.cfg, where ModuleManager is installed.
    public class ReDefinitionLegGrip : PartModule
    {
        private const float DefaultFriction = 0.4f;

        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "Traction")]
        [UI_ChooseOption(options = new[] { "Default", "MediumGrip", "HighGrip", "ExtremeGrip" },
            display = new[] { "Default", "Medium Grip", "High Grip", "Extreme Grip" },
            affectSymCounterparts = UI_Scene.All, scene = UI_Scene.All)]
        public string grip = "Default";

        private ModuleWheelBase wheel;

        public override void OnStart(StartState state)
        {
            wheel = part.FindModuleImplementing<ModuleWheelBase>();
        }

        // Every physics frame: KSP puts the multiplier back to 1 where it sets up
        // the leg's friction controls (ModuleWheelBase.ActionUIUpdate).
        private void FixedUpdate()
        {
            if (wheel == null || !HighLogic.LoadedSceneIsFlight) return;
            float multiplier = Multiplier(grip);
            if (!Mathf.Approximately(wheel.frictionMultiplier, multiplier)) wheel.frictionMultiplier = multiplier;
        }

        internal static float Multiplier(string grip)
        {
            switch (grip)
            {
                case "MediumGrip": return 0.5f / DefaultFriction;
                case "HighGrip": return 0.75f / DefaultFriction;
                case "ExtremeGrip": return 1.1f / DefaultFriction;
                default: return 1f;
            }
        }

        public override string GetInfo()
        {
            return "Traction: Default, Medium, High or Extreme Grip, as on a grip pad.";
        }
    }
}
