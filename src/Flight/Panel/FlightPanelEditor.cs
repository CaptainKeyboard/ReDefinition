using System.Collections.Generic;
using ReDefinition.Core;
using ReDefinition.Engineer;
using ReDefinition.Flight.Readouts;
using UnityEngine;

namespace ReDefinition.Flight.Panel
{
    // The window the flight panel's pencil opens: the profile of the tab shown,
    // with its name, its readouts in order (each up, down or out) and every other
    // readout to add, by group; a new profile, removing one, and the defaults. Made
    // of KSP's dialog parts (PopupDialog, MultiOptionDialog), so that a UI theme
    // themes it as it themes KSP's. A change takes effect at once in the tabs and
    // is saved (FlightProfiles.Save); the window is made anew after each, above
    // the panel; a new profile's tab opens (FlightPanel.Refresh). While it is open, the
    // keyboard does not steer the vessel (a control lock), so that typing a name
    // does not stage it.
    internal static class FlightPanelEditor
    {
        // The window's width and a row's height; the window shown, null while closed; the profile it edits, by its index.
        private const float Width = 400f;
        private const float RowHeight = 22f;
        private static PopupDialog shown;
        private static int editing;
        // Above the flight panel, at the bottom left.
        private static readonly Vector2 Place = new Vector2(0.2f, 0.45f);
        // Keys typed into its name field go to the field, not to the vessel's
        // controls: staging is on the space bar.
        private const string Lock = "ReDefinitionFlightPanelEditor";

        // Opens the window on that profile.
        internal static void Open(int profile)
        {
            editing = Mathf.Clamp(profile, 0, FlightProfiles.Current.Count - 1);
            Show();
        }

        // A name typed but not yet saved.
        private static bool renamed;

        // Closes the window, saving a name typed, and lets go of the keyboard.
        internal static void Close()
        {
            if (renamed) FlightProfiles.Save();
            renamed = false;
            if (shown != null) shown.Dismiss();
            shown = null;
            InputLockManager.RemoveControlLock(Lock);
        }

        // The lock let go where the window went another way than its Close: KSP
        // dismisses popups as a scene ends.
        internal static void Tick()
        {
            // The name field, still selected after its edit: Return would open it again.
            if (shown != null) ReDefinition.Window.UiHygiene.ReleaseEnded(shown.gameObject);
            if (shown == null && InputLockManager.GetControlLock(Lock) != ControlTypes.None) InputLockManager.RemoveControlLock(Lock);
        }

        // Makes the window anew for the profile edited, where it stood and scrolled as far, and holds the keyboard.
        private static void Show()
        {
            // Made anew after each change: where the player put the window and how
            // far its list is scrolled are kept across that.
            Vector3? place = null;
            float scrolled = 0f;
            if (shown != null)
            {
                if (shown.RTrf != null) place = shown.RTrf.position;
                UnityEngine.UI.ScrollRect before = shown.GetComponentInChildren<UnityEngine.UI.ScrollRect>();
                if (before != null && before.content != null) scrolled = before.content.anchoredPosition.y;
                shown.Dismiss();
            }
            List<FlightProfile> profiles = FlightProfiles.Current;
            if (profiles.Count == 0) return;
            FlightProfile profile = profiles[editing];
            float rowWidth = Width - 40f;
            List<DialogGUIBase> rows = new List<DialogGUIBase>();

            // Its name, and the other profiles to switch to.
            List<DialogGUIBase> names = new List<DialogGUIBase> { new DialogGUILabel("Profile", 70f, RowHeight) };
            DialogGUITextInput name = new DialogGUITextInput(profile.Name, false, 24, text =>
            {
                // Kept at once, saved with the next change or as the window closes.
                if (!string.IsNullOrEmpty(text) && text != profile.Name)
                {
                    profile.Name = text;
                    renamed = true;
                    FlightPanel.Refresh(editing, false);
                }
                return text;
            }, 180f, RowHeight);
            names.Add(name);
            names.Add(new DialogGUIButton("Next", () =>
            {
                editing = (editing + 1) % FlightProfiles.Current.Count;
                if (renamed) FlightProfiles.Save();
                renamed = false;
                Show();
            }, 60f, RowHeight, false));
            rows.Add(new DialogGUIHorizontalLayout(rowWidth, RowHeight, 4f, new RectOffset(), TextAnchor.MiddleLeft, names.ToArray()));
            rows.Add(new DialogGUISpace(6f));

            // Its readouts, in order.
            rows.Add(new DialogGUILabel("<b>Shown, in this order</b>", rowWidth, RowHeight));
            for (int i = 0; i < profile.Keys.Count; i++)
            {
                int index = i;
                Readout readout = ReadoutCatalog.Find(profile.Keys[i]);
                if (readout == null) continue;
                rows.Add(new DialogGUIHorizontalLayout(rowWidth, RowHeight, 4f, new RectOffset(), TextAnchor.MiddleLeft,
                    new DialogGUILabel(Title(readout), rowWidth - 3f * 58f - 3f * 4f, RowHeight),
                    new DialogGUIButton("Up", () => Move(profile, index, -1), 58f, RowHeight, false),
                    new DialogGUIButton("Down", () => Move(profile, index, 1), 58f, RowHeight, false),
                    new DialogGUIButton("Remove", () => Remove(profile, index), 58f, RowHeight, false)));
            }
            rows.Add(new DialogGUISpace(6f));

            // Every other readout, by group.
            foreach (string group in ReadoutCatalog.Groups)
            {
                List<Readout> others = ReadoutCatalog.All.FindAll(readout => readout.Group == group && !profile.Keys.Contains(readout.Key));
                if (others.Count == 0) continue;
                rows.Add(new DialogGUILabel("<b>Add: " + group + "</b>", rowWidth, RowHeight));
                foreach (Readout readout in others)
                {
                    string key = readout.Key;
                    rows.Add(new DialogGUIButton("+  " + Title(readout), () => Add(profile, key), rowWidth, RowHeight, false));
                }
            }

            DialogGUIScrollList list = new DialogGUIScrollList(new Vector2(Width - 20f, 360f), false, true,
                new DialogGUIVerticalLayout(rowWidth, -1f, 2f, new RectOffset(4, 4, 4, 4), TextAnchor.UpperLeft, rows.ToArray()));
            DialogGUIHorizontalLayout buttons = new DialogGUIHorizontalLayout(Width - 20f, 30f, 6f, new RectOffset(), TextAnchor.MiddleCenter,
                new DialogGUIButton("New profile", NewProfile, 100f, 26f, false),
                Deletable(new DialogGUIButton("Delete profile", () => Delete(profile), 100f, 26f, false), profile),
                new DialogGUIButton("Defaults", Defaults, 80f, 26f, false),
                new DialogGUIButton("Close", Close, 70f, 26f, false));
            MultiOptionDialog dialog = new MultiOptionDialog("ReDefinitionFlightPanelEditor", "", "Flight panel", HighLogic.UISkin,
                Width, list, buttons);
            shown = PopupDialog.SpawnPopupDialog(Place, Place, dialog, false, HighLogic.UISkin);
            PlannerWidgets.Settle(list);
            // The wheel over a button scrolls the list, and no control takes the
            // keyboard's selection (UiHygiene).
            ReDefinition.Window.ScrollThrough.Apply(shown.gameObject);
            ReDefinition.Window.UiHygiene.MouseOnly(shown.gameObject);
            // Escape in the name field ends the typing, not the window.
            ReDefinition.Window.EscapeGuard.Own(shown);
            if (place != null && shown.RTrf != null) shown.RTrf.position = place.Value;
            UnityEngine.UI.ScrollRect after = shown.GetComponentInChildren<UnityEngine.UI.ScrollRect>();
            if (scrolled > 0f && after != null && after.content != null)
            {
                Canvas.ForceUpdateCanvases();
                float most = Mathf.Max(0f, after.content.rect.height - ((RectTransform)after.transform).rect.height);
                after.content.anchoredPosition = new Vector2(after.content.anchoredPosition.x, Mathf.Min(scrolled, most));
            }
            InputLockManager.SetControlLock(ControlTypes.KEYBOARDINPUT, Lock);
        }

