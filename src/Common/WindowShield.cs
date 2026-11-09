using System;
using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.LowLevel;

namespace ReDefinition.Ui
{
    // Keeps clicks on a window of ours from reaching what lies behind it: the space centre's buildings, the IVA switches, parts in flight, the
    // map. Unity's mouse events know nothing of KSP's UI. KSP guards its objects each its own way. The buildings have the KSC_FACILITIES
    // lock, the main menu's entries MAIN_MENU, parts in flight EventSystem.IsPointerOverGameObject, and the IVA switches no guard at all.
    // A dialog, modal or not, locks none of those (PopupDialog, UIMasterController: a modal one locks UI_DIALOGS only).
    //
    // Two systems stand in Unity's player loop, right before and right after PreUpdate.SendMouseEvents. Before it, while the cursor is over a
    // window we shield, the control lock is set and every camera's event mask is nothing; after it, each camera has its own mask again.
    // Both mods of ours that open windows (ReDefinition and the planner) shield through this class; the one that comes first installs the
    // systems, the other finds them there.
    internal static class WindowShield
    {
        // The loop tells systems apart by type; Unity's profiler shows the names.
        private struct ReDefinitionWindowShield { }
        private struct ReDefinitionWindowShieldEnd { }

        // What a mod adds around the pass: ReDefinition lends its redirected cameras their screen there. They run in order, after the
        // shield's own work before the pass and before its own work after it.
        internal static readonly List<Action> BeforePass = new List<Action>();
        internal static readonly List<Action> AfterPass = new List<Action>();

        // Windows of a mod that are no dialog and no frame (an IMGUI window, another mod's window): true where the cursor is over one.
        internal static readonly List<Func<bool>> OtherWindows = new List<Func<bool>>();

        // Whether our dialogs let raycasts through at the moment: a window drawn on top by IMGUI, whose clicks must not press the control of
        // our dialog under it.
        internal static readonly List<Func<bool>> PassThroughWhile = new List<Func<bool>>();

        private static readonly List<PopupDialog> shielded = new List<PopupDialog>();
        private static readonly List<KeyValuePair<Camera, int>> masked = new List<KeyValuePair<Camera, int>>();
        private static Camera[] cameras = new Camera[0];
        private const string LockName = "ReDefinitionWindow";
        private static bool locked;
        private static bool tried;
        private static bool working;

        // Dialogs that hold the whole screen: a question over the settings window, with the screen behind it darkened. A click on the dark
        // goes nowhere, Unity's mouse events included.
        private static readonly List<PopupDialog> screens = new List<PopupDialog>();

        // A dialog that holds the whole screen: while it stands, no click reaches anything behind it.
        internal static PopupDialog ShieldScreen(PopupDialog dialog)
        {
            Install();
            if (dialog != null && !screens.Contains(dialog)) screens.Add(dialog);
            return dialog;
        }

        // A window of ours: clicks on it stay on it. Forgotten once it is gone.
        internal static PopupDialog Shield(PopupDialog dialog)
        {
            Install();
            if (dialog == null || shielded.Contains(dialog)) return dialog;
            shielded.Add(dialog);
            if (passing) PassThrough(dialog, true);
            return dialog;
        }

        private sealed class ShieldedFrame
        {
            public RectTransform Frame;
            public Func<bool> Shown;
        }

        private static readonly List<ShieldedFrame> frames = new List<ShieldedFrame>();

        // A panel of ours that is no dialog: clicks on its frame stay on it while `shown` says it is there.
        internal static void ShieldFrame(RectTransform frame, Func<bool> shown)
        {
            Install();
            frames.RemoveAll(old => old.Frame == null);
            if (frame != null && !frames.Exists(old => old.Frame == frame)) frames.Add(new ShieldedFrame { Frame = frame, Shown = shown });
        }

        // Puts the two systems into the player loop, once; what other mods put into the loop stays. False where Unity's mouse event pass is
        // not in the loop: nothing is shielded then.
        internal static bool Install()
        {
            if (tried) return working;
            tried = true;
            try
            {
                PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
                Type present = Find(loop, typeof(ReDefinitionWindowShield).Name);
                if (present == typeof(ReDefinitionWindowShield))
                {
                    working = true;
                    return true;
                }
                if (present != null)
                {
                    // A second copy of this DLL loaded in another assembly context put its systems in: two would remove each other's lock.
                    Debug.LogWarning(Log.Tag + " Another copy of ReDefinition.Common keeps clicks on windows from reaching what lies behind them;"
                                     + " the windows of this one are not covered.");
                    return false;
                }

                if (!InsertAround(ref loop, typeof(UnityEngine.PlayerLoop.PreUpdate.SendMouseEvents)))
                {
                    Debug.LogWarning(Log.Tag + " Unity's mouse event pass is not in the player loop: clicks on our windows reach what lies behind them.");
                    return false;
                }

                PlayerLoop.SetPlayerLoop(loop);
                working = true;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Clicks on our windows could not be kept from what lies behind them: " + e);
                return false;
            }
        }

