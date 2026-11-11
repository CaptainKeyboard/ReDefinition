using System.Collections.Generic;
using System.Linq;

namespace ReDefinition.Atmospheres
{
    // Turns the layers the scan of the game found (EveLayer) into the plain data the role discovery takes, so that the scan can ask
    // LayerRoleDiscovery which of them to drive. A scanned layer has a coverage map and a type map by construction (the scan skips
    // the others) and is not an fx only layer (an fx only layer has no type map in the packs' configs).
    internal static class LiveLayerInfo
    {
        // The layers of the body as discovery input. InnerRadiusM is the layer's radius from the body's centre; the altitude is the
        // height above the body's radius (the order of the layers is what matters).
        internal static List<PackLayerInfo> From(IEnumerable<EveLayer> layers, double bodyRadiusM)
        {
            var infos = new List<PackLayerInfo>();
            if (layers == null) return infos;
            foreach (EveLayer h in layers)
            {
                if (h == null) continue;
                infos.Add(new PackLayerInfo
                {
                    Name = h.Name,
                    Body = h.BodyName,
                    AltitudeM = h.InnerRadiusM - bodyRadiusM,
                    TypeNames = h.TypeNames.ToList(),
                    HasCoverageMap = true,
                    HasTypeMap = true,
                    Origin = "game"
                });
            }
            return infos;
        }

        // The scanned layers that the discovery took, in the order of the discovery.
        internal static List<EveLayer> Taken(LayerDiscovery discovery, IEnumerable<EveLayer> layers)
        {
            var result = new List<EveLayer>();
            if (discovery == null || layers == null) return result;
            List<EveLayer> all = layers.ToList();
            foreach (PackLayerInfo info in discovery.Taken)
            {
                EveLayer match = all.FirstOrDefault(l => l.Name == info.Name && l.BodyName == info.Body);
                if (match != null) result.Add(match);
            }
            return result;
        }
    }
}
