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
    // The main menu's entries are no dialog but text objects in the scene: each a
    // TextMeshPro with a TextProButton3D and a BoxCollider, all children of
    // "stage 1" in one column -- Start Game, Settings, Community, Addons,
    // Credits, Merch, Quit (the main menu scene, level2, read with UnityPy).
    // MainMenu holds them as fields and gives each its onTap in Start
    // (decompiled). The entry is a copy of Settings, so it has the same parent,
    // font, colours and hover; it gets its own text and tap. It takes the row
    // under Settings, and the entries below move down by one row: the distance
    // between Settings and the entry under it.
    //
    // The entries fade in and out with the camera's distance: MainMenuEnvLogic
    // sets the alpha of every text in its uiTexts each frame (DistanceFadeUI,
    // decompiled), which is what makes them appear once the menu has loaded.
    // The copy is added to that list wherever Settings is in it.
    //
    // MainMenu locks its entries while one of its dialogs stands
    // (lockEverything, unlockEverything); Harmony postfixes lock the copy with
    // them.
    //
    // Anything that fails leaves the menu as KSP built it.
    [KSPAddon(KSPAddon.Startup.MainMenu, false)]
    public class MainMenuEntry : MonoBehaviour
    {
        private const string HarmonyId = "ReDefinition.MainMenuEntry";
        private const string Label = "ReDefinition";

        // Entries whose x differs by less than this stand in one column; the
        // column's entries differ by a thousandth.
        private const float ColumnTolerance = 0.05f;

        private static bool patched;
        private static TextProButton3D entry;

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

        private static bool Add()
        {
            MainMenu menu = FindObjectOfType<MainMenu>();
            if (menu == null || menu.settingBtn == null) return false;

            Transform settings = menu.settingBtn.transform;
            Transform column = settings.parent;
            if (column == null) return false;

            // The entries under Settings in its column, top to bottom.
            List<Transform> below = new List<Transform>();
            foreach (Transform child in column)
            {
                if (child == settings || child.GetComponent<TextProButton3D>() == null) continue;
                Vector3 at = child.localPosition;
                if (Mathf.Abs(at.x - settings.localPosition.x) > ColumnTolerance) continue;
                if (at.y < settings.localPosition.y) below.Add(child);
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

            foreach (Transform moved in below)
                moved.localPosition -= new Vector3(0f, row, 0f);
            return true;
        }

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

        private static void Patch()
        {
            if (patched) return;
            patched = true;

            HarmonyLib.Harmony harmony = new HarmonyLib.Harmony(HarmonyId);
            Patch(harmony, "lockEverything", nameof(LockPostfix));
            Patch(harmony, "unlockEverything", nameof(UnlockPostfix));
        }

        private static void Patch(HarmonyLib.Harmony harmony, string method, string postfixName)
        {
            MethodInfo original = typeof(MainMenu).GetMethod(method,
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (original == null)
                throw new MissingMethodException("MainMenu", method);

            MethodInfo postfix = typeof(MainMenuEntry).GetMethod(postfixName, BindingFlags.NonPublic | BindingFlags.Static);
            harmony.Patch(original, postfix: new HarmonyLib.HarmonyMethod(postfix));
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
