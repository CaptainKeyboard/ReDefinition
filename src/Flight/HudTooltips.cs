using System;
using System.Reflection;
using KSP.UI.Screens;
using KSP.UI.Screens.Flight;
using ReDefinition.Core;
using ReDefinition.Window;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Flight
{
    // Tooltips for the buttons of KSP's HUD that have none, in KSP's own wording --
    // a name or two words, as "Toggle SAS", "Docking Mode", "Warp to Sunrise"
    // (TooltipController_Text on KSP's own buttons; sharedassets7, with KSP's
    // dictionary) -- and with KSP's own tooltip (UiHygiene.Tooltip):
    //
    // the staging stack, in flight and in the editors: a stage's plus and minus
    // buttons (StageGroup.addButton, deleteButton), its delta-v bar, which opens
    // and closes its details (StageGroup.DeltaVHeadingObject; the click is the
    // group's, StageGroup.OnPointerClick), the total delta-v's button
    // (StageManager.deltaVTotalButton), and the button of KSP's delta-v tools on
    // the toolbar (DeltaVApp.appLauncherButton);
    //
    // in flight: the alarm clock's button in the time panel (TimeFrame/alarmButton),
    // the altimeter's mode switch (AltitudeModeButton) and the navball's speed
    // field, a click on which goes from surface to orbit to target
    // (SpeedDisplay.button);
    //
    // in the editors: the staging's reset button (StageManager.resetButton), which
    // also asks before it resets.
    //
    // Stages come and go, and KSP builds its HUD over the first frames: looked at
    // twice a second, and set only where the tooltip is not there yet.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class HudTooltipsFlight : HudTooltips
    {
        private bool found;

        protected override void Pass()
        {
            base.Pass();
            if (found) return;
            AltitudeTumbler tumbler = AltitudeTumbler.Instance;
            Transform mode = tumbler != null ? Named(Above(tumbler.transform, "SlideFrameParent"), "AltitudeModeButton") : null;
            Transform alarm = null;
            foreach (AlarmClockUINextAlarm next in Resources.FindObjectsOfTypeAll<AlarmClockUINextAlarm>())
            {
                if (next == null || !next.gameObject.scene.IsValid()) continue;
                Transform frame = Above(next.transform, "TimeFrame");
                if (frame != null) alarm = frame.Find("alarmButton");
            }
            SpeedDisplay speed = SpeedDisplay.Instance;
            if (mode == null || alarm == null || speed == null || speed.button == null) return;
            Tip(mode.gameObject, "Toggle Altitude Mode");
            Tip(alarm.gameObject, "Alarm Clock");
            Tip(speed.button.gameObject, "Toggle Speed Mode");
            found = true;
            Debug.Log(Log.Tag + " Tooltips on the alarm clock's button, the altimeter's mode switch and the navball's speed field.");
        }
    }

    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class HudTooltipsEditor : HudTooltips
    {
        private static readonly MethodInfo ResetMethod =
            typeof(StageManager).GetMethod("Reset", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        private Button asking;

        protected override void Pass()
        {
            base.Pass();
            StageManager manager = StageManager.Instance;
            Button reset = manager != null ? manager.resetButton : null;
            if (reset == null) return;
            Tip(reset.gameObject, "Reset Staging");
            if (asking == reset || ResetMethod == null) return;
            // KSP's own listener is the reset itself (StageManager.Start); ours asks
            // first.
            asking = reset;
            reset.onClick.RemoveAllListeners();
            reset.onClick.AddListener(() => Ask(manager));
            Debug.Log(Log.Tag + " The staging's reset button asks before it resets.");
        }

        private static void Ask(StageManager manager)
        {
            MultiOptionDialog confirm = new MultiOptionDialog("ReDefinitionResetStaging",
                "Put every part back into the stage KSP gives it?", "Reset Staging", HighLogic.UISkin, 320f,
                new DialogGUIHorizontalLayout(
                    new DialogGUIButton("Reset", () =>
                    {
                        if (manager != null) ResetMethod.Invoke(manager, null);
                    }, 100f, 30f, true),
                    new DialogGUIFlexibleSpace(),
                    new DialogGUIButton(KSP.Localization.Localizer.Format("#autoLOC_174783"), () => { }, 100f, 30f, true)));
            UnityMouseEvents.Shield(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), confirm, false,
                HighLogic.UISkin));
        }
    }

    public abstract class HudTooltips : MonoBehaviour
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo AddButton = typeof(StageGroup).GetField("addButton", Private);
        private static readonly FieldInfo DeleteButton = typeof(StageGroup).GetField("deleteButton", Private);
        private static readonly FieldInfo DeltaVBar = typeof(StageGroup).GetField("DeltaVHeadingObject", Private);

        private float next;
        private bool failed;

        private void Update()
        {
            if (failed || Time.unscaledTime < next) return;
            next = Time.unscaledTime + 0.5f;
            try
            {
                Pass();
            }
            catch (Exception e)
            {
                failed = true;
                CompatibilityLog.Warn("hud-tooltips", "Some of the HUD's buttons have no tooltip (" + CompatibilityLog.Reason(e) + ").");
            }
        }

        protected virtual void Pass()
        {
            StageManager manager = StageManager.Instance;
            if (manager != null)
            {
                if (manager.deltaVTotalButton != null) Tip(manager.deltaVTotalButton.gameObject, "Toggle All Stage Details");
                if (manager.Stages != null)
                    foreach (StageGroup group in manager.Stages)
                    {
                        if (group == null) continue;
                        Tip(AddButton, group, "Add Stage");
                        Tip(DeleteButton, group, "Remove Stage");
                        Tip(DeltaVBar, group, "Toggle Stage Details");
                    }
            }
            DeltaVApp app = DeltaVApp.Instance;
            if (app != null && app.appLauncherButton != null) Tip(app.appLauncherButton.gameObject, "Delta-V Tools");
        }

        private static void Tip(FieldInfo field, StageGroup group, string text)
        {
            Component part = field != null ? field.GetValue(group) as Component : null;
            if (part != null) Tip(part.gameObject, text);
        }

        // Only where it is not there yet: asked for twice a second.
        protected static void Tip(GameObject target, string text)
        {
            if (target == null) return;
            KSP.UI.TooltipTypes.TooltipController_Text tooltip = target.GetComponent<KSP.UI.TooltipTypes.TooltipController_Text>();
            if (tooltip != null && tooltip.textString == text) return;
            UiHygiene.Tooltip(target, text);
        }

        // The first transform of that name from `from` upwards.
        protected static Transform Above(Transform from, string name)
        {
            for (Transform at = from; at != null; at = at.parent)
                if (at.name == name) return at;
            return null;
        }

        // The first transform of that name under `under`, hidden ones too.
        protected static Transform Named(Transform under, string name)
        {
            if (under == null) return null;
            foreach (Transform child in under.GetComponentsInChildren<Transform>(true))
                if (child.name == name) return child;
            return null;
        }
    }
}
