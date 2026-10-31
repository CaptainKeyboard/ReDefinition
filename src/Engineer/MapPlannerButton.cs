using System;
using KSP.UI.Screens;
using ReDefinition.Ui;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // The planner's button among the game's own toolbar buttons (the top group, not the mods' below it), which shows in the map view and slides the planner's panel in and out. A click tells the
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
            if (button != null && ApplicationLauncher.Instance != null) ApplicationLauncher.Instance.RemoveApplication(button);
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
            button = ApplicationLauncher.Instance.AddApplication(() => setOpen(true), () => setOpen(false), null, null, null, null, icon);
            if (button == null) return;
            // The game's own buttons are in every scene unless told: this one is in the map view.
            button.VisibleInScenes = ApplicationLauncher.AppScenes.MAPVIEW;
            if (button.gameObject != null) UiHygiene.Tooltip(button.gameObject, Tooltip);
            Show(open);
        }

        private void Forget()
        {
            button = null;
        }

        // A route of three points joined by a line, white on nothing, as the game's own toolbar icons are drawn.
        private static Texture2D Draw()
        {
            var texture = new Texture2D(IconSize, IconSize, TextureFormat.ARGB32, false);
            texture.name = "ReDefinitionPlannerIcon";
            texture.filterMode = FilterMode.Bilinear;
            Color ground = new Color(1f, 1f, 1f, 0f);
            Color route = Color.white;
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
