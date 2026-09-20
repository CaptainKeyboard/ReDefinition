using System.Collections;
using System.Reflection;
using System;
using KSP.UI.Screens;
using KSP.UI.TooltipTypes;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Window
{
    // Button in KSP's own toolbar.
    //
    // In the main menu as well: ReDefinition's section lives in the settings
    // dialog of a running game (KspSettingsSection), so the settings window the
    // button opens (SettingsWindow) is where the settings can be changed before a
    // game is loaded.
    // TUFX, Scatterer, Kopernicus and Parallax show their buttons in every
    // scene (AppScenes.ALWAYS); KSP lists a button in the main menu when its
    // scenes include MAINMENU (ApplicationLauncher, decompiled).
    //
    // Through KSP's own ApplicationLauncher, in Assembly-CSharp.
    //
    // The icon comes from GameData/ReDefinition/Icons; the one drawn at run time
    // below stands in where that file is missing.
    internal class ToolbarButton
    {
        private const int IconSize = 38;

        // The icon in GameData, without the extension, as KSP's GameDatabase holds it.
        private const string IconPath = "ReDefinition/Icons/ReDefinitionIcon";

        // What the pointer shows over the button.
        private const string Tooltip = "ReDefinition - Settings";

        private readonly Callback onClick;   // KSP's own delegate, not System.Action
        private ApplicationLauncherButton button;
        private Texture2D icon;
        private bool registered;

        public ToolbarButton(Callback clickHandler)
        {
            onClick = clickHandler;
        }

        public void Register()
        {
            if (registered) return;
            registered = true;

            // The launcher does not exist yet when the addon loads; KSP calls
            // back once it is ready -- and again after every scene change.
            GameEvents.onGUIApplicationLauncherReady.Add(Add);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(Forget);
            Add();
        }

        public void Unregister()
        {
            if (!registered) return;
            registered = false;

            GameEvents.onGUIApplicationLauncherReady.Remove(Add);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(Forget);
            Remove();
        }

        // The button shows whether the window is open. If it is closed via the
        // keyboard the button has to follow, otherwise the two drift apart.
        public void Reflect(bool windowVisible)
        {
            if (button == null) return;

            if (windowVisible) button.SetTrue(false);
            else button.SetFalse(false);
        }

        private void Add()
        {
            if (button != null || ApplicationLauncher.Instance == null) return;

            Texture drawn = FromGameData(IconPath);
            if (drawn == null)
            {
                if (icon == null) icon = BuildIcon();
                drawn = icon;
            }

            button = AddWith(drawn);
            SetTooltip(button, Tooltip);
            KeepFirst();
        }

        // ReDefinition's button at the front of the toolbar, in what the launcher
        // shows and in the list it keeps. Only this mod's own entry is moved: it
        // is taken out of the list and put in at the front, and its object is made
        // the first child of the row. No other button is read, moved or removed,
        // and a failure leaves the toolbar as it was.
        //
        // KSP adds a mod's button in the order the mods ask for it, which is their
        // load order. Mods that add theirs later end up behind this one; one that
        // moves its own the same way wins the front until the next pass here.
        internal void KeepFirst()
        {
            if (button == null) return;

            try
            {
                if (button.gameObject != null && button.gameObject.transform != null
                    && button.gameObject.transform.GetSiblingIndex() != 0)
                    button.gameObject.transform.SetAsFirstSibling();

                ApplicationLauncher launcher = ApplicationLauncher.Instance;
                if (launcher == null) return;

                FieldInfo field = typeof(ApplicationLauncher).GetField("appListMod",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                IList list = field != null ? field.GetValue(launcher) as IList : null;
                if (list == null || list.Count < 2) return;

                int at = list.IndexOf(button);
                if (at <= 0) return;
                list.RemoveAt(at);
                list.Insert(0, button);
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("toolbar-first", "ReDefinition's toolbar button could not be moved to the front ("
                                      + CompatibilityLog.Reason(e) + "); it stays where the launcher put it.");
            }
        }

        // The tooltip KSP's own buttons use: a controller on the button's object with
        // the game's own prefab, as Astrogator's TooltipExtensions does it. Guarded:
        // a build without that prefab loses the tooltip, not the button.
        private static void SetTooltip(ApplicationLauncherButton target, string text)
        {
            if (target == null || target.gameObject == null) return;
            try
            {
                Tooltip_Text prefab = AssetBase.GetPrefab<Tooltip_Text>("Tooltip_Text");
                if (prefab == null) return;

                TooltipController_Text tooltip = target.gameObject.GetComponent<TooltipController_Text>()
                                                 ?? target.gameObject.AddComponent<TooltipController_Text>();
                if (tooltip == null) return;
                tooltip.prefab = prefab;
                tooltip.textString = text;
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("toolbar-tooltip", "The toolbar button has no tooltip ("
                                      + CompatibilityLog.Reason(e) + ").");
            }
        }

        // The texture a mod ships, as Kopernicus loads its own: null where the file
        // is not there, and then the one this mod draws is used.
        private static Texture FromGameData(string path)
        {
            try
            {
                return GameDatabase.Instance != null ? GameDatabase.Instance.GetTexture(path, false) : null;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        private ApplicationLauncherButton AddWith(Texture texture)
        {
            return ApplicationLauncher.Instance.AddModApplication(
                onClick, onClick,               // on and off both lead to a toggle
                null, null, null, null,
                ApplicationLauncher.AppScenes.FLIGHT
                | ApplicationLauncher.AppScenes.MAPVIEW
                | ApplicationLauncher.AppScenes.SPACECENTER
                | ApplicationLauncher.AppScenes.VAB
                | ApplicationLauncher.AppScenes.SPH
                | ApplicationLauncher.AppScenes.TRACKSTATION
                | ApplicationLauncher.AppScenes.MAINMENU,
                texture);
        }

        private void Forget()
        {
            button = null;
        }

        private void Remove()
        {
            if (button != null && ApplicationLauncher.Instance != null)
                ApplicationLauncher.Instance.RemoveModApplication(button);
            button = null;

            if (icon != null)
            {
                UnityEngine.Object.Destroy(icon);
                icon = null;
            }
        }

        // Four small pixels becoming one big one.
        private static Texture2D BuildIcon()
        {
            Texture2D texture = new Texture2D(IconSize, IconSize, TextureFormat.ARGB32, false);
            texture.name = "ReDefinitionIcon";
            texture.filterMode = FilterMode.Bilinear;

            Color background = new Color(0.10f, 0.13f, 0.18f, 0.90f);
            Color small = new Color(0.45f, 0.55f, 0.65f, 1f);
            Color large = new Color(0.55f, 0.85f, 1.00f, 1f);
            Color clear = new Color(0f, 0f, 0f, 0f);

            for (int y = 0; y < IconSize; y++)
            {
                for (int x = 0; x < IconSize; x++)
                {
                    // Rounded corners.
                    Color colour = InsideRoundedSquare(x, y) ? background : clear;

                    // Bottom left a grid of four small squares ...
                    if (x >= 5 && x <= 16 && y >= 5 && y <= 16
                        && (x - 5) % 7 < 5 && (y - 5) % 7 < 5)
                        colour = small;

                    // ... top right the one they have become.
                    if (x >= 20 && x <= 32 && y >= 20 && y <= 32)
                        colour = large;

                    texture.SetPixel(x, y, colour);
                }
            }

            texture.Apply(false);
            return texture;
        }

        private static bool InsideRoundedSquare(int x, int y)
        {
            const int radius = 6;
            int max = IconSize - 1;

            int dx = x < radius ? radius - x : (x > max - radius ? x - (max - radius) : 0);
            int dy = y < radius ? radius - y : (y > max - radius ? y - (max - radius) : 0);
            return dx * dx + dy * dy <= radius * radius;
        }
    }
}
