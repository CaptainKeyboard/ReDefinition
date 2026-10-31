using KSP.Localization;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System;
using ReDefinition.Autopilot.Planning;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // A section of the vessel as the player adds it: what it is for, which of the
    // vessel's parts it is, and for a satellite where it stays.
    internal sealed class PlannedSection
    {
        // 1 and up, the same for as long as the plan keeps the section.
        public int Id;
        public SectionKind Kind;
        // A part group's key (PartGroupChoice.Key), Automatic for what the planner
        // finds, empty where none is picked yet.
        public string Group = "";
        // A satellite: the destination it stays at, -1 for the last orbit before the
        // route lands or ends.
        public int DeployAt = -1;
    }

    // A section let go at a stop: the stop's index in the plan, and the section.
    internal sealed class StopRelease
    {
        public int Stop;
        public SectionRelease Section;
    }

    // One vessel's plan: its route, with who flies to each destination and the
    // choices for the steps before it, the stages saved or dropped on each step, the
    // margin, whether plane changes count, and the vessel's sections. Kept per vessel
    // name in GameData/ReDefinition/PluginData/planner.cfg, so that a craft brings its
    // plan back when it is loaded into the editor again.
    internal sealed class PlannerState
    {
        // The group key of a section the planner finds by itself, rather than one the player picked.
        internal const string Automatic = "Automatic";

        // The route, the start first; the margin over the route's delta-v, 0.1 for ten per cent;
        // the sections, with the planner's found ones once kept.
        public readonly List<RouteStop> Stops = new List<RouteStop>();
        public double Margin = 0.1;
        public readonly List<PlannedSection> Sections = new List<PlannedSection>();
        // The satellites let go at the stops, by the part that separates them, as the planner last worked them out; kept so that a flight that
        // is loaded again still knows what is to be let go.
        public readonly List<StopRelease> Releases = new List<StopRelease>();
        // The kinds the planner adds by itself that the player took away.
        public readonly HashSet<SectionKind> Dismissed = new HashSet<SectionKind>();
        private int nextId = 1;

        // The sections that are let go at the stop at this index.
        internal List<SectionRelease> ReleasesAt(int stop)
        {
            return Releases.FindAll(release => release.Stop == stop).ConvertAll(release => release.Section);
        }

        // The ids of the capsule, the satellite and the lander the planner finds, as
        // long as they are its own; the player's sections count up from 1.
        internal const int FoundCapsule = 901;
        internal const int FoundSatellite = 902;
        internal const int FoundLander = 903;

        // A new section of the player's, with the next free id below the planner's own.
        internal PlannedSection AddSection(SectionKind kind, string group)
        {
            // Below the planner's own ids, and free.
            if (nextId >= FoundCapsule) nextId = 1;
            while (Sections.Exists(item => item.Id == nextId)) nextId++;
            PlannedSection section = new PlannedSection { Id = nextId++, Kind = kind, Group = group ?? "" };
            Sections.Add(section);
            return section;
        }

        // A section the planner found, kept in the plan once the player sets something
        // on it.
        internal void Keep(PlannedSection section)
        {
            if (section != null && !Sections.Contains(section)) Sections.Insert(0, section);
        }

        // A section goes, and with the last of its kind the planner no longer adds one
        // by itself; the steps set to it are the planner's again.
        internal void RemoveSection(PlannedSection section)
        {
            if (section == null) return;
            Sections.Remove(section);
            if (!Sections.Exists(item => item.Kind == section.Kind)) Dismissed.Add(section.Kind);
            foreach (RouteStop stop in Stops)
                for (int i = 0; i < RouteStop.Roles; i++)
                    if (stop.By[i] > 0 && Flyer.FromCode(stop.By[i]).Section == section.Id) stop.By[i] = -1;
        }

        // Adjusts the plan to the stop at `index` being gone (Stops.RemoveAt). The steps into the stop
        // after it now start elsewhere and cost what they did not, so their stages
        // burn as KSP fires them again. A satellite staying there stays where the
        // planner puts it.
        internal void RemoveStop(int index)
        {
            foreach (PlannedSection section in Sections)
            {
                if (section.DeployAt == index) section.DeployAt = -1;
                else if (section.DeployAt > index) section.DeployAt--;
            }
            if (index < 0 || index >= Stops.Count) return;
            Stops[index].Stages = new[] { new StagePlan(), new StagePlan(), new StagePlan(), new StagePlan() };
        }

        // The file in PluginData the plans are kept in, and how many vessels' plans it keeps at most.
        private const string FileName = "planner.cfg";
        private const int MaxPlans = 200;

        // From the home body's surface, where every mission starts, to a destination
        // the player picks.
        internal static PlannerState Default(PlannerBodies bodies)
        {
            PlannerState state = new PlannerState();
            state.Stops.Add(new RouteStop { Body = bodies.Home, Kind = StopKind.Surface });
            state.Stops.Add(new RouteStop { Body = RouteStop.NoBody, Kind = StopKind.Orbit });
            return state;
        }

        // The vessel's plan from the file; the default plan where it has none, or none that can be read.
        internal static PlannerState Load(string vessel, PlannerBodies bodies)
        {
            try
            {
                string path = PluginData.Path(FileName);
                if (!File.Exists(path)) return Default(bodies);
                ConfigNode root = ConfigNode.Load(path);
                if (root == null) return Default(bodies);
                foreach (ConfigNode plan in root.GetNodes("PLAN"))
                {
                    if (plan.GetValue("vessel") != Key(vessel)) continue;
                    PlannerState state = FromNode(plan, bodies);
                    return state.Stops.Count >= 2 ? state : Default(bodies);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " The planner's plans could not be read: " + e.Message);
            }
            return Default(bodies);
        }

        // Writes the vessel's plan to the file in place of its old one, as the newest, keeping at most MaxPlans plans.
        internal void Save(string vessel, PlannerBodies bodies)
        {
            try
            {
                string path = PluginData.Path(FileName);
                ConfigNode root = File.Exists(path) ? ConfigNode.Load(path) ?? new ConfigNode() : new ConfigNode();
                // The plans saved last are at the end; the oldest go first.
                List<ConfigNode> others = new List<ConfigNode>();
                foreach (ConfigNode plan in root.GetNodes("PLAN"))
                    if (plan.GetValue("vessel") != Key(vessel)) others.Add(plan);
                ConfigNode kept = new ConfigNode();
                for (int i = Math.Max(0, others.Count - (MaxPlans - 1)); i < others.Count; i++) kept.AddNode(others[i]);
                kept.AddNode(ToNode(vessel, bodies));
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
                kept.Save(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning(Log.Tag + " The planner's plan could not be saved: " + e.Message);
            }
        }

        // The vessel's name as the file keeps it. A stock craft has the name the game shows: a vessel launched from it is named by the
        // localisation key (#autoLOC_...), and the editor has the text. It is escaped, since a config file reads "//" as the start of a comment.
        internal static string Key(string vessel)
        {
            if (vessel != null && vessel.StartsWith("#autoLOC")) vessel = Localizer.Format(vessel);
            return Uri.EscapeDataString(vessel ?? "");
        }

        // Who flies a step as the file keeps it: Auto, Ship, Section 3, Without 3.
        private static string FlyerText(int code)
        {
            if (code < 0) return "Auto";
            Flyer flyer = Flyer.FromCode(code);
            switch (flyer.Kind)
            {
                case FlyerKind.Alone: return "Section " + flyer.Section;
                case FlyerKind.Without: return "Without " + flyer.Section;
                default: return "Ship";
            }
        }

        // Who flies a step from the file's text: a Flyer's code, -1 for Auto or a text it does not know.
        private static int FlyerCode(string text)
        {
            text = (text ?? "").Trim();
            int id;
            if (text == "Ship") return 0;
            if (text.StartsWith("Section ") && int.TryParse(text.Substring(8), out id) && id > 0) return Flyer.Alone(id).Code;
            if (text.StartsWith("Without ") && int.TryParse(text.Substring(8), out id) && id > 0) return Flyer.Without(id).Code;
            return -1;
        }

        // The plan as the file keeps it: a PLAN node with its sections, its stops and their stage choices.
        private ConfigNode ToNode(string vessel, PlannerBodies bodies)
        {
            ConfigNode node = new ConfigNode("PLAN");
            node.AddValue("vessel", Key(vessel));
            node.AddValue("margin", Margin.ToString("0.00", CultureInfo.InvariantCulture));
            node.AddValue("nextSection", nextId);
            foreach (SectionKind kind in Dismissed) node.AddValue("dismissed", kind);
            foreach (PlannedSection section in Sections)
            {
                ConfigNode sectionNode = node.AddNode("SECTION");
                sectionNode.AddValue("id", section.Id);
                sectionNode.AddValue("kind", section.Kind);
                sectionNode.AddValue("group", section.Group);
                sectionNode.AddValue("deployAt", section.DeployAt);
            }
            foreach (StopRelease release in Releases)
            {
                ConfigNode releaseNode = node.AddNode("RELEASE");
                releaseNode.AddValue("stop", release.Stop);
                releaseNode.AddValue("name", release.Section.Name);
                releaseNode.AddValue("separator", release.Section.SeparatorId);
                releaseNode.AddValue("index", release.Section.Index);
                if (release.Section.Title != null) releaseNode.AddValue("title", release.Section.Title);
                releaseNode.AddValue("docking", release.Section.Docking);
            }
            foreach (RouteStop stop in Stops)
            {
                ConfigNode stopNode = node.AddNode("STOP");
                stopNode.AddValue("body", stop.Body >= 0 && stop.Body < bodies.Bodies.Count ? bodies.Bodies[stop.Body].Id : "");
                stopNode.AddValue("kind", stop.Kind);
                stopNode.AddValue("altitude", stop.Altitude.ToString("0", CultureInfo.InvariantCulture));
                stopNode.AddValue("useAtmosphere", stop.UseAtmosphere);
                // Per step on the way here, in the order launch, transfer, arrival,
                // landing.
                string[] by = new string[RouteStop.Roles];
                for (int i = 0; i < RouteStop.Roles; i++) by[i] = FlyerText(stop.By[i]);
                stopNode.AddValue("by", string.Join(", ", by));
                stopNode.AddValue("refuel", string.Join(", ", Array.ConvertAll(stop.Refuel, flag => flag.ToString())));
                stopNode.AddValue("refuelFrom", string.Join(", ", Array.ConvertAll(stop.RefuelFrom, stage => stage.ToString())));
                stopNode.AddValue("orbitFirst", stop.OrbitFirst);
                stopNode.AddValue("splitBurns", stop.SplitBurns);
                for (int i = 0; i < RouteStop.Roles; i++)
                {
                    foreach (int stage in stop.Stages[i].Save) AddStage(stopNode, (StepRole)i, stage, "Save");
                    foreach (int stage in stop.Stages[i].DropAfter) AddStage(stopNode, (StepRole)i, stage, "DropAfter");
                }
            }
            return node;
        }

        // One stage choice for one step on the way to the stop: Save or DropAfter.
        private static void AddStage(ConfigNode stopNode, StepRole role, int stage, string choice)
        {
            ConfigNode node = stopNode.AddNode("STAGE");
            node.AddValue("step", role);
            node.AddValue("stage", stage);
            node.AddValue("choice", choice);
        }

        // The plan from its PLAN node. A stop on a body the loaded system does not have is left out, and with it the stage
        // choices of the stops after it and every satellite's destination.
        private static PlannerState FromNode(ConfigNode node, PlannerBodies bodies)
        {
            PlannerState state = new PlannerState();
            double margin;
            if (double.TryParse(node.GetValue("margin"), NumberStyles.Float, CultureInfo.InvariantCulture, out margin))
                state.Margin = Math.Max(0.0, Math.Min(margin, 1.0));
            foreach (string dismissed in node.GetValues("dismissed"))
                if (Enum.IsDefined(typeof(SectionKind), dismissed)) state.Dismissed.Add((SectionKind)Enum.Parse(typeof(SectionKind), dismissed));
            foreach (ConfigNode sectionNode in node.GetNodes("SECTION"))
            {
                int id, deployAt;
                string kind = sectionNode.GetValue("kind") ?? "";
                if (!int.TryParse(sectionNode.GetValue("id"), out id) || id <= 0 || !Enum.IsDefined(typeof(SectionKind), kind)) continue;
                if (state.Sections.Exists(section => section.Id == id)) continue;
                PlannedSection loaded = new PlannedSection
                {
                    Id = id, Kind = (SectionKind)Enum.Parse(typeof(SectionKind), kind), Group = sectionNode.GetValue("group") ?? "",
                };
                if (int.TryParse(sectionNode.GetValue("deployAt"), out deployAt)) loaded.DeployAt = Math.Max(-1, deployAt);
                state.Sections.Add(loaded);
                if (id < FoundCapsule) state.nextId = Math.Max(state.nextId, id + 1);
            }
            int next;
            if (int.TryParse(node.GetValue("nextSection"), out next) && next < FoundCapsule) state.nextId = Math.Max(state.nextId, next);
            bool skipped = false;
            foreach (ConfigNode stopNode in node.GetNodes("STOP"))
            {
                string bodyName = stopNode.GetValue("body") ?? "";
                int body = bodyName.Length == 0 ? RouteStop.NoBody : bodies.IndexOf(bodyName);
                // A body the loaded system does not have: the plan was made for
                // another one.
                if (body < 0 && bodyName.Length > 0)
                {
                    skipped = true;
                    continue;
                }
                RouteStop stop = new RouteStop { Body = body };
                string kind = stopNode.GetValue("kind") ?? "";
                if (kind == StopKind.Surface.ToString() || kind == StopKind.Orbit.ToString())
                    stop.Kind = (StopKind)Enum.Parse(typeof(StopKind), kind);
                double altitude;
                stop.Altitude = double.TryParse(stopNode.GetValue("altitude"), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out altitude)
                    ? altitude
                    : body >= 0 ? bodies.Bodies[body].LowOrbitAltitude : 0.0;
                bool useAtmosphere;
                if (bool.TryParse(stopNode.GetValue("useAtmosphere"), out useAtmosphere)) stop.UseAtmosphere = useAtmosphere;
                string[] by = (stopNode.GetValue("by") ?? "").Split(',');
                string[] refuel = (stopNode.GetValue("refuel") ?? "").Split(',');
                string[] refuelFrom = (stopNode.GetValue("refuelFrom") ?? "").Split(',');
                for (int i = 0; i < RouteStop.Roles; i++)
                {
                    stop.By[i] = i < by.Length ? FlyerCode(by[i]) : -1;
                    bool flag;
                    if (i < refuel.Length && bool.TryParse(refuel[i].Trim(), out flag)) stop.Refuel[i] = flag;
                    int stage;
                    if (i < refuelFrom.Length && int.TryParse(refuelFrom[i].Trim(), out stage)) stop.RefuelFrom[i] = Math.Max(-1, stage);
                }
                bool orbitFirst;
                if (bool.TryParse(stopNode.GetValue("orbitFirst"), out orbitFirst)) stop.OrbitFirst = orbitFirst;
                bool split;
                if (bool.TryParse(stopNode.GetValue("splitBurns"), out split)) stop.SplitBurns = split;
                // With a stop before it gone, the steps here are not the ones the
                // stages were chosen for.
                if (!skipped)
                    foreach (ConfigNode stageNode in stopNode.GetNodes("STAGE"))
                    {
                        string step = stageNode.GetValue("step") ?? "";
                        int stage;
                        if (!Enum.IsDefined(typeof(StepRole), step) || !int.TryParse(stageNode.GetValue("stage"), out stage)) continue;
                        StagePlan plan = stop.Stages[(int)(StepRole)Enum.Parse(typeof(StepRole), step)];
                        string choice = stageNode.GetValue("choice");
                        if (choice == "Save") plan.Save.Add(stage);
                        else if (choice == "DropAfter") plan.DropAfter.Add(stage);
                    }
                state.Stops.Add(stop);
            }
            // With a stop gone, a satellite's destination is not the one it was set to.
            if (skipped)
                foreach (PlannedSection section in state.Sections) section.DeployAt = -1;
            else
                foreach (ConfigNode releaseNode in node.GetNodes("RELEASE"))
                {
                    int at;
                    uint separator;
                    bool docking;
                    if (!int.TryParse(releaseNode.GetValue("stop"), out at) || at < 0 || at >= state.Stops.Count) continue;
                    if (!uint.TryParse(releaseNode.GetValue("separator"), out separator) || separator == 0u) continue;
                    bool.TryParse(releaseNode.GetValue("docking"), out docking);
                    int index;
                    if (!int.TryParse(releaseNode.GetValue("index"), out index)) index = -1;
                    state.Releases.Add(new StopRelease
                    {
                        Stop = at,
                        Section = new SectionRelease
                        {
                            Name = releaseNode.GetValue("name") ?? "Satellite", SeparatorId = separator, Docking = docking, Index = index, Title = releaseNode.GetValue("title"),
                        },
                    });
                }
            return state;
        }
    }
}
