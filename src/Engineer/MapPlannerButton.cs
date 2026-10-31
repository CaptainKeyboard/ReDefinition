using System;
using System.Collections;
using KSP.UI.Screens;
using ReDefinition.Ui;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // The planner's button in KSP's own toolbar, which shows in the map view and slides the planner's panel in and out. A click tells the
    // window; the window tells the button where the panel was opened or shut another way.
    internal sealed class MapPlannerButton
    {
        private const int IconSize = 38;
        private const string Tooltip = "Route Planner";

        // KSP's own delegate type, called with the new state.
        private readonly Action<bool> setOpen;
        private ApplicationLauncherButton button;
        private Texture2D icon;
        private bool registered;
        // Whether the panel is out, for a button made after it was.
        private bool open;

        internal MapPlannerButton(Action<bool> setOpen)
        {
            this.setOpen = setOpen;
        }

        // Adds the button now where the launcher stands, and again whenever KSP makes the launcher anew.
        internal void Register()
        {
            if (registered) return;
            registered = true;
            GameEvents.onGUIApplicationLauncherReady.Add(Add);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(Forget);
            Add();
        }

        internal void Unregister()
        {
            if (!registered) return;
            registered = false;
            GameEvents.onGUIApplicationLauncherReady.Remove(Add);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(Forget);
            if (button != null && ApplicationLauncher.Instance != null) ApplicationLauncher.Instance.RemoveModApplication(button);
            button = null;
            if (icon != null) UnityEngine.Object.Destroy(icon);
            icon = null;
        }

        // Shows the button lit while the panel is out, without calling the window back.
        internal void Show(bool panelOpen)
        {
            open = panelOpen;
            if (button == null) return;
            if (panelOpen) button.SetTrue(false);
            else button.SetFalse(false);
        }

        private void Add()
        {
            if (button != null || ApplicationLauncher.Instance == null) return;
            if (icon == null) icon = Draw();
            button = ApplicationLauncher.Instance.AddModApplication(() => setOpen(true), () => setOpen(false), null, null, null, null,
                ApplicationLauncher.AppScenes.MAPVIEW, icon);
            if (button != null && button.gameObject != null) UiHygiene.Tooltip(button.gameObject, Tooltip);
            KeepFirst();
            Show(open);
        }

        // The button at the front of the toolbar, in what the launcher shows and in the list it keeps, as ReDefinition's settings button is.
        // The launcher scrolls where mods crowd it, and a button at its end is not seen. Only this one is moved, and a failure leaves
        // the toolbar as it was.
        private void KeepFirst()
        {
            if (button == null) return;
            try
            {
                if (button.gameObject != null && button.gameObject.transform.GetSiblingIndex() != 0) button.gameObject.transform.SetAsFirstSibling();
                ApplicationLauncher launcher = ApplicationLauncher.Instance;
                IList list = launcher != null ? Window.ToolbarTakeover.ModList(launcher) : null;
                if (list == null || list.Count < 2) return;
                int at = list.IndexOf(button);
                if (at <= 0) return;
                list.RemoveAt(at);
                list.Insert(0, button);
            }
            catch (Exception e)
            {
                Core.CompatibilityLog.Warn("planner-toolbar-first", "The planner's toolbar button could not be moved to the front ("
                                           + Core.CompatibilityLog.Reason(e) + "); it stays where the launcher put it.");
            }
        }

        private void Forget()
        {
            button = null;
        }

        // A route of three points joined by a line, light on a dark ground: the stand-in until the mod ships an icon file.
        private static Texture2D Draw()
        {
            var texture = new Texture2D(IconSize, IconSize, TextureFormat.ARGB32, false);
            texture.name = "ReDefinitionPlannerIcon";
            texture.filterMode = FilterMode.Bilinear;
            Color ground = new Color(0.10f, 0.13f, 0.18f, 0.90f);
            Color route = new Color(0.55f, 0.85f, 1.00f, 1f);
            var pixels = new Color[IconSize * IconSize];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = ground;
            Vector2[] points = { new Vector2(8f, 10f), new Vector2(19f, 26f), new Vector2(30f, 14f) };
            for (int leg = 0; leg < points.Length - 1; leg++)
                for (float at = 0f; at <= 1f; at += 0.01f) Dot(pixels, Vector2.Lerp(points[leg], points[leg + 1], at), 1.1f, route);
            foreach (Vector2 point in points) Dot(pixels, point, 3.6f, route);
            texture.SetPixels(pixels);
            texture.Apply(false, false);
            return texture;
        }

        private static void Dot(Color[] pixels, Vector2 centre, float radius, Color colour)
        {
            int from = Mathf.Max(0, (int)(centre.y - radius - 1f));
            int to = Mathf.Min(IconSize - 1, (int)(centre.y + radius + 1f));
            for (int y = from; y <= to; y++)
                for (int x = Mathf.Max(0, (int)(centre.x - radius - 1f)); x <= Mathf.Min(IconSize - 1, (int)(centre.x + radius + 1f)); x++)
                    if ((new Vector2(x + 0.5f, y + 0.5f) - centre).sqrMagnitude <= radius * radius) pixels[y * IconSize + x] = colour;
        }
    }
}
