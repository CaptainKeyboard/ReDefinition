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
    // The engineer view in the VAB and the SPH (docs/player/engineer.md): a mode
    // of the editor beside KSP's own -- parts, actions, crew, cargo -- with a
    // button of its own in their bar. Its view takes the place of the parts list,
    // in the same panel, and a summary line stands beside the parts list while it
    // is closed. The numbers are KSP's own delta-v simulation's (EditorReadout).
    //
    // KSP's mode buttons, read from the editor scene (sharedassets6, UnityPy)
    // and EditorLogic (decompiled): four Buttons in the top bar, 48 wide, placed
    // by hand rather than by a layout group, each drawing its tile as its own
    // sprite with sprite swap; the active mode's button is the one KSP makes not
    // interactable. KSP places them once, from the anchors crewBtnOrigPos,
    // cargoBtnOrigPos and editorBtnOrigPos, and moves the switch-editor button
    // up where the crew button is hidden. The engineer button takes the place one
    // step right of the last mode button, and the switch-editor button the one
    // after it -- set again every frame, so that whatever KSP does to the row, the
    // two follow it.
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
        private EngineerPanel panel;
        private EngineerSummary summary;
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
                summary = EngineerSummary.Create(this);
                Debug.Log(Log.Tag + " Engineer view added to the editor.");
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " The engineer view could not be added to the editor: " + e);
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
            if (summary != null) summary.Destroy();
            if (button != null) Destroy(button.gameObject);
        }

        private void Update()
        {
            if (editor == null) return;
            OwnSettings settings = Settings();
            if (settings != null && !KeyCapture.Busy && Key(settings.EngineerKey).Pressed()) Toggle();

            bool wanted = open || (summary != null && summary.Wanted(settings));
            if (wanted && Time.unscaledTime >= nextRefresh)
            {
                nextRefresh = Time.unscaledTime + RefreshInterval;
                try
                {
                    Readout = EditorReadout.Read(settings != null && settings.EngineerVectoredThrust);
                }
                catch (Exception e)
                {
                    CompatibilityLog.Warn("engineer-readout", "The engineer view could not read the vessel ("
                                                              + CompatibilityLog.Reason(e) + ").");
                }
            }

            try
            {
                if (open && panel != null) panel.Update(settings);
                if (summary != null) summary.Update(settings, !open && editor.editorScreen == EditorScreen.Parts);
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("engineer-update", "The engineer view could not be drawn ("
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
            if (open) Close();
            else Open();
        }

        internal void Open()
        {
            if (open || editor == null || !ModeSwitchAllowed()) return;
            if (editor.editorScreen != EditorScreen.Parts)
            {
                switching = true;
                try
                {
                    editor.SelectPanelParts();
                }
                finally
                {
                    switching = false;
                }
                if (editor.editorScreen != EditorScreen.Parts) return;
            }

            try
            {
                if (panel == null) panel = EngineerPanel.Create(this, EditorPanels.Instance.partsEditor.transform as RectTransform);
                if (panel == null) return;
                panel.Show(true);
                open = true;
                EditorPanels.Instance.searchField.Transition("Out");
                if (PartListTooltipMasterController.Instance != null) PartListTooltipMasterController.Instance.HideTooltip();
                nextRefresh = 0f;
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " The engineer view could not be opened: " + e);
            }
        }

        internal void Close()
        {
            if (!open) return;
            open = false;
            if (panel != null) panel.Show(false);
            if (editor != null && editor.editorScreen == EditorScreen.Parts && EditorPanels.Instance != null)
                EditorPanels.Instance.searchField.Transition("In");
            // The parts button was made clickable while the view stood open; KSP
            // sets its buttons as they belong again.
            if (editor != null) editor.UpdateUI();
        }

        private void OnScreenChange(EditorScreen screen)
        {
            if (!switching) Close();
        }

        // KSP's parts button, which the open view makes clickable again: it
        // leads back to the parts list.
        private void OnPartsButton()
        {
            Close();
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
            Destroy(holder);
            copy.name = "ButtonPanelEngineer";
            copy.SetActive(true);
            button = copy.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(Toggle);
            TooltipController_Text tooltip = copy.GetComponent<TooltipController_Text>();
            if (tooltip != null) tooltip.textString = "Engineer";

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
                "Kerbal Engineer Redux is installed. ReDefinition's engineer view -- the Δv button in the editor's"
                + " mode bar -- shows what Kerbal Engineer's build window shows, from KSP's own delta-v simulation,"
                + " inside KSP's editor. Kerbal Engineer is no longer developed, and both side by side show the same"
                + " numbers twice.\n\nUninstalling Kerbal Engineer is recommended: with CKAN, or by deleting"
                + " GameData/KerbalEngineer.",
                "Kerbal Engineer Redux", HighLogic.UISkin, 440f,
                new DialogGUIButton("OK", () => { }, true));
            UnityMouseEvents.Shield(DialogOverWindow.Raise(PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), dialog, false, HighLogic.UISkin)));
            Debug.Log(Log.Tag + " Kerbal Engineer Redux is installed; uninstalling it was recommended.");
        }
    }
}
