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
    // pixels too, so the container stays as KSP has it.
    // Each node is scaled about its own centre instead (MapNode's own transform, which KSP only moves).
    // Its icon, caption, hover label and click area grow with it.
    // 4kSP's own sizing of them is turned off (OtherScalers).
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class MapNodeScaleFlight : MapNodeScale
    {
    }

    // The same in the tracking station.
    [KSPAddon(KSPAddon.Startup.TrackingStation, false)]
    public class MapNodeScaleTrackingStation : MapNodeScale
    {
    }

    // The scaling itself; KSPAddon starts a class in one scene, hence one subclass per scene.
    public abstract class MapNodeScale : MonoBehaviour
    {
        // Whether the log has noted the scaling.
        private bool noted;

        // Every frame: each node's scale set to the UI scale where it differs; noted once, when the first nodes exist.
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
            UiScaling.Note("the map's icons and captions");
        }
    }
}
