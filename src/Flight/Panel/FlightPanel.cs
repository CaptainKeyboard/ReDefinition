using System;
using System.Collections.Generic;
using System.Reflection;
using KSP.UI;
using ReDefinition.Core;
using ReDefinition.Settings;
using UnityEngine;

namespace ReDefinition.Flight.Panel
{
    // The flight panel: KSP's maneuver panel with the player's profiles in place of
    // its three orbit tabs (docs/player/flight-panel.md). KSP builds the panel's
    // tabs from ManeuverNodeEditorManager.maneuverNodeEditorTabs as its Start runs,
    // each tab a copy of a prefab with a button beside the others (setupAllTabs;
    // decompiled). A prefix on that Start puts a tab of our own per profile in
    // place of the orbit, advanced orbit and intercept tabs (the left side's); the
    // maneuver node's own tabs, on the right, stay. The rendezvous tab takes the
    // intercept tab's name, by which KSP finds it for its target caption.
    //
    // The panel grows to the right where a profile needs more than its two
    // columns: its left pane (LeftPane, 185 wide) by as much, its right pane
    // (RightPane, at 185) and the whole (UIModeManNodeEditor, 313) with it, and the
    // mode buttons beside it (UIModeSelector, a UIPanelTransition whose states 3 and
    // 4 serve the maneuver modes; sharedassets7, FlightUIModeController's
    // UIPanelStates) by as much again.
    //
    // It switches to the profile of the situation the vessel is in
    // (FlightProfiles.Situation) once that has held for a second, while the
    // setting says so (OwnSettings.FlightPanelAuto). A tab the player picks stays
    // until the situation changes.
    //
    // The prefix is installed once as the game starts (FlightPanelHooks), before
    // any flight scene's panel starts.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class FlightPanelHooks : MonoBehaviour
    {
        private void Awake()
        {
            FlightPanel.Hook();
        }
    }

    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class FlightPanel : MonoBehaviour
    {

        private static bool hooked;
        private static FlightPanel current;

        internal static GameObject BlockTemplate;

        private static readonly FieldInfo KeptPosition =
            typeof(UIPanelTransition).GetField("panelPosition", BindingFlags.Instance | BindingFlags.NonPublic);

        private RectTransform leftPane;
        private RectTransform rightPane;
        private RectTransform root;
        private UIPanelTransition selector;
        private float applied;

        private void Awake()
        {
            current = this;
        }

        // Once, at the game's start, before any flight scene's panel starts.
        internal static void Hook()
        {
            if (hooked) return;
            hooked = true;
            HarmonyHooks.Safely("flight-panel", "The maneuver panel keeps KSP's orbit tabs", () =>
                HarmonyHooks.Install("ReDefinition.FlightPanel", typeof(FlightPanel), new List<HarmonyHook>
                {
                    new HarmonyHook(typeof(ManeuverNodeEditorManager).GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic),
                        nameof(BeforeStart), null, null),
                }));
        }

        private void OnDestroy()
        {
            if (current == this) current = null;
        }

