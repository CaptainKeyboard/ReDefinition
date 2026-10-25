using System.Collections;
using System.Reflection;
using KSP.UI.Screens.Flight;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Flight
{
    // The flight scene starts in KSP's maneuver mode rather than its staging mode,
    // while the setting says so (OwnSettings.FlightStartsInManeuverMode). KSP's
    // FlightUIModeController.Start sets STAGING (decompiled); its maneuver button
    // sets MANEUVER_INFO through SetMode and then updates its buttons' highlight
    // (OnManeuverPress, UpdateHighlighting), which is done here the same way once
    // KSP's Start has run.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class FlightStartMode : MonoBehaviour
    {
        // KSP's FlightUIModeController.UpdateHighlighting, not public; null where it has none.
        private static readonly MethodInfo UpdateHighlighting =
            typeof(FlightUIModeController).GetMethod("UpdateHighlighting", BindingFlags.Instance | BindingFlags.NonPublic);

        // Waits for KSP's own Start to set the staging mode, then switches to the maneuver mode as its button does.
        private IEnumerator Start()
        {
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            if (addon == null || !addon.Live.FlightStartsInManeuverMode) yield break;
            float until = Time.unscaledTime + 10f;
            while (Time.unscaledTime < until
                   && (FlightUIModeController.Instance == null || FlightUIModeController.Instance.Mode != FlightUIMode.STAGING))
                yield return null;
            // A frame more, so that KSP's own Start and the panels' first
            // transitions are done.
            yield return null;
            FlightUIModeController controller = FlightUIModeController.Instance;
            if (controller == null || controller.Mode != FlightUIMode.STAGING) yield break;
            controller.SetMode(FlightUIMode.MANEUVER_INFO);
            if (UpdateHighlighting != null) UpdateHighlighting.Invoke(controller, null);
            Debug.Log(Log.Tag + " Flight started in the maneuver mode.");
        }
    }
}
