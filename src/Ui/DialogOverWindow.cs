using System.Reflection;
using System;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Ui
{
    // A dialog that stands over the settings window, such as a question or NVIDIA's
    // download, set apart from it.
    // The screen behind it is darkened, which also
    // takes the clicks meant for the window. The dialog is drawn opaque, with a light
    // edge around it.
    //
    // It is spawned not modal.
    // A modal dialog, as it goes, hands the focus to the modal one below it,
    // the pause menu or KSP's settings, where the window was opened from one of them.
    // That one is then raised over the settings window,
    // and the window, not modal, is left without raycasts
    // (UIMasterController.UnregisterModalDialog, FocusModalDialog; decompiled).
    // What a modal dialog would give is set here instead.
    // The darkened screen takes the UI's clicks, the UI_DIALOGS lock stands while it is there, and
    // Unity's mouse events are kept off what lies behind the dark
    // (UnityMouseEvents.ShieldScreen).
    // It also makes links: buttons without a background, for what opens a
    // page rather than acts.
    internal static class DialogOverWindow
    {
        // The darkened screen's object name and colour, the dialog's edge colour and width, and the links' colour.
        private const string ScrimName = "ReDefinitionScrim";
        private static readonly Color Scrim = new Color(0f, 0f, 0f, 0.6f);
        private static readonly Color Edge = new Color(0.8f, 0.8f, 0.8f, 0.55f);
        private static readonly Vector2 EdgeWidth = new Vector2(1.5f, -1.5f);
        private const string LinkColor = "#8fc8ff";

        // The dialog as given, raised; a failure leaves it as the skin draws it.
        internal static PopupDialog Raise(PopupDialog dialog)
        {
            if (dialog == null || dialog.popupWindow == null) return dialog;
            try
            {
                ScreenFit.Add(dialog);
                Hold(dialog);
                Darken(dialog);
                WindowBackdrop.Add(dialog, true);
                Image window = dialog.popupWindow.GetComponent<Image>();
                if (window != null)
                {
                    Outline outline = dialog.popupWindow.AddComponent<Outline>();
                    outline.effectColor = Edge;
                    outline.effectDistance = EdgeWidth;
                }
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("dialog-over-window", "A dialog over the settings window stays as the skin draws"
                                                            + " it (" + CompatibilityLog.Reason(e) + ").");
            }
            return dialog;
        }

        // A button that reads as a link: no background, the text in the links'
        // colour and underlined.
        internal static DialogGUIButton Link(string text, Callback onClick, float width)
        {
            DialogGUIButton link = new DialogGUIButton("<color=" + LinkColor + "><u>" + text + "</u></color>", onClick,
                width, 24f, false);
            ClearBackground(link);
            return link;
        }

        // KSP's own ClearButtonImage is internal; the field it sets, before the
        // button is built, hides the background (DialogGUIButton.Create).
        internal static void ClearBackground(DialogGUIButton button)
        {
            FieldInfo clear = typeof(DialogGUIButton).GetField("clearButtonImage",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (clear != null) clear.SetValue(button, true);
        }

        // The UI_DIALOGS lock's name, and how many of these dialogs stand.
        private const string LockName = "ReDefinitionQuestion";
        private static int holding;

        // As a modal dialog holds KSP's UI (UIMasterController.RegisterModalDialog),
        // for as long as one of these stands.
        private static void Hold(PopupDialog dialog)
        {
            UnityMouseEvents.ShieldScreen(dialog);
            EscapeGuard.Own(dialog);
            if (holding++ == 0) InputLockManager.SetControlLock(ControlTypes.UI_DIALOGS, LockName);
            dialog.onDestroy.AddListener(() =>
            {
                if (--holding > 0) return;
                holding = 0;
                InputLockManager.RemoveControlLock(LockName);
            });
        }

        // Over the whole canvas, directly under the dialog, and gone with it.
        private static void Darken(PopupDialog dialog)
        {
            Transform canvas = dialog.transform.parent;
            if (canvas == null) return;
            GameObject scrim = UiHygiene.Child(ScrimName, canvas, typeof(RectTransform), typeof(Image));
            RectTransform rect = (RectTransform)scrim.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.SetSiblingIndex(dialog.transform.GetSiblingIndex());
            Image image = scrim.GetComponent<Image>();
            image.color = Scrim;
            image.raycastTarget = true;
            dialog.onDestroy.AddListener(() =>
            {
                if (scrim != null) UnityEngine.Object.Destroy(scrim);
            });
        }
    }
}
