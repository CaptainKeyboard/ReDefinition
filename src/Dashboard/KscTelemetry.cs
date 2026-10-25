using System;
using System.Collections.Generic;
using Contracts;
using ReDefinition.Core;
using UnityEngine;
using Upgradeables;

namespace ReDefinition.Dashboard
{
    // What the dashboard's Space Center page shows and does: the buildings, the vessels to fly, the
    // alarms, the active contracts and the crew; entering a building, flying a vessel and warping to an
    // alarm, each as KSP's own button there does it. The lists are read at most once a second, and only
    // at the Space Center; times go out as UT, for the page to count down from g.ut.
    internal sealed class KscTelemetry
    {
        private const float ListsEvery = 1f;
        // The alarms shown: the next ones.
        private const int AlarmsShown = 8;

        private float listsRead = -1e9f;
        private Json.Raw buildings, vessels, alarms, contracts, crew;

        private static bool AtSpaceCenter
        {
            get { return HighLogic.LoadedScene == GameScenes.SPACECENTER && HighLogic.CurrentGame != null; }
        }

        internal object Buildings() { return Lists() ? buildings : null; }
        internal object Vessels() { return Lists() ? vessels : null; }
        internal object Alarms() { return Lists() ? alarms : null; }
        internal object Contracts() { return Lists() ? contracts : null; }
        internal object Crew() { return Lists() ? crew : null; }

        private bool Lists()
        {
            if (!AtSpaceCenter) return false;
            float now = Time.realtimeSinceStartup;
            if (buildings != null && now - listsRead < ListsEvery) return true;
            listsRead = now;
            buildings = Raw(ReadBuildings());
            vessels = Raw(ReadVessels());
            alarms = Raw(ReadAlarms());
            contracts = Raw(ReadContracts());
            crew = Raw(ReadCrew());
            return true;
        }

        private static Json.Raw Raw(object value)
        {
            return new Json.Raw(Json.Value(value));
        }

        // Each building by its facility (SpaceCenterFacility's names: VehicleAssemblyBuilding, LaunchPad,
        // ...), its level and its last, and whether it stands; the flag pole is no building to enter.
        private static List<Dictionary<string, object>> ReadBuildings()
        {
            var list = new List<Dictionary<string, object>>();
            foreach (SpaceCenterBuilding building in UnityEngine.Object.FindObjectsOfType<SpaceCenterBuilding>())
            {
                if (building is FlagPoleFacility) continue;
                UpgradeableFacility facility = building.Facility;
                list.Add(new Dictionary<string, object>
                {
                    { "id", FacilityName(facility != null ? facility.id : null, building.facilityName) },
                    { "level", facility != null ? (object)facility.FacilityLevel : null },
                    { "max", facility != null ? (object)facility.MaxLevel : null },
                    { "open", building.Operational },
                });
            }
            return list;
        }

        // "SpaceCenter/LaunchPad" is the launch pad's facility; a building without one goes by its own name.
        internal static string FacilityName(string facilityId, string fallback)
        {
            if (string.IsNullOrEmpty(facilityId)) return fallback;
            int slash = facilityId.LastIndexOf('/');
            return slash < 0 ? facilityId : facilityId.Substring(slash + 1);
        }

        // The vessels listed: no debris, flags or space objects.
        internal static bool Flyable(VesselType type)
        {
            return type != VesselType.Debris && type != VesselType.Flag && type != VesselType.SpaceObject && type != VesselType.Unknown
                   && type != VesselType.DroppedPart;
        }

        // And of those what the Tracking Station lets fly: the program's own (DiscoveryInfo Owned).
        private static bool Flyable(Vessel vessel)
        {
            return vessel != null && vessel.state != Vessel.State.DEAD && Flyable(vessel.vesselType) && vessel.DiscoveryInfo != null
                   && vessel.DiscoveryInfo.Level == DiscoveryLevels.Owned;
        }

        private static List<Dictionary<string, object>> ReadVessels()
        {
            var list = new List<Dictionary<string, object>>();
            foreach (Vessel vessel in FlightGlobals.Vessels)
            {
                if (!Flyable(vessel)) continue;
                list.Add(new Dictionary<string, object>
                {
                    { "id", vessel.id.ToString("N") },
                    { "name", vessel.vesselName },
                    { "type", vessel.vesselType.ToString() },
                    { "situation", vessel.situation.ToString() },
                    { "body", vessel.mainBody != null ? vessel.mainBody.bodyName : null },
                    { "launched", vessel.launchTime },
                });
            }
            list.Sort((a, b) => string.Compare((string)a["name"], (string)b["name"], StringComparison.CurrentCultureIgnoreCase));
            return list;
        }

