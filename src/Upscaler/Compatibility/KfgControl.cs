using System.Collections.Generic;
using System.Text;
using System;
using ReDefinition.Core;

namespace ReDefinition.Upscaler
{
    // Kerbal Frame Generator's frame blend: switched off while the upscaler runs and put back on if it was on (HostStack).
    internal sealed class KfgControl : IHostFilter
    {
        // Kerbal Frame Generator (MangoTechKSP, MIT) blends each frame with the
        // one before in OnRenderImage (InterpolationEffect.cs). In flight it sits on
        // the camera KSP's GalaxyCameraControl belongs to, elsewhere on
        // Camera.main (KFG_UI_Controller.LateUpdate), the camera tagged
        // MainCamera, which in the editors the scene decides. On top of the
        // upscaler the blend is ghosting the upscaler cannot take out: it has no
        // motion vectors. The switch is global, a
        // public static field it reads every frame and never saves
        // (KSR2_Settings.cs), so what is set here lasts for this run only,
        // whichever camera the effect sits on.
        private const string SettingsTypeName = "KFG_Settings";
        private const string SwitchField = "effectEnabled";

        // The switch as first found, or on once found on; null: untouched.
        private bool? found;

        // The name in the notes of a step that failed.
        public string Label
        {
            get { return "Kerbal Frame Generator"; }
        }

        // Switches the blend off and appends what it found to the report.
        public void Apply(StringBuilder report)
        {
            report.Append(Disable()).Append(' ');
        }

        // Switches the blend off again if it was switched on since.
        public void Reassert()
        {
            Disable();
        }

        // Switches the blend back on if it was on, and appends what it did to the report.
        public void Restore(StringBuilder report)
        {
            report.Append(PutBack());
        }

        // The row for the blend's switch: suitable only when it is off. Nothing where the mod is not installed.
        public void AddLines(List<HostStack.Line> lines)
        {
            Type kfgType = TypeLookup.Find(SettingsTypeName);
            if (kfgType == null) return;

            object blend;
            bool readable = ForeignReflection.TryGetStatic(kfgType, SwitchField, out blend) && blend is bool;
            lines.Add(new HostStack.Line
            {
                Label = "Kerbal Frame Generator",
                Value = !readable ? "not readable"
                      : (bool)blend ? "switch on -- blends frames wherever its effect runs" : "switch off",
                Ok = readable && !(bool)blend,
                Present = true,
            });
        }

        // Switches Kerbal Frame Generator's blend off for this run and remembers in `found` whether it was on.
        internal string Disable()
        {
            Type type = TypeLookup.Find(SettingsTypeName);
            if (type == null) return "Kerbal Frame Generator not present.";

            object value;
            if (!ForeignReflection.TryGetStatic(type, SwitchField, out value) || !(value is bool))
                return "Kerbal Frame Generator not readable.";
            // As first found -- or on, once it is found on: it was turned on after
            // it was switched off here, and Restore gives it back on.
            if (found == null || (bool)value) found = (bool)value;
            if (!(bool)value) return "Kerbal Frame Generator blend off.";
            ForeignReflection.TrySetStatic(type, SwitchField, false);
            return "Kerbal Frame Generator blend switched off.";
        }

        // Back on only if it was found on at any point (`found`) and still
        // holds the off written here. The switch is written only by KFG's initializer and
        // the player's toggle (KSR2_UI_Controller.cs), so that follows the
        // player.
        internal string PutBack()
        {
            bool? wasOn = found;
            found = null;
            if (wasOn != true) return "";

            Type type = TypeLookup.Find(SettingsTypeName);
            object value;
            if (type == null || !ForeignReflection.TryGetStatic(type, SwitchField, out value)
                || !(value is bool) || (bool)value) return "";
            ForeignReflection.TrySetStatic(type, SwitchField, true);
            return " Kerbal Frame Generator blend back on.";
        }
    }
}
