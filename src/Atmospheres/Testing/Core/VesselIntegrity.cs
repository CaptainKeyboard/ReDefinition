using System;

namespace ReDefinition.Atmospheres
{
    // What a series probe counts of the active vessel to say it came out of the viewer's placements whole: its parts and the vessels in the scene (a part that breaks off becomes a
    // vessel of its own, debris).
    internal struct VesselCount
    {
        internal bool Valid;
        internal int Parts, Vessels;
        internal string Name;
    }

    internal static class VesselIntegrity
    {
        // The active vessel as the game holds it now; not Valid where there is none or the game cannot be read (a test).
        internal static VesselCount Read()
        {
            try
            {
                Vessel v = FlightGlobals.ActiveVessel;
                if (v == null || v.parts == null) return default(VesselCount);
                return new VesselCount { Valid = true, Parts = v.parts.Count, Vessels = FlightGlobals.Vessels.Count, Name = v.vesselName };
            }
            catch (Exception)
            {
                return default(VesselCount);
            }
        }

        // Whether the vessel at the end is the vessel at the start: the same number of parts and no vessel more in the scene. The detail says what was read.
        internal static bool Intact(VesselCount before, VesselCount now, out string detail)
        {
            if (!before.Valid || !now.Valid)
            {
                detail = "the vessel could not be read (before " + (before.Valid ? "ok" : "not") + ", now " + (now.Valid ? "ok" : "not") + ")";
                return false;
            }
            detail = before.Name + ": " + before.Parts + " parts and " + before.Vessels + " vessels before, " + now.Parts + " parts and " + now.Vessels + " vessels after";
            return now.Parts == before.Parts && now.Vessels <= before.Vessels;
        }

        // Whether the active vessel is loaded and unpacked (a pad vessel is packed for the first seconds of the flight scene).
        internal static bool Unpacked()
        {
            try
            {
                Vessel v = FlightGlobals.ActiveVessel;
                return v != null && v.loaded && !v.packed;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
