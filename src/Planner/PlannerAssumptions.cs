using System.Collections.Generic;
using ReDefinition.Core;

namespace ReDefinition.Planner
{
    // What the planner takes for granted about MechJeb's and Parsek's code, with the fingerprints of the builds it was checked against
    // (ForeignAssumption). ReDefinition's catalogue lists these too, so its report and its checks cover them.
    internal static class PlannerAssumptions
    {
        private const string Bool = "System.Boolean";
        private const string Int = "System.Int32";
        private const string Double = "System.Double";
        private const string StringType = "System.String";
        private const string MechJebCore = "MuMech.MechJebCore";

        // MechJeb as the map window's autopilot flies a route (Autopilot/MechJebDriver): the node executor, the landing and ascent
        // autopilots, the target and warp controllers and the staging limit, reached through the vessel's master core. MechJeb 2.15.4.0, the
        // stable release, and 2.16 (the development builds), decompiled: the members named here are the same in both. The ascent is PVG in
        // 2.15 and PSG in 2.16, and is reached through the settings' AscentAutopilot, which both have. Without it the autopilot is greyed out.
        public static readonly ForeignAssumption MechJebAutopilot =
            new ForeignAssumption("mechjeb-autopilot", "MechJeb", "MechJeb flying the planner's route")
                .Type(MechJebCore).Type("MuMech.VesselExtensions")
                .Method("MuMech.VesselExtensions", "GetMasterMechJeb", "Vessel")
                .Field(MechJebCore, "Node").Field(MechJebCore, "Landing").Field(MechJebCore, "Target").Field(MechJebCore, "Warp")
                .Field(MechJebCore, "Staging").Field(MechJebCore, "AscentSettings")
                .Method(MechJebCore, "GetComputerModule", StringType)
                .Method("MuMech.MechJebModuleNodeExecutor", "ExecuteOneNode", "System.Object")
                .Method("MuMech.MechJebModuleNodeExecutor", "ExecuteAllNodes", "System.Object")
                .Method("MuMech.MechJebModuleNodeExecutor", "Abort")
                .Field("MuMech.MechJebModuleNodeExecutor", "Autowarp").Field("MuMech.MechJebModuleNodeExecutor", "LeadTime")
                .Field("MuMech.MechJebModuleNodeExecutor", "State")
                .Method("MuMech.MechJebModuleLandingAutopilot", "LandAtPositionTarget", "System.Object")
                .Method("MuMech.MechJebModuleLandingAutopilot", "LandUntargeted", "System.Object")
                .Method("MuMech.MechJebModuleLandingAutopilot", "StopLanding")
                .Method("MuMech.MechJebModuleTargetController", "SetPositionTarget", "CelestialBody", Double, Double)
                .Method("MuMech.MechJebModuleTargetController", "Unset")
                .Method("MuMech.MechJebModuleWarpController", "WarpToUT", Double, Double)
                .Method("MuMech.MechJebModuleStagingController", "AutoStageLimitRequest", Int, "System.Object")
                .Method("MuMech.MechJebModuleStagingController", "AutoStageLimitRemove", "System.Object")
                .Field("MuMech.MechJebModuleAscentSettings", "DesiredOrbitAltitude")
                .Field("MuMech.MechJebModuleAscentSettings", "DesiredInclination")
                .Method("MuMech.MechJebModuleAscentSettings", "get_AscentAutopilot")
                .Method("MuMech.EditableDoubleMult", "set_Val", Double)
                .Field("MuMech.ComputerModule", "Users").Method("MuMech.ComputerModule", "get_Enabled")
                .Method("MuMech.UserPool", "Add", "System.Object").Method("MuMech.UserPool", "Remove", "System.Object")
                .Tested("69a956b331f3", "e28434d47a4d") // MechJeb 2.16.0.0, development build 1771; 2.15.4.0
                .TestedVersions("2.15.4.0", "2.16.0.0")
                .OnChange(WhenChanged.Fallback, "the autopilot is greyed out, and the nodes are flown by hand");

