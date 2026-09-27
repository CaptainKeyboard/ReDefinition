using System.Collections.Generic;
using System.Collections;
using System;
using KSP.UI;
using KSP.UI.Screens;
using KSP.UI.Screens.Editor;
using KSP.UI.TooltipTypes;
using ReDefinition.Core;
using ReDefinition.Settings;
using ReDefinition.Window;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Engineer
{
    // The engineer in the VAB and the SPH: the engineer window with the vessel's
    // stages, beside the parts list while the vessel is built
    // (docs/player/engineer.md), and the planner, a mode of the editor beside
    // KSP's own -- parts, actions, crew, cargo -- with a button of its own in
    // their bar (docs/player/planner.md). The numbers are KSP's own delta-v
    // simulation's (EditorReadout).
    //
    // KSP's mode buttons, read from the editor scene (sharedassets6, UnityPy)
    // and EditorLogic (decompiled): four Buttons in the top bar, 48 wide, placed
    // by hand rather than by a layout group, each drawing its tile as its own
    // sprite with sprite swap; the active mode's button is the one KSP makes not
    // interactable. KSP places them once, from the anchors crewBtnOrigPos,
    // cargoBtnOrigPos and editorBtnOrigPos, and moves the switch-editor button
    // up where the crew button is hidden. The engineer button takes the place one
    // step right of the last mode button, and the switch-editor button the one
    // after it. Both are set again every frame, so that they follow whatever KSP
    // does to the row, and the planner's button takes the cargo button's size.
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class EditorEngineer : MonoBehaviour
    {
        private const string TilePath = "ReDefinition/Icons/EngineerButton_";
        private const float RefreshInterval = 0.25f;

        private static bool kerNoticeShown;

        private EditorLogic editor;
        private Button button;
        private Sprite tileActive;
        private Sprite tileDisabled;
        private PlannerPanel panel;
        private EngineerWindow window;
        private bool measured;
        private bool open;
        // Set while this view switches KSP's mode itself, so that the screen
        // change it causes does not close the view it is opening.
        private bool switching;
        private float nextRefresh;
        private string keyText;
        private KeyCombination key = KeyCombination.None;

        internal EditorReadout Readout { get; private set; }

        internal static bool KerbalEngineerInstalled
        {
            get
            {
                foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
                    if (string.Equals(loaded.name, "KerbalEngineer", StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
        }

        private IEnumerator Start()
        {
            // EditorLogic sets its buttons up in its own Start.
            while (EditorLogic.fetch == null || EditorPanels.Instance == null) yield return null;
            yield return null;

            editor = EditorLogic.fetch;
            Readout = new EditorReadout();
            try
            {
                AddButton();
                GameEvents.onEditorScreenChange.Add(OnScreenChange);
                editor.partPanelBtn.onClick.AddListener(OnPartsButton);
                Canvas.willRenderCanvases += KeepButtons;
                RectTransform panels = EditorPanels.Instance.partsEditor.transform as RectTransform;
                window = EngineerWindow.Create(this, panels != null ? panels.parent as RectTransform : null);
                Debug.Log(Log.Tag + " Engineer window and planner added to the editor.");
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " The engineer window and the planner could not be added to the editor: " + e);
            }
            if (KerbalEngineerInstalled && !kerNoticeShown)
            {
                kerNoticeShown = true;
                ShowKerNotice();
            }
        }

        private void OnDestroy()
        {
            GameEvents.onEditorScreenChange.Remove(OnScreenChange);
            Canvas.willRenderCanvases -= KeepButtons;
            if (editor != null && editor.partPanelBtn != null) editor.partPanelBtn.onClick.RemoveListener(OnPartsButton);
            if (panel != null) panel.Destroy();
            if (window != null) window.Destroy();
            BodyPicker.Close();
            if (button != null) Destroy(button.gameObject);
        }

        private void Update()
        {
            if (editor == null) return;
            OwnSettings settings = Settings();
            if (settings != null && !KeyCapture.Busy && Key(settings.EngineerKey).Pressed() && window != null)
                window.ToggleFolded();

            // The vessel is read while the window or the planner shows it.
            bool partsShown = editor.editorScreen == EditorScreen.Parts;
            bool wanted = open || (partsShown && EngineerWindow.Wanted(settings));
            if (wanted && Time.unscaledTime >= nextRefresh)
            {
                nextRefresh = Time.unscaledTime + RefreshInterval;
                try
                {
                    Readout = EditorReadout.Read(settings != null && settings.EngineerVectoredThrust);
                }
                catch (Exception e)
                {
                    CompatibilityLog.Warn("engineer-readout", "The engineer could not read the vessel ("
                                                              + CompatibilityLog.Reason(e) + ").");
                }
            }

            try
            {
                if (open && panel != null) panel.Update();
                if (window != null) window.Update(settings, partsShown, open);
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("engineer-update", "The engineer window or the planner could not be drawn ("
                                                         + CompatibilityLog.Reason(e) + ").");
            }
        }

        internal static OwnSettings Settings()
        {
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            return addon != null ? addon.Live : null;
        }

        private KeyCombination Key(string text)
        {
            if (text == keyText) return key;
            keyText = text;
            key = text == null ? KeyCombination.None : KeyCombination.Parse(text);
            return key;
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
            if (window != null) window.ClearMark();
            if (PartListTooltipMasterController.Instance != null) PartListTooltipMasterController.Instance.HideTooltip();
            nextRefresh = 0f;
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
            if (editor.editorScreen == EditorScreen.Parts) return true;
            return editor.partPanelBtn != null && editor.partPanelBtn.interactable;
        }

        // ---------------------------------------------------------------- button

        private void AddButton()
        {
            Button cargo = editor.cargoPanelBtn;
            // Made under an inactive holder, so that nothing of KSP's cargo button
            // wakes up in the copy before it is taken off: only the Button, its tile
            // and its tooltip are the engineer's.
            GameObject holder = new GameObject("ReDefinitionEngineerHolder");
            holder.SetActive(false);
            GameObject copy = Instantiate(cargo.gameObject, holder.transform, false);
            foreach (MonoBehaviour behaviour in copy.GetComponents<MonoBehaviour>())
            {
                if (behaviour is Button || behaviour is Image || behaviour is TooltipController_Text) continue;
                DestroyImmediate(behaviour);
            }
            copy.transform.SetParent(cargo.transform.parent, false);
            // Right after the cargo button among its siblings: the bar drawn
            // after the mode buttons covers their lower part, and would not
            // cover the copy if it came last.
            copy.transform.SetSiblingIndex(cargo.transform.GetSiblingIndex() + 1);
            Destroy(holder);
            copy.name = "ButtonPanelEngineer";
            copy.SetActive(true);
            button = copy.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(Toggle);
            TooltipController_Text tooltip = copy.GetComponent<TooltipController_Text>();
            if (tooltip != null) tooltip.textString = "Planner";

            Sprite normal = Tile("Normal");
            Sprite hover = Tile("Hover");
            tileActive = Tile("Active");
            tileDisabled = Tile("Disabled");
            Image image = copy.GetComponent<Image>();
            if (normal != null && image != null)
            {
                image.sprite = normal;
                button.transition = Selectable.Transition.SpriteSwap;
                SpriteState states = button.spriteState;
                states.highlightedSprite = hover;
                states.pressedSprite = tileActive;
                states.disabledSprite = tileActive;
                button.spriteState = states;
            }
            else
            {
                Debug.LogWarning(Log.Tag + " The engineer button's tiles are missing from GameData/ReDefinition/Icons;"
                                 + " it shows KSP's cargo tile.");
            }
            KeepButtons();
        }

        private static Sprite Tile(string state)
        {
            Texture2D texture = GameDatabase.Instance.GetTexture(TilePath + state, false);
            if (texture == null) return null;
            return Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
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
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("engineer-button", "The engineer button could not be kept in its place ("
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
            Image source = cargo.GetComponent<Image>();
            Image image = button.GetComponent<Image>();
            if (source != null && image != null)
            {
                image.type = source.type;
                image.preserveAspect = source.preserveAspect;
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

        private void Place()
        {
            Transform last = null;
            foreach (Button mode in new[] { editor.partPanelBtn, editor.actionPanelBtn, editor.crewPanelBtn, editor.cargoPanelBtn })
            {
                if (mode == null || !mode.gameObject.activeSelf) continue;
                if (last == null || mode.transform.position.x > last.position.x) last = mode.transform;
            }
            if (last == null) return;
            Vector3 step = editor.editorBtnOrigPos != null && editor.cargoBtnOrigPos != null
                ? editor.editorBtnOrigPos.position - editor.cargoBtnOrigPos.position
                : Vector3.zero;
            if (step == Vector3.zero) return;
            button.transform.position = last.position + step;
            if (editor.switchEditorBtn != null) editor.switchEditorBtn.transform.position = last.position + step * 2f;
        }

        // ---------------------------------------------------------------- notice

        private static void ShowKerNotice()
        {
            MultiOptionDialog dialog = new MultiOptionDialog("ReDefinitionKerNotice",
                "Kerbal Engineer Redux is installed. ReDefinition's engineer window at the bottom of the editor"
                + " shows what Kerbal Engineer's build window shows, from KSP's own delta-v simulation. Kerbal"
                + " Engineer is no longer developed, and both side by side show the same numbers twice.\n\n"
                + "Uninstalling Kerbal Engineer is recommended: with CKAN, or by deleting GameData/KerbalEngineer.",
                "Kerbal Engineer Redux", HighLogic.UISkin, 440f,
                new DialogGUIButton("OK", () => { }, true));
            UnityMouseEvents.Shield(DialogOverWindow.Raise(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), dialog, false, HighLogic.UISkin)));
            Debug.Log(Log.Tag + " Kerbal Engineer Redux is installed; uninstalling it was recommended.");
        }
    }
}
