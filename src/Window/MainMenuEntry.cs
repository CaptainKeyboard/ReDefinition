using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using System;
using ReDefinition.Core;
using TMPro;
using UnityEngine;

namespace ReDefinition.Window
{
    // A ReDefinition entry in KSP's main menu, directly under Settings.
    //
    // The main menu's entries are no dialog but text objects in the scene.
    // Each is a TextMeshPro with a TextProButton3D and a BoxCollider, all children of
    // "stage 1" in one column: Start Game, Settings, Community, Addons,
    // Credits, Merch, Quit (the main menu scene, level2, read with UnityPy).
    // MainMenu holds them as fields and gives each its onTap in Start
    // (decompiled). The entry is a copy of Settings, so it has the same parent,
    // font, colours and hover; it gets its own text and tap. It takes the row
    // under Settings, and the entries below move down by one row: the distance
    // between Settings and the entry under it (KeepHeight).
    //
    // The entries fade in with the camera's distance (FadeWithTheOthers).
    //
    // MainMenu locks its entries while one of its dialogs stands
    // (lockEverything, unlockEverything); Harmony postfixes lock the copy with
    // them.
    //
    // With Replace original settings on, no entry is added: KSP's own Settings opens ReDefinition's window.
    // Anything that fails leaves the menu as KSP built it.
    [KSPAddon(KSPAddon.Startup.MainMenu, false)]
    public class MainMenuEntry : MonoBehaviour
    {
        // The Harmony id of the lock postfixes, and the entry's text.
        private const string HarmonyId = "ReDefinition.MainMenuEntry";
        private const string Label = "ReDefinition";

        // Entries whose x differs by less than this stand in one column; the
        // column's entries differ by a thousandth.
        private const float ColumnTolerance = 0.05f;

        // Whether the postfixes are installed (once per run), and the entry made in the current main menu.
        private static bool patched;
        private static TextProButton3D entry;

        // A frame after the menu has started: the postfixes installed and the entry added.
        private IEnumerator Start()
        {
            // MainMenu.Start wires its entries and hides those of expansions that
            // are not installed; the column is final one frame later.
            yield return null;

            try
            {
                Patch();
                if (Add()) Debug.Log(Log.Tag + " Entry added to the main menu.");
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Could not add the entry to the main menu: " + e);
            }
        }

        // Adds the entry under Settings and fits the column to its old height; false where it is left as KSP built it --
        // or where KSP's Settings opens ReDefinition's window in its place.
        private static bool Add()
        {
            MainMenu menu = FindObjectOfType<MainMenu>();
            if (menu == null || menu.settingBtn == null) return false;

            // With *Replace original settings* on, KSP's own Settings entry
            // opens ReDefinition's window, and the column stays as KSP built it.
            ReDefinitionAddon addon = ReDefinitionAddon.Instance;
            if (addon != null && addon.ReplaceKspSettings)
            {
                menu.settingBtn.onTap = Open;
                Debug.Log(Log.Tag + " The main menu's Settings opens ReDefinition's window (Replace original settings).");
                return false;
            }

            Transform settings = menu.settingBtn.transform;
            Transform column = settings.parent;
            if (column == null) return false;

            // The column's entries, and those under Settings, top to bottom.
            List<Transform> entries = new List<Transform>();
            List<Transform> below = new List<Transform>();
            foreach (Transform child in column)
            {
                if (child.GetComponent<TextProButton3D>() == null) continue;
                Vector3 at = child.localPosition;
                if (Mathf.Abs(at.x - settings.localPosition.x) > ColumnTolerance) continue;
                entries.Add(child);
                if (child != settings && at.y < settings.localPosition.y) below.Add(child);
            }
            // Without an entry under Settings there is no row height to go by.
            if (below.Count == 0) return false;
            below.Sort((a, b) => b.localPosition.y.CompareTo(a.localPosition.y));

            float row = settings.localPosition.y - below[0].localPosition.y;
            if (row <= 0f) return false;

            GameObject copy = Instantiate(settings.gameObject, column, false);
            copy.name = Label;
            copy.transform.localPosition = settings.localPosition - new Vector3(0f, row, 0f);
            copy.transform.localRotation = settings.localRotation;
            copy.transform.localScale = settings.localScale;

            entry = copy.GetComponent<TextProButton3D>();
            TextMeshPro text = entry.Text;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Overflow;
            text.text = Label;
            ColliderOverText(copy, text);
            entry.onTap = Open;
            FadeWithTheOthers(menu, menu.settingBtn.Text, text);

            float top = settings.localPosition.y;
            foreach (Transform one in entries) top = Mathf.Max(top, one.localPosition.y);
            float bottom = below[below.Count - 1].localPosition.y;

            foreach (Transform moved in below)
                moved.localPosition -= new Vector3(0f, row, 0f);

            entries.Add(copy.transform);
            KeepHeight(entries, top, top - bottom, top - bottom + row);
            return true;
        }

