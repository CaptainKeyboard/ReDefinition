using System;
using System.Collections.Generic;
using ReDefinition.Bridges;
using ReDefinition.Core;
using ReDefinition.Upscaler;
using ReDefinition.Window;
using UnityEngine;

namespace ReDefinition
{
    // The add-on's part for its diagnostics window (IMGUI), the shell: a live strip at
    // the top (ReDefinitionAddon.DiagnosticsLiveStrip), the pages' list on the left, the
    // selected page on the right (DiagnosticsPages, DiagnosticsMods), the pinned options
    // at the foot. The window's options are data (DiagnosticsOptions); their rows are
    // drawn by DiagnosticsRows.
    public partial class ReDefinitionAddon
    {
        private bool windowVisible;

        private const string ContextFailedText =
            "frame generation's context could not be made (see ReDefinitionProxy.log)";

        // ContextFailedText, with the way out once the proxy no longer tries.
        private static string ContextFailedNote()
        {
            return ContextFailedText + (FrameGenerationBridge.ContextFailureStands
                ? "; switch frame generation off and on to try again"
                : "");
        }

        private const float WindowWidth = 720f;
        private const float WindowHeight = 500f;
        private const float CategoryWidth = 150f;
        private const string SearchControl = "ReDefinitionDiagnosticsSearch";
        private const string TypingLock = "ReDefinitionDiagnosticsSearch";

        // The page shown; changed at the next layout pass (ApplyPendingChanges), as
        // everything that changes which controls the window holds: IMGUI needs the same
        // controls in the layout and in the event that follows it.
        private DebugCategory category = DebugCategory.Overview;
        private Vector2 paneScroll;
        // Closed by default: the motion and depth previews read the image back from the GPU and
        // cost frame rate while they are shown -- which is while their page is.
        private UpscalerRig.PreviewMode previewMode = UpscalerRig.PreviewMode.MotionVectors;
        private string hostStackMessage;

        // The search and the "Changed" filter: what is typed, and what the pages show.
        private string typedQuery = "";
        private string query = "";
        private bool changedOnly;
        private bool searchTyping;
        private Rect searchRect;

        private DebugCategory? pendingCategory;
        private string pendingPin;
        private bool pendingChangedToggle;
        private bool pendingReset;

        private Rect window = new Rect(60f, 60f, WindowWidth, WindowHeight);
        private int windowId;
        private DiagnosticsStyles styles;
        private string tooltipNow = "";

        private bool FilterActive
        {
            get { return query.Trim().Length > 0 || changedOnly; }
        }

        private void ToggleWindow()
        {
            windowVisible = !windowVisible;
            if (!windowVisible) ReleaseTyping();
        }

        // From the settings window's Diagnostics button.
        internal void ShowDiagnostics()
        {
            windowVisible = true;
        }

        // Drawn with KSP's own IMGUI skin, as RemoteTech, Parallax and TUFX draw
        // their windows. HUDReplacer patches that skin's getter and replaces its
        // textures by name -- window, button, toggle, label, slider, scrollbars
        // (Patches/HighLogic_Skin.cs there) -- so a UI theme such as ZTheme reaches
        // this window for every texture it ships under those names. Unity resets
        // GUI.skin to its default before every OnGUI (GUIUtility.BeginGUI) and hands
        // the skin current at GUILayout.Window to the window function, so setting it
        // here is enough. The cached styles copy HighLogic.Skin, never Unity's
        // default.
        private void OnGUI()
        {
            // Not while KSP's interface is hidden (F2, screenshots), as the rest of it.
            if (!windowVisible || (KSP.UI.UIMasterController.Instance != null && !KSP.UI.UIMasterController.Instance.IsUIShowing))
            {
                UnityMouseEvents.ImguiWindow = Rect.zero;
                ReleaseTyping();
                return;
            }

            GUI.skin = HighLogic.Skin;
            if (styles == null) styles = new DiagnosticsStyles(HighLogic.Skin);
            EnsureOptions();

            window.width = WindowWidth;
            window.height = WindowHeight;
            window = GUILayout.Window(windowId, window, DrawWindow, "ReDefinition",
                                      GUILayout.Width(WindowWidth), GUILayout.Height(WindowHeight));
            // A part of it stays on the screen to be taken hold of; a window taller
            // than the screen can still be dragged up to its lower end.
            const float grip = 40f;
            window.x = Mathf.Clamp(window.x, grip - window.width, Screen.width - grip);
            window.y = Mathf.Clamp(window.y, grip - window.height, Screen.height - grip);
            // Not click-through: what lies behind it is kept from the click
            // (UnityMouseEvents), as behind the settings window.
            UnityMouseEvents.ImguiWindow = window;
        }

