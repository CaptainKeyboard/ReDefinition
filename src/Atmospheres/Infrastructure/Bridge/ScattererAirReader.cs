using System;
using System.Globalization;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // Scatterer's atmosphere node of a body, read from the game database at run time (E40 horizon fade): the numbers the cloud pass's aerial perspective needs so that it
    // has the optical depth of the air Scatterer draws. The node is Scatterer's own config text, not its code, so nothing is hooked or reflected into the DLL:
    //
    //   Scatterer_atmosphere { Atmo { name = Kerbin  HR = 3.84  HM = 1  m_betaR = 0.01534,0.03571,0.08755  BETA_MSca = 0.01214,0.01214,0.01214 ... } }
    //
    // (StockScattererConfigs/Planets/Kerbin/atmo.cfg; units per km and km, see CloudBridgeAir.FromScatterer). A node that is missing, whose keys do not parse or whose
    // values are not positive gives no air and a reason; the caller keeps the pass's own air in that case.
    internal static class ScattererAirReader
    {
        internal const string RootNode = "Scatterer_atmosphere", BodyNode = "Atmo";

        // The air of the body named bodyName among the root nodes (null or empty roots: none).
        internal static CloudBridgeAir.Air Read(ConfigNode[] roots, string bodyName, out string why)
        {
            why = null;
            var none = new CloudBridgeAir.Air();
            if (roots == null || roots.Length == 0) { why = "no " + RootNode + " node in the game database"; return none; }
            if (string.IsNullOrEmpty(bodyName)) { why = "the body has no name"; return none; }
            foreach (ConfigNode root in roots)
            {
                if (root == null) continue;
                foreach (ConfigNode atmo in root.GetNodes(BodyNode))
                {
                    if (!string.Equals(atmo.GetValue("name"), bodyName, StringComparison.Ordinal)) continue;
                    Vector3 betaR, betaM;
                    float hr, hm;
                    if (!TryVector(atmo.GetValue("m_betaR"), out betaR)) { why = "m_betaR of " + bodyName + " is missing or not three numbers"; return none; }
                    if (!TryVector(atmo.GetValue("BETA_MSca"), out betaM)) { why = "BETA_MSca of " + bodyName + " is missing or not three numbers"; return none; }
                    if (!TryFloat(atmo.GetValue("HR"), out hr)) { why = "HR of " + bodyName + " is missing or not a number"; return none; }
                    if (!TryFloat(atmo.GetValue("HM"), out hm)) { why = "HM of " + bodyName + " is missing or not a number"; return none; }
                    CloudBridgeAir.Air air = CloudBridgeAir.FromScatterer(betaR, betaM, hr, hm);
                    if (!air.Valid) why = "the values of " + bodyName + " are not positive";
                    return air;
                }
            }
            why = "no " + BodyNode + " node named " + bodyName;
            return none;
        }

        // The air from the game database.
        internal static CloudBridgeAir.Air Read(string bodyName, out string why)
        {
            ConfigNode[] roots = null;
            try
            {
                if (GameDatabase.Instance != null) roots = GameDatabase.Instance.GetConfigNodes(RootNode);
            }
            catch (Exception e)
            {
                why = "the database could not be read (" + e.GetType().Name + ")";
                return new CloudBridgeAir.Air();
            }
            return Read(roots, bodyName, out why);
        }

        internal static bool TryFloat(string text, out float value)
        {
            value = 0f;
            return !string.IsNullOrEmpty(text) && float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        // "a,b,c" (spaces allowed) as a vector.
        internal static bool TryVector(string text, out Vector3 value)
        {
            value = Vector3.zero;
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Split(',');
            float x, y, z;
            if (parts.Length != 3 || !TryFloat(parts[0], out x) || !TryFloat(parts[1], out y) || !TryFloat(parts[2], out z)) return false;
            value = new Vector3(x, y, z);
            return true;
        }
    }
}
