using System.Collections;
using KSP.UI.Screens;
using ReDefinition.Autopilot;
using ReDefinition.Autopilot.Flight;
using ReDefinition.Core;
using ReDefinition.Ui;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Hud
{
    // MechJeb's Auto Staging as a button in the staging stack's bottom group, right before the total delta-v: lit while MechJeb stages by itself.
    // It is MechJeb's own switch (MechJebDriver.SetSwitch), the same the planner's window shows; the button is not there without MechJeb.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class AutoStagingButton : MonoBehaviour
    {
        private const float Height = 18f;
        private static readonly Color Off = new Color(0.75f, 0.78f, 0.8f, 1f);
        private static readonly Color On = new Color(0.45f, 0.9f, 0.45f, 1f);

        private GameObject holder;
        private TextMeshProUGUI label;
        private Image face;

        private IEnumerator Start()
        {
            float until = Time.unscaledTime + 15f;
            while (Time.unscaledTime < until && (StageManager.Instance == null || StageManager.Instance.deltaVTotalSection == null)) yield return null;
            StageManager manager = StageManager.Instance;
            if (manager == null || manager.deltaVTotalSection == null || manager.deltaVTotalSection.transform.parent == null) yield break;
            Transform total = manager.deltaVTotalSection.transform;
            TextMeshProUGUI font = total.GetComponentInChildren<TextMeshProUGUI>(true);

            holder = UiHygiene.Child("ReDefinitionAutoStaging", total.parent, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            holder.transform.SetSiblingIndex(total.GetSiblingIndex());
            LayoutElement layout = holder.GetComponent<LayoutElement>();
            layout.preferredHeight = Height;
            layout.minHeight = Height;
            face = holder.GetComponent<Image>();
            face.color = new Color(0f, 0f, 0f, 0.35f);
            GameObject text = UiHygiene.Child("Label", holder.transform, typeof(RectTransform), typeof(TextMeshProUGUI));
            RectTransform rect = (RectTransform)text.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            label = text.GetComponent<TextMeshProUGUI>();
            if (font != null) label.font = font.font;
            label.fontSize = 12f;
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;
            label.text = "Auto Staging";
            holder.GetComponent<Button>().onClick.AddListener(Toggle);
            UiHygiene.MouseOnly(holder);
            UiHygiene.Tooltip(holder, "Auto Staging");
            Debug.Log(Log.Tag + " The Auto Staging button is in the staging stack.");

            var wait = new WaitForSecondsRealtime(0.25f);
            while (true)
            {
                Refresh();
                yield return wait;
            }
        }

        private static bool Has(out bool on)
        {
            on = false;
            Vessel vessel = FlightGlobals.ActiveVessel;
            return vessel != null && MechJebDriver.GetSwitch(vessel, MechJebDriver.Switch.AutoStaging, out on);
        }

        private void Toggle()
        {
            bool on;
            if (Has(out on))
            {
                MechJebDriver.SetSwitch(FlightGlobals.ActiveVessel, MechJebDriver.Switch.AutoStaging, !on);
                RouteState kept = RouteState.Of(FlightGlobals.ActiveVessel);
                if (kept != null) kept.AutoStaging = !on;
            }
            Refresh();
        }

        private void Refresh()
        {
            bool on;
            bool has = Has(out on);
            if (holder.activeSelf != has) holder.SetActive(has);
            if (has) label.color = on ? On : Off;
        }
    }
}