        private void DrawWindow(int id)
        {
            if (Event.current.type == EventType.Layout) ApplyPendingChanges();

            GUILayout.BeginVertical();

            DrawLiveStrip();
            GUILayout.Space(4f);

            GUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
            DrawCategoryList();
            GUILayout.Space(6f);
            DrawPane();
            GUILayout.EndHorizontal();

            GUILayout.Space(4f);
            DrawFoot();

            GUILayout.EndVertical();

            EndOfWindow();
            GUI.DragWindow();
        }

        // The changes the last events asked for, made where the layout starts.
        private void ApplyPendingChanges()
        {
            if (pendingCategory.HasValue)
            {
                category = pendingCategory.Value;
                paneScroll = Vector2.zero;
                pendingCategory = null;
            }
            if (pendingPin != null)
            {
                pins.Toggle(pendingPin);
                settings.DiagnosticsPins = pins.Text;
                pendingPin = null;
            }
            if (pendingChangedToggle)
            {
                changedOnly = !changedOnly;
                paneScroll = Vector2.zero;
                pendingChangedToggle = false;
            }
            if (pendingReset)
            {
                pendingReset = false;
                int reset = debugOptions.Reset();
                Debug.Log(Log.Tag + " Debug switches put back to their defaults (" + reset + " changed).");
            }
            if (typedQuery != query)
            {
                query = typedQuery;
                paneScroll = Vector2.zero;
            }
            RefreshOptionStates();
        }

        // The pages, as a list of toggle buttons; with a search or the "Changed" filter
        // each says how many of its options pass.
        private void DrawCategoryList()
        {
            GUILayout.BeginVertical(GUILayout.Width(CategoryWidth));
            bool filtering = FilterActive;
            foreach (DebugCategory page in Pages)
            {
                string text = DebugOptionSet.Title(page);
                bool optionPage = page != DebugCategory.Previews && page != DebugCategory.OtherMods;
                int count = filtering && optionPage ? debugOptions.Count(page, query, changedOnly) : -1;
                if (count > 0) text += "  (" + count + ")";
                Color previous = GUI.contentColor;
                if (count == 0 && page != category) GUI.contentColor = DiagnosticsStyles.Grey;
                if (GUILayout.Toggle(category == page, new GUIContent(text, ""), styles.Category) && category != page)
                    pendingCategory = page;
                GUI.contentColor = previous;
            }
            GUILayout.EndVertical();
        }

        private static readonly DebugCategory[] Pages =
        {
            DebugCategory.Overview, DebugCategory.UpscalerInputs, DebugCategory.FrameGeneration,
            DebugCategory.GameSettings, DebugCategory.Capture, DebugCategory.Previews, DebugCategory.OtherMods,
        };

        // The selected page, scrolling by itself; while a search or the "Changed" filter is on,
        // every option that passes, from all the pages (the page of other mods' lines
        // filters its own lines).
        private void DrawPane()
        {
            paneScroll = GUILayout.BeginScrollView(paneScroll, false, false, GUILayout.ExpandWidth(true),
                                                   GUILayout.ExpandHeight(true));
            if (category == DebugCategory.OtherMods) DrawOtherMods();
            else if (FilterActive) DrawResults();
            else
                switch (category)
                {
                    case DebugCategory.Overview: DrawOverview(); break;
                    case DebugCategory.UpscalerInputs: DrawUpscalerInputs(); break;
                    case DebugCategory.FrameGeneration: DrawFrameGeneration(); break;
                    case DebugCategory.GameSettings: DrawGameSettings(); break;
                    case DebugCategory.Capture: DrawCapture(); break;
                    default: DrawPreview(); break;
                }
            GUILayout.EndScrollView();
        }

        // The window's foot: the pinned options as compact toggles, and Close.
        private void DrawFoot()
        {
            List<DebugOption> pinned = pins.Options(debugOptions);
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            if (pinned.Count == 0)
                GUILayout.Label("Pin an option with its star to keep it here.", styles.Dim);
            else
                DrawPinned(pinned, WindowWidth - 110f);
            GUILayout.EndVertical();
            if (GUILayout.Button("Close", GUILayout.Width(80f))) ToggleWindow();
            GUILayout.EndHorizontal();
        }