        // The autopilot meets a vessel and docks with it through MechJeb's rendezvous and docking autopilots (Autopilot/MechJebDriver). They are
        // modules of the master core that GetComputerModule finds by the class's name; the target controller takes the vessel or port to meet.
        // MechJeb 2.15.4.0 and 2.16, source read (MechJebModuleRendezvousAutopilot, MechJebModuleDockingAutopilot, MechJebModuleTargetController):
        // the two differ in the names of the vessel state's members only.
        public static readonly ForeignAssumption MechJebRendezvous =
            new ForeignAssumption("mechjeb-rendezvous", "MechJeb", "the autopilot meets another vessel and docks with it")
                .Type("MuMech.MechJebModuleRendezvousAutopilot").Type("MuMech.MechJebModuleDockingAutopilot").Type("MuMech.MechJebModuleTargetController")
                .Field("MuMech.MechJebCore", "Target")
                .Method("MuMech.MechJebCore", "GetComputerModule", "System.String")
                .Method("MuMech.MechJebModuleTargetController", "Set", "ITargetable")
                .Field("MuMech.MechJebModuleRendezvousAutopilot", "desiredDistance")
                .Tested("02286d128350", "6614bed5fc17") // MechJeb 2.16.0.0, development build 1771; 2.15.4.0
                .TestedVersions("2.15.4.0", "2.16.0.0")
                .OnChange(WhenChanged.Fallback, "the route stops at the orbit instead of meeting the target");

        // The autopilot button's tooltip names what MechJeb does now (Hud/AutopilotActivity): the status text of its rendezvous and docking
        // autopilots and the time the warp controller takes warp to. MechJeb 2.16, source read.
        public static readonly ForeignAssumption MechJebActivity =
            new ForeignAssumption("mechjeb-activity", "MechJeb", "the autopilot button's tooltip names what MechJeb does")
                .Type("MuMech.MechJebModuleWarpController").Type("MuMech.MechJebModuleRendezvousAutopilot").Type("MuMech.MechJebModuleDockingAutopilot")
                .Field("MuMech.MechJebModuleRendezvousAutopilot", "status")
                .Field("MuMech.MechJebModuleDockingAutopilot", "status")
                .Method("MuMech.MechJebModuleWarpController", "get_warpToUT")
                .Tested("c514c9e45f46") // MechJeb 2.15.4.0
                .OnChange(WhenChanged.Fallback, "the tooltip leaves out MechJeb's own status and warp target");

        // The Auto Staging switch also takes the ascent autopilot's own autostage choice (Autopilot/Flight/MechJebDriver.SetSwitch).
        public static readonly ForeignAssumption MechJebAscentStaging =
            new ForeignAssumption("mechjeb-ascent-staging", "MechJeb", "the Auto Staging switch also switches the ascent autopilot's staging")
                .Type("MuMech.MechJebModuleAscentSettings")
                .Method("MuMech.MechJebModuleAscentSettings", "set_Autostage", Bool)
                .Tested("73ab98b6051f") // MechJeb 2.15.4.0
                .OnChange(WhenChanged.Fallback, "the ascent autopilot keeps staging with the switch off");

        // The Auto Staging switch also takes the landing autopilot's own autostage choice (Autopilot/Flight/MechJebDriver.SetSwitch). Builds that
        // do not have the field keep the landing autopilot's staging as it is.
        public static readonly ForeignAssumption MechJebLandingStaging =
            new ForeignAssumption("mechjeb-landing-staging", "MechJeb", "the Auto Staging switch also switches the landing autopilot's staging")
                .Type("MuMech.MechJebModuleHoverslamAutopilot")
                .Field("MuMech.MechJebModuleHoverslamAutopilot", "Autostage")
                .OnChange(WhenChanged.Fallback, "the landing autopilot keeps staging with the switch off");

        // Parsek's ghosts as the autopilot tells them (Autopilot/ParsekBridge). Parsek has no API: a ghost is told by its persistent id through
        // reflection. Parsek 0.10.6, source read (a beta: the format is generation 4 and changes). Nothing of Parsek is written. Each further
        // thing the autopilot takes from Parsek gets an entry of its own, so that a change in one switches off that one alone.
        public static readonly ForeignAssumption ParsekGhost =
            new ForeignAssumption("parsek-ghost", "Parsek", "the autopilot kept off Parsek's ghosts")
                .Type("Parsek.GhostMapPresence")
                .Method("Parsek.GhostMapPresence", "IsGhostMapVessel", "System.UInt32")
                .Tested("10d2995d828d") // Parsek 0.10.6
                .TestedVersions("0.10.6.0")
                .OnChange(WhenChanged.Fallback, "every vessel counts as real");

