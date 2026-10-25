using System.Collections;
using System.Reflection;
using KSP.UI.Screens;
using KSP.UI.Screens.Flight;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Flight
{
    // The crew portraits' buttons that add and remove a portrait slot, which KSP
    // labels "|<" and ">|" (KerbalPortraitGallery: GalleryAddSlotBtn and
    // GalleryRemSlotBtn, each an 18-unit Button with a TextMeshPro child "Text";
    // sharedassets7, read with UnityPy), with the look of the staging stack's
    // buttons that add and delete a stage (StageGroup.addButton and deleteButton on
    // StageManager.stageGroupPrefab): their picture, colour and states, the
    // theme's where one replaces them. Their "Add Slot" and "Remove Slot" labels on
    // hover stay.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class CrewSlotButtons : MonoBehaviour
    {
        // KSP's members of the portrait gallery that are not public, public ones too.
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        // The gallery's add-slot and remove-slot buttons.
        private static readonly FieldInfo AddSlot = typeof(KerbalPortraitGallery).GetField("btnAddSlot", Private);
        private static readonly FieldInfo RemoveSlot = typeof(KerbalPortraitGallery).GetField("btnRemSlot", Private);

        // Waits for the portraits and the staging stack, then gives both slot buttons the stack's add and delete look.
        private IEnumerator Start()
        {
            if (!FlightUiSwitch.On(s => s.CrewSlotButtons)) yield break;
            float until = Time.unscaledTime + 15f;
            while (Time.unscaledTime < until && (KerbalPortraitGallery.Instance == null || StageManager.Instance == null
                                                 || StageManager.Instance.stageGroupPrefab == null))
                yield return null;
            KerbalPortraitGallery gallery = KerbalPortraitGallery.Instance;
            StageGroup stage = StageManager.Instance != null ? StageManager.Instance.stageGroupPrefab : null;
            if (gallery == null || stage == null || AddSlot == null || RemoveSlot == null || StageGroupFields.AddButton == null || StageGroupFields.DeleteButton == null)
            {
                Debug.Log(Log.Tag + " The crew slot buttons keep KSP's look: the portraits or the staging stack's buttons were not found.");
                yield break;
            }
            int done = 0;
            if (Dress(AddSlot.GetValue(gallery) as Button, StageGroupFields.AddButton(stage))) done++;
            if (Dress(RemoveSlot.GetValue(gallery) as Button, StageGroupFields.DeleteButton(stage))) done++;
            Debug.Log(Log.Tag + " The crew slot buttons take the staging stack's add and delete look: " + done + " of 2.");
        }

        // `slot` with `look`'s picture, colour and states; its glyph taken away.
        private static bool Dress(Button slot, Button look)
        {
            if (slot == null || look == null) return false;
            Image face = slot.targetGraphic as Image;
            if (face == null) face = slot.GetComponent<Image>();
            Image lookFace = look.targetGraphic as Image;
            if (lookFace == null) lookFace = look.GetComponent<Image>();
            if (face == null || lookFace == null) return false;
            face.sprite = lookFace.sprite;
            face.type = Image.Type.Simple;
            face.preserveAspect = true;
            face.color = lookFace.color;
            slot.transition = look.transition;
            slot.spriteState = look.spriteState;
            slot.colors = look.colors;
            Transform glyph = slot.transform.Find("Text");
            if (glyph != null) glyph.gameObject.SetActive(false);
            return true;
        }
    }
}
