using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres.Viewer
{
    // The weather lab's side in the game: the generator's state of the body the camera is over (its parameter set gives the baseline values), the layers of the own cloud pass that have
    // an anvil type (their levers are set in place and put back), the game's seed and the rebuild of the generator and the cloud maps.
    internal sealed class GameLabHost : ILabHost
    {
        private sealed class Original
        {
            internal bool CellList;
            internal float ClusterScale, AnvilScale, UpdraftMs;
        }

        private readonly Dictionary<CloudLayerDef, Original> originals = new Dictionary<CloudLayerDef, Original>();

        private static CelestialBody Body
        {
            get { return FlightGlobals.currentMainBody; }
        }

        public double GeneratorValue(string field)
        {
            GeneratorField gf = GeneratorModule.Current;
            CelestialBody b = Body;
            GeneratorState state = gf == null || b == null ? null : gf.StateOf(b.flightGlobalsIndex);
            return state == null ? double.NaN : GeneratorViewerOverrides.Read(state.Parameters, field);
        }

        private static IEnumerable<CloudLayerDef> AnvilLayers()
        {
            OwnCloudModule m = OwnCloudModule.Current;
            LayerStackSource stack = m == null ? null : m.Stack as LayerStackSource;
            if (stack == null) yield break;
            for (int l = 0; l < stack.LayerCount; l++)
            {
                CloudLayerDef d = stack.LayerAt(l);
                if (AnvilMap.AnvilTypeIndex(d) >= 0) yield return d;
            }
        }

        public double LayerValue(string field)
        {
            foreach (CloudLayerDef d in AnvilLayers())
            {
                switch (field)
                {
                    case "cellList": return d.CellList ? 1.0 : 0.0;
                    case "anvilClusterScale": return d.AnvilClusterScale;
                    case "anvilScale": return d.AnvilScale;
                    case "anvilUpdraftMs": return d.AnvilUpdraftMs;
                    default: return double.NaN;
                }
            }
            return double.NaN;
        }

        public void SetLayerValue(string field, double value)
        {
            foreach (CloudLayerDef d in AnvilLayers())
            {
                if (!originals.ContainsKey(d))
                    originals[d] = new Original { CellList = d.CellList, ClusterScale = d.AnvilClusterScale, AnvilScale = d.AnvilScale, UpdraftMs = d.AnvilUpdraftMs };
                switch (field)
                {
                    case "cellList": d.CellList = value != 0.0; break;
                    case "anvilClusterScale": d.AnvilClusterScale = (float)value; break;
                    case "anvilScale": d.AnvilScale = (float)value; break;
                    case "anvilUpdraftMs": d.AnvilUpdraftMs = (float)value; break;
                }
            }
        }

        public void RestoreLayers()
        {
            foreach (KeyValuePair<CloudLayerDef, Original> pair in originals)
            {
                pair.Key.CellList = pair.Value.CellList;
                pair.Key.AnvilClusterScale = pair.Value.ClusterScale;
                pair.Key.AnvilScale = pair.Value.AnvilScale;
                pair.Key.AnvilUpdraftMs = pair.Value.UpdraftMs;
            }
            originals.Clear();
        }

        public int Seed
        {
            get { return AtmospheresSettings.GenSeed; }
            set { AtmospheresSettings.SetNumber(AtmospheresSettings.Keys.GenSeed, value); }
        }

        public void RequestRebuild(string reason)
        {
            GeneratorModule.RequestRebuild(reason);
        }
    }
}