        // Parsek's recorder as the map window shows it: whether this flight is being recorded, and how many recordings are kept. Both are read, nothing
        // is started or stopped. Parsek 0.10.6, source read: ParsekFlight.Instance holds the flight's recorder, whose IsRecording says it,
        // and RecordingStore.CommittedRecordings is the list of the recordings that were committed.
        public static readonly ForeignAssumption ParsekRecorder =
            new ForeignAssumption("parsek-recorder", "Parsek", "the map window says whether Parsek records the flight")
                .Type("Parsek.ParsekFlight").Type("Parsek.FlightRecorder").Type("Parsek.RecordingStore")
                .Method("Parsek.ParsekFlight", "get_Instance")
                .Field("Parsek.ParsekFlight", "recorder", "Parsek.FlightRecorder")
                .Method("Parsek.FlightRecorder", "get_IsRecording")
                .Method("Parsek.RecordingStore", "get_CommittedRecordings")
                .Tested("dd556c4ede68") // Parsek 0.10.6
                .TestedVersions("0.10.6.0")
                .OnChange(WhenChanged.Fallback, "the window does not say whether Parsek records");

        // The autopilot has Parsek record the flight it flies, which Parsek does by itself only for a launch, and ends the recording with the route.
        // Parsek 0.10.6, source read: ParsekFlight.StartRecording(suppressStartScreenMessage, fromPrelaunchTransition) begins the recording of the
        // active vessel in the flight's recorder, FlightRecorder.IsRecording says whether one runs, and ParsekFlight.StopRecording ends it. Parsek's
        // own merge dialog commits it when the flight is left.
        public static readonly ForeignAssumption ParsekStart =
            new ForeignAssumption("parsek-start", "Parsek", "the autopilot has Parsek record its flight")
                .Type("Parsek.ParsekFlight").Type("Parsek.FlightRecorder")
                .Method("Parsek.ParsekFlight", "get_Instance")
                .Method("Parsek.ParsekFlight", "StartRecording", "System.Boolean", "System.Boolean").Method("Parsek.ParsekFlight", "StopRecording")
                .Field("Parsek.ParsekFlight", "recorder", "Parsek.FlightRecorder")
                .Method("Parsek.FlightRecorder", "get_IsRecording")
                .Tested("fe44ccbb6097") // Parsek 0.10.6
                .TestedVersions("0.10.6.0")
                .OnChange(WhenChanged.Fallback, "Parsek is not asked to record the autopilot's flight");