        // The type of the first system of the loop or its subsystems that has that name; null for none. Found by name, so that the systems of
        // another copy of this DLL are seen too.
        private static Type Find(PlayerLoopSystem system, string name)
        {
            if (system.type != null && system.type.Name == name) return system.type;
            if (system.subSystemList == null) return null;
            foreach (PlayerLoopSystem child in system.subSystemList)
            {
                Type found = Find(child, name);
                if (found != null) return found;
            }
            return null;
        }

        // The two systems as the anchor's neighbours, wherever in the loop it is.
        private static bool InsertAround(ref PlayerLoopSystem system, Type anchor)
        {
            PlayerLoopSystem[] children = system.subSystemList;
            if (children == null) return false;

            for (int i = 0; i < children.Length; i++)
            {
                if (children[i].type == anchor)
                {
                    List<PlayerLoopSystem> list = new List<PlayerLoopSystem>(children);
                    list.Insert(i + 1, new PlayerLoopSystem { type = typeof(ReDefinitionWindowShieldEnd), updateDelegate = EndPass });
                    list.Insert(i, new PlayerLoopSystem { type = typeof(ReDefinitionWindowShield), updateDelegate = BeginPass });
                    system.subSystemList = list.ToArray();
                    return true;
                }

                if (InsertAround(ref children[i], anchor)) return true;
            }
            return false;
        }

        // Both run inside Unity's player loop: nothing may escape.
        private static void BeginPass()
        {
            try
            {
                bool others = AnyTrue(OtherWindows);
                bool over = others || OverShieldedWindow();
                Lock(over);
                if (over) MaskCameras();
                PassThroughOurWindows(AnyTrue(PassThroughWhile));
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("mouse-events-shield", "Clicks on a window of ours could not be kept from what lies behind it ("
                                                             + CompatibilityLog.Reason(e) + ").");
            }

            Run(BeforePass);
        }

        // After Unity's mouse events: the mod's part first, then every camera's event mask back.
        private static void EndPass()
        {
            Run(AfterPass);
            UnmaskCameras();
        }

        // One at a time: a failing part of a mod must not keep the others, nor the masks.
        private static void Run(List<Action> parts)
        {
            for (int i = 0; i < parts.Count; i++)
            {
                try
                {
                    parts[i]();
                }
                catch (Exception e)
                {
                    CompatibilityLog.Warn("mouse-events-part", "A part of a mod around Unity's mouse events failed (" + CompatibilityLog.Reason(e) + ").");
                }
            }
        }

        private static bool AnyTrue(List<Func<bool>> tests)
        {
            for (int i = 0; i < tests.Count; i++)
            {
                if (tests[i]()) return true;
            }
            return false;
        }

        // Whether a screen-holding dialog stands; those gone are forgotten.
        private static bool ScreenShielded()
        {
            for (int i = screens.Count - 1; i >= 0; i--)
            {
                if (screens[i] == null) screens.RemoveAt(i);
                else if (screens[i].isActiveAndEnabled) return true;
            }
            return false;
        }

        // Whether the cursor is over a panel registered with ShieldFrame that shows.
        private static bool OverShieldedFrame(Vector3 mouse)
        {
            for (int i = frames.Count - 1; i >= 0; i--)
            {
                ShieldedFrame shielded = frames[i];
                if (shielded.Frame == null)
                {
                    frames.RemoveAt(i);
                    continue;
                }
                if (!shielded.Frame.gameObject.activeInHierarchy || (shielded.Shown != null && !shielded.Shown())) continue;
                Canvas canvas = shielded.Frame.GetComponentInParent<Canvas>();
                Canvas root = canvas != null ? canvas.rootCanvas : null;
                if (root == null || !root.isActiveAndEnabled) continue;
                Camera eye = root.renderMode != RenderMode.ScreenSpaceOverlay ? root.worldCamera : null;
                if (RectTransformUtility.RectangleContainsScreenPoint(shielded.Frame, mouse, eye)) return true;
            }
            return false;
        }

        // Whether the cursor is over one of our windows: a screen-holding dialog, a panel of our own, or a shielded dialog on a canvas that shows.
        private static bool OverShieldedWindow()
        {
            if (screens.Count > 0 && ScreenShielded()) return true;
            Vector3 mouse = Input.mousePosition;
            if (frames.Count > 0 && OverShieldedFrame(mouse)) return true;
            if (shielded.Count == 0) return false;
            for (int i = shielded.Count - 1; i >= 0; i--)
            {
                PopupDialog dialog = shielded[i];
                if (dialog == null)
                {
                    canvases.Remove(dialog);
                    shielded.RemoveAt(i);
                    continue;
                }
                if (!dialog.isActiveAndEnabled) continue;

                RectTransform frame = dialog.popupWindow != null ? dialog.popupWindow.transform as RectTransform : null;
                if (frame == null) frame = dialog.RTrf;
                if (frame == null) continue;
                Canvas canvas, root;
                CanvasOf(dialog, frame, out canvas, out root);
                // Hidden with the rest of KSP's interface -- F2 and screenshots switch the canvases off (UIMasterController.HideUI), not the
                // dialogs -- it is not there to click.
                if (root == null || !canvas.isActiveAndEnabled || !root.isActiveAndEnabled) continue;
                Camera eye = root != null && root.renderMode != RenderMode.ScreenSpaceOverlay ? root.worldCamera : null;
                if (RectTransformUtility.RectangleContainsScreenPoint(frame, mouse, eye)) return true;
            }
            return false;
        }

