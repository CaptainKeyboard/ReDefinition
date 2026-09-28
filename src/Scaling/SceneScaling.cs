using System;
using System.Collections.Generic;
using CommNet;
using KSP.UI.Screens;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Scaling
{
    // Installs what scales at KSP's methods (ScalingHooks) and other mods' windows
    // (ModWindowScale), and turns off other mods' scaling of the same things
    // (OtherScalers), once, as the game starts.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class ScalingAddon : MonoBehaviour
    {
        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            Install("ui-scaling-hooks", "The editor gizmos, orbit lines and portrait gallery are not scaled", ScalingHooks.Install);
            Install("mod-window-scale-hooks", "Other mods' windows are not scaled", ModWindowScale.Install);
            Install("other-scalers", "Other mods' scaling of the same things could not be turned off", OtherScalers.Install);
        }

        private static void Install(string key, string what, Action install)
        {
            try
            {
                install();
            }
            catch (Exception e)
            {
                CompatibilityLog.Warn(key, what + " (" + CompatibilityLog.Reason(e) + ").");
            }
        }
    }

    // In flight: the vessel labels, CommNet's lines and the maneuver gizmo.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class FlightScaling : MonoBehaviour
    {
        private readonly CommNetLines commNet = new CommNetLines();
        private readonly ManeuverGizmos gizmos = new ManeuverGizmos();
        private bool noted;

        private void LateUpdate()
        {
            float scale = UiScaling.World;
            VesselLabelsAt(scale);
            commNet.At(scale);
            gizmos.At(scale);
            if (noted) return;
            noted = true;
            UiScaling.Note("the vessel labels and CommNet's lines");
        }

        // KSP keeps each label at the size of a pixel: it parents it keeping its
        // world scale, and puts it back so as the UI scale changes
        // (BaseLabel.ResetParent: SetParent(null), localScale one, SetParent back),
        // and places it by anchoredPosition in the canvas's units (VesselLabels,
        // WorldToMainCanvas); decompiled. Its own scale, about its anchor, is set
        // here to the world scale wanted over its parent's.
        private static void VesselLabelsAt(float scale)
        {
            VesselLabels labels = VesselLabels.Instance;
            if (labels == null) return;
            Transform parent = labels.transform;
            float parentScale = parent.lossyScale.x;
            if (parentScale <= 0f) return;
            Vector3 wanted = Vector3.one * (scale / parentScale);
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform label = parent.GetChild(i);
                if (label.GetComponent<BaseLabel>() == null) continue;
                if ((label.localScale - wanted).sqrMagnitude > 1e-8f) label.localScale = wanted;
            }
        }
    }

    [KSPAddon(KSPAddon.Startup.TrackingStation, false)]
    public class TrackingStationScaling : MonoBehaviour
    {
        private readonly CommNetLines commNet = new CommNetLines();

        private void LateUpdate()
        {
            commNet.At(UiScaling.World);
        }
    }

    // In the space centre: the R&D tree opens larger. It lies in the R&D screen's
    // canvas under the main canvas, so it takes the UI scale as its panels do, but
    // opens at its own zoom of 1 and zooms in no further (UIGridArea: zoom 1,
    // zoomMax 1; decompiled), small beside the panels. It opens at 1.5 and zooms in
    // to 2.5; out to KSP's 0.6. 4kSP's R&D scaler is turned off (OtherScalers).
    [KSPAddon(KSPAddon.Startup.SpaceCentre, false)]
    public class SpaceCentreScaling : MonoBehaviour
    {
        private const float ZoomOpen = 1.5f;
        private const float ZoomMax = 2.5f;
        private static readonly System.Reflection.MethodInfo ZoomTo = typeof(UIGridArea).GetMethod("ZoomTo",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        private RDController opened;
        private bool noted;

        private void Update()
        {
            RDController controller = RDController.Instance;
            if (controller == null || controller == opened || controller.gridArea == null) return;
            opened = controller;
            UIGridArea grid = controller.gridArea;
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
                      + ZoomMax.ToString("0.0") + ".");
        }
    }

    // CommNet's lines: KSP sets them every frame from its public widths, 4 px in
    // the map and 1 px in flight (CommNetUI.lineWidth2D, lineWidth3D;
    // decompiled). Those widths are set from KSP's own times the scale.
    internal sealed class CommNetLines
    {
        private CommNetUI ui;
        private float width2D;
        private float width3D;

        internal void At(float scale)
        {
            CommNetUI current = CommNetUI.Instance;
            if (current == null) return;
            if (current != ui)
            {
                ui = current;
                width2D = current.lineWidth2D;
                width3D = current.lineWidth3D;
            }
            current.lineWidth2D = width2D * scale;
            current.lineWidth3D = width3D * scale;
        }
    }

    // The maneuver node's gizmo. KSP draws it at a fixed number of screen pixels
    // (ManeuverGizmoBase: screenSize, from the gizmo's prefab, over Screen.height in
    // LateUpdate; decompiled). Each gizmo's own screenSize, as it appears, is
    // multiplied by the scale; one KSP shrinks to minimise it (screenSize 0) is left
    // to shrink, and one opened again is a new one. Its handles and their drag
    // distances follow its size. DMagic's Maneuver Node Evolved's writes to the same
    // field are removed (OtherScalers), so that the size seen first is KSP's.
    internal sealed class ManeuverGizmos
    {
        // Each gizmo's own size as it first appeared open, shared with OtherScalers.
        private static readonly Dictionary<ManeuverGizmo, float> sizes = new Dictionary<ManeuverGizmo, float>();
        private readonly List<ManeuverGizmo> gone = new List<ManeuverGizmo>();
        private bool logged;

        // The gizmo's size when open, at the current scale; 0 for one not seen open.
        internal static float OpenSize(ManeuverGizmoBase gizmo)
        {
            ManeuverGizmo maneuver = gizmo as ManeuverGizmo;
            float own;
            return maneuver != null && sizes.TryGetValue(maneuver, out own) ? own * UiScaling.World : 0f;
        }

        internal void At(float scale)
        {
            Vessel vessel = FlightGlobals.ActiveVessel;
            PatchedConicSolver solver = vessel != null ? vessel.patchedConicSolver : null;
            if (solver == null || solver.maneuverNodes == null) return;
            List<ManeuverNode> nodes = solver.maneuverNodes;
            for (int i = 0; i < nodes.Count; i++)
            {
                ManeuverGizmo gizmo = nodes[i] != null ? nodes[i].attachedGizmo : null;
                if (gizmo == null || gizmo.screenSize <= 0f) continue;
                float own;
                if (!sizes.TryGetValue(gizmo, out own))
                {
                    own = gizmo.screenSize;
                    sizes[gizmo] = own;
                    if (!logged)
                    {
                        logged = true;
                        Debug.Log(Log.Tag + " UI scaling: the maneuver gizmo at " + (own * scale).ToString("0") + " pixels instead of "
                                  + own.ToString("0") + ".");
                    }
                }
                if (!Mathf.Approximately(gizmo.screenSize, own * scale)) gizmo.screenSize = own * scale;
            }
            gone.Clear();
            foreach (ManeuverGizmo gizmo in sizes.Keys)
                if (gizmo == null) gone.Add(gizmo);
            foreach (ManeuverGizmo gizmo in gone) sizes.Remove(gizmo);
        }
    }
}