        // As the panel titles it.
        private static string Title(Readout readout)
        {
            return readout.Title;
        }

        // Moves the readout `by` places up or down the profile.
        private static void Move(FlightProfile profile, int index, int by)
        {
            int to = index + by;
            if (to < 0 || to >= profile.Keys.Count) return;
            string key = profile.Keys[index];
            profile.Keys.RemoveAt(index);
            profile.Keys.Insert(to, key);
            Changed();
        }

        // Takes the readout out of the profile.
        private static void Remove(FlightProfile profile, int index)
        {
            if (index < 0 || index >= profile.Keys.Count) return;
            profile.Keys.RemoveAt(index);
            Changed();
        }

        // Adds the readout at the profile's end.
        private static void Add(FlightProfile profile, string key)
        {
            if (!profile.Keys.Contains(key)) profile.Keys.Add(key);
            Changed();
        }

        // A new profile of the player's own, numbered after the others, and opened in the window.
        private static void NewProfile()
        {
            FlightProfiles.Current.Add(new FlightProfile { Name = "Profile " + (FlightProfiles.Current.Count + 1), Situation = PanelSituation.Manual });
            editing = FlightProfiles.Current.Count - 1;
            Changed();
        }

        // Only a profile of the player's own can go: the six shipped ones, one per
        // situation, stay.
        private static DialogGUIButton Deletable(DialogGUIButton button, FlightProfile profile)
        {
            button.OptionInteractableCondition = () => !profile.Builtin;
            button.tooltipText = profile.Builtin ? "The profiles for the situations stay; only your own can be deleted." : "";
            return button;
        }

        // Deletes a profile of the player's own; the built-in ones stay.
        private static void Delete(FlightProfile profile)
        {
            if (profile.Builtin || FlightProfiles.Current.Count <= 1) return;
            FlightProfiles.Current.Remove(profile);
            editing = Mathf.Clamp(editing, 0, FlightProfiles.Current.Count - 1);
            Changed();
        }

        // The built-in profiles as shipped.
        private static void Defaults()
        {
            // The situations' profiles as shipped, the same objects, so that their
            // tabs keep them; the player's own stay as they are.
            foreach (FlightProfile shipped in FlightProfiles.Defaults())
            {
                FlightProfile profile = FlightProfiles.Current.Find(item => item.Situation == shipped.Situation);
                if (profile == null) continue;
                profile.Name = shipped.Name;
                profile.Keys.Clear();
                profile.Keys.AddRange(shipped.Keys);
            }
            Changed();
        }

        // Saved, the tabs built anew, and the window made again.
        private static void Changed()
        {
            FlightProfiles.Save();
            renamed = false;
            FlightPanel.Refresh(editing);
            Debug.Log(Log.Tag + " Flight panel profile changed.");
            Show();
        }
    }
}
