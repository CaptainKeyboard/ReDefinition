using System;
using System.Collections.Generic;
using System.Reflection;
using ReDefinition.Core;
using ReDefinition.Settings;
using UnityEngine;

namespace ReDefinition.Scaling
{
    // Other mods' IMGUI windows at KSP's UI scale. KSP scales its own canvases but
    // no IMGUI window (it sets GUI.matrix nowhere; decompiled), so windows of mods
    // that do not scale themselves keep their pixel size. Unity keeps the GUI.matrix
    // a window is registered with and draws the window with it later
    // (GUI.BeginWindows keeps the matrix around the native calls; decompiled), and
    // maps the pointer back through it. So a prefix on every GUI.Window,
    // GUI.ModalWindow and GUILayout.Window (all take the id, the rect and the window
    // function first) sets a matrix that scales about the window's top left corner,
    // shifted back onto the screen where the grown window would leave it, and a
    // postfix puts the one before back; a finalizer does so where the call throws.
    //
    // Left as they are: a window drawn while GUI.matrix is not the identity; those
    // of Unity and KSP and ReDefinition's own; and those of mods that scale their
    // windows themselves -- with a matrix that is the identity at their own scale of
    // 1 (MechJeb, Kerbal Alarm Clock, RCSBuildAid, Janitor's Closet, CameraTools),
    // or by UI_SCALE in their size or text (Kopernicus, KSPCommunityFixes,
    // RemoteTech, KSP Craft Organizer); their sources read. The prefix runs first
    // and the postfix last among the patches of these methods, so that
    // ClickThroughBlocker's postfix, and ReDefinition's own (ModWindowClose, on
    // GUI.DoWindow within), see the window where it is drawn. Where each scaled
    // window is on the screen is kept for ReDefinition's click shield
    // (UnityMouseEvents): mods that keep clicks from going through test the rect
    // they passed in, which covers only the unscaled part. 4kSP's window scaler,
    // which does the same, is kept from installing (OtherScalers).
    internal static class ModWindowScale
    {
        private const string HarmonyId = "ReDefinition.ModWindowScale";
        private const int First = 800;
        private const int Last = 0;

        private static readonly HashSet<string> ScaleThemselves = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MechJeb2", "KerbalAlarmClock", "RCSBuildAid", "RCSBuildAidToolbar", "JanitorsCloset", "CameraTools",
            "Kopernicus", "KSPCommunityFixes", "RemoteTech", "KspCraftOrganizerPlugin", "ReDefinition",
        };

        private sealed class Drawn
        {
            public Rect Area;
            public int Frame;
        }

        // By the assembly of a window's function: whether its windows are scaled.
        private static readonly Dictionary<Assembly, bool> scaled = new Dictionary<Assembly, bool>();
        // By window id: where a scaled window was last drawn, in the screen's pixels.
        private static readonly Dictionary<int, Drawn> drawn = new Dictionary<int, Drawn>();
        private static bool noted;

        internal static void Install()
        {
            List<HarmonyHook> hooks = new List<HarmonyHook>();
            foreach (MethodInfo method in typeof(GUI).GetMethods(BindingFlags.Public | BindingFlags.Static))
                if ((method.Name == "Window" || method.Name == "ModalWindow") && Hookable(method))
                    hooks.Add(new HarmonyHook(method, nameof(Prefix), nameof(Postfix), nameof(Finalizer), First, Last));
            foreach (MethodInfo method in typeof(GUILayout).GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (method.Name == "Window" && Hookable(method))
                    hooks.Add(new HarmonyHook(method, nameof(Prefix), nameof(Postfix), nameof(Finalizer), First, Last));
            HarmonyHooks.Install(HarmonyId, typeof(ModWindowScale), hooks);
            Debug.Log(Log.Tag + " UI scaling: other mods' windows through " + hooks.Count + " window methods.");
        }

        // The id, the rect and the window function first, as every overload has them.
        internal static bool Hookable(MethodInfo method)
        {
            ParameterInfo[] parameters = method.GetParameters();
            return parameters.Length >= 3 && parameters[0].ParameterType == typeof(int) && parameters[1].ParameterType == typeof(Rect)
                   && parameters[2].ParameterType == typeof(GUI.WindowFunction);
        }

        private static void Prefix(Rect __1, GUI.WindowFunction __2, out object __state)
        {
            __state = null;
            try
            {
                if (__2 == null || !UiScaling.ModWindows) return;
                float scale = GameSettings.UI_SCALE;
                if (Mathf.Abs(scale - 1f) < 0.01f || GUI.matrix != Matrix4x4.identity || !Scaled(__2)) return;
                // Back onto the screen where the grown window would leave it; a
                // GUILayout window's rect can have no size before its layout.
                Vector2 shift = Vector2.zero;
                if (__1.width > 0f) shift.x = Mathf.Min(0f, Screen.width - (__1.x + __1.width * scale));
                if (__1.height > 0f) shift.y = Mathf.Min(0f, Screen.height - (__1.y + __1.height * scale));
                shift.x = Mathf.Max(shift.x, -__1.x);
                shift.y = Mathf.Max(shift.y, -__1.y);
                Vector3 corner = new Vector3(__1.x, __1.y, 0f);
                __state = GUI.matrix;
                GUI.matrix = Matrix4x4.TRS(corner + (Vector3)shift, Quaternion.identity, Vector3.one)
                             * Matrix4x4.Scale(new Vector3(scale, scale, 1f))
                             * Matrix4x4.TRS(-corner, Quaternion.identity, Vector3.one);
                if (noted) return;
                noted = true;
                Debug.Log(Log.Tag + " UI scaling: other mods' windows at " + scale.ToString("0.00") + ", first "
                          + __2.Method.DeclaringType + ".");
            }
            catch (Exception e)
            {
                __state = null;
                CompatibilityLog.Warn("mod-window-scale", "A mod's window could not be scaled (" + CompatibilityLog.Reason(e) + ").");
            }
        }

        // Where the window is drawn, noted, then the matrix before put back.
        private static void Postfix(int __0, Rect __result, object __state)
        {
            if (!(__state is Matrix4x4)) return;
            try
            {
                Matrix4x4 matrix = GUI.matrix;
                Vector3 a = matrix.MultiplyPoint3x4(new Vector3(__result.xMin, __result.yMin, 0f));
                Vector3 b = matrix.MultiplyPoint3x4(new Vector3(__result.xMax, __result.yMax, 0f));
                Drawn window;
                if (!drawn.TryGetValue(__0, out window))
                {
                    window = new Drawn();
                    drawn[__0] = window;
                }
                window.Area = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
                window.Frame = Time.frameCount;
            }
            finally
            {
                GUI.matrix = (Matrix4x4)__state;
            }
        }

        // The matrix put back where the window's call threw, which skips the postfix.
        private static void Finalizer(Exception __exception, object __state)
        {
            if (__exception != null && __state is Matrix4x4) GUI.matrix = (Matrix4x4)__state;
        }

        // Whether the pointer is over a scaled window drawn in the last frames. The
        // mouse is measured from the bottom left.
        internal static bool PointerOverScaledWindow()
        {
            if (drawn.Count == 0) return false;
            Vector3 mouse = Input.mousePosition;
            Vector2 point = new Vector2(mouse.x, Screen.height - mouse.y);
            int frame = Time.frameCount;
            foreach (Drawn window in drawn.Values)
                if (frame - window.Frame <= 2 && window.Area.Contains(point)) return true;
            return false;
        }

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
