using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ReDefinition.Framework
{
    // A close button on the own settings window of a mod bundled here. Such a
    // window opens and closes with the mod's toolbar button -- EVE's and
    // Distant Object's have no close of their own -- and that button is hidden
    // while the mod's settings are bundled, so without this a window opened
    // through Advanced could not be closed at all.
    //
    // Every IMGUI window is drawn through one of two functions of Unity's
    // IMGUIModule, decompiled: GUILayout.DoWindow, which GUILayout.Window and
    // its overloads call, and GUI.DoWindow, which GUI.Window calls -- and which
    // GUILayout.DoWindow calls in turn with a window function of Unity's own
    // (LayoutedWindow). ClickThroughBlocker's windows end in the same two. A
    // Harmony prefix on each swaps the window function of a bundled mod's
    // settings window (IBundledMod.OwnWindow) for one that draws a close button
    // at its top right, then the mod's own. The button comes first: these
    // windows call GUI.DragWindow last, and a control called earlier gets the
    // click. It closes the window as the mod's button does
    // (ToolbarTakeover.CloseOwnWindow) -- and only while that button is hidden
    // here; with the bundling off the mods are as they were. A postfix notes
    // where the window is, for the click shield (UnityMouseEvents).
    //
    // Both run for every window of every mod in every GUI event: a window of
    // another type costs a field read and a lookup by its type. Nothing is
    // looked up before the mods are built: built from here, in the first window
    // any mod draws, they would be built while the game still loads, before the
    // configs they read.
    internal static class ModWindowClose
    {
        private const string HarmonyId = "ReDefinition.ModWindowClose";
        private const float Size = 20f;

        private sealed class Closable
        {
            public readonly IBundledMod Mod;
            public readonly GUI.WindowFunction Outer;
            private readonly GUI.WindowFunction inner;
            public float Width;
            // Where it was last drawn, in the screen's pixels from the top left,
            // and in which frame.
            public Rect Area;
            public int Frame = -1;

            public Closable(IBundledMod mod, GUI.WindowFunction inner)
            {
                Mod = mod;
                this.inner = inner;
                Outer = Draw;
            }

            private void Draw(int id)
            {
                try
                {
                    if (Width > 3f * Size && GUI.Button(new Rect(Width - Size - 6f, 2f, Size, Size - 2f), "X"))
                        ToolbarTakeover.CloseOwnWindow(Mod);
                }
                catch (Exception e)
                {
                    CompatibilityLog.Warn("own-window-close-" + Mod.Id, Mod.ModName + "'s own window got no close button"
                                          + " (" + CompatibilityLog.Reason(e) + ").");
                }
                inner(id);
            }
        }

        // A bundled mod's settings window: drawn by the method of this name on
        // this type or one derived from it. Distant Object's is a plain
        // SettingsGui, held by one MonoBehaviour per scene (SettingsGuiOnMainMenu,
        // SettingsGuiOnGameScenes; decompiled).
        private sealed class Owner
        {
            public IBundledMod Mod;
            public Type Type;
            public string Method;
        }

        private static List<Owner> owners;
        // Whether any of those methods is static, drawn by a delegate with no
        // object to tell its type by.
        private static bool anyStatic;
        // By the type drawing a window: the owners it can be one of -- more than
        // one where their types are related -- or null.
        private static readonly Dictionary<Type, List<Owner>> ownersOf = new Dictionary<Type, List<Owner>>();

        // By the mod's window function -- equal for the same method on the same
        // object however often a mod makes the delegate anew. Only the bundled
        // mods' settings windows; one not drawn for a few frames goes as another
        // is added, so that a window's object made anew at an opening leaves no
        // trail, and all of them at a scene change.
        private static readonly Dictionary<GUI.WindowFunction, Closable> known =
            new Dictionary<GUI.WindowFunction, Closable>();

        // By mod: when its own settings window was last drawn with the close
        // button, in unscaled seconds -- for the settings window's sync, which
        // follows all of a mod's settings only while its window is open.
        private static readonly Dictionary<IBundledMod, float> lastDrawn = new Dictionary<IBundledMod, float>();

        // Whether the mod's own settings window was drawn within the last second:
        // long enough for the sync, a few times a second, to see what was applied
        // there just before it closed. Known once the mods are built, however
        // the window was opened.
        internal static bool RecentlyOpen(IBundledMod mod)
        {
            float drawn;
            return mod != null && lastDrawn.TryGetValue(mod, out drawn) && Time.unscaledTime - drawn <= 1f;
        }

        internal static void Install()
        {
            MethodInfo layout = typeof(GUILayout).GetMethod("DoWindow", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo window = typeof(GUI).GetMethod("DoWindow", BindingFlags.NonPublic | BindingFlags.Static);
            HarmonyHooks.Install(HarmonyId, typeof(ModWindowClose), new[]
            {
                new HarmonyHook(layout, nameof(LayoutWindowPrefix), nameof(WindowPostfix), null),
                new HarmonyHook(window, nameof(WindowPrefix), nameof(WindowPostfix), null),
            });
        }

        internal static void Forget()
        {
            known.Clear();
        }

        // Whether the cursor is over a bundled mod's own window drawn in the last
        // frames. The mouse is measured from the bottom left.
        internal static bool PointerOverModWindow()
        {
            if (known.Count == 0) return false;
            Vector3 mouse = Input.mousePosition;
            Vector2 point = new Vector2(mouse.x, Screen.height - mouse.y);
            int frame = Time.frameCount;
            foreach (Closable closable in known.Values)
            {
                if (frame - closable.Frame > 2) continue;
                if (closable.Area.width > 0f && closable.Area.height > 0f && closable.Area.Contains(point)) return true;
            }
            return false;
        }

        private static void LayoutWindowPrefix(ref GUI.WindowFunction func, Rect screenRect, out object __state)
        {
            __state = Swap(ref func, screenRect);
        }

        private static void WindowPrefix(ref GUI.WindowFunction func, Rect clientRect, out object __state)
        {
            __state = Swap(ref func, clientRect);
        }

        // Where the window is as Unity returns it -- moved, and grown by its
        // layout, which the rect passed in is not yet -- in the screen's pixels
        // through GUI.matrix, as ClickThroughBlocker's IMGUIWindowTracker
        // records it.
        private static void WindowPostfix(Rect __result, object __state)
        {
            Closable closable = __state as Closable;
            if (closable == null) return;
            try
            {
                Matrix4x4 matrix = GUI.matrix;
                Vector3 a = matrix.MultiplyPoint3x4(new Vector3(__result.xMin, __result.yMin, 0f));
                Vector3 b = matrix.MultiplyPoint3x4(new Vector3(__result.xMax, __result.yMax, 0f));
                closable.Area = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y),
                    Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
                // The window's own coordinates, which its function draws in. Its
                // frame is noted as it is swapped.
                closable.Width = __result.width;
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("own-window-area", "Where a bundled mod's own window is could not be told ("
                                                         + CompatibilityLog.Reason(e) + ").");
            }
        }

        // In every OnGUI of every window: nothing may escape.
        private static Closable Swap(ref GUI.WindowFunction func, Rect area)
        {
            try
            {
                if (func == null || (owners == null && !Resolve()) || owners.Count == 0) return null;

                object target = func.Target;
                List<Owner> candidates;
                if (target != null) candidates = OwnersOf(target.GetType());
                else if (anyStatic) candidates = OwnersOf(func.Method.DeclaringType);
                else return null;
                if (candidates == null) return null;

                string name = func.Method.Name;
                Owner owner = null;
                foreach (Owner candidate in candidates)
                {
                    if (candidate.Method != name) continue;
                    owner = candidate;
                    break;
                }
                if (owner == null) return null;
                // Noted however the window was opened -- by its hotkey too, its
                // button not hidden -- for the settings window's sync.
                lastDrawn[owner.Mod] = Time.unscaledTime;
                if (!BundledSettings.Enabled || !ToolbarTakeover.Hides(owner.Mod)) return null;

                Closable closable = ClosableFor(func, owner.Mod);
                // Until the first return tells: the width passed in.
                if (closable.Frame < 0) closable.Width = area.width;
                closable.Frame = Time.frameCount;
                func = closable.Outer;
                return closable;
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("own-window-close", "A bundled mod's own window got no close button ("
                                                          + CompatibilityLog.Reason(e) + ").");
                return null;
            }
        }

        private static bool Resolve()
        {
            if (!BundledSettings.Built) return false;
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                                     | BindingFlags.Instance;
            List<Owner> found = new List<Owner>();
            foreach (IBundledMod mod in BundledSettings.Installed())
            {
                if (mod.OwnWindow == null || mod.OwnWindowType == null) continue;
                Type type = mod.OwnWindowType;
                string method = mod.OwnWindow.Substring(mod.OwnWindow.LastIndexOf('.') + 1);
                foreach (MethodInfo candidate in type.GetMethods(All))
                    if (candidate.Name == method && candidate.IsStatic) anyStatic = true;
                found.Add(new Owner { Mod = mod, Type = type, Method = method });
            }
            owners = found;
            return true;
        }

        private static List<Owner> OwnersOf(Type type)
        {
            List<Owner> found;
            if (type == null) return null;
            if (ownersOf.TryGetValue(type, out found)) return found;
            foreach (Owner candidate in owners)
            {
                if (!candidate.Type.IsAssignableFrom(type)) continue;
                if (found == null) found = new List<Owner>();
                found.Add(candidate);
            }
            ownersOf[type] = found;
            return found;
        }

        private static Closable ClosableFor(GUI.WindowFunction func, IBundledMod mod)
        {
            Closable closable;
            if (known.TryGetValue(func, out closable)) return closable;

            int frame = Time.frameCount;
            List<GUI.WindowFunction> closed = null;
            foreach (KeyValuePair<GUI.WindowFunction, Closable> pair in known)
            {
                if (frame - pair.Value.Frame <= 2) continue;
                if (closed == null) closed = new List<GUI.WindowFunction>();
                closed.Add(pair.Key);
            }
            if (closed != null)
                foreach (GUI.WindowFunction key in closed) known.Remove(key);

            closable = new Closable(mod, func);
            known[func] = closable;
            return closable;
        }
    }
}
