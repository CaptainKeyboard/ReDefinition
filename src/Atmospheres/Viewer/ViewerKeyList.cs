using System.Collections.Generic;

namespace ReDefinition.Atmospheres.Viewer
{
    // The viewer's list of tuning keys: the registry's keys and the per-layer keys of the stack that stands. The stack may stand after the viewer opened (the pass builds it
    // a moment after the scene loads), so the list is built again whenever the layers it was built for are not the layers there are now.
    internal sealed class ViewerKeyList
    {
        private string signature;

        internal ViewerKeyList()
        {
            Keys = CloudKeyRegistry.Expand(null);
            foreach (CloudKey k in Keys) Names.Add(k.Key);
        }

        internal List<CloudKey> Keys { get; private set; }

        // The names of Keys, in their order.
        internal List<string> Names { get; } = new List<string>();

        // The number of layers the list was built for; -1 before it was built.
        internal int LayerCount { get; private set; } = -1;

        // Builds the list for these layers; true where it changed (the first call always builds).
        internal bool Sync(IList<string> layerNames)
        {
            string now = string.Join("|", layerNames);
            if (signature != null && signature == now) return false;
            signature = now;
            LayerCount = layerNames.Count;
            Keys = CloudKeyRegistry.Expand(layerNames);
            Names.Clear();
            foreach (CloudKey k in Keys) Names.Add(k.Key);
            return true;
        }
    }
}
