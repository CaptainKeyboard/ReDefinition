using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KSP.UI.Screens;
using UnityEngine.UI;

namespace ReDefinition.Flight
{
    // KSP's private fields of a stage in the staging list (StageGroup), read and
    // written without a FieldInfo's boxing: several are read for every stage in
    // every frame. Null where KSP has no such field.
    internal static class StageGroupFields
    {
        // The stage's info rows; its info panel's width, whether it is shown and whether it slides, and its layout; the
        // stage's add and delete buttons in the editor; and its delta-v heading.
        internal static readonly AccessTools.FieldRef<StageGroup, List<StageGroupInfoItem>> Infos =
            Ref<List<StageGroupInfoItem>>("stageGroupInfos");
        internal static readonly AccessTools.FieldRef<StageGroup, float> PanelWidth = Ref<float>("InfoPanelWidth");
        internal static readonly AccessTools.FieldRef<StageGroup, bool> PanelShown = Ref<bool>("infoPanelShown");
        internal static readonly AccessTools.FieldRef<StageGroup, bool> PanelSliding = Ref<bool>("infoPanelSliding");
        internal static readonly AccessTools.FieldRef<StageGroup, LayoutElement> Panel = Ref<LayoutElement>("InfoPanelLayout");
        internal static readonly AccessTools.FieldRef<StageGroup, Button> AddButton = Ref<Button>("addButton");
        internal static readonly AccessTools.FieldRef<StageGroup, Button> DeleteButton = Ref<Button>("deleteButton");
        internal static readonly AccessTools.FieldRef<StageGroup, LayoutElement> DeltaVHeading = Ref<LayoutElement>("DeltaVHeadingObject");

        // A ref to KSP's private field of that name; null where StageGroup has none such or it cannot be reached.
        private static AccessTools.FieldRef<StageGroup, T> Ref<T>(string name)
        {
            FieldInfo field = typeof(StageGroup).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) return null;
            try
            {
                return AccessTools.FieldRefAccess<StageGroup, T>(field);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
