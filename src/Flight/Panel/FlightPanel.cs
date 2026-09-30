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
    // It switches to the profile of the situation the vessel is in once that has
    // held (FlightProfiles.Held), while the setting says so
    // (OwnSettings.FlightPanelAuto). A tab the player picks stays until the
    // situation changes.
    //
    // A profile added or deleted in the editor gets or loses its tab at once
    // (Refresh): ours are taken out of KSP's lists of tabs and buttons and made
    // anew the way its setupAllTabs makes them.
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
                        nameof(BeforeStart), nameof(AfterStart), null),
                }));
        }

        private void OnDestroy()
        {
            if (current == this) current = null;
            FlightPanelEditor.Close();
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
            holder = new GameObject("ReDefinitionPanelTemplates");
            holder.layer = manager.gameObject.layer;
            holder.SetActive(false);
            holder.transform.SetParent(manager.transform, false);
            BlockTemplate = Instantiate(block.gameObject, holder.transform, false);
            BlockTemplate.name = "ReadoutBlock";

            int at = tabs.IndexOf(orbit);
            tabs.RemoveAll(tab => tab is ManeuverNodeEditorTabOrbitBasic || tab is ManeuverNodeEditorTabOrbitAdv
                                  || tab is ManeuverNodeEditorTabIntercept);
            orbitTab = orbit;
            interceptTab = intercept;
            List<FlightProfile> profiles = FlightProfiles.Current;
            for (int i = 0; i < profiles.Count; i++) tabs.Insert(Math.Min(at + i, tabs.Count), Template(profiles[i], i));
            Debug.Log(Log.Tag + " Flight panel: " + profiles.Count + " profiles in place of KSP's orbit tabs.");
        }

        private static GameObject holder;
        private static ManeuverNodeEditorTab orbitTab;
        private static ManeuverNodeEditorTab interceptTab;

        // The prefab of a profile's tab, the `index`th of FlightProfiles.Current.
        private static ProfileTab Template(FlightProfile profile, int index)
        {
            GameObject template = ReDefinition.Window.UiHygiene.Child("ReDefinitionProfile_" + profile.Name, holder.transform, typeof(RectTransform));
            ProfileTab tab = template.AddComponent<ProfileTab>();
            tab.Profile = index;
            tab.tabPosition = orbitTab.tabPosition;
            tab.tabName = profile.Situation == PanelSituation.Rendezvous ? "Intercept" : "ReDefinition" + profile.Name;
            tab.tabTooltipCaptionActive = tab.tabTooltipCaptionInactive = profile.Name;
            tab.updateCooldownSeconds = 0.25f;
            Sprite on, off;
            Icons(profile.Situation, orbitTab, interceptTab, out on, out off);
            tab.tabIconOn = on;
            tab.tabIconOff = off;
            return tab;
        }

        private static readonly FieldInfo LeftButtons =
            typeof(ManeuverNodeEditorManager).GetField("leftTabButtons", BindingFlags.Instance | BindingFlags.NonPublic);

        // After KSP built its tabs: the tab buttons narrowed to fit the pane
        // (KSP's are 44 wide, three of them), and a pencil after them, a copy of
        // one without its toggle, that opens the profile editor for the tab shown.
        private static void AfterStart(ManeuverNodeEditorManager __instance)
        {
            try
            {
                Pencil(__instance);
                if (__instance.LeftTabs != null)
                    foreach (ManeuverNodeEditorTab tab in __instance.LeftTabs)
                    {
                        ProfileTab profile = tab as ProfileTab;
                        if (profile != null && profile.gameObject.activeSelf) Resize(profile, profile.Width);
                    }
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " The flight panel has no pencil to edit its profiles: " + e.Message);
            }
        }

        private static void Pencil(ManeuverNodeEditorManager manager)
        {
            List<ManeuverNodeEditorTabButton> buttons = LeftButtons != null ? LeftButtons.GetValue(manager) as List<ManeuverNodeEditorTabButton> : null;
            if (buttons == null || buttons.Count == 0) return;
            Transform row = buttons[0].transform.parent;
            float each = Narrow(manager, buttons);

            // A copy of KSP's tab button prefab, not of a tab's button: that one
            // is a Toggle switched on in the tabs' group, and a second one
            // switched on there would switch the first tab's off as it wakes.
            ManeuverNodeEditorTabButton prefab = ButtonPrefab != null ? ButtonPrefab.GetValue(manager) as ManeuverNodeEditorTabButton : null;
            GameObject pencil = Instantiate(prefab != null ? prefab.gameObject : buttons[0].gameObject, holder != null ? holder.transform : row, false);
            pencil.name = "ReDefinitionPanelEdit";
            // The Button takes the Toggle's look under the pointer, as the tabs
            // have it, and no selection from the keyboard (UiHygiene).
            UnityEngine.UI.Toggle toggle = pencil.GetComponent<UnityEngine.UI.Toggle>();
            UnityEngine.UI.Selectable.Transition transition = toggle != null ? toggle.transition : UnityEngine.UI.Selectable.Transition.ColorTint;
            UnityEngine.UI.SpriteState sprites = toggle != null ? toggle.spriteState : new UnityEngine.UI.SpriteState();
            UnityEngine.UI.ColorBlock colours = toggle != null ? toggle.colors : UnityEngine.UI.ColorBlock.defaultColorBlock;
            UnityEngine.UI.Graphic target = toggle != null ? toggle.targetGraphic : null;
            DestroyImmediate(pencil.GetComponent<ManeuverNodeEditorTabButton>());
            if (toggle != null) DestroyImmediate(toggle);
            UnityEngine.UI.Image background = pencil.GetComponent<UnityEngine.UI.Image>();
            UnityEngine.UI.Button click = pencil.AddComponent<UnityEngine.UI.Button>();
            click.targetGraphic = target != null ? target : background;
            click.transition = transition;
            click.spriteState = sprites;
            click.colors = colours;
            ReDefinition.Window.UiHygiene.MouseOnly(click);
            click.onClick.AddListener(() => FlightPanelEditor.Open(Shown(manager)));
            pencil.transform.SetParent(row, false);
            pencil.transform.localPosition = Vector3.zero;
            pencil.transform.localScale = Vector3.one;
            UnityEngine.UI.LayoutElement width = pencil.GetComponent<UnityEngine.UI.LayoutElement>()
                                                 ?? pencil.AddComponent<UnityEngine.UI.LayoutElement>();
            width.preferredWidth = each;
            Transform onIcon = pencil.transform.Find("IconOn");
            if (onIcon != null) onIcon.gameObject.SetActive(false);
            Transform offIcon = pencil.transform.Find("IconOff");
            Sprite sprite = Icon("Edit_Off");
            if (offIcon != null && sprite != null) offIcon.GetComponent<UnityEngine.UI.Image>().sprite = sprite;
            KSP.UI.TooltipTypes.TooltipController_Text tooltip = pencil.GetComponent<KSP.UI.TooltipTypes.TooltipController_Text>();
            if (tooltip != null) tooltip.textString = "Edit Profiles";
            Debug.Log(Log.Tag + " Flight panel: " + buttons.Count + " tab buttons " + each.ToString("0") + " wide, and a pencil.");
        }

        // The tab buttons as wide as lets them and the pencil share KSP's pane,
        // at most KSP's 44.
        private static float Narrow(ManeuverNodeEditorManager manager, List<ManeuverNodeEditorTabButton> buttons)
        {
            RectTransform pane = manager.transform.Find("LeftPane") as RectTransform;
            float room = pane != null ? pane.rect.width - (current != null ? current.applied : 0f) : 185f;
            float each = Mathf.Min(44f, Mathf.Floor(room / (buttons.Count + 1)));
            foreach (ManeuverNodeEditorTabButton button in buttons)
            {
                UnityEngine.UI.LayoutElement layout = button.GetComponent<UnityEngine.UI.LayoutElement>()
                                                      ?? button.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
                layout.preferredWidth = each;
            }
            // The pencil shares the row.
            Transform pencil = buttons.Count > 0 && buttons[0].transform.parent != null
                ? buttons[0].transform.parent.Find("ReDefinitionPanelEdit") : null;
            UnityEngine.UI.LayoutElement own = pencil != null ? pencil.GetComponent<UnityEngine.UI.LayoutElement>() : null;
            if (own != null) own.preferredWidth = each;
            return each;
        }

        private static readonly FieldInfo LeftTabsParent =
            typeof(ManeuverNodeEditorManager).GetField("leftTabsParent", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo LeftButtonsParent =
            typeof(ManeuverNodeEditorManager).GetField("leftTabButtonsParent", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ButtonPrefab =
            typeof(ManeuverNodeEditorManager).GetField("tabButtonPrefab", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo SetupTab =
            typeof(ManeuverNodeEditorTab).GetMethod("Setup", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(Transform) }, null);

        // The tabs after the editor changed the profiles, `show` the one to open:
        // where the same profiles stand in the same order, their names on their
        // buttons and, with `build`, each built anew; otherwise ours taken out of KSP's lists and made
        // again, their buttons before the pencil.
        internal static void Refresh(int show, bool build = true)
        {
            ManeuverNodeEditorManager manager = ManeuverNodeEditorManager.Instance;
            List<ManeuverNodeEditorTab> tabs = manager != null ? manager.LeftTabs : null;
            List<ManeuverNodeEditorTabButton> buttons = manager != null && LeftButtons != null
                ? LeftButtons.GetValue(manager) as List<ManeuverNodeEditorTabButton> : null;
            if (tabs == null || buttons == null || buttons.Count != tabs.Count || holder == null || orbitTab == null) return;
            List<FlightProfile> profiles = FlightProfiles.Current;

            List<ProfileTab> ours = new List<ProfileTab>();
            foreach (ManeuverNodeEditorTab tab in tabs)
                if (tab is ProfileTab) ours.Add((ProfileTab)tab);
            bool same = ours.Count == profiles.Count;
            for (int i = 0; same && i < ours.Count; i++) same = ours[i].Bound == profiles[i];
            if (same)
            {
                for (int i = 0; i < tabs.Count; i++)
                {
                    ProfileTab tab = tabs[i] as ProfileTab;
                    if (tab == null) continue;
                    tab.tabTooltipCaptionActive = tab.tabTooltipCaptionInactive = tab.Bound.Name;
                    if (buttons[i].tooltip != null) buttons[i].tooltip.textString = tab.Bound.Name;
                    if (build) tab.Build();
                }
                return;
            }

            RectTransform tabsParent = LeftTabsParent != null ? LeftTabsParent.GetValue(manager) as RectTransform : null;
            RectTransform buttonsParent = LeftButtonsParent != null ? LeftButtonsParent.GetValue(manager) as RectTransform : null;
            ManeuverNodeEditorTabButton prefab = ButtonPrefab != null ? ButtonPrefab.GetValue(manager) as ManeuverNodeEditorTabButton : null;
            if (tabsParent == null || buttonsParent == null || prefab == null || SetupTab == null)
            {
                Debug.LogWarning(Log.Tag + " The flight panel's tabs follow the profiles with the next flight scene: KSP's panel differs.");
                return;
            }

            int at = tabs.FindIndex(tab => tab is ProfileTab);
            if (at < 0) at = tabs.Count;
            for (int i = tabs.Count - 1; i >= 0; i--)
            {
                if (!(tabs[i] is ProfileTab)) continue;
                Destroy(tabs[i].gameObject);
                Destroy(buttons[i].gameObject);
                tabs.RemoveAt(i);
                buttons.RemoveAt(i);
            }
            // The prefabs too, for KSP's list of them to match.
            List<ManeuverNodeEditorTab> prefabs = manager.maneuverNodeEditorTabs;
            int prefabAt = prefabs.FindIndex(tab => tab is ProfileTab);
            if (prefabAt < 0) prefabAt = prefabs.Count;
            foreach (ManeuverNodeEditorTab old in prefabs.FindAll(tab => tab is ProfileTab)) Destroy(old.gameObject);
            prefabs.RemoveAll(tab => tab is ProfileTab);

            Transform pencil = buttonsParent.Find("ReDefinitionPanelEdit");
            for (int i = 0; i < profiles.Count; i++)
            {
                ProfileTab template = Template(profiles[i], i);
                prefabs.Insert(Math.Min(prefabAt + i, prefabs.Count), template);
                ProfileTab tab = Instantiate(template.gameObject).GetComponent<ProfileTab>();
                // Switched off until SetCurrentTab opens one: a tab shown asks the
                // panel for its width.
                tab.gameObject.SetActive(false);
                SetupTab.Invoke(tab, new object[] { tabsParent });
                tabs.Insert(at + i, tab);
                ManeuverNodeEditorTabButton button = Instantiate(prefab);
                button.Setup(buttonsParent, template, manager.OnTabButtonPressed);
                if (pencil != null) button.transform.SetSiblingIndex(pencil.GetSiblingIndex());
                buttons.Insert(at + i, button);
            }
            Narrow(manager, buttons);
            int open = at + Mathf.Clamp(show, 0, profiles.Count - 1);
            if (open < tabs.Count) manager.SetCurrentTab(open, ManeuverNodeEditorTabPosition.LEFT);
            Debug.Log(Log.Tag + " Flight panel: tabs made anew for " + profiles.Count + " profiles.");
        }

        // The profile of the tab shown.
        private static int Shown(ManeuverNodeEditorManager manager)
        {
            if (manager.LeftTabs != null)
                foreach (ManeuverNodeEditorTab tab in manager.LeftTabs)
                {
                    ProfileTab profile = tab as ProfileTab;
                    if (profile != null && profile.gameObject.activeSelf && profile.Bound != null)
                        return Math.Max(0, FlightProfiles.Current.IndexOf(profile.Bound));
                }
            return 0;
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
            // Not while KSP builds its tabs: every copy is active then, and only
            // the one its Start shows counts (AfterStart).
            ManeuverNodeEditorManager manager = ManeuverNodeEditorManager.Instance;
            if (panel == null || tab == null || !tab.isActiveAndEnabled || manager == null || !manager.IsReady) return;
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
            root.sizeDelta += new Vector2(delta, 0f);
            // The right pane has a transition of its own (In at 185, Out at -230),
            // and the mode buttons theirs: the states the maneuver modes use move.
            if (rightTransition != null) Move(rightTransition, delta, 0);
            else rightPane.anchoredPosition += new Vector2(delta, 0f);
            if (selector != null) Move(selector, delta, 3, 4);
            applied = extra;
            Debug.Log(Log.Tag + " Flight panel " + extra.ToString("0") + " wider for " + width.ToString("0") + " of readouts.");
        }

        // A transition's `states` by `delta` sideways, and where it stands in one of
        // them, its panel and the position it keeps to animate from.
        private static void Move(UIPanelTransition transition, float delta, params int[] states)
        {
            if (transition.states == null) return;
            Vector2 by = new Vector2(delta, 0f);
            foreach (int state in states)
                if (state < transition.states.Length && transition.states[state] != null) transition.states[state].position += by;
            if (Array.IndexOf(states, transition.StateIndex) < 0) return;
            RectTransform moved = transition.panelTransform != null ? transition.panelTransform : transition.transform as RectTransform;
            if (moved != null) moved.anchoredPosition += by;
            if (KeptPosition != null && KeptPosition.FieldType == typeof(Vector2))
                KeptPosition.SetValue(transition, (Vector2)KeptPosition.GetValue(transition) + by);
        }

        private UIPanelTransition rightTransition;

        private bool Find(ProfileTab tab)
        {
            if (leftPane != null && rightPane != null && root != null) return true;
            ManeuverNodeEditorManager manager = tab.mannodeEditorManager != null ? tab.mannodeEditorManager : ManeuverNodeEditorManager.Instance;
            if (manager == null) return false;
            root = manager.transform as RectTransform;
            leftPane = manager.transform.Find("LeftPane") as RectTransform;
            rightPane = manager.transform.Find("RightPane") as RectTransform;
            rightTransition = rightPane != null ? rightPane.GetComponent<UIPanelTransition>() : null;
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
        private PanelSituation? chosen;

        private void Update()
        {
            FlightPanelEditor.Tick();
            if (Time.unscaledTime < next) return;
            next = Time.unscaledTime + Interval;
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            ManeuverNodeEditorManager manager = ManeuverNodeEditorManager.Instance;
            if (addon == null || !addon.Live.FlightPanelAuto || manager == null || !manager.IsReady || manager.LeftTabs == null) return;
            PanelSituation situation = FlightProfiles.Held(FlightGlobals.ActiveVessel);
            if (chosen == situation) return;

            int open = -1;
            for (int i = 0; i < manager.LeftTabs.Count; i++)
                if (manager.LeftTabs[i] != null && manager.LeftTabs[i].gameObject.activeSelf) open = i;
            // The player picked another tab since: it stays until the situation
            // changes, which it just did.
            int wanted = -1;
            for (int i = 0; i < manager.LeftTabs.Count; i++)
            {
                ProfileTab tab = manager.LeftTabs[i] as ProfileTab;
                if (tab != null && tab.Bound != null && FlightProfiles.Current.Contains(tab.Bound) && tab.Bound.Situation == situation)
                {
                    wanted = i;
                    break;
                }
            }
            chosen = situation;
            if (wanted < 0 || wanted == open) return;
            manager.SetCurrentTab(wanted, ManeuverNodeEditorTabPosition.LEFT);
            Debug.Log(Log.Tag + " Flight panel switched to " + ((ProfileTab)manager.LeftTabs[wanted]).Bound.Name + ".");
        }
    }
}
