using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // One vessel's plan: its route, with who flies to each destination and the
    // choices for the steps before it, the stages saved or dropped on each step, the
    // margin, whether plane changes count, the lander and the capsule. Kept per
    // vessel name in GameData/ReDefinition/PluginData/planner.cfg, so that a craft
    // brings its plan back when it is loaded into the editor again.
    internal sealed class PlannerState
    {
        public readonly List<RouteStop> Stops = new List<RouteStop>();
        public double Margin = 0.1;
        public bool PlaneChanges;

        // The stop at `index` is gone (Stops.RemoveAt): the steps into the stop
        // after it now start elsewhere and cost what they did not, so their stages
        // burn as KSP fires them again.
        internal void RemoveStop(int index)
        {
            if (index < 0 || index >= Stops.Count) return;
            Stops[index].Stages = new[] { new StagePlan(), new StagePlan(), new StagePlan(), new StagePlan() };
        }

        // The lander and the capsule: a part group's key (PartGroupChoice.Key), None
        // for none, Automatic for what the planner finds (VesselShape).
        internal const string Automatic = "Automatic";
        internal const string None = "None";
        public string Lander = Automatic;
        public string Capsule = Automatic;

        private const string FileName = "planner.cfg";
        private const int MaxPlans = 200;

        // From the home body's surface to its low orbit: where every mission
        // starts.
        internal static PlannerState Default(PlannerBodies bodies)
        {
            PlannerState state = new PlannerState();
            int home = bodies.Home;
            state.Stops.Add(new RouteStop { Body = home, Kind = StopKind.Surface });
            state.Stops.Add(new RouteStop
            {
                Body = home, Kind = StopKind.Orbit,
                Altitude = home < bodies.Bodies.Count ? bodies.Bodies[home].LowOrbitAltitude : 80000.0,
            });
            return state;
        }

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

        // The vessel's name as the file keeps it: escaped, since a config file
        // reads "//" as the start of a comment.
        internal static string Key(string vessel)
        {
            return Uri.EscapeDataString(vessel ?? "");
        }

        internal static bool Exists(string vessel)
        {
            try
            {
                string path = PluginData.Path(FileName);
                if (!File.Exists(path)) return false;
                ConfigNode root = ConfigNode.Load(path);
                if (root == null) return false;
                foreach (ConfigNode plan in root.GetNodes("PLAN"))
                    if (plan.GetValue("vessel") == Key(vessel)) return true;
            }
            catch (Exception)
            {
            }
            return false;
        }

        private ConfigNode ToNode(string vessel, PlannerBodies bodies)
        {
            ConfigNode node = new ConfigNode("PLAN");
            node.AddValue("vessel", Key(vessel));
            node.AddValue("margin", Margin.ToString("0.00", CultureInfo.InvariantCulture));
            node.AddValue("planeChanges", PlaneChanges);
            node.AddValue("lander", Lander);
            node.AddValue("capsule", Capsule);
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
                for (int i = 0; i < RouteStop.Roles; i++) by[i] = stop.By[i] >= 0 ? ((Vehicle)stop.By[i]).ToString() : "Auto";
                stopNode.AddValue("by", string.Join(", ", by));
                stopNode.AddValue("refuel", string.Join(", ", Array.ConvertAll(stop.Refuel, flag => flag.ToString())));
                stopNode.AddValue("orbitFirst", stop.OrbitFirst);
                stopNode.AddValue("leavesPayload", stop.LeavesPayload);
                stopNode.AddValue("splitBurns", stop.SplitBurns);
                for (int i = 0; i < RouteStop.Roles; i++)
                {
                    foreach (int stage in stop.Stages[i].Save) AddStage(stopNode, (StepRole)i, stage, "Save");
                    foreach (int stage in stop.Stages[i].DropAfter) AddStage(stopNode, (StepRole)i, stage, "DropAfter");
                }
            }
            return node;
        }

        private static void AddStage(ConfigNode stopNode, StepRole role, int stage, string choice)
        {
            ConfigNode node = stopNode.AddNode("STAGE");
            node.AddValue("step", role);
            node.AddValue("stage", stage);
            node.AddValue("choice", choice);
        }

        private static PlannerState FromNode(ConfigNode node, PlannerBodies bodies)
        {
            PlannerState state = new PlannerState();
            double margin;
            if (double.TryParse(node.GetValue("margin"), NumberStyles.Float, CultureInfo.InvariantCulture, out margin))
                state.Margin = Math.Max(0.0, Math.Min(margin, 1.0));
            bool planes;
            if (bool.TryParse(node.GetValue("planeChanges"), out planes)) state.PlaneChanges = planes;
            if (!string.IsNullOrEmpty(node.GetValue("lander"))) state.Lander = node.GetValue("lander");
            if (!string.IsNullOrEmpty(node.GetValue("capsule"))) state.Capsule = node.GetValue("capsule");
            bool skipped = false;
            foreach (ConfigNode stopNode in node.GetNodes("STOP"))
            {
                int body = bodies.IndexOf(stopNode.GetValue("body"));
                // A body the loaded system does not have: the plan was made for
                // another one.
                if (body < 0)
                {
                    skipped = true;
                    continue;
                }
                RouteStop stop = new RouteStop { Body = body };
                string kind = stopNode.GetValue("kind") ?? "";
                if (Enum.IsDefined(typeof(StopKind), kind) && kind != StopKind.Entry.ToString())
                    stop.Kind = (StopKind)Enum.Parse(typeof(StopKind), kind);
                double altitude;
                stop.Altitude = double.TryParse(stopNode.GetValue("altitude"), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out altitude)
                    ? altitude
                    : bodies.Bodies[body].LowOrbitAltitude;
                bool useAtmosphere;
                if (bool.TryParse(stopNode.GetValue("useAtmosphere"), out useAtmosphere)) stop.UseAtmosphere = useAtmosphere;
                string[] by = (stopNode.GetValue("by") ?? "").Split(',');
                string[] refuel = (stopNode.GetValue("refuel") ?? "").Split(',');
                for (int i = 0; i < RouteStop.Roles; i++)
                {
                    string name = i < by.Length ? by[i].Trim() : "";
                    if (Enum.IsDefined(typeof(Vehicle), name)) stop.By[i] = (int)(Vehicle)Enum.Parse(typeof(Vehicle), name);
                    bool flag;
                    if (i < refuel.Length && bool.TryParse(refuel[i].Trim(), out flag)) stop.Refuel[i] = flag;
                }
                bool orbitFirst;
                if (bool.TryParse(stopNode.GetValue("orbitFirst"), out orbitFirst)) stop.OrbitFirst = orbitFirst;
                bool payload;
                if (bool.TryParse(stopNode.GetValue("leavesPayload"), out payload)) stop.LeavesPayload = payload;
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
            return state;
        }
    }
}
