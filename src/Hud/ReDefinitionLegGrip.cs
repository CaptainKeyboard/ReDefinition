using UnityEngine;

namespace ReDefinition.Hud
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
        // The default physics material's static friction, which the grips' frictions are taken against.
        private const float DefaultFriction = 0.4f;

        // The grips as the part keeps them, and as the button names them.
        private static readonly string[] Grips = { "Default", "MediumGrip", "HighGrip", "ExtremeGrip" };
        private static readonly string[] Names = { "Default", "Medium Grip", "High Grip", "Extreme Grip" };

        // The grip chosen, kept with the vessel.
        [KSPField(isPersistant = true)]
        public string grip = "Default";

        // The leg's wheel, whose friction multiplier the grip sets.
        private ModuleWheelBase wheel;

        // Finds the leg's wheel and names the button after the grip.
        public override void OnStart(StartState state)
        {
            wheel = part.FindModuleImplementing<ModuleWheelBase>();
            Label();
        }

        // A button that steps through the grips, as the grip pads' does in the editor
        // (ModulePhysicMaterial.ChangePhysicMaterial: "Traction: Medium Grip"), the
        // legs placed in symmetry with it.
        [KSPEvent(guiActiveEditor = true, guiName = "Traction")]
        public void ChangeGrip()
        {
            int at = System.Array.IndexOf(Grips, grip);
            string next = Grips[(at + 1) % Grips.Length];
            Set(next);
            foreach (Part counterpart in part.symmetryCounterparts)
            {
                ReDefinitionLegGrip other = counterpart != null ? counterpart.FindModuleImplementing<ReDefinitionLegGrip>() : null;
                if (other != null) other.Set(next);
            }
        }

        // The grip chosen, and the button named after it.
        private void Set(string value)
        {
            grip = value;
            Label();
        }

        // The button reads "Traction: " and the grip's name.
        private void Label()
        {
            int at = System.Array.IndexOf(Grips, grip);
            Events["ChangeGrip"].guiName = "Traction: " + Names[at < 0 ? 0 : at];
        }

        // Every physics frame: KSP puts the multiplier back to 1 where it sets up
        // the leg's friction controls (ModuleWheelBase.ActionUIUpdate).
        private void FixedUpdate()
        {
            if (wheel == null || !HighLogic.LoadedSceneIsFlight) return;
            float multiplier = Multiplier(grip);
            if (!Mathf.Approximately(wheel.frictionMultiplier, multiplier)) wheel.frictionMultiplier = multiplier;
        }

        // The wheel's friction multiplier for the grip: its static friction over the default's; 1 for Default.
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

        // The line in the part's info in the editor.
        public override string GetInfo()
        {
            return "Traction: Default, Medium, High or Extreme Grip, as on a grip pad.";
        }
    }
}