        // After the controls: the tooltip under the pointer, and the keyboard.
        private void EndOfWindow()
        {
            if (Event.current.type == EventType.Repaint)
            {
                tooltipNow = GUI.tooltip ?? "";
                DrawTooltip();
                bool typing = GUI.GetNameOfFocusedControl() == SearchControl;
                if (typing != searchTyping) SetTyping(typing);
            }
            // A click anywhere but in the search field ends the typing.
            if (Event.current.type == EventType.MouseDown && searchTyping && !searchRect.Contains(Event.current.mousePosition))
                GUI.FocusControl("");
        }

        private void DrawTooltip()
        {
            if (tooltipNow.Length == 0) return;
            Vector2 size = styles.Tip.CalcSize(new GUIContent(tooltipNow));
            Vector2 mouse = Event.current.mousePosition;
            Rect area = new Rect(mouse.x + 14f, mouse.y + 18f, size.x, size.y + 2f);
            area.x = Mathf.Clamp(area.x, 2f, Mathf.Max(2f, WindowWidth - area.width - 2f));
            if (area.yMax > WindowHeight - 2f) area.y = mouse.y - area.height - 4f;
            GUI.Box(area, tooltipNow, styles.Tip);
        }

        // The search field has the keyboard: KSP's own keys stay quiet, and the add-on's
        // hotkeys (HandleHotkeys) too.
        private void SetTyping(bool typing)
        {
            searchTyping = typing;
            if (typing) InputLockManager.SetControlLock(ControlTypes.KEYBOARDINPUT, TypingLock);
            else InputLockManager.RemoveControlLock(TypingLock);
        }

        private void ReleaseTyping()
        {
            if (!searchTyping) return;
            GUIUtility.keyboardControl = 0;
            SetTyping(false);
        }

        // Whether the diagnostics window's search field has the keyboard.
        internal bool DiagnosticsTyping
        {
            get { return searchTyping && windowVisible; }
        }

        // The scene probe's way in: the window opened on a page, with a search, the filter, a
        // pin or a switch set, so that a shot shows it (-redefinitionProbeDiagnostics). Commands
        // separated by commas: show, hide, category=<page>, list=<state|registrations|code>,
        // search=<text>, changed=<1|0>, pin=<id>, set=<id>:<value>, technique=<name>, reset.
        // Returns what it did, for the log.
        internal string ProbeDiagnostics(string commands)
        {
            EnsureOptions();
            System.Text.StringBuilder done = new System.Text.StringBuilder();
            foreach (string command in commands.Split(','))
            {
                string[] parts = command.Split(new[] { '=' }, 2);
                string name = parts[0].Trim().ToLowerInvariant();
                string value = parts.Length > 1 ? parts[1] : "";
                if (name == "show") windowVisible = true;
                else if (name == "hide") windowVisible = false;
                else if (name == "category")
                {
                    DebugCategory wanted;
                    if (!Enum.TryParse(value, true, out wanted)) { done.Append(" no page ").Append(value).Append(';'); continue; }
                    pendingCategory = wanted;
                }
                else if (name == "search") typedQuery = value;
                else if (name == "changed") pendingChangedToggle = changedOnly != (value != "0");
                else if (name == "pin")
                {
                    if (debugOptions.ById(value) == null) { done.Append(" no switch ").Append(value).Append(';'); continue; }
                    pins.Toggle(value);
                    settings.DiagnosticsPins = pins.Text;
                }
                else if (name == "set")
                {
                    string[] pair = value.Split(':');
                    DebugOption option = debugOptions.ById(pair[0]);
                    int number;
                    if (option == null || pair.Length < 2 || !int.TryParse(pair[1], out number)) { done.Append(" no switch ").Append(value).Append(';'); continue; }
                    option.Set(number);
                }
                else if (name == "reset") pendingReset = true;
                else if (name == "scroll")
                {
                    float offset;
                    float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out offset);
                    paneScroll = new Vector2(0f, offset);
                }
                else if (name == "list")
                {
                    ModsList wantedList;
                    if (!Enum.TryParse(value, true, out wantedList)) { done.Append(" no list ").Append(value).Append(';'); continue; }
                    pendingModsList = wantedList;
                }
                else if (name == "technique")
                {
                    UpscalerBackend wantedBackend;
                    if (!Enum.TryParse(value, true, out wantedBackend)) { done.Append(" no technique ").Append(value).Append(';'); continue; }
                    SetBackend(wantedBackend);
                }
                else { done.Append(" unknown ").Append(name).Append(';'); continue; }
                done.Append(' ').Append(command.Trim()).Append(';');
            }
            return done.ToString();
        }
    }
}
