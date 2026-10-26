using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KSP.UI;
using ReDefinition.Core;
using ReDefinition.Window;
using TMPro;
using UnityEngine;

namespace ReDefinition.Hud
{
    // Better Time Warp Continued's drop-down button as part of KSP's time panel.
    // The mod draws it with IMGUI at a place worked out from KSP's stock panel
    // (BetterTimeWarp.OnGUI: `GUI.Toggle(new Rect(scale * y, x, ...), windowOpen,
    // buttonContent, skin.button)`; its source read). A UI theme's panel does
    // not keep that place. The mod draws it in every scene its OnGUI runs in, the loading
    // between scenes too. Here a copy of the panel's MET button (its sprites, states and theme;
    // the panel read with UnityPy from sharedassets7), square, stands at the end of
    // the warp row. It is as high as the row, as wide as the warp-to-sunrise button below
    // it (UIWarpToNextMorning.button) and right above it, showing the mod's arrow
    // and opening the mod's window (BetterTimeWarp.UIOpen). TimeWarpBar keeps it in
    // its place. The mod's own IMGUI button is not drawn while the copy stands
    // (Toggle), nor where there is no time panel. Where the mod's own settings switch
    // the mod or its drop-down button off, no copy stands either (ModDrawsButton).
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class TimeWarpButton : MonoBehaviour
    {
        // Better Time Warp's main class, by its full name.
        internal const string ModType = "BetterTimeWarp.BetterTimeWarp";
        // Between the label and the button, in KSP's UI units.
        private const float Gap = 4f;

        // The copy in the time panel stands: the mod's own button is not drawn.
        internal static bool Replaced;
        // The copy is being made in this scene (TimeWarpBar.Start): the mod's toolbar
        // button is hidden from then on, not only once it stands.
        internal static bool Coming;
        private static TextMeshProUGUI label;
        // The warp row's label once found, when it was last looked for, and how many of the mod's two calls Route routed.
        private static float lookedAt = -10f;
        private static int routed;

        // Whether the mod's own button is drawn through Toggle, which hides it while
        // the copy stands: without that, a copy would stand beside it.
        internal static bool Routed
        {
            get { return routed == 2; }
        }

        // Whether Better Time Warp is loaded.
        internal static bool Installed
        {
            get { return TypeLookup.Find(ModType) != null; }
        }

        // The namespace the mod registers its toolbar button with in ToolbarControl
        // (BetterTimeWarp.MODID; its source read).
        private const string ToolbarNamespace = "BetterTimeWarp_NS";

        // Where Better Time Warp is loaded: its toolbar button hidden while the copy stands, and its OnGUI routed through
        // Toggle and Place.
        private void Awake()
        {
            System.Type mod = TypeLookup.Find(ModType);
            if (mod == null) return;
            // Its toolbar button only where the player keeps it (OwnSettings.TimeWarpToolbarButton),
            // and hidden only where the copy stands in its place: the mod registers it once, for
            // the space centre, the flight, the map and the tracking station alike
            // (BetterTimeWarp.Start, AddToAllToolbars; its source read), and the copy is made
            // in the flight and at the space centre only, so in the tracking station it stays.
            ToolbarTakeover.KeepOwn(ToolbarNamespace, () =>
            {
                ReDefinitionAddon addon = ReDefinitionAddon.Instance;
                return (addon != null && addon.Live.TimeWarpToolbarButton) || !(Replaced || Coming);
            });
            HarmonyHooks.Safely("timewarp-button", "Better Time Warp's button stays where the mod puts it", () =>
            {
                MethodInfo onGui = mod.GetMethod("OnGUI", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                HarmonyHooks.Install("ReDefinition.TimeWarpButton", typeof(TimeWarpButton),
                    new List<HarmonyHook> { new HarmonyHook(onGui, null, null, null, -1, -1, nameof(Route)) });
                Debug.Log(Log.Tag + " Better Time Warp's button: " + routed + " of 2 calls routed.");
            });
        }

        // The mod's GUI.Toggle through Toggle, and the Rect built right before it
        // through Place -- both or neither: the toggle routed without its Rect would
        // draw the button where the mod put it, hidden only where the copy stands.
        private static IEnumerable<CodeInstruction> Route(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> original = new List<CodeInstruction>(instructions);
            List<CodeInstruction> code = new List<CodeInstruction>(original);
            routed = 0;
            ConstructorInfo rect = typeof(Rect).GetConstructor(new[] { typeof(float), typeof(float), typeof(float), typeof(float) });
            MethodInfo place = typeof(TimeWarpButton).GetMethod(nameof(Place), BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo toggle = typeof(TimeWarpButton).GetMethod(nameof(Toggle), BindingFlags.Static | BindingFlags.NonPublic);
            for (int i = 0; i < code.Count; i++)
            {
                MethodInfo called = code[i].operand as MethodInfo;
                if (code[i].opcode != OpCodes.Call || called == null || called.DeclaringType != typeof(GUI) || called.Name != "Toggle"
                    || called.GetParameters().Length != 4) continue;
                code[i] = new CodeInstruction(OpCodes.Call, toggle) { labels = code[i].labels, blocks = code[i].blocks };
                routed++;
                for (int j = i - 1; j >= 0; j--)
                {
                    if (code[j].opcode != OpCodes.Newobj || !Equals(code[j].operand, rect)) continue;
                    code[j] = new CodeInstruction(OpCodes.Call, place) { labels = code[j].labels, blocks = code[j].blocks };
                    routed++;
                    break;
                }
                break;
            }
            if (routed == 2) return code;
            routed = 0;
            return original;
        }

        // The mod's GUI.Toggle, which a transpiler routes here. The button is not drawn while the copy
        // stands, nor while loading or where there is no time panel. Elsewhere it stays, right of the
        // WARP label (Place), and is drawn as the mod draws it.
        private static bool Toggle(Rect position, bool value, GUIContent content, GUIStyle style)
        {
            if (Replaced) return value;
            // Switched off: drawn where and when the mod draws it.
            if (!FlightUiSwitch.On(s => s.TimeWarpPanelButton)) return GUI.Toggle(position, value, content, style);
            if (HighLogic.LoadedScene == GameScenes.LOADING || HighLogic.LoadedScene == GameScenes.LOADINGBUFFER || Label() == null)
                return value;
            return GUI.Toggle(position, value, content, style);
        }

        // Right of the label's text, centred on it, in screen pixels from the top left
        // as IMGUI counts them; the mod's own place where it is not found.
        private static Rect Place(float x, float y, float width, float height)
        {
            if (!FlightUiSwitch.On(s => s.TimeWarpPanelButton)) return new Rect(x, y, width, height);
            TextMeshProUGUI text = Label();
            Camera camera = UIMasterController.Instance != null ? UIMasterController.Instance.uiCamera : null;
            if (text == null || camera == null || !text.gameObject.activeInHierarchy) return new Rect(x, y, width, height);
            Bounds bounds = text.textBounds;
            Vector2 end = RectTransformUtility.WorldToScreenPoint(camera,
                text.rectTransform.TransformPoint(new Vector3(bounds.max.x, bounds.center.y, 0f)));
            return new Rect(end.x + Gap * GameSettings.UI_SCALE, Screen.height - end.y - height / 2f, width, height);
        }

        // Whether the mod draws its drop-down button in this game. Its OnGUI returns at
        // once where its "Mod Enabled?" is off. It draws no button where "Hide the
        // dropdown button in the flight scene?" is on, at the space centre too
        // (BTWCustomParams.enabled, hideDropdownButtonInFlight, read in OnGUI from
        // HighLogic.CurrentGame.Parameters; its source read). Where they cannot be read,
        // it returns true, as the mod draws by default.
        internal static bool ModDrawsButton()
        {
            if (!paramsResolved)
            {
                paramsResolved = true;
                paramsType = TypeLookup.Find(ParamsType);
                paramEnabled = paramsType != null ? paramsType.GetField("enabled", BindingFlags.Instance | BindingFlags.Public) : null;
                paramHidden = paramsType != null
                    ? paramsType.GetField("hideDropdownButtonInFlight", BindingFlags.Instance | BindingFlags.Public)
                    : null;
                if (paramEnabled != null && paramEnabled.FieldType != typeof(bool)) paramEnabled = null;
                if (paramHidden != null && paramHidden.FieldType != typeof(bool)) paramHidden = null;
            }
            Game game = HighLogic.CurrentGame;
            if (paramsType == null || game == null || game.Parameters == null) return true;
            try
            {
                object node = game.Parameters.CustomParams(paramsType);
                if (node == null) return true;
                if (paramEnabled != null && !(bool)paramEnabled.GetValue(node)) return false;
                return paramHidden == null || !(bool)paramHidden.GetValue(node);
            }
            catch (System.Exception e)
            {
                CompatibilityLog.Warn("timewarp-params", "Better Time Warp's own switches could not be read, so its button"
                                                         + " is copied as the mod draws it by default ("
                                                         + CompatibilityLog.Reason(e) + ").");
                return true;
            }
        }

        // The mod's settings class, by its full name; that type and its two switches once looked up; whether they were.
        private const string ParamsType = "BetterTimeWarp.BTWCustomParams";
        private static System.Type paramsType;
        private static FieldInfo paramEnabled, paramHidden;
        private static bool paramsResolved;

        // The warp row's label, looked for again a second after it was not found.
        internal static TextMeshProUGUI Label()
        {
            if (label != null) return label;
            if (Time.unscaledTime - lookedAt < 1f) return null;
            lookedAt = Time.unscaledTime;
            foreach (TextMeshProUGUI text in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
                if (text != null && text.name == "Label_Warp" && text.gameObject.scene.IsValid()) label = text;
            return label;
        }
    }
}
