using System.Collections.Generic;
using System.Reflection;
using System;
using ReDefinition.Core;
using ReDefinition.Upscaler;
using UnityEngine.LowLevel;
using UnityEngine;

namespace ReDefinition.Window
{
    // Unity's mouse events -- OnMouseDown, OnMouseEnter and the rest, sent to
    // the collider under the cursor -- skip every camera that renders into a
    // texture (SendMouseEvents.DoSendMouseEvents, decompiled from KSP's
    // UnityEngine.InputLegacyModule: "if (camera == null || (skipRTCameras !=
    // 0 && camera.targetTexture != null)) continue;"), and every camera the rig
    // redirects is one. KSP takes the space centre's buildings
    // (SpaceCenterBuilding.OnMouseDown) and the IVA switches
    // (InternalButton.OnMouseDown) only that way, and so do FreeIva's hatches
    // (ClickWatcher.OnMouseDown): with the upscaler on, none of them would react.
    //
    // So each redirected camera gets its own target back -- the screen -- for
    // the length of Unity's mouse event pass, and the rig's texture straight
    // after: two systems in Unity's player loop, right before and right after
    // PreUpdate.SendMouseEvents. The pass runs once a frame before any Update,
    // long before a camera renders, and the handlers it calls see each camera
    // unredirected: pixelRect and ScreenPointToRay in the screen's pixels, which
    // the mouse position is given in. Public API (UnityEngine.LowLevel.PlayerLoop);
    // what other mods put into the loop stays.
    //
    // The same two systems keep clicks on ReDefinition's windows from reaching
    // what lies behind them. Unity's mouse events know nothing of KSP's UI, and
    // KSP guards its objects each its own way: the buildings by the
    // KSC_FACILITIES lock, the main menu's entries by MAIN_MENU, parts in flight by
    // EventSystem.IsPointerOverGameObject, the IVA switches not at all -- and a
    // dialog, modal or not, locks none of those (PopupDialog, UIMasterController:
    // a modal one locks UI_DIALOGS only). ClickThroughBlocker keeps clicks from
    // IMGUI windows by locking ALLBUTCAMERAS while the cursor is over one
    // (FocusLock, decompiled), which keeps out what asks for locks. So while the
    // cursor is over one of ReDefinition's windows that lock is set, and for what
    // asks for none every camera's event mask is nothing for the length of Unity's
    // pass -- DoSendMouseEvents skips a camera whose eventMask is 0, and what was
    // hovered gets its OnMouseExit -- and its own straight after.
    internal static class UnityMouseEvents
    {
        // The loop tells systems apart by type; Unity's profiler shows the names.
        private struct ReDefinitionLendScreens { }
        private struct ReDefinitionTakeBackScreens { }

        private static readonly List<CameraRedirect> lent = new List<CameraRedirect>();
        private static UpscalerRig lentFor;

        // For the one line per rig that shows it working. Unity's hit array is
        // private and never replaced (static readonly).
        private static Array currentHits;
        private static FieldInfo hitTarget;
        private static FieldInfo hitCamera;
        private static UpscalerRig loggedFor;

        // ReDefinition's windows, and the cameras masked for the pass with their own masks.
        private static readonly List<PopupDialog> shielded = new List<PopupDialog>();
        private static readonly List<KeyValuePair<Camera, int>> masked = new List<KeyValuePair<Camera, int>>();
        private static Camera[] cameras = new Camera[0];
        private const string LockName = "ReDefinitionWindow";
        private static bool locked;

        // The mod's IMGUI window where it is open, in GUI coordinates; empty while
        // it is closed. Clicks on it stay on it too.
        internal static Rect ImguiWindow;

        // A window of this mod: clicks on it stay on it. Forgotten once it is
        // gone.
        internal static PopupDialog Shield(PopupDialog dialog)
        {
            if (dialog == null || shielded.Contains(dialog)) return dialog;
            shielded.Add(dialog);
            if (passing) PassThrough(dialog, true);
            return dialog;
        }

