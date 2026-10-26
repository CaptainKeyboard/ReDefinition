using System;
using System.Collections.Generic;
using System.Reflection;
using KSP.UI.Screens;
using ReDefinition.Core;
using UnityEngine;
using UnityEngine.UI;

namespace ReDefinition.Scaling
{
    // The R&D screen. It lies in its own canvas under the main canvas, so it takes
    // the UI scale as its panels do, but the tree's nodes and the parts list's
    // tiles stay small beside the panels (decompiled).
    // The tree opens larger and zooms in further (ZoomOpen, ZoomMax).
    // The tree's nodes keep their share of the tree at any UI scale and zoom (RnDNodeHooks, NodeShare).
    // The parts list's tiles are drawn larger (TileScale).
    // 4kSP Expanded's R&D scaler is turned off (OtherScalers).
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public class SpaceCentreScaling : MonoBehaviour
    {
        // The zoom the tree opens at, and the furthest it zooms in.
        // KSP opens the tree at UIGridArea.zoom 1 and zooms in no further (zoomMax 1).
        // Here the tree zooms out to KSP's 0.6.
        private const float ZoomOpen = 1.5f;
        private const float ZoomMax = 2.5f;
        // A node's share of the tree against KSP's at a UI scale and zoom of 1.
        // It is what was set at a UI scale of 1.5: twice the tree's scale at the opening zoom.
        internal const float NodeShare = 2f / (1.5f * ZoomOpen);
        // How much larger each tile of the parts list is drawn.
        // A GridLayoutGroup lays the tiles out at the prefab's cell size, so ScaleTiles draws each tile
        // 1.2 times as large about its centre, and WidenTiles widens the gaps, padding and columns to match.
        private const float TileScale = 1.2f;
        private static readonly MethodInfo ZoomTo = typeof(UIGridArea).GetMethod("ZoomTo",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private RDController opened;
        private GridLayoutGroup tiles;
        private bool noted;

        // After every Update: a click on a node makes the parts list's tiles anew
        // (RDPartList.SetupParts), and they are scaled before they are drawn.
        private void LateUpdate()
        {
            RDController controller = RDController.Instance;
            if (controller == null) return;
            if (controller != opened)
            {
                opened = controller;
                tiles = null;
                OpenTree(controller);
            }
            if (tiles == null) tiles = WidenTiles(controller);
            if (tiles != null) ScaleTiles(tiles.transform);
        }

        // Opens a new R&D screen's tree at ZoomOpen and lets it zoom in to ZoomMax; the log notes it once.
        private void OpenTree(RDController controller)
        {
            UIGridArea grid = controller.gridArea;
            if (grid == null) return;
            grid.zoomMax = ZoomMax;
            try
            {
                if (ZoomTo != null) ZoomTo.Invoke(grid, new object[] { ZoomOpen, true });
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn("rnd-zoom", "The R&D tree could not open larger (" + CompatibilityLog.Reason(e) + ").");
            }
            // KSP enables the zoom-in button by zoom != zoomMax as the tree opens
            // (UIGridArea.SetZoomButtonState; decompiled).
            if (grid.zoom_up != null) grid.zoom_up.interactable = true;
            if (grid.zoom_down != null) grid.zoom_down.interactable = true;
            if (noted) return;
            noted = true;
            Debug.Log(Log.Tag + " UI scaling: the R&D tree opens at " + ZoomOpen.ToString("0.0") + " times, zooms in to "
                      + ZoomMax.ToString("0.0") + ", its nodes at " + NodeShare.ToString("0.00") + " of KSP's share of the tree and the parts list at "
                      + TileScale.ToString("0.0") + " times.");
        }

        // The grid's gaps and padding grown by what each tile grows, and as many fewer columns.
        // It runs once per R&D screen: the screen and its grid are made anew
        // at every opening (RDSceneSpawner), so the prefab's values are the base.
        private static GridLayoutGroup WidenTiles(RDController controller)
        {
            RectTransform mask = controller.partList != null ? controller.partList.partTransformMask : null;
            if (mask == null) return null;
            GridLayoutGroup grid = mask.GetComponent<GridLayoutGroup>();
            if (grid == null) grid = mask.GetComponentInChildren<GridLayoutGroup>(true);
            if (grid == null) return null;
            Vector2 cell = grid.cellSize;
            float grow = TileScale - 1f;
            grid.spacing = grid.spacing + cell * grow;
            int x = Mathf.RoundToInt(cell.x * grow * 0.5f);
            int y = Mathf.RoundToInt(cell.y * grow * 0.5f);
            RectOffset padding = grid.padding;
            grid.padding = new RectOffset(padding.left + x, padding.right + x, padding.top + y, padding.bottom + y);
            if (grid.constraint != GridLayoutGroup.Constraint.Flexible && grid.constraintCount > 0)
                grid.constraintCount = Mathf.Max(1, Mathf.FloorToInt(grid.constraintCount / TileScale));
            return grid;
        }

        // Every tile, the parts' and the upgrades' alike, as the list fills.
        private static void ScaleTiles(Transform grid)
        {
            Vector3 wanted = new Vector3(TileScale, TileScale, 1f);
            for (int i = 0; i < grid.childCount; i++)
            {
                Transform tile = grid.GetChild(i);
                if (tile.localScale != wanted) tile.localScale = wanted;
            }
        }
    }

    // The tree nodes' scale for RDNode.Setup (SpaceCentreScaling).
    // RDNode.Setup builds a tree node at the scene's root, at its tech tree's scale
    // (RDNode.scale, 0.6 in the stock tree) in pixels.
    // It then puts the node under RDNodeRoot keeping that size, and takes the node's width and height,
    // where its arrows meet it, from the same scale.
    // So a node is as many pixels at any UI scale and zoom, and the larger either is, the smaller it is beside the tree.
    // Here the scale is multiplied by RDNodeRoot's scale, the UI scale times the zoom,
    // so that a node keeps its share of the tree, times SpaceCentreScaling.NodeShare.
    // It applies to the Setup only, so that a tree saved from the game keeps its own.
    internal static class RnDNodeHooks
    {
        // The id the hook is installed under.
        private const string HarmonyId = "ReDefinition.UiScaling.RnD";

        // RDNode.Setup, the overload without parameters; also for the tests.
        internal static MethodBase Target()
        {
            return typeof(RDNode).GetMethod("Setup", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
        }

        // Whether the hook is in: without it 4kSP's R&D scaling stays (OtherScalers).
        internal static bool Installed;

        // Patches RDNode.Setup; from ScalingAddon.
        internal static void Install()
        {
            HarmonyHooks.Install(HarmonyId, typeof(RnDNodeHooks),
                new List<HarmonyHook> { new HarmonyHook(Target(), nameof(SetupPrefix), nameof(SetupPostfix), null) });
            Installed = true;
        }

        // Only a tree node not yet set up; the list of what a tech requires shows
        // nodes of its own (treeNode false).
        private static void SetupPrefix(RDNode __instance, bool ___setup, out float __state)
        {
            __state = __instance.scale;
            RDController controller = RDController.Instance;
            Transform root = controller != null ? controller.RDNodeRoot : null;
            if (___setup || !__instance.treeNode || root == null) return;
            __instance.scale *= root.lossyScale.x * SpaceCentreScaling.NodeShare;
        }

        // Gives the node back the scale its tech tree set, after Setup sized it.
        private static void SetupPostfix(RDNode __instance, float __state)
        {
            __instance.scale = __state;
        }
    }
}