        // The canvas a dialog's frame stands on and the root of it, as GetComponentInParent finds them. They are looked up again only when the
        // frame or its parent is another or the canvas is gone: the dialogs stay where KSP put them, and the search climbs the hierarchy in
        // every frame otherwise.
        private sealed class DialogCanvas
        {
            public Transform Frame;
            public Transform Parent;
            public Canvas Canvas;
            public Canvas Root;
        }

        private static readonly Dictionary<PopupDialog, DialogCanvas> canvases = new Dictionary<PopupDialog, DialogCanvas>();

        private static void CanvasOf(PopupDialog dialog, RectTransform frame, out Canvas canvas, out Canvas root)
        {
            DialogCanvas known;
            if (canvases.TryGetValue(dialog, out known) && known.Frame == frame && known.Parent == frame.parent
                && known.Canvas != null && known.Root != null)
            {
                canvas = known.Canvas;
                root = known.Root;
                return;
            }
            canvas = frame.GetComponentInParent<Canvas>();
            root = canvas != null ? canvas.rootCanvas : null;
            if (known == null)
            {
                known = new DialogCanvas();
                canvases[dialog] = known;
            }
            known.Frame = frame;
            known.Parent = frame.parent;
            known.Canvas = canvas;
            known.Root = root;
        }

        // Another window drawn over one of ours: IMGUI draws on top, but uGUI's raycasts still find our window below it, and a click on the
        // other window would press the control there too. While the cursor is over such a window, our windows let raycasts through -- by a
        // canvas group of their own on the window's frame. The dialog's own group is the one KSP takes raycasts from the other dialogs by while
        // a modal one is open (UIMasterController.FocusModalDialog), and a group below it can only narrow what that lets through.
        private static bool passing;

        private static void PassThroughOurWindows(bool pass)
        {
            if (pass == passing) return;
            passing = pass;
            foreach (PopupDialog dialog in shielded) PassThrough(dialog, pass);
        }

        private static void PassThrough(PopupDialog dialog, bool pass)
        {
            if (dialog == null || dialog.popupWindow == null || dialog.popupWindow == dialog.gameObject) return;
            CanvasGroup group = dialog.popupWindow.GetComponent<CanvasGroup>();
            if (group == null) group = dialog.popupWindow.AddComponent<CanvasGroup>();
            group.blocksRaycasts = !pass;
        }

        // Set and removed only as the cursor comes and goes: every change fires GameEvents.onInputLocksModified. In the editors the editor's
        // own lock, as ClickThroughBlocker takes it there (FocusLock: EditorLogic.Lock) -- all but cameras also locks part handling, gizmos and
        // undo there. EditorLogic.Unlock only removes the lock by name. ClickThroughBlocker keeps clicks from IMGUI windows by locking
        // ALLBUTCAMERAS while the cursor is over one (FocusLock, decompiled), which keeps out what asks for locks.
        private static void Lock(bool on)
        {
            // Set again where KSP cleared every lock meanwhile (the flight UI's docking mode, the pause menus: InputLockManager.ClearControlLocks).
            if (on == locked && (!on || InputLockManager.GetControlLock(LockName) != ControlTypes.None)) return;
            locked = on;
            if (!on) InputLockManager.RemoveControlLock(LockName);
            else if (HighLogic.LoadedSceneIsEditor && EditorLogic.fetch != null)
                EditorLogic.fetch.Lock(true, true, true, LockName);
            else InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS, LockName);
        }

        // Sets every camera's event mask to nothing for the pass, their own masks kept. That keeps out what asks for no lock: DoSendMouseEvents
        // skips a camera whose eventMask is 0, and what was hovered gets its OnMouseExit.
        private static void MaskCameras()
        {
            int count = Camera.allCamerasCount;
            if (cameras.Length != count) cameras = new Camera[count];
            Camera.GetAllCameras(cameras);
            foreach (Camera camera in cameras)
            {
                if (camera == null || camera.eventMask == 0) continue;
                masked.Add(new KeyValuePair<Camera, int>(camera, camera.eventMask));
                camera.eventMask = 0;
            }
        }

        // One at a time: a camera left masked would take no clicks at all.
        private static void UnmaskCameras()
        {
            for (int i = 0; i < masked.Count; i++)
            {
                try
                {
                    if (masked[i].Key != null) masked[i].Key.eventMask = masked[i].Value;
                }
                catch (Exception e)
                {
                    CompatibilityLog.Warn("mouse-events-unmask", "A camera could not take clicks again after a window of ours kept them ("
                                                                 + CompatibilityLog.Reason(e) + ").");
                }
            }
            masked.Clear();
        }
    }
}
