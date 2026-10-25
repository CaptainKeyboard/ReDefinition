using System.Collections;
using System.Collections.Generic;
using System;
using KSP.UI;
using KSP.UI.Screens;
using KSP.UI.Screens.Editor;
using KSP.UI.TooltipTypes;
using ReDefinition.Core;
using ReDefinition.Window;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Engineer
{
    // The planner in the VAB and the SPH: a mode of the editor beside KSP's own
    // -- parts, actions, crew, cargo -- with a button of its own in their bar
    // (docs/player/planner.md). The vessel's numbers are KSP's own delta-v
    // simulation's (EditorReadout), read while the planner is open.
    //
    // KSP's mode buttons, read from the editor scene (sharedassets6, UnityPy)
    // and EditorLogic (decompiled): four Buttons in the top bar, 48 wide, placed
    // by hand rather than by a layout group, each drawing its tile as its own
    // sprite with sprite swap; the active mode's button is the one KSP makes not
    // interactable. KSP places them once, from the anchors crewBtnOrigPos,
    // cargoBtnOrigPos and editorBtnOrigPos, and moves the switch-editor button
    // up where the crew button is hidden. The planner button takes the place one
    // step right of the last mode button, and the switch-editor button the one
    // after it. Both are set again every frame, so that they follow whatever KSP
    // does to the row, and the planner's button takes the cargo button's size.
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class EditorEngineer : MonoBehaviour
    {
        private const string TilePath = "ReDefinition/Icons/EngineerButton_";
        // The vessel read again only where it changed: KSP modified, loaded or
        // started a new one (onEditorShipModified, which the part action window's
        // fields and resource sliders fire too; onEditorLoad, onEditorRestart),
        // finished its delta-v simulation (onDeltaVCalcsCompleted, which follows a
        // crossfeed, a resource's flow or a staging change), or its delta-v app
        // took another situation (onDeltaVAppAtmosphereChanged); and as the
        // planner opens, for what changed while it was closed (the crew).
        // Decompiled: UIPartActionFieldItem, UIPartActionResourceEditor,
        // VesselDeltaV, DeltaVAppSituation. At most one read in RefreshInterval,
        // so that a change and the simulation it starts make one or two reads.
        private const float RefreshInterval = 0.25f;

        private EditorLogic editor;
        private Button button;
        private Sprite tileActive;
        private Sprite tileDisabled;
        private PlannerPanel panel;
        private bool measured;
        private bool open;
        // Set while this view switches KSP's mode itself, so that the screen
        // change it causes does not close the view it is opening.
        private bool switching;
        private float nextRefresh;
        private bool changed = true;

        internal EditorReadout Readout { get; private set; }

        private IEnumerator Start()
        {
            // EditorLogic sets its buttons up in its own Start.
            float until = Time.unscaledTime + 15f;
            while (Time.unscaledTime < until && (EditorLogic.fetch == null || EditorPanels.Instance == null)) yield return null;
            if (EditorLogic.fetch == null || EditorPanels.Instance == null) yield break;
            yield return null;

            editor = EditorLogic.fetch;
            Readout = new EditorReadout();
            try
            {
                AddButton();
                GameEvents.onEditorScreenChange.Add(OnScreenChange);
                editor.partPanelBtn.onClick.AddListener(OnPartsButton);
                Canvas.willRenderCanvases += KeepButtons;
                Debug.Log(Log.Tag + " Planner added to the editor.");
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " The planner could not be added to the editor: " + e);
            }
            // A UI theme such as ZTheme swaps the pixels of KSP's textures as the
            // scene's UI is ready (HUDReplacer, onLevelWasLoadedGUIReady): the
            // planner button's tiles are made again after it.
            GameEvents.onLevelWasLoadedGUIReady.Add(OnGuiReady);
            GameEvents.onEditorShipModified.Add(OnShipModified);
            GameEvents.onDeltaVCalcsCompleted.Add(OnShipChanged);
            GameEvents.onDeltaVAppAtmosphereChanged.Add(OnSituationChanged);
            GameEvents.onEditorLoad.Add(OnShipLoaded);
            GameEvents.onEditorRestart.Add(OnShipChanged);
            StartCoroutine(TilesAgainLater());
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            GameEvents.onEditorScreenChange.Remove(OnScreenChange);
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnGuiReady);
            GameEvents.onEditorShipModified.Remove(OnShipModified);
            GameEvents.onDeltaVCalcsCompleted.Remove(OnShipChanged);
            GameEvents.onDeltaVAppAtmosphereChanged.Remove(OnSituationChanged);
            GameEvents.onEditorLoad.Remove(OnShipLoaded);
            GameEvents.onEditorRestart.Remove(OnShipChanged);
            Canvas.willRenderCanvases -= KeepButtons;
            if (editor != null && editor.partPanelBtn != null) editor.partPanelBtn.onClick.RemoveListener(OnPartsButton);
            if (panel != null) panel.Destroy();
            if (button != null) Destroy(button.gameObject);
            DestroyTiles();
        }

        // The editor's planner while a dashboard shows it (src/Dashboard/EditorTelemetry.cs):
        // it asks at every update it sends, and the planner works out its plan with the panel
        // closed as well, as long as it keeps asking.
        internal static EditorEngineer Instance { get; private set; }
        private const float AskedFor = 1f;
        private float dashboardAsked = -1e9f;
        private bool dashboardTried;

        // The planner, made where it was not yet; null where it cannot be.
        internal PlannerView PlannerForDashboard()
        {
            dashboardAsked = Time.unscaledTime;
            if (editor == null) return null;
            // Tried once: one that fails does not fail again at every update.
            if (panel == null && !dashboardTried)
            {
                dashboardTried = true;
                try
                {
                    panel = PlannerPanel.Create(this);
                }
                catch (Exception e)
                {
                    Debug.LogWarning(Log.Tag + " The planner could not be made for the dashboard: " + e);
                }
            }
            return panel != null ? panel.View : null;
        }

        private bool DashboardShows
        {
            get { return Time.unscaledTime - dashboardAsked < AskedFor; }
        }

        private void Awake()
        {
            Instance = this;
        }

        private void Update()
        {
            if (editor == null || (!open && !DashboardShows)) return;
            if (changed && Time.unscaledTime >= nextRefresh)
            {
                nextRefresh = Time.unscaledTime + RefreshInterval;
                changed = false;
                try
                {
                    Readout = EditorReadout.Read();
                }
                catch (Exception e)
                {
                    CompatibilityLog.Warn("engineer-readout", "The planner could not read the vessel ("
                                                              + CompatibilityLog.Reason(e) + ").");
                }
            }

            try
            {
                if (panel != null)
                {
                    if (open) panel.Tick();
                    else panel.TickHidden();
                }
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("engineer-update", "The planner could not be drawn ("
                                                         + CompatibilityLog.Reason(e) + ").");
            }
        }

        private void OnShipModified(ShipConstruct ship)
        {
            changed = true;
        }

        private void OnShipChanged()
        {
            changed = true;
        }

        private void OnShipLoaded(ShipConstruct ship, CraftBrowserDialog.LoadType type)
        {
            changed = true;
        }

        private void OnSituationChanged(DeltaVSituationOptions situation)
        {
            changed = true;
        }

        // --------------------------------------------------------------- opening

        internal void Toggle()
        {
            if (open) Close(true);
            else Open();
        }

        internal void Open()
        {
            if (open || editor == null || !ModeSwitchAllowed()) return;
            try
            {
                if (panel == null) panel = PlannerPanel.Create(this);
                if (panel == null) return;
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " The planner could not be made: " + e);
                return;
            }
            open = true;
            if (PartListTooltipMasterController.Instance != null) PartListTooltipMasterController.Instance.HideTooltip();
            nextRefresh = 0f;
            changed = true;
            if (editor.editorScreen != EditorScreen.Parts)
            {
                // KSP counts the planner as part of the parts mode: switch to
                // it first, and bring the planner in once the parts list is in,
                // so that the two transitions do not run against each other.
                switching = true;
                try
                {
                    editor.SelectPanelParts();
                }
                finally
                {
                    switching = false;
                }
                if (editor.editorScreen != EditorScreen.Parts)
                {
                    open = false;
                    return;
                }
                StartCoroutine(BringInWhenSettled());
                return;
            }
            panel.BringIn();
        }

        private IEnumerator BringInWhenSettled()
        {
            UIPanelTransition parts = EditorPanels.Instance != null ? EditorPanels.Instance.partsEditor : null;
            float until = Time.unscaledTime + 3f;
            while (parts != null && (parts.Transitioning || parts.State != "In") && Time.unscaledTime < until) yield return null;
            if (open && panel != null) panel.BringIn();
        }

        // `toParts`: back to the parts list, as KSP's parts button asks; any
        // other mode KSP brings in itself, and takes the planner out with it.
        internal void Close(bool toParts)
        {
            if (!open) return;
            open = false;
            if (panel != null)
            {
                panel.Closed();
                if (toParts) panel.BackToParts();
            }
            // The parts button was made clickable while the planner stood
            // open; KSP sets its buttons as they belong again.
            if (editor != null) editor.UpdateUI();
        }

        private void OnScreenChange(EditorScreen screen)
        {
            if (!switching) Close(false);
        }

        // KSP's parts button, which the open view makes clickable again: it
        // leads back to the parts list.
        private void OnPartsButton()
        {
            Close(true);
        }

        // As KSP allows its own mode buttons: not while a part is held, nor while
        // a mode change is under way or locked.
        private bool ModeSwitchAllowed()
        {
            if (!InputLockManager.IsUnlocked(ControlTypes.EDITOR_MODE_SWITCH)) return false;
            if (PanelsSliding()) return false;
            if (editor.editorScreen == EditorScreen.Parts) return true;
            return editor.partPanelBtn != null && editor.partPanelBtn.interactable;
        }

        // ---------------------------------------------------------------- button

        private void AddButton()
        {
            Button cargo = editor.cargoPanelBtn;
            // Made under an inactive holder, so that nothing of KSP's cargo button
            // wakes up in the copy before it is taken off: only the Button, its tile
            // and its tooltip are the planner's.
            GameObject holder = new GameObject("ReDefinitionEngineerHolder");
            holder.SetActive(false);
            GameObject copy = Instantiate(cargo.gameObject, holder.transform, false);
            foreach (MonoBehaviour behaviour in copy.GetComponents<MonoBehaviour>())
            {
                if (behaviour is Button || behaviour is Image || behaviour is TooltipController_Text) continue;
                DestroyImmediate(behaviour);
            }
            copy.transform.SetParent(cargo.transform.parent, false);
            // Right before the cargo button among its siblings. The mode buttons
            // overlap, and KSP's order draws each over the left edge of the one
            // to its right: the parts button, leftmost, comes last (the editor
            // scene's Top Bar, read with UnityPy). So the cargo button is drawn
            // over the copy's left edge, as over its other neighbours'.
            copy.transform.SetSiblingIndex(cargo.transform.GetSiblingIndex());
            Destroy(holder);
            copy.name = "ButtonPanelEngineer";
            copy.SetActive(true);
            button = copy.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(Toggle);
            TooltipController_Text tooltip = copy.GetComponent<TooltipController_Text>();
            if (tooltip != null) tooltip.textString = "Planner";

            Tiles(cargo);
            KeepButtons();
        }

        // The copy's tiles, made from the cargo button's tiles as they are: KSP's
        // own, or a UI theme's (ModeButtonTiles). Made again if the cargo
        // button's tile changes, as when a theme sets its own after the editor
        // starts. The drawn tiles stand in where one cannot be made.
        private Sprite madeFrom;

        private void OnGuiReady(GameScenes scene)
        {
            madeFrom = null;
        }

        private IEnumerator TilesAgainLater()
        {
            yield return new WaitForSecondsRealtime(1f);
            madeFrom = null;
        }

        // The cargo button's and the planner button's pictures, looked up once per
        // pair: asked for at every canvas render.
        private Button imagesOfCargo, imagesOfButton;
        private Image cargoImage, buttonImage;

        private void Images(Button cargo, out Image source, out Image image)
        {
            if (!ReferenceEquals(cargo, imagesOfCargo) || !ReferenceEquals(button, imagesOfButton) || cargoImage == null || buttonImage == null)
            {
                imagesOfCargo = cargo;
                imagesOfButton = button;
                cargoImage = cargo != null ? cargo.GetComponent<Image>() : null;
                buttonImage = button != null ? button.GetComponent<Image>() : null;
            }
            source = cargoImage;
            image = buttonImage;
        }

        private void Tiles(Button cargo)
        {
            Image source, image;
            Images(cargo, out source, out image);
            if (source == null || image == null || source.sprite == madeFrom) return;
            madeFrom = source.sprite;
            SpriteState from = cargo.spriteState;
            List<Sprite> making = new List<Sprite>();
            Sprite normal = Made(ModeButtonTiles.From(source.sprite), making) ?? Tile("Normal");
            Sprite hover = Made(ModeButtonTiles.From(from.highlightedSprite), making) ?? Tile("Hover");
            Sprite pressed = Made(ModeButtonTiles.From(from.pressedSprite), making) ?? Tile("Active");
            tileActive = Made(ModeButtonTiles.From(from.disabledSprite), making) ?? Tile("Active");
            tileDisabled = tileActive;
            if (normal == null)
            {
                madeTiles.AddRange(making);
                Debug.LogWarning(Log.Tag + " The planner button's tiles could not be made; it shows the cargo tile.");
                return;
            }
            image.sprite = normal;
            button.transition = Selectable.Transition.SpriteSwap;
            SpriteState states = button.spriteState;
            states.highlightedSprite = hover;
            states.selectedSprite = hover;
            states.pressedSprite = pressed;
            states.disabledSprite = open ? tileActive : tileDisabled;
            button.spriteState = states;
            DestroyTiles();
            madeTiles.AddRange(making);
            Debug.Log(Log.Tag + " Planner button tiles made from " + source.sprite.name + ".");
        }

        // The tiles ModeButtonTiles made, each with a texture of its own; destroyed
        // once the button shows newer ones, and with the editor.
        private readonly List<Sprite> madeTiles = new List<Sprite>();

        private static Sprite Made(Sprite sprite, List<Sprite> making)
        {
            if (sprite != null) making.Add(sprite);
            return sprite;
        }

        private void DestroyTiles()
        {
            foreach (Sprite sprite in madeTiles)
            {
                if (sprite == null) continue;
                if (sprite.texture != null) Destroy(sprite.texture);
                Destroy(sprite);
            }
            madeTiles.Clear();
        }

        private static Sprite Tile(string state)
        {
            return Shared.IconSprites.Load(TilePath + state);
        }

        // While a panel slides in or out, no mode button can be clicked. KSP
        // locks its buttons for its own transitions, but the planner's pass
        // its lock by; a mode chosen during one left the planner standing
        // beside the new panel (UIPanelTransitionManager, decompiled). KSP
        // sets the buttons as they belong again once the panels stand still.
        private bool locked;

        private static bool PanelsSliding()
        {
            EditorPanels panels = EditorPanels.Instance;
            if (panels == null || panels.panelManager == null || panels.panelManager.panels == null) return false;
            foreach (UIPanelTransition panel in panels.panelManager.panels)
                if (panel != null && panel.Transitioning) return true;
            return false;
        }

        private void LockWhileSliding()
        {
            if (PanelsSliding())
            {
                locked = true;
                foreach (Button mode in new[] { editor.partPanelBtn, editor.actionPanelBtn, editor.crewPanelBtn, editor.cargoPanelBtn })
                    if (mode != null) mode.interactable = false;
                button.interactable = false;
            }
            else if (locked)
            {
                locked = false;
                editor.UpdateUI();
            }
        }

        // Before the canvases draw, after every Update and LateUpdate: the row's
        // places, and which buttons can be clicked. KSP sets its buttons'
        // interactable state in its own update; the open view makes the parts
        // button clickable, which leads back to the parts list.
        private void KeepButtons()
        {
            if (button == null || editor == null) return;
            try
            {
                Place();
                Match(editor.cargoPanelBtn);
                Tiles(editor.cargoPanelBtn);
                bool allowed = ModeSwitchAllowed();
                SpriteState states = button.spriteState;
                Sprite disabled = open ? tileActive : tileDisabled;
                if (disabled != null && states.disabledSprite != disabled)
                {
                    states.disabledSprite = disabled;
                    button.spriteState = states;
                }
                button.interactable = !open && allowed;
                if (open && editor.partPanelBtn != null) editor.partPanelBtn.interactable = allowed;
                LockWhileSliding();
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("engineer-button", "The planner button could not be kept in its place ("
                                                         + CompatibilityLog.Reason(e) + ").");
            }
        }

        // The cargo button's size, anchors, scale and picture settings: the copy
        // was made from it, but KSP may change the original after it starts.
        // The first frame writes both to the log.
        private void Match(Button cargo)
        {
            if (cargo == null) return;
            RectTransform from = (RectTransform)cargo.transform;
            RectTransform to = (RectTransform)button.transform;
            if (!measured)
            {
                measured = true;
                Debug.Log(Log.Tag + " Mode buttons: cargo " + Describe(cargo) + "; planner " + Describe(button) + ".");
            }
            if (to.sizeDelta != from.sizeDelta) to.sizeDelta = from.sizeDelta;
            if (to.localScale != from.localScale) to.localScale = from.localScale;
            if (to.pivot != from.pivot) to.pivot = from.pivot;
            if (to.anchorMin != from.anchorMin) to.anchorMin = from.anchorMin;
            if (to.anchorMax != from.anchorMax) to.anchorMax = from.anchorMax;
            Image source, image;
            Images(cargo, out source, out image);
            if (source != null && image != null)
            {
                if (image.type != source.type) image.type = source.type;
                if (image.preserveAspect != source.preserveAspect) image.preserveAspect = source.preserveAspect;
            }
        }

        private static string Describe(Button button)
        {
            RectTransform rect = (RectTransform)button.transform;
            Image image = button.GetComponent<Image>();
            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            string sprite = image != null && image.sprite != null
                ? image.sprite.name + " " + image.sprite.rect.width.ToString("0") + "x" + image.sprite.rect.height.ToString("0")
                : "none";
            return "size " + rect.sizeDelta + ", scale " + rect.lossyScale + ", world " + (corners[2] - corners[0]) + ", image "
                   + (image != null ? image.type + (image.preserveAspect ? " kept aspect" : "") : "none") + ", sprite " + sprite
                   + ", sibling " + rect.GetSiblingIndex() + " under " + (rect.parent != null ? rect.parent.name : "nothing");
        }

        // KSP's mode buttons, kept: asked for at every canvas render.
        private readonly Button[] modes = new Button[4];

        private void Place()
        {
            Transform last = null;
            modes[0] = editor.partPanelBtn;
            modes[1] = editor.actionPanelBtn;
            modes[2] = editor.crewPanelBtn;
            modes[3] = editor.cargoPanelBtn;
            foreach (Button mode in modes)
            {
                if (mode == null || !mode.gameObject.activeSelf) continue;
                if (last == null || mode.transform.position.x > last.position.x) last = mode.transform;
            }
            if (last == null) return;
            Vector3 step = editor.editorBtnOrigPos != null && editor.cargoBtnOrigPos != null
                ? editor.editorBtnOrigPos.position - editor.cargoBtnOrigPos.position
                : Vector3.zero;
            if (step == Vector3.zero) return;
            MoveTo(button.transform, last.position + step);
            if (editor.switchEditorBtn != null) MoveTo(editor.switchEditorBtn.transform, last.position + step * 2f);
        }

        // Written only where it changes: a write marks the layout dirty.
        private static void MoveTo(Transform moved, Vector3 position)
        {
            if (moved.position != position) moved.position = position;
        }
    }
}
