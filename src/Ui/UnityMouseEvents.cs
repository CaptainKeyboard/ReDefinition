using System.Collections.Generic;
using System.Reflection;
using System;
using ReDefinition.Core;
using ReDefinition.Upscaler;
using ReDefinition.Window;
using UnityEngine;

namespace ReDefinition.Ui
{
    // Unity's mouse events (OnMouseDown, OnMouseEnter and the rest, sent to
    // the collider under the cursor) skip every camera that renders into a texture.
    // That is in SendMouseEvents.DoSendMouseEvents (decompiled from KSP's
    // UnityEngine.InputLegacyModule: "if (camera == null || (skipRTCameras !=
    // 0 && camera.targetTexture != null)) continue;").
    // Every camera the rig redirects is one.
    // KSP takes the space centre's buildings
    // (SpaceCenterBuilding.OnMouseDown) and the IVA switches
    // (InternalButton.OnMouseDown) only that way, and so do FreeIva's hatches
    // (ClickWatcher.OnMouseDown). With the upscaler on, none of them would react.
    //
    // So each redirected camera gets its own target back, the screen, for
    // the length of Unity's mouse event pass, and the rig's texture straight
    // after (Install). The two parts stand in the shield's passes (WindowShield.BeforePass,
    // AfterPass), which also keep clicks on our windows from reaching what lies behind them.
    //
    // What this class adds to the shield is what only ReDefinition has: the windows of the mods it bundles, the diagnostics window.
    internal static class UnityMouseEvents
    {
        // The cameras lent the screen for this pass, and the rig they belong to.
        private static readonly List<CameraRedirect> lent = new List<CameraRedirect>();
        private static UpscalerRig lentFor;

        // For the one line per rig that shows it working. Unity's hit array is
        // private and never replaced (static readonly).
        private static Array currentHits;
        private static FieldInfo hitTarget;
        private static FieldInfo hitCamera;
        private static UpscalerRig loggedFor;

        // The mod's IMGUI window where it is open, in GUI coordinates; empty while
        // it is closed. Clicks on it stay on it too.
        internal static Rect ImguiWindow;

        private static bool installed;

        // From the addon's Awake.
        // The shield's two systems stand in Unity's player loop, right before and right after PreUpdate.SendMouseEvents.
        // The pass runs once a frame before any Update, long before a camera renders,
        // and the handlers it calls see each camera
        // unredirected: pixelRect and ScreenPointToRay in the screen's pixels, which
        // the mouse position is given in.
        public static void Install()
        {
            if (installed) return;
            installed = true;
            try
            {
                if (!WindowShield.Install())
                {
                    Debug.LogWarning(Log.Tag + " Unity's mouse event pass is not in the player loop:"
                                     + " while the upscaler runs, buildings in the space centre and switches in"
                                     + " IVA do not react to clicks.");
                    return;
                }

                // A bundled mod's own window counts as well: opened through Advanced beside ReDefinition's, it lets clicks through the same
                // way. So does a mod's window drawn larger (ModWindowScale), where the mod's own shield covers only its unscaled part.
                WindowShield.OtherWindows.Add(() => ModWindowClose.PointerOverModWindow() || Scaling.ModWindowScale.PointerOverScaledWindow());
                WindowShield.OtherWindows.Add(OverDiagnostics);
                // This mod's diagnostics window over one of its dialogs too: the click is the window's, not the control's under it. Other
                // mods' unscaled windows are not counted: some draw see-through read-outs as windows (Kerbal Engineer's HUD), and the dialog
                // under one would take no click.
                WindowShield.PassThroughWhile.Add(() => ModWindowClose.PointerOverModWindow() || Scaling.ModWindowScale.PointerOverScaledWindow()
                                                        || OverDiagnostics());
                WindowShield.BeforePass.Add(LendScreens);
                WindowShield.AfterPass.Add(TakeBackScreens);
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

        // Before Unity's mouse events: every redirected camera gets the screen.
        private static void LendScreens()
        {
            try
            {
                UpscalerRig rig = UpscalerRig.Current;
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

        // After Unity's mouse events: each lent camera on the rig's texture again, and the first hit noted.
        private static void TakeBackScreens()
        {
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

        // Whether the cursor is over the diagnostics window (ImguiWindow), drawn by
        // IMGUI: GUI coordinates run down from the top of the screen.
        private static bool OverDiagnostics()
        {
            if (ImguiWindow.width <= 0f) return false;
            Vector3 mouse = Input.mousePosition;
            return ImguiWindow.Contains(new Vector2(mouse.x, Screen.height - mouse.y));
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

        // Whether the camera is one lent the screen in this pass.
        private static bool WasLent(Camera camera)
        {
            for (int i = 0; i < lent.Count; i++)
            {
                if (lent[i] != null && lent[i].Camera == camera) return true;
            }
            return false;
        }

        // Finds Unity's private hit array and its fields, for NoteFirstHit; nothing where Unity's shape differs.
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
