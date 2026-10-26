using System;
using System.Reflection;
using KSP.UI.Screens;
using KSP.UI.Screens.Flight;
using ReDefinition.Core;
using ReDefinition.Ui;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Hud
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
        // The flight's three buttons, each set as soon as it is found, looked for
        // over the first half minute of the scene, as KSP builds its HUD: the
        // alarm clock's through a search of every loaded object, the costly one,
        // at growing intervals (0.5 s, doubled each time), the others at every
        // pass. Then what was not found is written to the log, and none is looked
        // for again.
        private const float LookFor = 30f;
        private bool modeSet, alarmSet, speedSet, done;
        private float giveUpAt, alarmNext, alarmEvery = 0.5f;

        // The half minute to look for the flight's buttons starts with the scene.
        protected override void Awake()
        {
            base.Awake();
            giveUpAt = Time.unscaledTime + LookFor;
        }

        // The staging stack's tooltips, then the flight's three buttons, each until it is found or the half minute is over.
        protected override void Pass()
        {
            base.Pass();
            if (done || !Tips) return;
            float now = Time.unscaledTime;
            if (!modeSet)
            {
                AltitudeTumbler tumbler = AltitudeTumbler.Instance;
                Transform mode = tumbler != null ? Named(Above(tumbler.transform, "SlideFrameParent"), "AltitudeModeButton") : null;
                if (mode != null)
                {
                    Tip(mode.gameObject, "Toggle Altitude Mode");
                    modeSet = true;
                }
            }
            if (!speedSet)
            {
                SpeedDisplay speed = SpeedDisplay.Instance;
                if (speed != null && speed.button != null)
                {
                    Tip(speed.button.gameObject, "Toggle Speed Mode");
                    speedSet = true;
                }
            }
            if (!alarmSet && now >= alarmNext)
            {
                alarmNext = now + alarmEvery;
                alarmEvery *= 2f;
                Transform alarm = null;
                foreach (AlarmClockUINextAlarm next in Resources.FindObjectsOfTypeAll<AlarmClockUINextAlarm>())
                {
                    if (next == null || !next.gameObject.scene.IsValid()) continue;
                    Transform frame = Above(next.transform, "TimeFrame");
                    if (frame != null) alarm = frame.Find("alarmButton");
                }
                if (alarm != null)
                {
                    Tip(alarm.gameObject, "Alarm Clock");
                    alarmSet = true;
                }
            }
            if (modeSet && alarmSet && speedSet)
            {
                done = true;
                Debug.Log(Log.Tag + " Tooltips on the alarm clock's button, the altimeter's mode switch and the navball's speed field.");
            }
            else if (now >= giveUpAt)
            {
                done = true;
                System.Collections.Generic.List<string> missing = new System.Collections.Generic.List<string>();
                if (!modeSet) missing.Add("the altimeter's mode switch");
                if (!alarmSet) missing.Add("the alarm clock's button");
                if (!speedSet) missing.Add("the navball's speed field");
                Debug.Log(Log.Tag + " Not found in " + LookFor.ToString("0") + " s, so without a tooltip: " + string.Join(", ", missing.ToArray()) + ".");
            }
        }
    }

    // The editors' tooltips, with the staging's reset button, which asks before it resets.
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class HudTooltipsEditor : HudTooltips
    {
        // KSP's StageManager.Reset, not public: what the reset button does once the player agrees; the button whose click
        // asks now.
        private static readonly MethodInfo ResetMethod =
            typeof(StageManager).GetMethod("Reset", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        private Button asking;
        // The reset asks first in this scene (OwnSettings.StagingResetAsks).
        private bool asks;

        // Reads whether the reset asks first in this scene.
        protected override void Awake()
        {
            base.Awake();
            asks = FlightUiSwitch.On(s => s.StagingResetAsks);
        }

        // The staging stack's tooltips, the reset button's, and its click made to ask first.
        protected override void Pass()
        {
            base.Pass();
            StageManager manager = StageManager.Instance;
            Button reset = manager != null ? manager.resetButton : null;
            if (reset == null) return;
            if (Tips) Tip(reset.gameObject, "Reset Staging");
            if (asking == reset || ResetMethod == null || !asks) return;
            // KSP's own listener is the reset itself (StageManager.Start); ours asks
            // first.
            asking = reset;
            reset.onClick.RemoveAllListeners();
            reset.onClick.AddListener(() => Ask(manager));
            Debug.Log(Log.Tag + " The staging's reset button asks before it resets.");
        }

        // Asks before the staging is reset as KSP's reset button would.
        private static void Ask(StageManager manager)
        {
            ConfirmDialog.Show("ReDefinitionResetStaging", "Reset Staging", "Put every part back into the stage KSP gives it?", 320f,
                "Reset", () =>
                {
                    if (manager != null) ResetMethod.Invoke(manager, null);
                }, 100f);
        }
    }

    // The tooltips both scenes share: the staging stack's and the delta-v tools' button, checked twice a second.
    public abstract class HudTooltips : MonoBehaviour
    {
        // When the next pass runs; whether one failed, which ends the passes for the scene.
        private float next;
        private bool failed;
        // The tooltips in this scene (OwnSettings.HudTooltips).
        protected bool Tips { get; private set; }

        // Reads whether the tooltips are on in this scene.
        protected virtual void Awake()
        {
            Tips = FlightUiSwitch.On(s => s.HudTooltips);
        }

        // A pass twice a second; one that fails is logged and ends the passes.
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

        // The staging stack's tooltips: the total delta-v's button, each stage's plus, minus and delta-v bar, and the delta-v
        // tools' button.
        protected virtual void Pass()
        {
            if (!Tips) return;
            StageManager manager = StageManager.Instance;
            if (manager != null)
            {
                if (manager.deltaVTotalButton != null) Tip(manager.deltaVTotalButton.gameObject, "Toggle All Stage Details");
                if (manager.Stages != null)
                    foreach (StageGroup group in manager.Stages)
                    {
                        if (group == null) continue;
                        Tip(StageGroupFields.AddButton, group, "Add Stage");
                        Tip(StageGroupFields.DeleteButton, group, "Remove Stage");
                        Tip(StageGroupFields.DeltaVHeading, group, "Toggle Stage Details");
                    }
            }
            DeltaVApp app = DeltaVApp.Instance;
            if (app != null && app.appLauncherButton != null) Tip(app.appLauncherButton.gameObject, "Delta-V Tools");
        }

        // The tooltip on the stage's part that `field` reaches, where the stage has it.
        private static void Tip<T>(HarmonyLib.AccessTools.FieldRef<StageGroup, T> field, StageGroup group, string text) where T : Component
        {
            T part = field != null ? field(group) : null;
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
