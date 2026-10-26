using System;
using System.Collections.Generic;
using ReDefinition.Bridges;
using ReDefinition.Core;
using ReDefinition.Diagnostics;
using ReDefinition.Ui;
using ReDefinition.Upscaler;
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
        // Whether the diagnostics window is open.
        private bool windowVisible;

        // The note where frame generation is on but the proxy could not make its context.
        private const string ContextFailedText =
            "frame generation's context could not be made (see ReDefinitionProxy.log)";

        // ContextFailedText, with the way out once the proxy no longer tries.
        private static string ContextFailedNote()
        {
            return ContextFailedText + (FrameGenerationBridge.ContextFailureStands
                ? "; switch frame generation off and on to try again"
                : "");
        }

        // The window's size as made, and the width of the pages' list on its left.
        private const float WindowWidth = 720f;
        private const float WindowHeight = 600f;
        private const float CategoryWidth = 150f;

        // As tall as it is made, but within the screen (at 1080p 600 fits).
        private static float ShownHeight
        {
            get { return Mathf.Max(360f, Mathf.Min(WindowHeight, Screen.height - 40f)); }
        }
        // The search field's IMGUI control name, and the name of the keyboard lock it holds while typed into.
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

        // The changes the events ask for, made at the next layout pass (ApplyPendingChanges): the page to show, the
        // option to pin or unpin, the "Changed" filter to flip, and the reset of every switch.
        private DebugCategory? pendingCategory;
        private string pendingPin;
        private bool pendingChangedToggle;
        private bool pendingReset;

        // The window's place and size, its IMGUI id, the styles made from KSP's skin, and the tooltip under the pointer.
        private Rect window = new Rect(60f, 60f, WindowWidth, WindowHeight);
        private int windowId;
        private DiagnosticsStyles styles;
        private string tooltipNow = "";

        // Whether a search or the "Changed" filter narrows what the pages show.
        private bool FilterActive
        {
            get { return query.Trim().Length > 0 || changedOnly; }
        }

        // The lists the pages draw, made when first asked in a layout pass and kept for the
        // events of that pass: the layout and the repaint must see the same rows, and the
        // lists are not built again for each. Dropped at the start of every layout pass.
        private readonly Dictionary<DebugCategory, List<DebugOption>> pageLists = new Dictionary<DebugCategory, List<DebugOption>>();
        private readonly Dictionary<DebugCategory, List<DebugOption>> resultLists = new Dictionary<DebugCategory, List<DebugOption>>();
        private readonly Dictionary<DebugCategory, int> passCounts = new Dictionary<DebugCategory, int>();
        private List<DebugOption> foundList;
        private List<DebugOption> pinnedList;
        // Frame generation's context failure, as the layout pass found it: a call into the
        // proxy per event, and the note is a control, so the repaint must not ask again.
        private bool contextFailing;
        private bool contextFailingAny;

        // Drops the lists kept for the last layout pass, and asks the proxy afresh whether the context failed.
        private void ForgetLists()
        {
            pageLists.Clear();
            resultLists.Clear();
            passCounts.Clear();
            foundList = null;
            pinnedList = null;
            contextFailingAny = FrameGenerationBridge.ContextFailing;
            contextFailing = frameGeneration && contextFailingAny;
        }

        // The options a page shows.
        private List<DebugOption> OptionsOf(DebugCategory page)
        {
            List<DebugOption> list;
            if (!pageLists.TryGetValue(page, out list)) pageLists[page] = list = debugOptions.Of(page);
            return list;
        }

        // The options that pass the search and the "Changed" filter, from every page.
        private List<DebugOption> Found()
        {
            return foundList ?? (foundList = debugOptions.Filter(query, changedOnly));
        }

        // Those of them that are on this page.
        private List<DebugOption> FoundOn(DebugCategory page)
        {
            List<DebugOption> list;
            if (resultLists.TryGetValue(page, out list)) return list;
            list = new List<DebugOption>();
            foreach (DebugOption option in Found())
                if (option.Category == page) list.Add(option);
            resultLists[page] = list;
            return list;
        }

        // How many of the page's options pass the search and the filter, counted once per layout pass.
        private int PassCount(DebugCategory page)
        {
            int count;
            if (!passCounts.TryGetValue(page, out count)) passCounts[page] = count = debugOptions.Count(page, query, changedOnly);
            return count;
        }

        // The pinned options, for the window's foot, made once per layout pass.
        private List<DebugOption> Pinned()
        {
            return pinnedList ?? (pinnedList = pins.Options(debugOptions));
        }

        // Opens or closes the window; closing it gives the keyboard back.
        private void ToggleWindow()
        {
            windowVisible = !windowVisible;
            if (!windowVisible) ReleaseTyping();
        }

        // From the settings window's Diagnostics button.
        // Opens on the page where the upscaler's options live; a window already open stays
        // on its page.
        internal void ShowDiagnostics()
        {
            if (!windowVisible) pendingCategory = DebugCategory.UpscalerInputs;
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
            EndTypingByEvent(Event.current);

            window.width = WindowWidth;
            float height = ShownHeight;
            window.height = height;
            window = GUILayout.Window(windowId, window, DrawWindow, "ReDefinition",
                                      GUILayout.Width(WindowWidth), GUILayout.Height(height));
            // A part of it stays on the screen to be taken hold of; a window taller
            // than the screen can still be dragged up to its lower end.
            const float grip = 40f;
            window.x = Mathf.Clamp(window.x, grip - window.width, Screen.width - grip);
            window.y = Mathf.Clamp(window.y, grip - window.height, Screen.height - grip);
            // Kept for the next session; saved with the other settings.
            settings.DiagnosticsX = window.x;
            settings.DiagnosticsY = window.y;
            settings.DiagnosticsPage = category;
            // Not click-through: what lies behind it is kept from the click
            // (UnityMouseEvents), as behind the settings window.
            UnityMouseEvents.ImguiWindow = window;
        }

        // The window's content: the live strip, the pages' list beside the page, and the foot.
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
            ForgetLists();
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
                bool optionPage = page != DebugCategory.Previews && page != DebugCategory.OtherMods;
                int count = filtering && optionPage ? PassCount(page) : -1;
                Color previous = GUI.contentColor;
                if (count == 0 && page != category) GUI.contentColor = DiagnosticsStyles.Grey;
                if (GUILayout.Toggle(category == page, content.PageEntry(page, count), styles.Category) && category != page)
                    pendingCategory = page;
                GUI.contentColor = previous;
            }
            GUILayout.EndVertical();
        }

        // The pages in the order the list shows them.
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
                    case DebugCategory.Previews: DrawPreview(); break;
                    default: DrawOverview(); break;
                }
            GUILayout.EndScrollView();
        }

        // The window's foot: the pinned options as compact toggles, and Close.
        private void DrawFoot()
        {
            List<DebugOption> pinned = Pinned();
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
        }

        // A click anywhere but in the search field, inside the window or outside it, ends
        // the typing; so do Escape and Return in it. Asked before the window function: the
        // controls of the window are FocusType.Passive and use the click, so IMGUI leaves
        // the keyboard control on the field (UiHygiene.Typing) and the field would be typed
        // into, with KSP's keys locked, until another text field is clicked.
        private void EndTypingByEvent(Event current)
        {
            if (current == null || !searchTyping) return;
            if (current.type == EventType.MouseDown)
            {
                // searchRect is in the window's own coordinates.
                if (!searchRect.Contains(current.mousePosition - window.position)) ReleaseTyping();
            }
            else if (current.type == EventType.KeyDown && DiagnosticsContent.EndsTyping(current.keyCode))
                ReleaseTyping();
        }


        // The tooltip box beside the pointer, kept inside the window.
        private void DrawTooltip()
        {
            if (tooltipNow.Length == 0) return;
            if (content.Tip.text != tooltipNow) content.Tip.text = tooltipNow;
            Vector2 size = styles.Tip.CalcSize(content.Tip);
            Vector2 mouse = Event.current.mousePosition;
            Rect area = new Rect(mouse.x + 14f, mouse.y + 18f, size.x, size.y + 2f);
            area.x = Mathf.Clamp(area.x, 2f, Mathf.Max(2f, WindowWidth - area.width - 2f));
            if (area.yMax > window.height - 2f) area.y = mouse.y - area.height - 4f;
            GUI.Box(area, content.Tip, styles.Tip);
        }

        // The search field has the keyboard: KSP's own keys stay quiet, and the add-on's
        // hotkeys (HandleHotkeys) too.
        private void SetTyping(bool typing)
        {
            searchTyping = typing;
            if (typing) InputLockManager.SetControlLock(ControlTypes.KEYBOARDINPUT, TypingLock);
            else InputLockManager.RemoveControlLock(TypingLock);
        }

        // Takes the keyboard from the search field where it had it.
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
                    settings.BackendAuto = false;
                    resolvedFor = null;
                    SetBackend(wantedBackend);
                }
                else { done.Append(" unknown ").Append(name).Append(';'); continue; }
                done.Append(' ').Append(command.Trim()).Append(';');
            }
            return done.ToString();
        }
    }
}