        // Before KSP builds its tabs: the profiles in place of its orbit tabs.
        private static void BeforeStart(ManeuverNodeEditorManager __instance)
        {
            try
            {
                Replace(__instance);
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " The flight panel keeps KSP's orbit tabs: " + e.Message);
            }
        }

        private static void Replace(ManeuverNodeEditorManager manager)
        {
            List<ManeuverNodeEditorTab> tabs = manager.maneuverNodeEditorTabs;
            ManeuverNodeEditorTab orbit = tabs.Find(tab => tab is ManeuverNodeEditorTabOrbitBasic);
            ManeuverNodeEditorTab intercept = tabs.Find(tab => tab is ManeuverNodeEditorTabIntercept);
            Transform block = orbit != null ? orbit.transform.Find("ResultApoapsis") : null;
            if (orbit == null || block == null) throw new InvalidOperationException("no orbit tab with its apoapsis block");

            // Kept under the manager, switched off, so that neither the block nor
            // the tabs' prefabs run.
            GameObject holder = new GameObject("ReDefinitionPanelTemplates");
            holder.SetActive(false);
            holder.transform.SetParent(manager.transform, false);
            BlockTemplate = Instantiate(block.gameObject, holder.transform, false);
            BlockTemplate.name = "ReadoutBlock";

            int at = tabs.IndexOf(orbit);
            tabs.RemoveAll(tab => tab is ManeuverNodeEditorTabOrbitBasic || tab is ManeuverNodeEditorTabOrbitAdv
                                  || tab is ManeuverNodeEditorTabIntercept);
            List<FlightProfile> profiles = FlightProfiles.Current;
            for (int i = 0; i < profiles.Count; i++)
            {
                GameObject template = new GameObject("ReDefinitionProfile_" + profiles[i].Name, typeof(RectTransform));
                template.transform.SetParent(holder.transform, false);
                ProfileTab tab = template.AddComponent<ProfileTab>();
                tab.Profile = i;
                tab.tabPosition = orbit.tabPosition;
                tab.tabName = profiles[i].Situation == PanelSituation.Rendezvous ? "Intercept" : "ReDefinition" + profiles[i].Name;
                tab.tabTooltipCaptionActive = tab.tabTooltipCaptionInactive = profiles[i].Name;
                tab.updateCooldownSeconds = 0.25f;
                Sprite on, off;
                Icons(profiles[i].Situation, orbit, intercept, out on, out off);
                tab.tabIconOn = on;
                tab.tabIconOff = off;
                tabs.Insert(Math.Min(at + i, tabs.Count), tab);
            }
            Debug.Log(Log.Tag + " Flight panel: " + profiles.Count + " profiles in place of KSP's orbit tabs.");
        }

        // KSP's own icons for the orbit and the intercept; ours for the others
        // (GameData/ReDefinition/Icons/FlightPanel_*.png, tools/make_icon.py).
        private static void Icons(PanelSituation situation, ManeuverNodeEditorTab orbit, ManeuverNodeEditorTab intercept,
                                  out Sprite on, out Sprite off)
        {
            if (situation == PanelSituation.Orbit || (situation == PanelSituation.Rendezvous && intercept == null))
            {
                on = orbit.tabIconOn;
                off = orbit.tabIconOff;
                return;
            }
            if (situation == PanelSituation.Rendezvous)
            {
                on = intercept.tabIconOn;
                off = intercept.tabIconOff;
                return;
            }
            on = Icon(situation + "_On") ?? orbit.tabIconOn;
            off = Icon(situation + "_Off") ?? orbit.tabIconOff;
        }

        internal static Sprite Icon(string name)
        {
            Texture2D texture = GameDatabase.Instance != null ? GameDatabase.Instance.GetTexture("ReDefinition/Icons/FlightPanel_" + name, false) : null;
            return texture != null ? Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f)) : null;
        }

        // ----------------------------------------------------------- widening

        // A tab asks for `width`: the panel grows to the right by what it needs
        // beyond the two columns KSP's pane holds, or back.
        internal static void Resize(ProfileTab tab, float width)
        {
            FlightPanel panel = current;
            if (panel == null || tab == null || !tab.isActiveAndEnabled) return;
            panel.Grow(tab, width);
        }

        private void Grow(ProfileTab tab, float width)
        {
            if (!Find(tab)) return;
            // The tabs' own area (SelectedContent, 177 wide, fixed) grows with the
            // pane where it does not stretch with it.
            RectTransform content = tab.transform.parent as RectTransform;
            float room = 2f * ProfileTab.CellWidth + ProfileTab.Gap;
            float extra = Mathf.Max(0f, width - room);
            float delta = extra - applied;
            if (Mathf.Abs(delta) < 0.5f) return;
            if (content != null && Mathf.Approximately(content.anchorMin.x, content.anchorMax.x))
            {
                content.sizeDelta += new Vector2(delta, 0f);
                content.anchoredPosition += new Vector2(delta * content.pivot.x, 0f);
            }
            leftPane.sizeDelta += new Vector2(delta, 0f);
            rightPane.anchoredPosition += new Vector2(delta, 0f);
            root.sizeDelta += new Vector2(delta, 0f);
            if (selector != null && selector.states != null)
            {
                for (int i = 3; i <= 4 && i < selector.states.Length; i++)
                    if (selector.states[i] != null) selector.states[i].position += new Vector2(delta, 0f);
                if (selector.StateIndex == 3 || selector.StateIndex == 4)
                {
                    RectTransform moved = selector.panelTransform != null ? selector.panelTransform : selector.transform as RectTransform;
                    if (moved != null) moved.anchoredPosition += new Vector2(delta, 0f);
                    if (KeptPosition != null && KeptPosition.FieldType == typeof(Vector2))
                        KeptPosition.SetValue(selector, (Vector2)KeptPosition.GetValue(selector) + new Vector2(delta, 0f));
                }
            }
            applied = extra;
            Debug.Log(Log.Tag + " Flight panel " + extra.ToString("0") + " wider for " + width.ToString("0") + " of readouts.");
        }

        private bool Find(ProfileTab tab)
        {
            if (leftPane != null && rightPane != null && root != null) return true;
            ManeuverNodeEditorManager manager = tab.mannodeEditorManager != null ? tab.mannodeEditorManager : ManeuverNodeEditorManager.Instance;
            if (manager == null) return false;
            root = manager.transform as RectTransform;
            leftPane = manager.transform.Find("LeftPane") as RectTransform;
            rightPane = manager.transform.Find("RightPane") as RectTransform;
            FlightUIModeController controller = FlightUIModeController.Instance;
            Transform modes = controller != null && controller.uiModeFrame != null ? controller.uiModeFrame.transform : null;
            if (modes != null)
                foreach (UIPanelTransition transition in modes.GetComponentsInChildren<UIPanelTransition>(true))
                    if (transition.name == "UIModeSelector") selector = transition;
            return leftPane != null && rightPane != null && root != null;
        }

        // ----------------------------------------------------------- switching

        private const float Interval = 0.5f;
        private float next;
        private PanelSituation? seen;
        private float seenSince;
        private PanelSituation? chosen;

        private void Update()
        {
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + Interval;
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            ManeuverNodeEditorManager manager = ManeuverNodeEditorManager.Instance;
            if (addon == null || !addon.Live.FlightPanelAuto || manager == null || !manager.IsReady || manager.LeftTabs == null) return;
            PanelSituation situation = FlightProfiles.Situation(FlightGlobals.ActiveVessel);
            if (seen != situation)
            {
                seen = situation;
                seenSince = Time.unscaledTime;
                return;
            }
            if (Time.unscaledTime - seenSince < 1f || chosen == situation) return;

            int open = -1;
            for (int i = 0; i < manager.LeftTabs.Count; i++)
                if (manager.LeftTabs[i] != null && manager.LeftTabs[i].gameObject.activeSelf) open = i;
            // The player picked another tab since: it stays until the situation
            // changes, which it just did.
            int wanted = -1;
            for (int i = 0; i < manager.LeftTabs.Count; i++)
            {
                ProfileTab tab = manager.LeftTabs[i] as ProfileTab;
                if (tab != null && tab.Profile < FlightProfiles.Current.Count && FlightProfiles.Current[tab.Profile].Situation == situation)
                {
                    wanted = i;
                    break;
                }
            }
            chosen = situation;
            if (wanted < 0 || wanted == open) return;
            manager.SetCurrentTab(wanted, ManeuverNodeEditorTabPosition.LEFT);
            Debug.Log(Log.Tag + " Flight panel switched to " + FlightProfiles.Current[((ProfileTab)manager.LeftTabs[wanted]).Profile].Name + ".");
        }
    }
}