        // From the addon's Awake.
        public static void Install()
        {
            try
            {
                PlayerLoopSystem loop = UnityEngine.LowLevel.PlayerLoop.GetCurrentPlayerLoop();
                if (Contains(loop, typeof(ReDefinitionLendScreens))) return;

                if (!InsertAround(ref loop, typeof(UnityEngine.PlayerLoop.PreUpdate.SendMouseEvents)))
                {
                    Debug.LogWarning(Log.Tag + " Unity's mouse event pass is not in the player loop:"
                                     + " while the upscaler runs, buildings in the space centre and switches in"
                                     + " IVA do not react to clicks.");
                    return;
                }

                UnityEngine.LowLevel.PlayerLoop.SetPlayerLoop(loop);
                ResolveHits();
                Debug.Log(Log.Tag + " Redirected cameras take part in Unity's mouse events."
                          + (currentHits == null ? " The objects they reach cannot be named in the log here." : ""));
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " Redirected cameras could not be added to Unity's mouse"
                                 + " events: " + e);
            }
        }

        private static bool Contains(PlayerLoopSystem system, Type type)
        {
            if (system.type == type) return true;
            if (system.subSystemList == null) return false;
            foreach (PlayerLoopSystem child in system.subSystemList)
            {
                if (Contains(child, type)) return true;
            }
            return false;
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
                    list.Insert(i + 1, new PlayerLoopSystem
                    {
                        type = typeof(ReDefinitionTakeBackScreens),
                        updateDelegate = TakeBackScreens
                    });
                    list.Insert(i, new PlayerLoopSystem
                    {
                        type = typeof(ReDefinitionLendScreens),
                        updateDelegate = LendScreens
                    });
                    system.subSystemList = list.ToArray();
                    return true;
                }

