using System;
using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Scaling
{
    // Other mods' IMGUI windows at KSP's UI scale. KSP scales its own canvases but
    // no IMGUI window (it sets GUI.matrix nowhere; decompiled), so windows of mods
    // that do not scale themselves keep their pixel size. Unity keeps the GUI.matrix
    // a window is registered with and draws the window with it later
    // (GUI.BeginWindows keeps the matrix around the native calls; decompiled), and
    // maps the pointer back through it. So a prefix on every GUI.Window,
    // GUI.ModalWindow and GUILayout.Window overload sets a matrix that scales about
    // the window's top left corner as it was first drawn. All overloads take the id,
    // the rect and the window function first (Hookable).
    // The matrix shifts the window back onto the screen where the grown window would leave it.
    // A postfix puts the matrix before back, and a finalizer does so where the call throws.
    // AnchorOf explains why the point scaled about stays put.
    //
    // A window drawn while GUI.matrix is not the identity stays as it is.
    // So do the windows of Unity, KSP, ReDefinition and the mods in ScaleThemselves.
    // ModWindowScale.First and ModWindowScale.Last put the patches in order.
    // ModWindowScale.drawn keeps where each scaled window is for ReDefinition's click shield (UnityMouseEvents).
    // 4kSP's window scaler, which does the same, is kept from installing (OtherScalers).
    internal static class ModWindowScale
    {
        // The Harmony id, and the priorities that put the prefix first and the postfix last among the patches.
        // So ClickThroughBlocker's postfix and ReDefinition's own (ModWindowClose, on GUI.DoWindow within)
        // see the window where it is drawn.
        private const string HarmonyId = "ReDefinition.ModWindowScale";
        private const int First = 800;
        private const int Last = 0;

        // The assemblies of mods that scale their windows themselves, and ReDefinition's own.
        // MechJeb, Kerbal Alarm Clock, RCSBuildAid, Janitor's Closet and CameraTools use a matrix that is the identity
        // at their own scale of 1. Kopernicus, KSPCommunityFixes, RemoteTech and KSP Craft Organizer use UI_SCALE in
        // their size or text. Their sources were read.
        private static readonly HashSet<string> ScaleThemselves = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MechJeb2", "KerbalAlarmClock", "RCSBuildAid", "RCSBuildAidToolbar", "JanitorsCloset", "CameraTools",
            "Kopernicus", "KSPCommunityFixes", "RemoteTech", "KspCraftOrganizerPlugin", "ReDefinition",
        };

        // A scaled window as it was last drawn.
        private sealed class Drawn
        {
            // Where the window was last drawn, in the screen's pixels, and in which frame.
            public Rect Area;
            public int Frame;
            // The point the window is scaled about.
            public Vector2 Anchor;
            // Where the last call returned the window, in its own coordinates.
            // A rect passed in elsewhere was placed by its mod's code (centred, clamped, a saved place loaded).
            // It is scaled about its new corner, so that it stands where it was put
            // and does not drift by (scale - 1) times the way it was moved (AnchorOf).
            public Vector2 Returned;
            public bool HasReturned;
            // Drawn without a background (Kerbal Engineer's HUD read-outs): no window to
            // click on, so not shielded.
            public bool SeeThrough;
        }

        // By the assembly of a window's function: whether its windows are scaled.
        private static readonly Dictionary<Assembly, bool> scaled = new Dictionary<Assembly, bool>();
        // By window id: where a scaled window was last drawn, in the screen's pixels,
        // and the point it is scaled about. Windows not drawn for a while are dropped
        // now and then: a mod may draw each window under a new id.
        // The click shield reads the areas: mods that keep clicks from going through test the rect they passed in,
        // which covers only the unscaled part.
        private static readonly Dictionary<int, Drawn> drawn = new Dictionary<int, Drawn>();
        private const int PruneEvery = 300;
        private static int prunedFrame;
        private static readonly List<int> stale = new List<int>();

        // What the prefix hands the postfix: whether it set a matrix, and the one
        // before.
        private struct State
        {
            // Whether the prefix set a matrix, the matrix before it, and whether the window has no background.
            public bool Applied;
            public Matrix4x4 Saved;
            public bool SeeThrough;
        }
        // Whether the log has noted the first window scaled.
        private static bool noted;
        // Whether the hooks are in: without them 4kSP's window scaling stays (OtherScalers).
        internal static bool Installed;

        // Patches every GUI.Window, GUI.ModalWindow and GUILayout.Window overload; from ScalingAddon.
        internal static void Install()
        {
            List<HarmonyHook> hooks = new List<HarmonyHook>();
            foreach (MethodInfo method in typeof(GUI).GetMethods(BindingFlags.Public | BindingFlags.Static))
                if ((method.Name == "Window" || method.Name == "ModalWindow") && Hookable(method))
                    hooks.Add(Hook(method));
            foreach (MethodInfo method in typeof(GUILayout).GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (method.Name == "Window" && Hookable(method))
                    hooks.Add(Hook(method));
            HarmonyHooks.Install(HarmonyId, typeof(ModWindowScale), hooks);
            Installed = true;
            Debug.Log(Log.Tag + " UI scaling: other mods' windows through " + hooks.Count + " window methods.");
        }

        // The id, the rect and the window function first, as every overload has them.
        internal static bool Hookable(MethodInfo method)
        {
            ParameterInfo[] parameters = method.GetParameters();
            return parameters.Length >= 3 && parameters[0].ParameterType == typeof(int) && parameters[1].ParameterType == typeof(Rect)
                   && parameters[2].ParameterType == typeof(GUI.WindowFunction);
        }

        // The overloads with a style take it fifth, after the title; the prefix of
        // those reads it by its place, as Harmony's __args would build an array on
        // every call.
        private static HarmonyHook Hook(MethodInfo method)
        {
            return new HarmonyHook(method, StyleFifth(method) ? nameof(PrefixStyled) : nameof(Prefix), nameof(Postfix),
                                   nameof(Finalizer), First, Last);
        }

        // Whether the overload takes a GUIStyle fifth.
        internal static bool StyleFifth(MethodInfo method)
        {
            ParameterInfo[] parameters = method.GetParameters();
            return parameters.Length > 4 && parameters[4].ParameterType == typeof(GUIStyle);
        }

        // The prefix of the overloads without a style.
        private static void Prefix(int __0, Rect __1, GUI.WindowFunction __2, out State __state)
        {
            __state = Begin(__0, __1, __2, null);
        }

        // The prefix of the overloads with a style fifth (StyleFifth).
        private static void PrefixStyled(int __0, Rect __1, GUI.WindowFunction __2, GUIStyle __4, out State __state)
        {
            __state = Begin(__0, __1, __2, __4);
        }

        // A window whose style draws no background is see-through; the overloads
        // without a style take the skin's window, which has one.
        private static State Begin(int id, Rect rect, GUI.WindowFunction function, GUIStyle style)
        {
            State state = new State();
            try
            {
                if (function == null || !UiScaling.ModWindows) return state;
                float scale = UiScaling.World;
                if (Mathf.Abs(scale - 1f) < 0.01f || GUI.matrix != Matrix4x4.identity || !Scaled(function)) return state;
                Vector2 anchor = AnchorOf(id, rect);
                // Where the grown window lands, then back onto the screen where it
                // would leave it; a GUILayout window's rect can have no size before
                // its layout.
                Vector2 at = anchor + scale * (rect.position - anchor);
                Vector2 shift = Vector2.zero;
                if (rect.width > 0f) shift.x = Mathf.Min(0f, Screen.width - (at.x + rect.width * scale));
                if (rect.height > 0f) shift.y = Mathf.Min(0f, Screen.height - (at.y + rect.height * scale));
                shift.x = Mathf.Max(shift.x, -at.x);
                shift.y = Mathf.Max(shift.y, -at.y);
                Vector3 pivot = new Vector3(anchor.x, anchor.y, 0f);
                state.Saved = GUI.matrix;
                state.Applied = true;
                state.SeeThrough = style != null && style.normal.background == null;
                GUI.matrix = Matrix4x4.TRS(pivot + (Vector3)shift, Quaternion.identity, Vector3.one)
                             * Matrix4x4.Scale(new Vector3(scale, scale, 1f))
                             * Matrix4x4.TRS(-pivot, Quaternion.identity, Vector3.one);
                if (noted) return state;
                noted = true;
                Debug.Log(Log.Tag + " UI scaling: other mods' windows at " + scale.ToString("0.00") + ", first "
                          + function.Method.DeclaringType + ".");
            }
            catch (Exception e)
            {
                if (state.Applied) GUI.matrix = state.Saved;
                state = new State();
                CompatibilityLog.Warn("mod-window-scale", "A mod's window could not be scaled (" + CompatibilityLog.Reason(e) + ").");
            }
            return state;
        }

        // Where the window is drawn, noted, then the matrix before put back.
        private static void Postfix(int __0, Rect __result, State __state)
        {
            try
            {
                if (!__state.Applied) return;
                Matrix4x4 matrix = GUI.matrix;
                Vector3 a = matrix.MultiplyPoint3x4(new Vector3(__result.xMin, __result.yMin, 0f));
                Vector3 b = matrix.MultiplyPoint3x4(new Vector3(__result.xMax, __result.yMax, 0f));
                Note(drawn, __0, Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y)),
                     __result.position, __state.SeeThrough);
            }
            finally
            {
                if (__state.Applied) GUI.matrix = __state.Saved;
            }
        }

        // Where window `id` was drawn on the screen this frame and where its call returned it.
        private static void Note(Dictionary<int, Drawn> where, int id, Rect area, Vector2 returned, bool seeThrough)
        {
            Drawn window;
            if (!where.TryGetValue(id, out window))
            {
                window = new Drawn();
                where[id] = window;
            }
            window.Area = area;
            window.Returned = returned;
            window.HasReturned = true;
            window.Frame = Time.frameCount;
            window.SeeThrough = seeThrough;
        }

        // The point window id is scaled about: its top left corner when first drawn,
        // or when its code last put it somewhere other than where the call before
        // returned it.
        // The point stays put while the window is dragged: GUI.DragWindow moves the rect by the pointer's travel
        // mapped back through the matrix, 1/scale of it. A matrix that followed the corner would leave the window
        // behind the pointer (above 1) or ahead of it (below).
        private static Vector2 AnchorOf(int id, Rect rect)
        {
            Prune(drawn);
            Drawn window;
            if (!drawn.TryGetValue(id, out window))
            {
                window = new Drawn { Anchor = rect.position, Frame = Time.frameCount };
                drawn[id] = window;
            }
            else if (window.HasReturned && PlacedByCode(window.Returned, rect.position))
                window.Anchor = rect.position;
            return window.Anchor;
        }

        // Half a pixel either way: a rect the mod rounds comes back a hair off
        // without having been moved.
        internal static bool PlacedByCode(Vector2 returned, Vector2 passed)
        {
            return Mathf.Abs(passed.x - returned.x) > 0.5f || Mathf.Abs(passed.y - returned.y) > 0.5f;
        }

        // The matrix put back where the window's call threw, which skips the postfix.
        private static void Finalizer(Exception __exception, State __state)
        {
            if (__exception != null && __state.Applied) GUI.matrix = __state.Saved;
        }

        // Whether the pointer is over a scaled window drawn in the last frames. The
        // mouse is measured from the bottom left.
        internal static bool PointerOverScaledWindow()
        {
            return Over(drawn);
        }

        // Over one of `windows` that has a background and was drawn in the last two frames.
        private static bool Over(Dictionary<int, Drawn> windows)
        {
            Prune(windows);
            if (windows.Count == 0) return false;
            Vector3 mouse = Input.mousePosition;
            Vector2 point = new Vector2(mouse.x, Screen.height - mouse.y);
            int frame = Time.frameCount;
            foreach (Drawn window in windows.Values)
                if (!window.SeeThrough && frame - window.Frame <= 2 && window.Area.width > 0f && window.Area.height > 0f && window.Area.Contains(point))
                    return true;
            return false;
        }

        // Only windows drawn in the last frames count (Over); a window not drawn for
        // PruneEvery frames is dropped with its anchor.
        private static void Prune(Dictionary<int, Drawn> windows)
        {
            int frame = Time.frameCount;
            if (frame - prunedFrame < PruneEvery) return;
            prunedFrame = frame;
            stale.Clear();
            foreach (KeyValuePair<int, Drawn> window in windows)
                if (frame - window.Value.Frame > PruneEvery) stale.Add(window.Key);
            foreach (int id in stale) windows.Remove(id);
        }

        // Whether the windows of the function's assembly are scaled: not Unity's, KSP's or those of ScaleThemselves.
        private static bool Scaled(GUI.WindowFunction function)
        {
            Type type = function.Method.DeclaringType;
            Assembly assembly = type != null ? type.Assembly : null;
            if (assembly == null) return false;
            bool result;
            if (scaled.TryGetValue(assembly, out result)) return result;
            string name = assembly.GetName().Name;
            result = !name.StartsWith("UnityEngine", StringComparison.Ordinal) && name != "Assembly-CSharp"
                     && name != "Assembly-CSharp-firstpass" && !ScaleThemselves.Contains(name);
            scaled[assembly] = result;
            return result;
        }
    }
}
