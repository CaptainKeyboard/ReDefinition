using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KSP.UI;
using KSP.UI.Screens.Flight;
using ReDefinition.Core;
using ReDefinition.Scaling;
using ReDefinition.Settings;
using UnityEngine;

namespace ReDefinition.Flight
{
    // Better Time Warp Continued's drop-down button right of the mission time's MET
    // button. The mod draws it with IMGUI at a place worked out from KSP's stock
    // time panel (BetterTimeWarp.OnGUI: `new Rect(scale * y, x, ...)`, y from 283
    // and the CommNet icons shown; its source read), which a UI theme's panel does
    // not keep, so the button stands apart from the time. Its Rect is taken here
    // from where KSP's MET button stands (METDisplay.buttonText's button, through the
    // UI camera), in the mod's own size: a transpiler routes that one Rect through
    // Place. Where the MET button is not found, the mod's own place stays.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class TimeWarpButton : MonoBehaviour
    {
        private const string ModType = "BetterTimeWarp.BetterTimeWarp";
        // Between the MET button and the drop-down button, in KSP's UI units.
        private const float Gap = 4f;

        private static RectTransform met;
        private static float lookedAt = -10f;
        private static int replaced;

        private void Awake()
        {
            System.Type mod = UiScaling.TypeOf(ModType);
            if (mod == null) return;
            HarmonyHooks.Safely("timewarp-button", "Better Time Warp's button stays where the mod puts it", () =>
            {
                MethodInfo onGui = mod.GetMethod("OnGUI", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                HarmonyHooks.Install("ReDefinition.TimeWarpButton", typeof(TimeWarpButton),
                    new List<HarmonyHook> { new HarmonyHook(onGui, null, null, null, -1, -1, nameof(RouteRect)) });
                Debug.Log(Log.Tag + " Better Time Warp's button placed right of the MET button (" + replaced + " place).");
            });
        }

        // The Rect built right before GUI.Toggle, the drop-down button's, built
        // through Place instead.
        private static IEnumerable<CodeInstruction> RouteRect(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            ConstructorInfo rect = typeof(Rect).GetConstructor(new[] { typeof(float), typeof(float), typeof(float), typeof(float) });
            MethodInfo place = typeof(TimeWarpButton).GetMethod(nameof(Place), BindingFlags.Static | BindingFlags.NonPublic);
            for (int i = 0; i < code.Count; i++)
            {
                MethodInfo called = code[i].operand as MethodInfo;
                if (code[i].opcode != OpCodes.Call || called == null || called.DeclaringType != typeof(GUI) || called.Name != "Toggle") continue;
                for (int j = i - 1; j >= 0; j--)
                {
                    if (code[j].opcode != OpCodes.Newobj || !Equals(code[j].operand, rect)) continue;
                    CodeInstruction call = new CodeInstruction(OpCodes.Call, place);
                    call.labels.AddRange(code[j].labels);
                    call.blocks.AddRange(code[j].blocks);
                    code[j] = call;
                    replaced++;
                    break;
                }
                break;
            }
            return code;
        }

        // Right of the MET button, centred on it, in screen pixels from the top left
        // as IMGUI counts them; the mod's own place where it is not found.
        private static Rect Place(float x, float y, float width, float height)
        {
            RectTransform button = Met();
            Camera camera = UIMasterController.Instance != null ? UIMasterController.Instance.uiCamera : null;
            if (button == null || camera == null || !button.gameObject.activeInHierarchy) return new Rect(x, y, width, height);
            Vector3[] corners = new Vector3[4];
            button.GetWorldCorners(corners);
            Vector2 low = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            Vector2 high = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
            float gap = Gap * GameSettings.UI_SCALE;
            float middle = Screen.height - (low.y + high.y) / 2f;
            return new Rect(high.x + gap, middle - height / 2f, width, height);
        }

        // KSP's MET button, looked for again a second after it was not found.
        private static RectTransform Met()
        {
            if (met != null) return met;
            if (Time.unscaledTime - lookedAt < 1f) return null;
            lookedAt = Time.unscaledTime;
            METDisplay display = FindObjectOfType<METDisplay>();
            Transform text = display != null && display.buttonText != null ? display.buttonText.transform : null;
            met = text != null ? (text.parent as RectTransform ?? text as RectTransform) : null;
            return met;
        }
    }
}
