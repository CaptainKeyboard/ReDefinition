using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System;
using ReDefinition.Core;
using ReDefinition.Settings;
using UnityEngine.Rendering;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // Kerbal Frame Generator (HostStack).
    internal static partial class HostStack
    {
        private static string DisableKfg()
        {
            Type type = TypeLookup.Find(KfgSettingsTypeName);
            if (type == null) return "Kerbal Frame Generator not present.";

            object value;
            if (!TryGetStatic(type, KfgSwitchField, out value) || !(value is bool))
                return "Kerbal Frame Generator not readable.";
            // As first found -- or on, once it is found on: it was turned on after
            // it was switched off here, and Restore gives it back on.
            if (kfgFound == null || (bool)value) kfgFound = (bool)value;
            if (!(bool)value) return "Kerbal Frame Generator blend off.";
            TrySetStatic(type, KfgSwitchField, false);
            return "Kerbal Frame Generator blend switched off.";
        }

        // Back on only if it was found on at any point (kfgFound) and still
        // holds the off written here -- the switch is written only by KFG's initializer and
        // the player's toggle (KSR2_UI_Controller.cs), so that follows the
        // player.
        private static string RestoreKfg()
        {
            bool? found = kfgFound;
            kfgFound = null;
            if (found != true) return "";

            Type type = TypeLookup.Find(KfgSettingsTypeName);
            object value;
            if (type == null || !TryGetStatic(type, KfgSwitchField, out value)
                || !(value is bool) || (bool)value) return "";
            TrySetStatic(type, KfgSwitchField, true);
            return " Kerbal Frame Generator blend back on.";
        }
    }
}
