using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KSP.UI;
using ReDefinition.Core;
using ReDefinition.Window;
using TMPro;
using UnityEngine;

namespace ReDefinition.Flight
{
    // Better Time Warp Continued's drop-down button as part of KSP's time panel.
    // The mod draws it with IMGUI at a place worked out from KSP's stock panel
    // (BetterTimeWarp.OnGUI: `GUI.Toggle(new Rect(scale * y, x, ...), windowOpen,
    // buttonContent, skin.button)`; its source read), which a UI theme's panel does
    // not keep, and in every scene its OnGUI runs in, the loading between scenes
    // too. Here a copy of the panel's MET button (its sprites, states and theme;
    // the panel read with UnityPy from sharedassets7), square, stands at the end of
    // the warp row: as high as the row, as wide as the warp-to-sunrise button below
    // it (UIWarpToNextMorning.button) and right above it, showing the mod's arrow
    // and opening the mod's window (BetterTimeWarp.UIOpen). It keeps one place on
    // the screen: at the end of the warp row while the panel is unfolded, at the
    // end of the MET row while it is folded, so that the sunrise extension, which
    // shows only while the panel is unfolded, slides out from under it; the
    // extension's top right corner is squared to join it. The mod's own IMGUI
    // button is not drawn while the copy stands (a transpiler routes its
    // GUI.Toggle through Toggle), nor where there is no time panel; elsewhere it
    // stays, right of the WARP label (Place).
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class TimeWarpButton : MonoBehaviour
    {
        internal const string ModType = "BetterTimeWarp.BetterTimeWarp";
        // Between the label and the button, in KSP's UI units.
        private const float Gap = 4f;

        // The copy in the time panel stands: the mod's own button is not drawn.
        internal static bool Replaced;
        private static TextMeshProUGUI label;
        private static float lookedAt = -10f;
        private static int routed;

        // Whether the mod's own button is drawn through Toggle, which hides it while
        // the copy stands: without that, a copy would stand beside it.
        internal static bool Routed
        {
            get { return routed == 2; }
        }

        internal static bool Installed
        {
            get { return TypeLookup.Find(ModType) != null; }
        }

        // The namespace the mod registers its toolbar button with in ToolbarControl
        // (BetterTimeWarp.MODID; its source read).
        private const string ToolbarNamespace = "BetterTimeWarp_NS";

        private void Awake()
        {
            System.Type mod = TypeLookup.Find(ModType);
            if (mod == null) return;
            // Its toolbar button only where the player keeps it (OwnSettings.TimeWarpToolbarButton).
            ToolbarTakeover.KeepOwn(ToolbarNamespace, () =>
            {
                ReDefinitionAddon addon = ReDefinitionAddon.Instance;
                return addon != null && addon.Live.TimeWarpToolbarButton;
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