        // Every entry scaled by the old height over the new, and its distance from
        // the top entry with it.
        // The column then keeps the height it had.
        // Every entry is made smaller and moved towards the top one by the
        // same factor, the old height over the new, so the last entry stands where
        // it stood before and the rows stay evenly spaced.
        private static void KeepHeight(List<Transform> entries, float top, float before, float after)
        {
            if (before <= 0f || after <= before) return;

            float factor = before / after;
            foreach (Transform one in entries)
            {
                Vector3 at = one.localPosition;
                one.localPosition = new Vector3(at.x, top - (top - at.y) * factor, at.z);
                one.localScale *= factor;
            }
        }

        // Adds the copy to the texts MainMenuEnvLogic fades with the camera, where Settings is among them.
        // MainMenuEnvLogic sets the alpha of every text in its uiTexts each frame (DistanceFadeUI,
        // decompiled), which is what makes the entries appear once the menu has loaded.
        private static void FadeWithTheOthers(MainMenu menu, TextMeshPro settings, TextMeshPro text)
        {
            MainMenuEnvLogic scene = menu.envLogic;
            if (scene == null || scene.uiTexts == null || Array.IndexOf(scene.uiTexts, settings) < 0) return;

            // TextProButton3D.Awake gave the copy its full colour; until the
            // next frame's fade it takes the alpha Settings has now.
            Color color = text.color;
            color.a = settings.color.a;
            text.color = color;

            TextMeshPro[] texts = new TextMeshPro[scene.uiTexts.Length + 1];
            Array.Copy(scene.uiTexts, texts, scene.uiTexts.Length);
            texts[scene.uiTexts.Length] = text;
            scene.uiTexts = texts;
        }

        // The copy's collider is Settings', sized for a shorter word: widened to
        // cover the text as drawn, never narrowed.
        private static void ColliderOverText(GameObject copy, TextMeshPro text)
        {
            BoxCollider box = copy.GetComponent<BoxCollider>();
            if (box == null) return;

            text.ForceMeshUpdate();
            Bounds drawn = text.textBounds;
            if (drawn.size.x <= 0f) return;

            Vector3 size = box.size;
            Vector3 center = box.center;
            float left = Mathf.Min(center.x - size.x / 2f, drawn.min.x);
            float right = Mathf.Max(center.x + size.x / 2f, drawn.max.x);
            box.size = new Vector3(right - left, size.y, size.z);
            box.center = new Vector3((left + right) / 2f, center.y, center.z);
        }

        // Opens ReDefinition's settings window, where it is not open.
        private static void Open()
        {
            try
            {
                if (!SettingsWindow.Visible) SettingsWindow.Toggle();
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " The settings window could not be opened from the main menu: " + e);
            }
        }

        // Installs the postfixes on MainMenu's lockEverything and unlockEverything, once.
        private static void Patch()
        {
            if (patched) return;
            patched = true;

            HarmonyHooks.Install(HarmonyId, typeof(MainMenuEntry), new List<HarmonyHook>
            {
                new HarmonyHook(Original("lockEverything"), null, nameof(LockPostfix), null),
                new HarmonyHook(Original("unlockEverything"), null, nameof(UnlockPostfix), null),
            });
        }

        // MainMenu's method of that name without parameters; an exception where it is missing.
        private static MethodInfo Original(string method)
        {
            MethodInfo original = typeof(MainMenu).GetMethod(method,
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (original == null)
                throw new MissingMethodException("MainMenu", method);
            return original;
        }

        // Inside KSP's own menu code: nothing may escape from here.
        private static void LockPostfix()
        {
            try
            {
                if (entry != null) entry.Lock();
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("main-menu-entry-lock", "The main menu's ReDefinition entry could not be locked"
                                      + " with the others (" + CompatibilityLog.Reason(e) + ").");
            }
        }

        // The same for unlockEverything.
        private static void UnlockPostfix()
        {
            try
            {
                if (entry != null) entry.Unlock();
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("main-menu-entry-unlock", "The main menu's ReDefinition entry could not be unlocked"
                                      + " with the others (" + CompatibilityLog.Reason(e) + ").");
            }
        }
    }
}
