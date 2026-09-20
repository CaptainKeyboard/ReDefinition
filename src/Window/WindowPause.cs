using System;
using ReDefinition.Core;
using UnityEngine.UI;
using UnityEngine;

namespace ReDefinition.Window
{
    // The pause button at the top of the settings window: while it is on and the
    // window is open, the flight is paused.
    //
    // The pause itself is KSP's own (FlightDriver.SetPause, as Parallax pauses
    // while it rebuilds its scatter). Closing the window lets the flight run
    // again, and opening it holds the flight anew -- unless KSP's own pause menu
    // stands, which is a pause of the player's own and stays.
    //
    // The button sits in the window's title row. The title is a child object of
    // the dialog's window named Title (PopupDialog.SetPopupData, decompiled), so
    // the button is moved there once the dialog stands: in the row where it
    // belongs, and out of the window's vertical layout, which would give it a
    // row of its own.
    //
    // Only in flight: the space centre and the editors have no physics to hold.
    // The choice is one of ReDefinition's own settings, so it is there again at the
    // next opening.
    //
    // The button carries a symbol rather than a word: two bars while the flight
    // runs, a triangle while this window holds it. Both are drawn here into a
    // sprite (DialogGUIButton takes one), which needs no file and no glyph the
    // game's font may not have.
    internal static class WindowPause
    {
        private const int IconSize = 24;
        private const float ButtonSize = 26f;
        // From the right edge of the title row.
        private const float TitleInset = 8f;

        private static readonly Color Glyph = new Color(0.898f, 0.898f, 0.910f, 1f);

        private static Sprite pauseSprite;
        private static Sprite playSprite;

        // What was last built, for the move into the title row.
        private static DialogGUIButton button;

        // Whether this window is holding the flight.
        private static bool holding;

        internal static bool Wanted
        {
            get
            {
                ReDefinitionAddon addon = ReDefinitionAddon.Instance;
                return addon != null && addon.PauseWhileOpen;
            }
        }

        internal static bool Possible
        {
            get { return HighLogic.LoadedSceneIsFlight && FlightDriver.fetch != null; }
        }

        // The window opened, or the switch was flipped while it stands.
        internal static void Refresh()
        {
            try
            {
                if (Wanted && Possible && SettingsWindow.Visible) Hold();
                else Release();
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("window-pause", "The flight could not be paused for the settings window ("
                                      + CompatibilityLog.Reason(e) + ").");
            }
        }

        // The window closed, the scene changed, or the switch went off.
        internal static void Release()
        {
            if (!holding) return;
            holding = false;
            if (!HighLogic.LoadedSceneIsFlight) return;
            // The flight runs again with the window gone. Only KSP's own pause
            // menu keeps it: that pause is the player's, not this window's.
            FlightDriver.SetPause(PauseMenuStands);
        }

        private static bool PauseMenuStands
        {
            get
            {
                try
                {
                    return PauseMenu.exists && PauseMenu.isOpen;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        private static void Hold()
        {
            if (holding) return;
            holding = true;
            FlightDriver.SetPause(true);
        }

        internal static DialogGUIBase Button()
        {
            button = new DialogGUIButton(PauseSprite(), Toggle, ButtonSize, ButtonSize, false);
            button.tooltipText = "Pauses the flight while this window is open. The flight runs again when the window"
                                 + " closes.";
            button.OptionInteractableCondition = () => Possible;
            return button;
        }

        // Into the title row, once the dialog's objects stand. Anchored to the
        // right of the title itself, so it keeps the row whatever the window's
        // width and the UI's scale are.
        internal static void PlaceInTitleRow(PopupDialog dialog)
        {
            try
            {
                if (button == null || dialog == null || dialog.popupWindow == null) return;

                GameObject item = button.uiItem;
                GameObject title = dialog.popupWindow.GetChild("Title");
                if (item == null || title == null) return;

                RectTransform placed = item.transform as RectTransform;
                if (placed == null) return;

                LayoutElement outside = item.GetComponent<LayoutElement>() ?? item.AddComponent<LayoutElement>();
                outside.ignoreLayout = true;

                placed.SetParent(title.transform, false);
                placed.anchorMin = new Vector2(1f, 0.5f);
                placed.anchorMax = new Vector2(1f, 0.5f);
                placed.pivot = new Vector2(1f, 0.5f);
                placed.sizeDelta = new Vector2(ButtonSize, ButtonSize);
                placed.anchoredPosition = new Vector2(-TitleInset, 0f);
                placed.localScale = Vector3.one;
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("window-pause-place", "The pause button stayed below the settings window's title"
                                      + " (" + CompatibilityLog.Reason(e) + ").");
            }
        }

        // The sprite a DialogGUIButton is built with cannot be swapped afterwards,
        // so the button shows what a click does: the bars where the window does not
        // hold the flight, the triangle where it does. Read from the choice, not
        // from `holding`: the button is built before the window takes hold.
        private static Sprite PauseSprite()
        {
            return Wanted && Possible ? Play() : Pause();
        }

        private static void Toggle()
        {
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            if (addon == null) return;

            addon.PauseWhileOpen = !addon.PauseWhileOpen;
            Debug.Log(Log.Tag + " Pause while the settings window is open: " + (addon.PauseWhileOpen ? "on" : "off"));
            Refresh();
            // The sprite of a button cannot be swapped, so the window is drawn
            // anew for the symbol to follow the state.
            if (SettingsWindow.Visible)
            {
                SettingsWindow.Close();
                SettingsWindow.Toggle();
            }
        }

        private static Sprite Pause()
        {
            if (pauseSprite != null) return pauseSprite;

            Texture2D texture = Blank();
            // Two bars, a third of the width each, with a third between them.
            for (int y = 4; y < IconSize - 4; y++)
                for (int x = 0; x < IconSize; x++)
                {
                    bool bar = (x >= 6 && x <= 9) || (x >= 14 && x <= 17);
                    if (bar) texture.SetPixel(x, y, Glyph);
                }
            texture.Apply(false);
            pauseSprite = ToSprite(texture, "ReDefinitionPauseIcon");
            return pauseSprite;
        }

        private static Sprite Play()
        {
            if (playSprite != null) return playSprite;

            Texture2D texture = Blank();
            // A triangle pointing right: its width falls off towards the tip.
            for (int y = 4; y < IconSize - 4; y++)
            {
                int fromMiddle = Math.Abs(y - IconSize / 2);
                int until = IconSize - 6 - fromMiddle * 2;
                for (int x = 7; x < until; x++) texture.SetPixel(x, y, Glyph);
            }
            texture.Apply(false);
            playSprite = ToSprite(texture, "ReDefinitionPlayIcon");
            return playSprite;
        }

        private static Texture2D Blank()
        {
            Texture2D texture = new Texture2D(IconSize, IconSize, TextureFormat.ARGB32, false);
            Color clear = new Color(0f, 0f, 0f, 0f);
            for (int y = 0; y < IconSize; y++)
                for (int x = 0; x < IconSize; x++)
                    texture.SetPixel(x, y, clear);
            return texture;
        }

        private static Sprite ToSprite(Texture2D texture, string name)
        {
            texture.name = name;
            texture.filterMode = FilterMode.Bilinear;
            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, IconSize, IconSize), new Vector2(0.5f, 0.5f));
            sprite.name = name;
            return sprite;
        }
    }
}
