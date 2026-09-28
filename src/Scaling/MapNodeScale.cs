using System.Collections.Generic;
using KSP.UI.Screens.Mapview;
using UnityEngine;

namespace ReDefinition.Scaling
{
    // The map's icons and their captions at the UI scale. KSP keeps them at a fixed
    // number of pixels: it puts every MapNode into one container under the main
    // canvas and scales that container by 1 / uiScale (MapViewCanvasUtil.
    // ResetNodeCanvasContainerScale), and sizes each icon in pixels
    // (MapNode.Create's pixelSize, 14 to 32); decompiled. The positions are in
    // pixels too, so the container stays as KSP has it; each node is scaled about
    // its own centre instead (MapNode's own transform, which KSP only moves), and
    // its icon, caption, hover label and click area grow with it. With 4kSP
    // installed, which sizes the icons and captions itself, this steps aside.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class MapNodeScaleFlight : MapNodeScale
    {
    }

    [KSPAddon(KSPAddon.Startup.TrackingStation, false)]
    public class MapNodeScaleTrackingStation : MapNodeScale
    {
    }

    public abstract class MapNodeScale : MonoBehaviour
    {
        private bool noted;

        private void Start()
        {
            if (!UiScaling.Installed(UiScaling.FourkSpMap)) return;
            UiScaling.Note("the map's icons and captions", "4kSP");
            enabled = false;
        }

        private void LateUpdate()
        {
            List<MapNode> nodes = MapNode.AllMapNodes;
            if (nodes == null) return;
            float scale = UiScaling.World;
            Vector3 wanted = new Vector3(scale, scale, scale);
            for (int i = 0; i < nodes.Count; i++)
            {
                MapNode node = nodes[i];
                if (node == null) continue;
                Transform transform = node.transform;
                if ((transform.localScale - wanted).sqrMagnitude > 1e-6f) transform.localScale = wanted;
            }
            if (noted || nodes.Count == 0) return;
            noted = true;
            UiScaling.Note("the map's icons and captions", null);
        }
    }
}