        // A recording of the route made ahead of the flight (Autopilot/ParsekRecording). It is built the way Parsek's own in-game test builds one
        // and committed with RecordingStore.CommitTree. Parsek then plays it, and puts the vessel back at its end when the player is not in the flight.
        // Parsek 0.10.6 (source of 0.10.7 read): Recording and TrajectoryPoint and OrbitSegment hold their data in public fields, a terminal orbit is Keplerian
        // elements in degrees and radians, and VesselSpawner.TryBackupSnapshot makes the vessel's snapshot.
        public static readonly ForeignAssumption ParsekRecording =
            new ForeignAssumption("parsek-recording", "Parsek", "the route is recorded ahead of the flight, for Parsek to play when the player leaves")
                .Type("Parsek.Recording").Type("Parsek.RecordingTree").Type("Parsek.TrajectoryPoint").Type("Parsek.OrbitSegment")
                .Type("Parsek.TerminalState").Type("Parsek.RecordingStore").Type("Parsek.VesselSpawner")
                .Field("Parsek.Recording", "RecordingId").Field("Parsek.Recording", "TreeId").Field("Parsek.Recording", "VesselName")
                .Field("Parsek.Recording", "VesselPersistentId").Field("Parsek.Recording", "TerminalStateValue")
                .Field("Parsek.Recording", "TerminalOrbitInclination").Field("Parsek.Recording", "TerminalOrbitEccentricity")
                .Field("Parsek.Recording", "TerminalOrbitSemiMajorAxis").Field("Parsek.Recording", "TerminalOrbitLAN")
                .Field("Parsek.Recording", "TerminalOrbitArgumentOfPeriapsis").Field("Parsek.Recording", "TerminalOrbitMeanAnomalyAtEpoch")
                .Field("Parsek.Recording", "TerminalOrbitEpoch").Field("Parsek.Recording", "TerminalOrbitBody")
                .Field("Parsek.Recording", "VesselSnapshot").Field("Parsek.Recording", "GhostVisualSnapshot")
                .Field("Parsek.Recording", "Points").Field("Parsek.Recording", "OrbitSegments")
                .Field("Parsek.RecordingTree", "Id").Field("Parsek.RecordingTree", "TreeName").Field("Parsek.RecordingTree", "RootRecordingId")
                .Field("Parsek.RecordingTree", "ActiveRecordingId").Field("Parsek.RecordingTree", "Recordings")
                .Field("Parsek.TrajectoryPoint", "ut").Field("Parsek.TrajectoryPoint", "latitude").Field("Parsek.TrajectoryPoint", "longitude")
                .Field("Parsek.TrajectoryPoint", "altitude").Field("Parsek.TrajectoryPoint", "rotation").Field("Parsek.TrajectoryPoint", "velocity")
                .Field("Parsek.TrajectoryPoint", "bodyName")
                .Field("Parsek.OrbitSegment", "startUT").Field("Parsek.OrbitSegment", "endUT").Field("Parsek.OrbitSegment", "inclination")
                .Field("Parsek.OrbitSegment", "eccentricity").Field("Parsek.OrbitSegment", "semiMajorAxis")
                .Field("Parsek.OrbitSegment", "longitudeOfAscendingNode").Field("Parsek.OrbitSegment", "argumentOfPeriapsis")
                .Field("Parsek.OrbitSegment", "meanAnomalyAtEpoch").Field("Parsek.OrbitSegment", "epoch").Field("Parsek.OrbitSegment", "bodyName")
                .Type("Parsek.RecordingSchema").Field("Parsek.RecordingSchema", "CurrentRecordingFormatVersion")
                .Field("Parsek.RecordingSchema", "CurrentRecordingSchemaGeneration")
                .Method("Parsek.RecordingStore", "CommitTree", "Parsek.RecordingTree").Method("Parsek.RecordingStore", "get_CommittedRecordings")
                .Method("Parsek.RecordingStore", "MarkTreeAsApplied", "Parsek.RecordingTree").Method("Parsek.Recording", "get_StartUT")
                .Type("Parsek.PlaybackScopeTracker").Method("Parsek.PlaybackScopeTracker", "NotePlayhead", "System.String", "System.Double", "System.Double")
                .Type("Parsek.LedgerOrchestrator").Method("Parsek.LedgerOrchestrator", "NotifyLedgerTreeCommitted", "Parsek.RecordingTree")
                .Type("Parsek.ClaimedVesselRemoval").Type("Parsek.LiveClaimedVesselRemovalTarget").Type("Parsek.IClaimedVesselRemovalTarget")
                .Method("Parsek.ClaimedVesselRemoval", "Remove", "Parsek.IClaimedVesselRemovalTarget", "System.String")
                .Method("Parsek.VesselSpawner", "TryBackupSnapshot", "Vessel")
                .Tested("5f90828888ca") // Parsek 0.10.6
                .TestedVersions("0.10.6.0")
                .OnChange(WhenChanged.Fallback, "the route is not recorded for Parsek, and the flight stops when the player leaves");

        // The entries, in the order the report lists them.
        public static readonly ForeignAssumption[] All =
        {
            MechJebAutopilot, MechJebRendezvous, MechJebActivity, MechJebAscentStaging, MechJebLandingStaging,
            ParsekGhost, ParsekRecorder, ParsekStart, ParsekRecording,
        };
    }
}