                if (InsertAround(ref children[i], anchor)) return true;
            }
            return false;
        }

        // Both run inside Unity's player loop: nothing may escape.
        private static void LendScreens()
        {
            try
            {
                // A bundled mod's own window counts as well: opened through
                // Advanced beside ReDefinition's, it lets clicks through the same way.
                bool overMods = ModWindowClose.PointerOverModWindow();
                bool over = overMods || OverShieldedWindow();
                Lock(over);
                if (over) MaskCameras();
                PassThroughOurWindows(overMods);
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("mouse-events-shield", "Clicks on ReDefinition's window could not be kept from what"
                                                             + " lies behind it (" + CompatibilityLog.Reason(e) + ").");
            }

            try
            {
                ReDefinitionAddon addon = ReDefinitionAddon.Instance;
                UpscalerRig rig = addon != null ? addon.CurrentRig : null;
                if (rig == null) return;

                lentFor = rig;
                rig.LendScreens(lent);
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("mouse-events-lend", "Redirected cameras could not take part in Unity's"
                                                           + " mouse events (" + CompatibilityLog.Reason(e) + ").");
            }
        }

        private static void TakeBackScreens()
        {
            UnmaskCameras();
            if (lent.Count == 0) return;

            // One at a time: a camera left on the screen would render past the
            // upscaler, so one failing must not keep the others.
            for (int i = 0; i < lent.Count; i++)
            {
                try
                {
                    if (lent[i] != null) lent[i].TakeBackScreen();
                }
                catch (Exception e)
                {
                    CompatibilityLog.Warn("mouse-events-take-back", "A redirected camera could not take the"
                                                                    + " upscaler's texture back after Unity's mouse"
                                                                    + " events (" + CompatibilityLog.Reason(e) + ").");
                }
            }

            try
            {
                NoteFirstHit();
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("mouse-events-hit", "Unity's mouse event hit could not be read ("
                                                          + CompatibilityLog.Reason(e) + ").");
            }

            lent.Clear();
        }

        // Whether the cursor is over the frame of one of ReDefinition's windows, on the
        // canvas it is drawn on.
        private static bool OverShieldedWindow()
        {
            Vector3 mouse = Input.mousePosition;
            // The diagnostics window, drawn by IMGUI: GUI coordinates run down from
            // the top of the screen. As the last OnGUI left it, a frame before this.
            if (ImguiWindow.width > 0f && ImguiWindow.Contains(new Vector2(mouse.x, Screen.height - mouse.y))) return true;
            if (shielded.Count == 0) return false;
            for (int i = shielded.Count - 1; i >= 0; i--)
            {
                PopupDialog dialog = shielded[i];
                if (dialog == null)
                {
                    shielded.RemoveAt(i);
                    continue;
                }
                if (!dialog.isActiveAndEnabled) continue;

                RectTransform frame = dialog.popupWindow != null ? dialog.popupWindow.transform as RectTransform : null;
                if (frame == null) frame = dialog.RTrf;
                if (frame == null) continue;
                Canvas canvas = frame.GetComponentInParent<Canvas>();
                Canvas root = canvas != null ? canvas.rootCanvas : null;
                // Hidden with the rest of KSP's interface -- F2 and screenshots
                // switch the canvases off (UIMasterController.HideUI), not the
                // dialogs -- it is not there to click.
                if (root == null || !canvas.isActiveAndEnabled || !root.isActiveAndEnabled) continue;
                Camera eye = root != null && root.renderMode != RenderMode.ScreenSpaceOverlay ? root.worldCamera : null;
                if (RectTransformUtility.RectangleContainsScreenPoint(frame, mouse, eye)) return true;
            }
            return false;
        }

        // A bundled mod's own window over one of ReDefinition's: IMGUI draws on top,
        // but uGUI's raycasts still find ReDefinition's window below it, and a click
        // on the mod's window would press the control there too. While the cursor is
        // over such a window, ReDefinition's windows let raycasts through -- by a
        // canvas group of their
        // own on the window's frame. The dialog's own group is the one KSP takes
        // raycasts from the other dialogs by while a modal one is open
        // (UIMasterController.FocusModalDialog), and a group below it can only
        // narrow what that lets through.
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

        // Set and removed only as the cursor comes and goes: every change fires
        // GameEvents.onInputLocksModified. In the editors the editor's own lock,
        // as ClickThroughBlocker takes it there (FocusLock: EditorLogic.Lock) --
        // all but cameras also locks part handling, gizmos and undo there.
        // EditorLogic.Unlock only removes the lock by name.
        private static void Lock(bool on)
        {
            if (on == locked) return;
            locked = on;
            if (!on) InputLockManager.RemoveControlLock(LockName);
            else if (HighLogic.LoadedSceneIsEditor && EditorLogic.fetch != null)
                EditorLogic.fetch.Lock(true, true, true, LockName);
            else InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS, LockName);
        }

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
                    CompatibilityLog.Warn("mouse-events-unmask", "A camera could not take clicks again after"
                                                                 + " ReDefinition's window kept them ("
                                                                 + CompatibilityLog.Reason(e) + ").");
                }
            }
            masked.Clear();
        }

        // Once per rig: the first object Unity's mouse events found through a
        // camera that had lent its screen -- the log line that shows clicks reach
        // the space centre's buildings and the cockpit's switches.
        private static void NoteFirstHit()
        {
            if (currentHits == null || lentFor == null || lentFor == loggedFor) return;

            // Every fifteenth frame: a hovered object stays under the cursor for
            // many, and reading Unity's private hits boxes on every read.
            if (Time.frameCount % 15 != 0) return;

            // 0 is IMGUI's, 1 the 3D physics hit, 2 the 2D one.
            for (int i = 1; i < currentHits.Length; i++)
            {
                object hit = currentHits.GetValue(i);
                GameObject target = hitTarget.GetValue(hit) as GameObject;
                Camera camera = hitCamera.GetValue(hit) as Camera;
                if (target == null || camera == null || !WasLent(camera)) continue;

                loggedFor = lentFor;
                Debug.Log(Log.Tag + " Unity's mouse events reach '" + target.name
                          + "' through the redirected camera '" + camera.name + "'.");
                return;
            }
        }

        private static bool WasLent(Camera camera)
        {
            for (int i = 0; i < lent.Count; i++)
            {
                if (lent[i] != null && lent[i].Camera == camera) return true;
            }
            return false;
        }

        private static void ResolveHits()
        {
            Type type = typeof(Input).Assembly.GetType("UnityEngine.SendMouseEvents");
            if (type == null) return;

            Type hitType = type.GetNestedType("HitInfo", BindingFlags.NonPublic);
            FieldInfo hits = type.GetField("m_CurrentHit", BindingFlags.NonPublic | BindingFlags.Static);
            if (hitType == null || hits == null) return;

            hitTarget = hitType.GetField("target", BindingFlags.Public | BindingFlags.Instance);
            hitCamera = hitType.GetField("camera", BindingFlags.Public | BindingFlags.Instance);
            if (hitTarget != null && hitCamera != null) currentHits = hits.GetValue(null) as Array;
        }
    }
}