        private static List<Dictionary<string, object>> ReadAlarms()
        {
            var list = new List<Dictionary<string, object>>();
            AlarmClockScenario clock = AlarmClockScenario.Instance;
            if (clock == null || clock.alarms == null) return list;
            var all = new List<AlarmTypeBase>(clock.alarms.Values);
            all.Sort((a, b) => a.ut.CompareTo(b.ut));
            double now = Planetarium.GetUniversalTime();
            foreach (AlarmTypeBase alarm in all)
            {
                if (alarm.ut < now || list.Count >= AlarmsShown) continue;
                list.Add(new Dictionary<string, object>
                {
                    { "id", (long)alarm.Id },
                    { "title", alarm.title },
                    { "vessel", string.IsNullOrEmpty(alarm.vesselName) ? null : alarm.vesselName },
                    { "ut", alarm.ut },
                });
            }
            return list;
        }

        private static List<Dictionary<string, object>> ReadContracts()
        {
            var list = new List<Dictionary<string, object>>();
            ContractSystem system = ContractSystem.Instance;
            if (system == null) return list;
            foreach (Contract contract in system.Contracts)
            {
                if (contract == null || contract.ContractState != Contract.State.Active) continue;
                list.Add(new Dictionary<string, object>
                {
                    { "title", contract.Title },
                    { "deadline", contract.DateDeadline > 0 ? (object)contract.DateDeadline : null },
                    { "funds", contract.FundsCompletion },
                    { "science", (double)contract.ScienceCompletion },
                    { "reputation", (double)contract.ReputationCompletion },
                });
            }
            list.Sort((a, b) => ((double?)a["deadline"] ?? double.MaxValue).CompareTo((double?)b["deadline"] ?? double.MaxValue));
            return list;
        }

        private static Dictionary<string, object> ReadCrew()
        {
            int available = 0, assigned = 0, lost = 0;
            foreach (ProtoCrewMember kerbal in HighLogic.CurrentGame.CrewRoster.Crew)
            {
                switch (kerbal.rosterStatus)
                {
                    case ProtoCrewMember.RosterStatus.Available: available++; break;
                    case ProtoCrewMember.RosterStatus.Assigned: assigned++; break;
                    default: lost++; break;
                }
            }
            return new Dictionary<string, object> { { "available", available }, { "assigned", assigned }, { "lost", lost } };
        }

        // k.enter[facility]: as a click on the building, with KSP's own word where it is closed or
        // locked; not while one of KSP's dialogs at the Space Center holds the buildings.
        internal static void Enter(string[] args)
        {
            if (!AtSpaceCenter || args.Length == 0) return;
            if (!InputLockManager.IsUnlocked(ControlTypes.KSC_FACILITIES))
            {
                LogLine("not entering " + args[0] + ": a dialog at the Space Center holds the buildings");
                return;
            }
            foreach (SpaceCenterBuilding building in UnityEngine.Object.FindObjectsOfType<SpaceCenterBuilding>())
            {
                UpgradeableFacility facility = building.Facility;
                if (FacilityName(facility != null ? facility.id : null, building.facilityName) != args[0]) continue;
                LogLine("entering " + args[0]);
                building.EnterBuilding();
                return;
            }
            LogLine("no building " + args[0] + " to enter");
        }

        // k.fly[id]: as the Tracking Station's Fly, the game saved and the vessel's flight started. Saving
        // builds the saved vessels afresh from FlightGlobals (Game.Updated), so the vessel's place among
        // them is looked up after it.
        internal static void Fly(string[] args)
        {
            if (!AtSpaceCenter || args.Length == 0 || HighLogic.CurrentGame.Mode == Game.Modes.MISSION_BUILDER) return;
            Vessel chosen = FlightGlobals.Vessels.Find(v => v != null && v.id.ToString("N") == args[0]);
            if (!Flyable(chosen))
            {
                LogLine("no vessel " + args[0] + " to fly");
                return;
            }
            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            int index = HighLogic.CurrentGame.flightState.protoVessels.FindIndex(p => p != null && p.vesselID == chosen.id);
            if (index < 0)
            {
                LogLine(chosen.vesselName + " was not saved, so it cannot be flown");
                return;
            }
            LogLine("flying " + chosen.vesselName);
            FlightDriver.StartAndFocusVessel("persistent", index);
        }

        // k.warpTo[alarm]: time warp to the alarm, as KSP warps to a maneuver node.
        internal static void WarpTo(string[] args)
        {
            uint id;
            AlarmTypeBase alarm;
            if (TimeWarp.fetch == null || args.Length == 0 || !uint.TryParse(args[0], out id) || !AlarmClockScenario.TryGetAlarm(id, out alarm)) return;
            if (alarm.ut <= Planetarium.GetUniversalTime()) return;
            TimeWarp.fetch.WarpTo(alarm.ut);
        }

        private static void LogLine(string message)
        {
            Debug.Log(Log.Tag + " Dashboard: " + message);
        }
    }
}
