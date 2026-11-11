namespace ReDefinition.Atmospheres
{
    // Which parts of the cloud-map module run for a body, pure (CONTRACT section 5, brief C23 items 2 and 7).
    //   eveMode: EVE renders the maps (cloudSource Eve or Both, and EVE is installed and its members are there).
    //   ownMode: nobody else shows the maps, so the own shell does (cloudSource Ours, and Eve or Both without EVE).
    // None runs nothing. A body that EVE has no 2D layer for gets the shell in every mode but None.
    internal static class CloudModeModel
    {
        internal static void Decide(CloudSourceKind source, bool eveAvailable, out bool eveMode, out bool ownMode)
        {
            eveMode = (source == CloudSourceKind.Eve || source == CloudSourceKind.Both) && eveAvailable;
            ownMode = source != CloudSourceKind.None && !eveMode;
        }

        // Whether the own shell stands in for the orbit and map picture: always for Ours, never for None, and for Eve and Both where
        // none of the body's layers carries a 2D layer EVE draws the picture with.
        internal static bool ShellWanted(CloudSourceKind source, bool eveCarriesPicture)
        {
            if (source == CloudSourceKind.Ours) return true;
            if (source == CloudSourceKind.None) return false;
            return !eveCarriesPicture;
        }
    }
}
